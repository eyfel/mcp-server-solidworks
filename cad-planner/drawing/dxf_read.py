#!/usr/bin/env python3
"""dxf_read.py -- DXF -> `draw` dialect extractor (analysis tool; version in ANALYSIS_VERSION).

The reverse pipeline's NEW front-end: turns a 2D DXF into the small, high-level description a model
can actually reason over. It is deliberately LEAN -- it emits only what was provably NEEDED to
reconstruct the four sample parts (f-1, s-1, f-2, s-2) exactly. Payload size is a first-class
constraint: every field here earned its place by being consumed in a real reconstruction.

What it does NOT do (on purpose): no feature classification, no 3D inference, no confidence scores.
It reports deterministic FACTS; interpretation belongs to the recipe.

Key facts it recovers (each one cost a real bug when missing):
  * TRUE dimension values  -- `get_measurement() x DIMSTYLE.dimlfac`. The raw value is PAPER space:
    on a 1:2 sheet every number is half. Skipping dimlfac silently halves the part.
  * Edge class per entity  -- linetype with BYLAYER resolved through the layer: Continuous =
    visible, HIDDEN = obscured, PHANTOM = section cut line, CENTER* = axis.
  * Views by clustering    -- bbox union-find; the cluster spanning the whole sheet is the FRAME
    (title block), never a view.
  * Bend notes             -- "UP 90 R 1" / "DOWN 90 R 1" with the bend line they annotate.
  * Free notes             -- e.g. a bare "2 mm" thickness note, and section labels.

Usage:
  python dxf_read.py <file.dxf>                 # print the draw-dialect JSON
  python dxf_read.py <file.dxf> --save          # also write <name>.analysis-v<VER>.json beside it
  python dxf_read.py <file.dxf> --save --desc-file part.txt   # attach the author's description

Saved artifacts are a RESEARCH POOL for cross-part pattern finding, not part of the normal build
flow -- the model does not need to read them. Same tool version overwrites in place; a new version
writes a new file, so several analyses of one part can coexist across versions.

Dependency: ezdxf (MIT).
"""
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import sys
from collections import Counter

try:
    from ezdxf import recover
except ImportError as _exc:      # NEVER sys.exit() here: this module is imported INTO a hosted
    raise ImportError(           # MCP server, where exiting would kill the whole process.
        "ezdxf is required for DXF reading - pip install ezdxf") from _exc

try:                             # normal: imported as part of the `drawing` package
    from . import contour, pairing
except ImportError:              # fallback: run directly as a script from this directory
    import contour
    import pairing

ANALYSIS_VERSION = "0.2.0"   # 0.2.0: contour chaining (loops + open_chains), emit-time duplicate
                             #        dedup, explicit arc endpoints, bend pairing by class + side
                             # 0.1.1: radius/diameter read off the GEOMETRY (+measures);
                             #        section-arrow clusters tagged role="annotation"
_HERE = os.path.dirname(os.path.abspath(__file__))

# Closed vocabularies (EDGE_CLASSES / VIEW_ROLES / BEND_DIRECTIONS) live in vocab.py.


def load_config():
    with open(os.path.join(_HERE, "config.json"), "r", encoding="utf-8") as fh:
        return json.load(fh)


# --------------------------------------------------------------------------- helpers
def _r(v, nd=4):
    return round(float(v), nd)


def edge_class(doc, e):
    """Entity linetype with BYLAYER resolved -> a closed class enum. This is the DXF replacement
    for the SLDDRW reader's GetPolylines7 display-block discriminator."""
    lt = getattr(e.dxf, "linetype", "BYLAYER") or "BYLAYER"
    if lt.upper() == "BYLAYER":
        try:
            lt = doc.layers.get(e.dxf.layer).dxf.linetype
        except Exception:
            lt = "Continuous"
    u = lt.upper()
    if u.startswith("HIDDEN"):
        return "hidden"
    if u.startswith("PHANTOM"):
        return "cut_line"
    if u.startswith("CENTER"):
        return "center"
    return "visible"


def bbox(e):
    t = e.dxftype()
    if t == "LINE":
        s, en = e.dxf.start, e.dxf.end
        return (min(s.x, en.x), min(s.y, en.y), max(s.x, en.x), max(s.y, en.y))
    if t in ("CIRCLE", "ARC"):
        c, r = e.dxf.center, e.dxf.radius
        return (c.x - r, c.y - r, c.x + r, c.y + r)
    return None


def cluster(items, gap):
    """Union-find on bbox proximity -> connected groups of primitives."""
    par = list(range(len(items)))

    def find(a):
        while par[a] != a:
            par[a] = par[par[a]]
            a = par[a]
        return a

    for i in range(len(items)):
        for j in range(i + 1, len(items)):
            a, b = items[i][1], items[j][1]
            if a[0] <= b[2] + gap and b[0] <= a[2] + gap and a[1] <= b[3] + gap and b[1] <= a[3] + gap:
                ra, rb = find(i), find(j)
                if ra != rb:
                    par[ra] = rb
    groups = {}
    for i, it in enumerate(items):
        groups.setdefault(find(i), []).append(it)
    return list(groups.values())


# --------------------------------------------------------------------------- main read
def read(path, cfg):
    doc, auditor = recover.readfile(path)
    gap = cfg["tolerance"]["cluster_gap_mm"]

    # ---- scale: TRUE value = raw measurement x dimlfac (per dimstyle). ----------------
    dimlfac = {}
    for ds in doc.dimstyles:
        try:
            dimlfac[ds.dxf.name] = float(getattr(ds.dxf, "dimlfac", 1) or 1)
        except Exception:
            dimlfac[ds.dxf.name] = 1.0
    used = [v for k, v in dimlfac.items() if k.upper().startswith("SLD")] or [1.0]
    sheet_scale = used[0]

    # ---- entities across ALL layouts (a DWG->DXF conversion puts them in paperspace) ---
    ents = []
    for layout in doc.layouts:
        for e in layout:
            ents.append(e)

    # A section CUT LINE overhangs the view it is drawn in, so it must not enlarge that view's
    # measured size (f-2's front view read 85.15 instead of 70 before this). It stays in the
    # geometry -- it is the evidence naming WHERE the section was taken -- just not in the bbox.
    geom = [(e, bbox(e)) for e in ents if bbox(e) and edge_class(doc, e) != "cut_line"]
    cut_lines = [(e, bbox(e)) for e in ents if bbox(e) and edge_class(doc, e) == "cut_line"]
    clusters = cluster(geom, gap) if geom else []

    # sheet extent = the biggest cluster's bbox (the border/title frame)
    def cbox(cl):
        xs = [b[0] for _, b in cl] + [b[2] for _, b in cl]
        ys = [b[1] for _, b in cl] + [b[3] for _, b in cl]
        return (min(xs), min(ys), max(xs), max(ys))

    frame = max(clusters, key=lambda c: (cbox(c)[2] - cbox(c)[0]) * (cbox(c)[3] - cbox(c)[1])) if clusters else None
    fb = cbox(frame) if frame else (0, 0, 0, 0)
    sheet_w, sheet_h = fb[2] - fb[0], fb[3] - fb[1]

    # ---- views: every cluster that is NOT the frame. Nested clusters (a hole inside an
    #      outline) are merged into the containing view by bbox containment. -------------
    cands = []
    for cl in clusters:
        if cl is frame:
            continue
        b = cbox(cl)
        # a DEGENERATE cluster (zero extent on an axis) is annotation furniture, not a view --
        # e.g. the section arrow tails, which are bare collinear segments beside the view
        if (b[2] - b[0]) < gap or (b[3] - b[1]) < gap:
            continue
        cands.append({"box": b, "ents": cl})
    cands.sort(key=lambda c: -((c["box"][2] - c["box"][0]) * (c["box"][3] - c["box"][1])))
    views = []
    for c in cands:
        host = None
        for v in views:
            b, vb = c["box"], v["box"]
            if b[0] >= vb[0] - gap and b[2] <= vb[2] + gap and b[1] >= vb[1] - gap and b[3] <= vb[3] + gap:
                host = v
                break
        if host:
            host["ents"].extend(c["ents"])
        else:
            views.append({"box": c["box"], "ents": list(c["ents"])})

    # cut lines rejoin the view whose box they fall inside (they overhang, so test the CENTRE)
    for e, b in cut_lines:
        cx, cy = (b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0
        for v in views:
            vb = v["box"]
            if vb[0] - gap <= cx <= vb[2] + gap and vb[1] - gap <= cy <= vb[3] + gap:
                v["ents"].append((e, b))
                break

    chain_eps = cfg["tolerance"].get("chain_eps_mm", 0.01)
    out_views = []
    for vi, v in enumerate(views):
        x0, y0, x1, y1 = v["box"]
        prims = {"lines": [], "arcs": [], "circles": []}
        # DEDUP at emit time. The SolidWorks DWG->DXF route is lossless but ADDITIVE: it re-emits a
        # section view's boundary geometry (f-2: 20->36 lines, 2->4 arcs; the direct DXF has none).
        # Duplicates make every shared endpoint look like a degree-4 junction, which would stop the
        # chainer dead -- and they are pure payload weight. Dropping them here (rather than in
        # contour.py) keeps loop indices pointing at the arrays the model actually receives.
        seen, dropped = set(), 0
        for e, _ in v["ents"]:
            t, k = e.dxftype(), edge_class(doc, e)
            if t == "LINE":
                s, en = e.dxf.start, e.dxf.end
                p = {"x1": _r((s.x - x0) * sheet_scale), "y1": _r((s.y - y0) * sheet_scale),
                     "x2": _r((en.x - x0) * sheet_scale), "y2": _r((en.y - y0) * sheet_scale),
                     "c": k}
                sig = ("l", k) + tuple(sorted([(p["x1"], p["y1"]), (p["x2"], p["y2"])]))
                bucket = "lines"
            elif t == "ARC":
                c, rad = e.dxf.center, _r(e.dxf.radius * sheet_scale)
                cx, cy = _r((c.x - x0) * sheet_scale), _r((c.y - y0) * sheet_scale)
                a1, a2 = _r(e.dxf.start_angle, 2), _r(e.dxf.end_angle, 2)
                # Explicit endpoints + sweep sense: a DXF ARC always runs CCW from a1 to a2, so a
                # `draw` arc is 1:1 with the IR's `arc` profile primitive and the model never has
                # to do trigonometry to place a corner round.
                ra1, ra2 = math.radians(a1), math.radians(a2)
                p = {"cx": cx, "cy": cy, "r": rad, "a1": a1, "a2": a2,
                     "x1": _r(cx + rad * math.cos(ra1)), "y1": _r(cy + rad * math.sin(ra1)),
                     "x2": _r(cx + rad * math.cos(ra2)), "y2": _r(cy + rad * math.sin(ra2)),
                     "dir": 1, "c": k}
                sig = ("a", k, cx, cy, rad, a1, a2)
                bucket = "arcs"
            elif t == "CIRCLE":
                c = e.dxf.center
                p = {"cx": _r((c.x - x0) * sheet_scale), "cy": _r((c.y - y0) * sheet_scale),
                     "d": _r(2 * e.dxf.radius * sheet_scale), "c": k}
                sig = ("c", k, p["cx"], p["cy"], p["d"])
                bucket = "circles"
            else:
                continue
            if sig in seen:
                dropped += 1
                continue
            seen.add(sig)
            prims[bucket].append(p)

        ov = {
            "vid": "v%d" % vi,
            "paper_box": [_r(x0, 2), _r(y0, 2), _r(x1, 2), _r(y1, 2)],
            "size": [_r((x1 - x0) * sheet_scale), _r((y1 - y0) * sheet_scale)],
            "geometry": prims,
        }
        if dropped:
            ov["dropped_duplicates"] = dropped
        # CONTOUR CHAINING: closed loops (outer + inner) and open chains, as index references into
        # `geometry`. An open chain is a segment that is NOT part of any contour -- a bend line, a
        # centre line, or (in an ortho view) a silhouette fragment stopped by a T-junction.
        ov.update(contour.chain_view(ov, chain_eps))
        out_views.append(ov)

    # ---- view ALIGNMENT: shared paper-X = a vertical projection pair (same width axis),
    #      shared paper-Y = a horizontal pair. Convention-independent; only the SIGN needs
    #      config['projection']. -------------------------------------------------------
    align = []
    for i in range(len(out_views)):
        for j in range(i + 1, len(out_views)):
            a, b = out_views[i]["paper_box"], out_views[j]["paper_box"]
            if abs(a[0] - b[0]) < 0.5 and abs(a[2] - b[2]) < 0.5:
                align.append({"a": out_views[i]["vid"], "b": out_views[j]["vid"], "shares": "x"})
            elif abs(a[1] - b[1]) < 0.5 and abs(a[3] - b[3]) < 0.5:
                align.append({"a": out_views[i]["vid"], "b": out_views[j]["vid"], "shares": "y"})

    def which_view(px, py):
        for v in out_views:
            b = v["paper_box"]
            if b[0] - 15 <= px <= b[2] + 15 and b[1] - 15 <= py <= b[3] + 15:
                return v["vid"]
        return None

    # ---- dimensions: TRUE value + kind + owning view -----------------------------------
    DIMKIND = {0: "linear", 1: "aligned", 2: "angular", 3: "diameter", 4: "radius",
               5: "angular3p", 6: "ordinate"}
    # every arc/circle in PAPER coords, tagged with its view-local id -- the lookup table a
    # radius/diameter dimension is resolved against (see the RADIUS note below)
    round_prims = []
    for v in out_views:
        x0, y0 = v["paper_box"][0], v["paper_box"][1]
        for i, a in enumerate(v["geometry"]["arcs"]):
            round_prims.append((x0 + a["cx"] / sheet_scale, y0 + a["cy"] / sheet_scale,
                                a["r"] / sheet_scale, "%s:a%d" % (v["vid"], i)))
        for i, c in enumerate(v["geometry"]["circles"]):
            round_prims.append((x0 + c["cx"] / sheet_scale, y0 + c["cy"] / sheet_scale,
                                c["d"] / sheet_scale / 2.0, "%s:c%d" % (v["vid"], i)))
    dims = []
    for e in ents:
        if e.dxftype() != "DIMENSION":
            continue
        try:
            raw = float(e.get_measurement())
        except Exception:
            continue
        f = dimlfac.get(getattr(e.dxf, "dimstyle", ""), 1.0)
        pts = []
        for a in ("defpoint", "defpoint2", "defpoint3", "defpoint4", "defpoint5"):
            if e.dxf.hasattr(a):
                p = getattr(e.dxf, a)
                pts.append([_r(p.x, 2), _r(p.y, 2)])
        try:
            kind = DIMKIND.get(int(e.dxf.dimtype) & 7, "?")
        except Exception:
            kind = "?"
        value = _r(raw * f)
        # RADIUS/DIAMETER: do not trust the stored measurement. A DWG->DXF round trip loses the
        # arc-side defpoint, and the value then degrades to |centre - origin| (f-2: R4.5 read as
        # 232.36 = hypot(67.99, 222.19)). The dim's first defpoint IS the arc centre and the ARC
        # itself survives intact, so read the radius off the geometry -- which also links the
        # dimension to the primitive it measures.
        measures = None
        if kind in ("radius", "diameter") and pts:
            cx, cy = pts[0]
            best, bd = None, 1e9
            for (ax, ay, ar, aid) in round_prims:
                dd = math.hypot(ax - cx, ay - cy)
                # a RADIUS dim anchors on the arc CENTRE (dd ~ 0); a DIAMETER dim anchors on a
                # point ON the circle (dd ~ r). Score both and take whichever fits.
                score = min(dd, abs(dd - ar))
                if score < bd:
                    bd, best = score, (ar, aid)
            if best is not None and bd <= 0.5:
                value = _r(best[0] * sheet_scale * (2.0 if kind == "diameter" else 1.0))
                measures = best[1]
        d = {"value": value, "kind": kind, "defpts": pts}
        if measures:
            d["measures"] = measures
        if pts:
            d["view"] = which_view(pts[-1][0], pts[-1][1])
        txt = getattr(e.dxf, "text", "") or ""
        if txt not in ("<>", ""):
            d["text"] = txt          # e.g. '8x <>' (count prefix) or '%%c<>' (diameter)
        dims.append(d)

    # ---- notes: bend annotations, free notes, section labels ---------------------------
    bend_re = re.compile(cfg["sheet_metal"]["bend_note_pattern"], re.I)
    notes, bends = [], []
    for e in ents:
        if e.dxftype() not in ("MTEXT", "TEXT"):
            continue
        txt = (e.text if e.dxftype() == "MTEXT" else e.dxf.text) or ""
        t = re.sub(r"\s+", " ", txt.replace("\n", " ")).strip()
        p = e.dxf.insert
        if not t:
            continue
        # inside the title block / sheet margin -> metadata, not part information (the margins
        # carry the A-F row and 1-4 column labels of the sheet format on ALL four edges)
        in_frame = (p.y < fb[1] + 0.25 * sheet_h or p.y > fb[3] - 0.03 * sheet_h
                    or p.x < fb[0] + 0.03 * sheet_w or p.x > fb[2] - 0.03 * sheet_w)
        m = bend_re.match(t.replace("°", " ").replace("  ", " ").strip()) or bend_re.match(t)
        if m:
            bends.append({"dir": m.group(1).upper(), "angle_deg": float(m.group(2)),
                          "radius": float(m.group(3)), "rot": _r(getattr(e.dxf, "rotation", 0.0) or 0.0, 2),
                          "at": [_r(p.x, 2), _r(p.y, 2)], "view": which_view(p.x, p.y)})
        elif not in_frame:
            notes.append({"text": t, "at": [_r(p.x, 2), _r(p.y, 2)], "view": which_view(p.x, p.y)})

    pairing.pair_bend_notes(bends, out_views, sheet_scale, cfg)

    # A SECTION INDICATOR (the cut line's arrow tails + heads) clusters on its own and would
    # otherwise be emitted as a tiny bogus view -- it survives the DWG route as real line
    # geometry. Mark, do not delete: a cluster sitting on a CUT-LINE ENDPOINT with no dimension
    # pointing at it is furniture. (Deleting by size would take s-1's 2 mm thickness view with it.)
    cut_ends = []
    for e, _b in cut_lines:
        if e.dxftype() == "LINE":
            cut_ends += [(e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)]
    dimmed = {d.get("view") for d in dims}
    for v in out_views:
        b = v["paper_box"]
        touches_end = any(b[0] - 1 <= px <= b[2] + 1 and b[1] - 1 <= py <= b[3] + 1
                          for px, py in cut_ends)
        v["role"] = "annotation" if (touches_end and v["vid"] not in dimmed) else "view"

    with open(path, "rb") as fh:
        digest = hashlib.sha256(fh.read()).hexdigest()

    return {
        "analysis_version": ANALYSIS_VERSION,
        "source": {"file": os.path.basename(path), "sha256": digest,
                   "bytes": os.path.getsize(path)},
        "config": {"projection": cfg["projection"], "k_factor": cfg["sheet_metal"]["k_factor"]},
        "sheet": {
            "dxf_version": getattr(doc, "dxfversion", None),
            "units": doc.header.get("$INSUNITS", None),
            "scale_factor": sheet_scale,      # TRUE = paper x this  (DIMLFAC)
            "paper_box": [_r(fb[0], 2), _r(fb[1], 2), _r(fb[2], 2), _r(fb[3], 2)],
            "audit_errors": len(auditor.errors),
        },
        "views": out_views,
        "alignment": align,
        "dimensions": dims,
        "bend_notes": bends,
        "notes": notes,
        "user_description": None,   # filled in by hand when the author described the part
    }


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    path = argv[1]
    cfg = load_config()
    art = read(path, cfg)

    if "--desc-file" in argv:
        with open(argv[argv.index("--desc-file") + 1], "r", encoding="utf-8") as fh:
            art["user_description"] = fh.read().strip()

    text = json.dumps(art, indent=1, ensure_ascii=False)
    if "--save" in argv:
        out = os.path.join(os.path.dirname(os.path.abspath(path)),
                           "%s.analysis-v%s.json" % (os.path.splitext(os.path.basename(path))[0],
                                                     ANALYSIS_VERSION))
        with open(out, "w", encoding="utf-8") as fh:
            fh.write(text)
        v = art["views"]
        print("wrote %s  (%d views, %d dims, %d bend notes, %d notes, %.1f KB)"
              % (out, len(v), len(art["dimensions"]), len(art["bend_notes"]),
                 len(art["notes"]), len(text) / 1024.0))
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
