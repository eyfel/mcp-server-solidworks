"""lowering.py -- the direct-buildable GATE + flat-pattern -> Feature Graph IR transcription.

WHY A CONVERTER AND NOT A PROMPT: transcribing an 80-segment laser contour by hand costs tokens
twice (once to read it, once to echo it into submit_feature_graph) and invites transcription errors
in between. Contour geometry is pure data, so it is lowered here and NEVER enters the model's
context. The model gets a ~10-line summary, judges it, and authorises the build with one short call.

WHAT STAYS THE MODEL'S JOB: everything interpretive. This module only fires when EVERY decision is
forced by the drawing; when any one of them is not, `assess` returns direct=False with the reason
and the caller falls back to handing over the full analysis. Failing the gate costs nothing -- it is
exactly today's behaviour -- so the gate is deliberately conservative.

Scope v1: sheet-metal FLAT PATTERNS. A plain plate is excluded on purpose (its thickness and which
view carries the profile are interpretive), and so is any blank whose fixed point cannot be pinned.

The emitted graph goes through the SAME pycompiler as submit_feature_graph and rebuild_from_ir --
a third door, never a second compiler (IR-ADR-005).
"""
from __future__ import annotations

import math

try:
    from . import contour, curvefit
    from .vocab import GATE_REASONS
except ImportError:
    import contour
    import curvefit
    from vocab import GATE_REASONS

_MM = 1000.0                    # the `draw` dialect is TRUE mm; the IR is meters
_SCHEMA_VERSION = "0.7.1-draft"


def _fail(reason, detail):
    """Guard: a gate reason must exist in the published vocabulary, or the schema is lying about
    what the model can be told."""
    assert reason in GATE_REASONS, "gate reason %r is not in vocab.GATE_REASONS" % reason
    return {"direct": False, "reason": reason, "detail": detail}


# --------------------------------------------------------------------------- geometry helpers
def _seg_points(view, item):
    """Resolve one [code, index, dir] entry to its (start, end) in view-local mm."""
    code, idx, d = item
    g = view["geometry"]
    if code == "l":
        p = g["lines"][idx]
    elif code == "a":
        p = g["arcs"][idx]
    elif code == "e" and "t1" in g["ellipses"][idx]:
        p = g["ellipses"][idx]                    # an ellipse ARC chains like an arc
    else:
        return None                               # a circle / full ellipse has no endpoints
    a, b = (p["x1"], p["y1"]), (p["x2"], p["y2"])
    return (b, a) if d < 0 else (a, b)


def _loop_polygon(view, loop):
    """Chord polygon of a loop -- adequate for centroid/containment (never for area, which
    contour.py already computed exactly)."""
    if loop["seq"] and loop["seq"][0][0] == "c":
        c = view["geometry"]["circles"][loop["seq"][0][1]]
        r = c["d"] / 2.0
        return [(c["cx"] + r, c["cy"]), (c["cx"], c["cy"] + r),
                (c["cx"] - r, c["cy"]), (c["cx"], c["cy"] - r)]
    if loop["seq"] and loop["seq"][0][0] == "e" and _seg_points(view, loop["seq"][0]) is None:
        el = curvefit.record_ellipse(view["geometry"]["ellipses"][loop["seq"][0][1]])
        return [curvefit.point_at(el, t) for t in (0.0, 90.0, 180.0, 270.0)]
    return [_seg_points(view, it)[0] for it in loop["seq"] if _seg_points(view, it)]


def _polygon_centroid(poly):
    a = cx = cy = 0.0
    for i in range(len(poly)):
        x0, y0 = poly[i]
        x1, y1 = poly[(i + 1) % len(poly)]
        cross = x0 * y1 - x1 * y0
        a += cross
        cx += (x0 + x1) * cross
        cy += (y0 + y1) * cross
    if abs(a) < 1e-9:
        xs = [p[0] for p in poly]
        ys = [p[1] for p in poly]
        return (sum(xs) / len(xs), sum(ys) / len(ys))
    return (cx / (3.0 * a), cy / (3.0 * a))


def _point_to_segment(pt, a, b):
    px, py = pt
    dx, dy = b[0] - a[0], b[1] - a[1]
    L2 = dx * dx + dy * dy
    if L2 < 1e-12:
        return math.hypot(px - a[0], py - a[1])
    t = max(0.0, min(1.0, ((px - a[0]) * dx + (py - a[1]) * dy) / L2))
    return math.hypot(px - (a[0] + t * dx), py - (a[1] + t * dy))


# --------------------------------------------------------------------------- the gate
def assess(art, cfg):
    """Can this drawing be built with no interpretive step at all?

    -> {"direct": True, "summary": {...}} | {"direct": False, "reason": <GATE_REASONS>, "detail": str}
    """
    views = {v["vid"]: v for v in art.get("views", [])}
    bends = art.get("bend_notes") or []
    if not bends:
        return _fail("no_bend_notes", "no UP/DOWN flat-pattern annotation — v1 handles sheet-metal "
                                      "flat patterns only")
    vids = {b.get("view") for b in bends}
    if len(vids) != 1 or None in vids:
        return _fail("bend_notes_split", "bend notes span views %s — the flat pattern must be one view"
                     % sorted(str(x) for x in vids))
    vid = vids.pop()
    fv = views.get(vid)
    if fv is None:
        return _fail("bend_notes_split", "bend notes reference unknown view %r" % vid)

    # --- G2: the blank contour. This one is HARD -- it is the foundation everything else sits on.
    if any(p["c"] == "cut_line" for p in fv["geometry"]["lines"]):
        return _fail("cut_line_in_flat_view", "view %s carries a section cut line" % vid)
    # TIER A ONLY on the direct-build path. A Tier B boundary is DETERMINED, not guessed, so this
    # is not a doubt about its correctness -- it is about who checks it. Direct-build is the one
    # route that turns a drawing into a solid with no human reading it, so it stays on the
    # mechanism that has been exercised on every sample since ADR-064. Tier B reaches the model
    # through the analysis, where the recipe makes it read the `tier` flag and corroborate.
    # Revisit once Tier B has round-tripped a part (phase 2).
    outers = [lp for lp in fv["loops"]
              if lp["role"] == "outer" and lp["class"] == "visible" and lp.get("tier") != "B"]
    if not outers:
        return _fail("no_outer_loop", "view %s: the outline did not close into a single loop "
                                      "(%d loops, %d open chains)"
                     % (vid, len(fv["loops"]), len(fv["open_chains"])))
    if len(outers) > 1:
        return _fail("multiple_outer_loops", "view %s: %d candidate blanks" % (vid, len(outers)))
    outer = outers[0]
    cutouts = [lp for lp in fv["loops"] if lp["parent"] == outer["id"]]
    # Count Tier A only here too: a Tier B boundary this path deliberately ignored must not then
    # be reported back as an unaccounted-for STRAY loop.
    tier_a = [lp for lp in fv["loops"] if lp.get("tier") != "B"]
    if len(tier_a) != 1 + len(cutouts):
        return _fail("stray_loop", "view %s: %d loops, but only the blank + %d cutouts are accounted for"
                     % (vid, len(tier_a), len(cutouts)))

    # --- G3: bends. An unmatched note is SKIPPED and reported; none matching is systematic.
    paired = [b for b in bends if b.get("bend_line")]
    skipped = [b for b in bends if not b.get("bend_line")]
    if not paired:
        return _fail("no_bend_paired", "none of the %d bend notes could be matched to a line" % len(bends))

    # R15's ledger, mechanised: every real line in the flat view is either blank contour or a bend.
    # A SKIPPED bend's candidate lines still count as accounted for -- they are explained (a bend
    # line we could not attribute) and reported, just not built. Only a line nothing explains at
    # all drops the whole drawing to the full-analysis path.
    claimed = {tuple(b["bend_line"]["seg"]) for b in paired}
    for b in skipped:
        claimed |= {tuple(c) for c in (b.get("unpaired") or {}).get("candidates", [])}
    for oc in fv["open_chains"]:
        if oc["class"] not in ("visible", "hidden"):
            continue                              # centre lines are drafting furniture
        for code, idx, _d in oc["seq"]:
            if (code, idx) not in claimed:
                return _fail("unclaimed_open_chain",
                             "view %s: %s%d belongs to no contour and no bend note" % (vid, code, idx))

    # --- G4: thickness. Exactly one source, or the model decides.
    th = _resolve_thickness(art, fv, views, cfg)
    if th["state"] != "ok":
        return _fail(th["state"], th["detail"])

    # --- G5: a fixed point that every bend can fold around, on flat material, clear of everything.
    poly = _loop_polygon(fv, outer)
    fixed = _polygon_centroid(poly)
    clearance = max(1.0, 2.0 * th["value"])
    if not contour.point_in_polygon(fixed, poly):
        return _fail("fixed_point_ambiguous", "the blank's centroid falls outside its own outline")
    for lp in cutouts:
        if contour.point_in_polygon(fixed, _loop_polygon(fv, lp)):
            return _fail("fixed_point_ambiguous", "the blank's centroid falls inside cutout %s" % lp["id"])
    for b in paired:
        a, c = _seg_points(fv, list(b["bend_line"]["seg"]) + [1])
        if _point_to_segment(fixed, a, c) < clearance:
            return _fail("fixed_point_ambiguous",
                         "the blank's centroid is %.2f mm from a bend line (need %.2f)"
                         % (_point_to_segment(fixed, a, c), clearance))

    blank_area = outer["area"] - sum(lp["area"] for lp in cutouts)
    return {
        "direct": True,
        "reason": None,
        "summary": {
            "flat_view": vid,
            "blank": {"size": fv["size"], "outer_segments": len(outer["seq"]),
                      "area_mm2": round(blank_area, 4),
                      "cutouts": [{"loop": lp["id"], "area_mm2": lp["area"], "bbox": lp["bbox"]}
                                  for lp in cutouts]},
            "thickness": {"value_mm": th["value"], "source": th["source"]},
            "bends": [_bend_row(fv, b) for b in paired],
            "skipped_bends": [{"dir": b["dir"], "at": b["at"],
                               "reason": (b.get("unpaired") or {}).get("reason"),
                               "candidates": (b.get("unpaired") or {}).get("candidates", [])}
                              for b in skipped],
            "k_factor": cfg["sheet_metal"]["k_factor"],
            "expected": {"volume_m3": round(blank_area * th["value"] * 1e-9, 12)},
            "fixed_point_mm": [round(fixed[0], 4), round(fixed[1], 4)],
        },
    }


def _bend_row(fv, b):
    ln = fv["geometry"]["lines"][b["bend_line"]["seg"][1]]
    return {"dir": b["dir"], "angle_deg": b["angle_deg"], "radius_mm": b["radius"],
            "seg": b["bend_line"]["seg"], "class": b["bend_line"]["class"],
            "in_loop": b["bend_line"]["in_loop"],
            "line": [ln["x1"], ln["y1"], ln["x2"], ln["y2"]]}


def _resolve_thickness(art, flat_view, views, cfg):
    """Two sources, and they must not compete:
      (a) a THICKNESS VIEW -- an edge-on view of the sheet: its short side carries a dimension of
          that value, and its long side matches one of the flat pattern's own sides (s-1's
          2.0 x 118.5664 beside an 80 x 118.5664 blank).
      (b) a bare `N mm` note (s-2).
    Both samples are covered, one each -- and s-2's 30 x 60 bent-state view is correctly NOT
    mistaken for a thickness view because its long side matches nothing on the blank."""
    eps = cfg["tolerance"].get("chain_eps_mm", 0.01)
    found = []
    flat_sides = flat_view["size"]
    for vid, v in views.items():
        if vid == flat_view["vid"] or v.get("role") == "annotation":
            continue
        short, long_ = min(v["size"]), max(v["size"])
        if not any(abs(long_ - s) <= eps for s in flat_sides):
            continue
        if any(abs(d["value"] - short) <= eps and d.get("view") == vid
               for d in art.get("dimensions", [])):
            found.append((short, "thickness view %s (%g x %g)" % (vid, v["size"][0], v["size"][1])))
    import re as _re
    for n in art.get("notes", []):
        m = _re.match(r"^([0-9]+(?:\.[0-9]+)?)\s*mm$", n["text"], _re.I)
        if m:
            found.append((float(m.group(1)), "note %r" % n["text"]))
    if not found:
        return {"state": "thickness_unresolved",
                "detail": "no thickness view and no `N mm` note"}
    if len({round(f[0], 4) for f in found}) > 1:
        return {"state": "thickness_ambiguous",
                "detail": "competing thickness sources: " + "; ".join("%g from %s" % f for f in found)}
    return {"state": "ok", "value": found[0][0], "source": found[0][1]}


# --------------------------------------------------------------------------- draw -> IR
def lower_flat_pattern(art, cfg, assessment=None):
    """-> a Feature Graph IR dict: sketch + sheet_metal, then one {sketch, sketched_bend} pair per
    DIRECTION group. Call only when assess() said direct=True.

    Conventions, all fixed here so nothing downstream re-derives them:
      * The blank sketches on the FRONT datum, so 2D (u,v) maps to 3D (u,v,0) with no rotation and
        the fixed pick is trivially on the sheet plane -- which is a face for any non-symmetric
        thickening, whichever side the material went.
      * The sketch origin is the blank's bounding-box centre.
      * Groups fold OUTER-FIRST (recipe R11): sorted by the group's greatest distance from the
        fixed point, descending, tie-break DOWN before UP.
      * UP/DOWN -> the tool's `flip` comes from config (`bend_flip_map`), because which way
        SolidWorks folds depends on the sheet's intrinsic orientation, not on the drawing.
    """
    a = assessment or assess(art, cfg)
    if not a["direct"]:
        raise ValueError("not directly buildable: %s (%s)" % (a["reason"], a["detail"]))
    s = a["summary"]
    fv = {v["vid"]: v for v in art["views"]}[s["flat_view"]]
    outer = [lp for lp in fv["loops"]                     # Tier A only -- same rule as the gate
             if lp["role"] == "outer" and lp["class"] == "visible" and lp.get("tier") != "B"][0]
    cutouts = [lp for lp in fv["loops"] if lp["parent"] == outer["id"]]

    ox = (outer["bbox"][0] + outer["bbox"][2]) / 2.0
    oy = (outer["bbox"][1] + outer["bbox"][3]) / 2.0

    def to_m(x, y):
        return (round((x - ox) / _MM, 9), round((y - oy) / _MM, 9))

    profile = _loop_profile(fv, outer, to_m)
    for lp in cutouts:
        profile += _loop_profile(fv, lp, to_m)

    t_m = s["thickness"]["value_mm"] / _MM
    radii = {b["radius_mm"] for b in s["bends"]}
    nodes = [
        {"id": "n1", "type": "sketch", "ref": {"datum": "front"}, "profile": profile},
        {"id": "n2", "type": "sheet_metal", "sketch": "n1", "thickness": t_m,
         "bend_radius": min(radii) / _MM, "k_factor": cfg["sheet_metal"]["k_factor"]},
    ]

    fx, fy = to_m(*s["fixed_point_mm"])
    flip_map = cfg["sheet_metal"].get("bend_flip_map") or {}
    groups = {}
    for b in s["bends"]:
        groups.setdefault(b["dir"], []).append(b)

    def group_reach(item):
        direction, rows = item
        far = max(_point_to_segment(s["fixed_point_mm"], (r["line"][0], r["line"][1]),
                                    (r["line"][2], r["line"][3])) for r in rows)
        return (-far, 0 if direction == "DOWN" else 1)

    n = 2
    for direction, rows in sorted(groups.items(), key=group_reach):
        n += 1
        sk = "n%d" % n
        nodes.append({"id": sk, "type": "sketch", "ref": {"datum": "front"},
                      "profile": [dict(zip(("x1", "y1", "x2", "y2"),
                                           to_m(r["line"][0], r["line"][1]) + to_m(r["line"][2], r["line"][3])),
                                       kind="line")
                                  for r in rows]})
        n += 1
        node = {"id": "n%d" % n, "type": "sketched_bend", "sketch": sk,
                # 9 decimals, not 6: the compiler converts back to degrees at the tool boundary and
                # a coarser round lands on 89.999998 deg instead of 90.
                "angle": round(math.radians(rows[0]["angle_deg"]), 9),
                "radius": rows[0]["radius_mm"] / _MM,
                "fixed": {"near": [fx, fy, 0.0],
                          "hint": "blank centroid — the region every bend folds around"}}
        if flip_map.get(direction) is True:
            node["flip"] = True
        nodes.append(node)

    return {"_intent": "Sheet-metal flat pattern transcribed from %s (%d bends%s)."
                       % (art["source"]["file"], len(s["bends"]),
                          ", %d SKIPPED" % len(s["skipped_bends"]) if s["skipped_bends"] else ""),
            "schema_version": _SCHEMA_VERSION, "units": "meters", "nodes": nodes}


def _loop_profile(view, loop, to_m):
    """One loop -> IR profile primitives, traversed in the loop's own order. Endpoints are shared
    exactly between consecutive primitives, which is what closes the contour on rebuild (the
    frozen-coordinate discipline: no constraints, no dimensions)."""
    out = []
    g = view["geometry"]
    for code, idx, d in loop["seq"]:
        if code == "c":
            c = g["circles"][idx]
            cx, cy = to_m(c["cx"], c["cy"])
            out.append({"kind": "circle", "diameter": round(c["d"] / _MM, 9), "cx": cx, "cy": cy})
        elif code == "l":
            p = g["lines"][idx]
            a, b = ((p["x1"], p["y1"]), (p["x2"], p["y2"]))
            if d < 0:
                a, b = b, a
            x1, y1 = to_m(*a)
            x2, y2 = to_m(*b)
            out.append({"kind": "line", "x1": x1, "y1": y1, "x2": x2, "y2": y2})
        elif code == "e":
            p = g["ellipses"][idx]
            el = curvefit.record_ellipse(p)
            if "t1" not in p:
                # a FULL ellipse maps 1:1 onto the IR's `ellipse` (centre + a point on each axis)
                cx, cy = to_m(p["cx"], p["cy"])
                mx, my = to_m(*curvefit.point_at(el, 0.0))
                nx, ny = to_m(*curvefit.point_at(el, 90.0))
                out.append({"kind": "ellipse", "cx": cx, "cy": cy,
                            "x1": mx, "y1": my, "x2": nx, "y2": ny})
            else:
                # The IR has NO partial-ellipse primitive (KNOWN-LIMITATIONS): transcribe the arc
                # as a SPLINE through points sampled at the drawing's own resolution (its source
                # segment count) -- visually equivalent, never bit-exact, exactly the IR's own
                # spline caveat. Ends are the record's exact vertices, so the contour still closes.
                pts = curvefit.sample_arc(p)
                if d < 0:
                    pts.reverse()
                flat = []
                for x, y in pts:
                    flat += list(to_m(x, y))
                out.append({"kind": "spline", "points": flat})
        else:
            p = g["arcs"][idx]
            a, b = ((p["x1"], p["y1"]), (p["x2"], p["y2"]))
            sweep = p.get("dir", 1) * d
            if d < 0:
                a, b = b, a
            cx, cy = to_m(p["cx"], p["cy"])
            x1, y1 = to_m(*a)
            x2, y2 = to_m(*b)
            out.append({"kind": "arc", "cx": cx, "cy": cy, "x1": x1, "y1": y1,
                        "x2": x2, "y2": y2, "dir": 1 if sweep > 0 else -1})
    return out
