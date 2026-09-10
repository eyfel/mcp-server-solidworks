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
    (title block), never a view -- but only once it PROVES itself by containing every other
    cluster (viewgraph.frame_plausible; a borderless sheet has NO frame and f-3's front view is
    a view, not furniture). The frame is REPORTED (counts + its round primitives) rather than
    dropped, and a cluster lying wholly inside the title-block band that no dimension points at is
    tagged role="frame_item" -- the projection-method symbol and the weld symbol are not views.
  * A view's true extent   -- `geom_box` / `size` come from the VISIBLE silhouette with true arc
    extents, not from the cluster box (centre lines overhang; an arc clusters by its whole circle).
    This is also what the projection-pair test compares.
  * Bend notes             -- "UP 90 R 1" / "DOWN 90 R 1" with the bend line they annotate.
  * Free notes             -- e.g. a bare "2 mm" thickness note, and section labels.
  * Title-block text       -- `frame_notes`, in reading order. NOT part information on a bare CAD
    template, but on a real industrial title block it IS the parameter table (length, width,
    thickness, material, scale, projection standard, weight). Separated, never dropped.
  * Text inside note BLOCKS -- SolidWorks wraps some notes in INSERTs (SW_NOTE, BIEGETEILE ...)
    whose MTEXT a top-level walk never sees: s-6's "Abwicklung / developed view" (the flat-pattern
    label) and "Biegelinie / bending line", s-7's manufacturing notes were all being dropped.
    Read as ROWS (the DWG route splits a row into per-glyph fragments; they are re-joined).
  * Parametric ellipses    -- the exporter writes every non-circular curve as a fan of short LINEs
    (a tilted hole's ellipse = 48 segments; 49% of f-3's payload). `curvefit` collapses a fan to
    ONE ellipse record when the whole polyline lies within `fit_eps` of the fitted curve (the
    fitter proposes, the residual decides); the real DXF ELLIPSE entity is read as the same
    record. Anything the fit refuses stays raw.
  * What was NOT read      -- `sheet.not_read` counts every entity class the reader saw and did
    not emit (HATCH, SOLID, OLE2FRAME, geometry inside blocks: centre marks, hatch lines, symbols),
    so nothing vanishes silently.

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

def _ezdxf_recover():
    """ezdxf, imported LAZILY at first read.

    Deferred on purpose: everything in this module except `read()` itself is pure arithmetic over
    values already pulled out of the file, and the 4th gate must be able to import those helpers
    (e.g. `reconcile_angular`) on a machine with NO ezdxf -- CI runs exactly that way. Importing at
    module level would silently drop those checks from CI, which is the failure class this project
    least wants. NEVER sys.exit() here: this module is imported INTO a hosted MCP server, where
    exiting would kill the whole process."""
    try:
        from ezdxf import recover
    except ImportError as _exc:
        raise ImportError(
            "ezdxf is required for DXF reading - pip install ezdxf") from _exc
    return recover

try:                             # normal: imported as part of the `drawing` package
    from . import contour, curvefit, pairing, viewgraph
except ImportError:              # fallback: run directly as a script from this directory
    import contour
    import curvefit
    import pairing
    import viewgraph

ANALYSIS_VERSION = "0.7.0"   # 0.7.0: the WIRE form (payload phase 1). Everything the model or the
                             #        saved artifact carries goes through `wire.encode`, while the
                             #        pipeline keeps the record shape it always had -- one boundary,
                             #        no consumer touched. `lines` become {class: [[index, x1, y1,
                             #        x2, y2], ...]} (the index is written IN, so a `seq` reference
                             #        never has to be recomputed from group lengths), and a
                             #        single-segment open chain collapses to [position, code,
                             #        index] in `open_singles` -- but ONLY where id, class and
                             #        direction are provably recoverable, else it stays a full
                             #        record. Measured on the 10 samples: lines -49%, chains -65%.
                             # 0.6.0: TIER B, phase 1 -- where a view's VISIBLE graph has NO free
                             #        end the figure is a closed planar subdivision, so its outer
                             #        boundary is DETERMINED and is walked by planar face traversal
                             #        (ordering by TANGENT, not chord) and emitted as a loop tagged
                             #        `tier: "B"`. Runs only where Tier A returned no visible outer
                             #        loop, so it can never contradict Tier A; the direct-build
                             #        path ignores it by design. Reaches 7 of 28 sample views --
                             #        every one a view Tier A left empty, f-3's SECTION A-A among
                             #        them. Also `view='v0,v2'`: several views in one pull.
                             # 0.5.2: an ANGULAR dim is RECONCILED, not handed to `printed` --
                             #        the DWG route corrupts EITHER candidate (usually the stored
                             #        measurement as 180+theta, but f-3's rendered text as
                             #        360-theta = 250 for a real 110). In all 7 angular dims of the
                             #        sample set exactly one candidate is <= 180 and it is the true
                             #        one every time; a drawing does not dimension a reflex angle.
                             #        `reconcile_angular` + a 7-case gate-4 golden. ezdxf's import
                             #        went LAZY so that golden can run in CI.
                             # 0.5.1: the printed string keeps its SYMBOL -- AutoCAD %% control
                             #        codes decoded (%%c->diameter, %%d->degree, %%p->+/-) instead
                             #        of leaking raw into the payload; and a LINEAR dim whose
                             #        override opens '%%c' is emitted as `diameter` + kind_from
                             #        ='printed' (4 of 10 samples hand-fake a hole this way, so a
                             #        reverse read was putting a length where a hole belonged).
                             #        Bumped although the change is small: 0.3.0 silently named TWO
                             #        readers (s-6/s-5 predate printed-arbitration, s-3/s-7 carry
                             #        it), which is exactly how a stale artifact reads as a
                             #        regression. Output changes => the version moves.
                             # 0.5.0: PARAMETRIC ellipses -- exploded curve fans collapsed by a
                             #        residual-gated fit (curvefit.py) and the real ELLIPSE entity
                             #        read (f-3: 39.3 -> 20.2 KB); text inside note BLOCKS read as
                             #        rows; sheet.not_read accounting; 3-decimal coordinates (1 um)
                             # 0.4.0: the frame must PROVE itself (contain every other cluster) or
                             #        the sheet has none -- f-3's borderless sheet had its front
                             #        view eaten whole; alignment GRADED (span > mid > label,
                             #        s-7's bevelled stack + cross-scale sections) and the solved
                             #        view-axis graph emitted (view_graph)
                             # 0.3.0: title-block text EMITTED (frame_notes) instead of dropped;
                             #        geom_box + true arc extents (size / alignment no longer
                             #        inflated); title-block furniture tagged role="frame_item";
                             #        the frame cluster itself reported instead of vanishing
                             # 0.2.0: contour chaining (loops + open_chains), emit-time duplicate
                             #        dedup, explicit arc endpoints, bend pairing by class + side
                             # 0.1.1: radius/diameter read off the GEOMETRY (+measures);
                             #        section-arrow clusters tagged role="annotation"
_HERE = os.path.dirname(os.path.abspath(__file__))

# Closed vocabularies (EDGE_CLASSES / VIEW_ROLES / BEND_DIRECTIONS) live in vocab.py.


def load_config():
    with open(os.path.join(_HERE, "config.json"), "r", encoding="utf-8") as fh:
        return json.load(fh)


# --------------------------------------------------------------------------- helpers
def _r(v, nd=3):
    """3 decimals = 1 um in TRUE mm, the house convention (a 4th decimal bought nothing and cost
    3% of every payload)."""
    return round(float(v), nd)


# Entity classes the reader consumes. Everything else it sees is COUNTED in sheet.not_read.
_READ_TYPES = frozenset(("LINE", "ARC", "CIRCLE", "ELLIPSE", "DIMENSION", "MTEXT", "TEXT",
                         "INSERT", "VIEWPORT"))


def _text_of(e):
    return (e.text if e.dxftype() == "MTEXT" else e.dxf.text) or ""


def _walk_block(ins):
    """Every entity inside an INSERT, nested blocks included, in WCS (ezdxf applies the insert
    transform). A block the file does not define yields nothing."""
    try:
        for ve in ins.virtual_entities():
            if ve.dxftype() == "INSERT":
                yield from _walk_block(ve)
            else:
                yield ve
    except Exception:
        return


def _block_rows(ins):
    """Text inside a note block -> [(row_text, x, y)] in reading order.

    A SolidWorks note block holds one MTEXT per line -- or, through the DWG route, one MTEXT per
    GLYPH RUN (a Turkish 'AKSİ BELİRTİLMEDİĞİ' arrives as 'AKS', 'İ', 'BEL', 'İ', 'RT' ...). The
    fragments of one row share a baseline; they are re-joined left to right, with a space only
    where the gap after the previous fragment's estimated extent says there was one."""
    frags = []
    for ve in _walk_block(ins):
        if ve.dxftype() in ("MTEXT", "TEXT"):
            t = re.sub(r"\s+", " ", _text_of(ve).replace("\n", " ")).strip()
            if t:
                h = (ve.dxf.char_height if ve.dxftype() == "MTEXT" else ve.dxf.height) or 1.0
                frags.append((t, ve.dxf.insert.x, ve.dxf.insert.y, h))
    frags.sort(key=lambda f: (-f[2], f[1]))
    rows = []
    for f in frags:
        if rows and abs(rows[-1][0][2] - f[2]) <= 0.5 * f[3]:
            rows[-1].append(f)
        else:
            rows.append([f])
    out = []
    for row in rows:
        row.sort(key=lambda f: f[1])
        text, end = row[0][0], row[0][1] + 0.75 * row[0][3] * len(row[0][0])
        for t, x, _y, h in row[1:]:
            text += (" " if x - end > 0.35 * h else "") + t
            end = x + 0.75 * h * len(t)
        out.append((text, row[0][1], row[0][2]))
    return out


def _block_has_geometry(ins):
    return any(ve.dxftype() not in ("MTEXT", "TEXT", "ATTRIB", "ATTDEF") for ve in _walk_block(ins))


def ellipse_params(e):
    """A DXF ELLIPSE -> (center, rx, ry, rot_deg, p1, p2, ccw) in PAPER mm; p1/p2 None = full.
    Read through ezdxf's construction tool so a flipped extrusion cannot mirror the arc: the
    endpoints and a mid-arc sample are taken in WCS, and the sweep sense is measured from them."""
    ct = e.construction_tool()
    maj = ct.major_axis
    rx = math.hypot(maj.x, maj.y)
    ry = rx * float(ct.ratio)
    rot = math.degrees(math.atan2(maj.y, maj.x)) % 180.0
    c = (float(ct.center.x), float(ct.center.y))
    s, en = float(ct.start_param), float(ct.end_param)
    span = (en - s) % (2.0 * math.pi)
    if span < 1e-9:
        return c, rx, ry, rot, None, None, True
    p1, p2, pm = [(float(v.x), float(v.y)) for v in ct.vertices([s, en, s + span / 2.0])]
    el = (c[0], c[1], rx, ry, rot)
    t1, t2, tm = (curvefit.param_of(p, el) for p in (p1, p2, pm))
    two_pi = 2.0 * math.pi
    ccw = ((tm - t1) % two_pi) <= ((t2 - t1) % two_pi)
    return c, rx, ry, rot, p1, p2, ccw


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
    if t == "ELLIPSE":
        return _ellipse_extent(e)
    return None


def _ellipse_extent(e):
    """True extent of an ELLIPSE (arc) by sampling its construction tool -- exact enough for
    clustering and for the visible-geometry box (64 samples: < 0.01 mm at any drawn size)."""
    try:
        ct = e.construction_tool()
        pts = [(float(v.x), float(v.y)) for v in ct.vertices(ct.params(64))]
    except Exception:
        return None
    if not pts:
        return None
    return (min(p[0] for p in pts), min(p[1] for p in pts),
            max(p[0] for p in pts), max(p[1] for p in pts))


# A drawing-scale ratio inside a view label ("A-A 1 : 1", "DETAIL B 2:1", "M 1:2"). Bare integers
# on either side only -- a bare "3:4" in prose would also match, which is why the caller additionally
# requires the text to sit beside a view and outside the title block.
_SCALE_RE = re.compile(r"(?<![\d.,])(\d+(?:[.,]\d+)?)\s*:\s*(\d+(?:[.,]\d+)?)(?![\d.,])")

_NUM_RE = re.compile(r"[-+]?\d+(?:[.,]\d+)?")


def printed_text(doc, e):
    """What the DRAWING PRINTS for this dimension.

    A DXF DIMENSION carries a reference to an anonymous BLOCK holding its drawn representation --
    extension lines, arrowheads, and the MTEXT with the value as the CAD system formatted it. That
    text is ground truth, and it is the only reliable source for two whole classes of value that
    the DWG->DXF route corrupts: every ANGULAR measurement comes back as 180+theta (s-7's 7.9 deg
    bend reads 187.8765), and a RADIUS loses its arc side. It also cross-checks every linear value
    against DIMLFAC and the per-view scale for free."""
    blk = e.dxf.geometry if e.dxf.hasattr("geometry") else None
    if not blk or blk not in doc.blocks:
        return None
    out = [(b.text if b.dxftype() == "MTEXT" else b.dxf.text)
           for b in doc.blocks[blk] if b.dxftype() in ("MTEXT", "TEXT")]
    return _decode_cad_escapes(" ".join(t for t in out if t).strip()) or None


# AutoCAD's %% control codes. They must be DECODED, not stripped: the symbol is evidence.
# s-6 prints two dimensions as '%%c<>' -- DXF dimtype base 0, i.e. plain LINEAR entities whose
# author typed a diameter prefix by hand. The entity type says "linear"; the drawing says
# "diameter 60". R18 -- what the drawing PRINTS arbitrates -- so the symbol has to survive into
# `printed`, where the reader of the payload can see it. The reader does NOT reclassify the
# dimension on that evidence: two instances in one sample is not a rule.
_CAD_ESCAPES = (("%%c", "Ø"), ("%%C", "Ø"),   # diameter
                ("%%d", "°"), ("%%D", "°"),   # degree
                ("%%p", "±"), ("%%P", "±"),   # plus/minus
                ("%%%", "%"))


def _decode_cad_escapes(txt):
    if not txt:
        return txt
    for code, glyph in _CAD_ESCAPES:
        txt = txt.replace(code, glyph)
    return txt


def printed_value(txt):
    """The number inside a printed dimension string, or None. Strips MTEXT formatting runs and the
    CAD symbols -- both as raw escapes (%%c diameter, %%d degree) and as the glyphs
    `_decode_cad_escapes` turns them into -- and accepts a comma decimal separator."""
    if not txt:
        return None
    t = re.sub(r"\\[A-Za-z][^;]*;", " ", txt)
    for junk in ("%%c", "%%C", "%%d", "%%D", "%%p", "%%P",
                 "Ø", "°", "±", "{", "}"):
        t = t.replace(junk, " ")
    m = _NUM_RE.search(t)
    return float(m.group(0).replace(",", ".")) if m else None


def reconcile_angular(computed, printed):
    """Which of an ANGULAR dim's two candidates is the real angle. Returns (value, mismatch).

    The DWG->DXF route loses an angular dimension's SIDE, and the damage lands on EITHER
    candidate: usually the stored measurement comes back as 180+theta (s-7: a 7.9 deg bend reads
    187.8765, a 45 deg weld bevel reads 225), but on f-3 it is the RENDERED BLOCK TEXT that is
    wrong -- it prints 250 where the measurement, the ray geometry AND the view's own ellipse
    foreshortening all say 110 (250 = 360 - 110, the explement).

    Measured over every angular dimension in the sample set (7): 2 agree, 4 have a corrupt
    measurement, 1 has corrupt printed text -- and in ALL SEVEN exactly one candidate is <= 180,
    which is the true one every time. A mechanical drawing does not dimension a reflex angle. So
    on disagreement take the candidate <= 180; if BOTH exceed it there is no evidence to choose on,
    so keep the printed value (R18's default) and let the caller flag `printed_mismatch`."""
    if printed is None:
        return computed, False
    if computed is None:
        return printed, False
    mismatch = abs(computed - printed) > max(0.01, 0.01 * abs(printed))
    if mismatch and (computed <= 180.0) != (printed <= 180.0):
        return (computed if computed <= 180.0 else printed), True
    return printed, mismatch


_CARDINALS = (0.0, 90.0, 180.0, 270.0)


def arc_bbox(cx, cy, r, a1, a2):
    """The TRUE bbox of an arc, not of its full circle.

    bbox() above deliberately returns the CIRCLE box: for CLUSTERING an over-estimate is safe. For
    MEASURING a view it is not — s-3's break-line arcs (r = 26.88 mm drawn across a 35 mm wide part)
    pushed its front view's reported size from 35 x 125 to 74 x 129 and, worse, moved its box far
    enough that the true projection pair with the side view failed the shared-span test."""
    a1, a2 = a1 % 360.0, a2 % 360.0
    sweep = (a2 - a1) % 360.0 or 360.0
    xs = [cx + r * math.cos(math.radians(a)) for a in (a1, a2)]
    ys = [cy + r * math.sin(math.radians(a)) for a in (a1, a2)]
    for ang in _CARDINALS:                       # a quadrant point is an extremum only if swept
        if ((ang - a1) % 360.0) <= sweep:
            xs.append(cx + r * math.cos(math.radians(ang)))
            ys.append(cy + r * math.sin(math.radians(ang)))
    return (min(xs), min(ys), max(xs), max(ys))


def geom_bbox(doc, items):
    """Bbox over REAL part geometry only — the box a view's SIZE and its projection-pair test must
    use, as opposed to the cluster box, which is an over-estimate by construction.

    Taken from the VISIBLE class alone, because the visible silhouette bounds an orthographic view
    by definition: hidden geometry is obscured and therefore inside it, centre lines overhang by
    drafting convention, cut lines overhang too — and BREAK-VIEW furniture, drawn hidden on s-3,
    overhangs the silhouette on both sides. Arcs contribute their true extent (arc_bbox). Falls
    back through hidden and then the raw cluster for a cluster with no visible primitive.

    KNOWN GAP (measured, deliberately not patched): on a borderless sheet the section-arrow
    apparatus (a lw-35 stroke + a lw=-1 arrowhead per end; real edges are lw 25) clusters INTO the
    view that carries the cut line and inflates its measured size (f-3's 100x40 front view reads
    124x49.6). Excluding it by cut-end proximity is UNSOUND — the cut line may run exactly along a
    real edge (f-3 cuts at its own step height), and any transitive flood from the ends eats the
    silhouette (f-2's front view measured 17x9 that way). The apparatus is symmetric about the cut
    line, so the MIDPOINT alignment grade survives the inflation; a future exclusion must key on
    the lineweight evidence, not on geometry."""
    for keep in (("visible",), ("visible", "hidden"), None):
        boxes = []
        for e, b in items:
            if keep is not None and edge_class(doc, e) not in keep:
                continue
            if e.dxftype() == "ARC":
                c = e.dxf.center
                boxes.append(arc_bbox(c.x, c.y, e.dxf.radius, e.dxf.start_angle, e.dxf.end_angle))
            else:
                boxes.append(b)
        if boxes:
            return (min(b[0] for b in boxes), min(b[1] for b in boxes),
                    max(b[2] for b in boxes), max(b[3] for b in boxes))
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
    doc, auditor = _ezdxf_recover().readfile(path)
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

    # ---- what is NOT read, counted so it cannot vanish silently: HATCH/SOLID (fills), OLE2FRAME
    #      (an embedded logo), and GEOMETRY inside blocks (centre-mark crosses, hatch-as-lines,
    #      a rolling-direction symbol). Block TEXT is read (see `texts` below); the VIEWPORT is
    #      the paper-space window, not drawing content.
    not_read, block_geom = Counter(), Counter()
    for e in ents:
        t = e.dxftype()
        if t == "INSERT":
            name = e.dxf.name or ""
            if _block_has_geometry(e):
                block_geom[re.sub(r"[_\d]+$", "", name) or name] += 1
        elif t not in _READ_TYPES:
            not_read[t] += 1

    # ---- every piece of TEXT on the sheet: top-level MTEXT/TEXT plus each ROW inside a note
    #      block (rotation = the block's). One list, so the scale-label pass and the notes pass
    #      cannot disagree about what text exists.
    texts = []
    for e in ents:
        t = e.dxftype()
        if t in ("MTEXT", "TEXT"):
            texts.append((_text_of(e), e.dxf.insert.x, e.dxf.insert.y,
                          getattr(e.dxf, "rotation", 0.0) or 0.0))
        elif t == "INSERT":
            rot = getattr(e.dxf, "rotation", 0.0) or 0.0
            for row, x, y in _block_rows(e):
                texts.append((row, x, y, rot))

    # A section CUT LINE overhangs the view it is drawn in, so it must not enlarge that view's
    # measured size (f-2's front view read 85.15 instead of 70 before this). It stays in the
    # geometry -- it is the evidence naming WHERE the section was taken -- just not in the bbox.
    geom = [(e, bbox(e)) for e in ents if bbox(e) and edge_class(doc, e) != "cut_line"]
    cut_lines = [(e, bbox(e)) for e in ents if bbox(e) and edge_class(doc, e) == "cut_line"]
    clusters = cluster(geom, gap) if geom else []

    def cbox(cl):
        xs = [b[0] for _, b in cl] + [b[2] for _, b in cl]
        ys = [b[1] for _, b in cl] + [b[3] for _, b in cl]
        return (min(xs), min(ys), max(xs), max(ys))

    # The FRAME candidate is the biggest cluster -- but it must PROVE itself by containing every
    # other cluster (a border encloses the drawing by construction). f-3 has no border at all, and
    # the unproven heuristic ate its FRONT VIEW whole: 152 lines + 8 arcs became "frame", seven
    # dimensions went view-less and the projection pairs vanished. No proof -> the sheet has NO
    # frame, and the candidate stays a view like any other.
    frame = None
    if clusters:
        cand = max(clusters, key=lambda c: (cbox(c)[2] - cbox(c)[0]) * (cbox(c)[3] - cbox(c)[1]))
        if viewgraph.frame_plausible(cbox(cand), [cbox(c) for c in clusters if c is not cand], gap):
            frame = cand
    # sheet extent = the frame's bbox; with no frame, the bbox of everything on the sheet
    fb = cbox(frame) if frame else (
        cbox([it for cl in clusters for it in cl]) if clusters else (0, 0, 0, 0))
    sheet_w, sheet_h = fb[2] - fb[0], fb[3] - fb[1]

    # ---- the TITLE BLOCK band. Its rows are ruled by horizontal lines spanning (nearly) the whole
    #      border width; the topmost such rule in the sheet's LOWER half is the block's top edge.
    #      This is what tells a small cluster down there apart from a real view: a projection-method
    #      symbol, a weld symbol and a boxed check dimension all clustered on their own on s-3/s-4
    #      and were emitted as views v2/v3/v4, because R3's "the sheet-spanning cluster is the
    #      frame" only catches furniture that TOUCHES the border.
    #      The block is anchored to the frame's inner RIGHT edge and does NOT span the sheet: on A4
    #      portrait its rules run the full inner width (18..205), on A3 landscape the same 187 mm
    #      block sits at 228..415 — so a "spans the sheet" test finds nothing there, and a
    #      y-only test would swallow s-6's thickness view, which sits BELOW the block's top edge but
    #      far to its left. Hence: right-anchored, long, and the band is a CORNER, not a strip.
    title_block_top = title_block_left = None
    rules = [(e.dxf.start.y, min(e.dxf.start.x, e.dxf.end.x), max(e.dxf.start.x, e.dxf.end.x))
             for e, _b in (frame or [])
             if e.dxftype() == "LINE" and abs(e.dxf.start.y - e.dxf.end.y) <= 0.5
             and e.dxf.start.y < (fb[1] + fb[3]) / 2.0]
    if rules:
        # The INNER border first: the longest horizontal rule that is not the sheet edge itself.
        inner = max((r for r in rules if (r[2] - r[1]) < 0.99 * sheet_w),
                    key=lambda r: r[2] - r[1], default=None)
        if inner:
            # The block's ROW GRID: rules that end at the inner right edge AND share one left x.
            # Grouping by left x is what makes this sheet-size independent — the block is a fixed
            # 187 mm on A4, A3 and A1 alike, so any "fraction of the sheet" threshold is wrong
            # (it found the block on A4, missed it on A1). The biggest such column IS the block.
            anchored = [r for r in rules if abs(r[2] - inner[2]) <= 0.5]
            cols = Counter(round(r[1], 1) for r in anchored)
            left, n = max(cols.items(), key=lambda kv: (kv[1], kv[0])) if cols else (None, 0)
            if n >= 2:
                grp = [r for r in anchored if abs(r[1] - left) <= 0.5]
                title_block_left, title_block_top = min(r[1] for r in grp), max(r[0] for r in grp)

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


    # ---- PER-VIEW SCALE. A view may be drawn at its OWN scale — s-7 is a 1:10 sheet carrying a
    #      section labelled "A-A 1 : 1", so the sheet DIMLFAC is wrong for that view's geometry AND
    #      its dimensions by a factor of ten (a 9.5 mm weld-prep leg read as 95). The DIMSTYLEs
    #      cannot tell them apart (all four of s-7's carry dimlfac 10), so the only deterministic
    #      source is the view's own LABEL. A label sits outside the view, overlapping its x span;
    #      it belongs to the nearest such view. Title-block text is excluded by construction —
    #      otherwise the block's own "Maßstab 1:10" would be read as a view label.
    for v in views:
        v["scale"] = sheet_scale
    for raw, px, py, _rot in texts:
        m = _SCALE_RE.search(raw.replace("\n", " "))
        if not m:
            continue
        if (title_block_top is not None
                and py <= title_block_top and px >= title_block_left):
            continue
        a, b = (float(m.group(i).replace(",", ".")) for i in (1, 2))
        if not (a > 0 and b > 0):
            continue
        best, bd = None, 0.1 * sheet_h
        for v in views:
            bx = v["box"]
            if not bx[0] <= px <= bx[2]:
                continue
            dist = bx[1] - py if py < bx[1] else py - bx[3] if py > bx[3] else 0.0
            if dist < bd:
                best, bd = v, dist
        if best is not None:
            best["scale"] = b / a

    chain_eps = cfg["tolerance"].get("chain_eps_mm", 0.01)
    out_views = []
    for vi, v in enumerate(views):
        x0, y0, x1, y1 = v["box"]
        vscale = v["scale"]                      # this VIEW's scale, not the sheet's (see above)
        prims = {"lines": [], "arcs": [], "circles": [], "ellipses": []}
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
                p = {"x1": _r((s.x - x0) * vscale), "y1": _r((s.y - y0) * vscale),
                     "x2": _r((en.x - x0) * vscale), "y2": _r((en.y - y0) * vscale),
                     "c": k}
                sig = ("l", k) + tuple(sorted([(p["x1"], p["y1"]), (p["x2"], p["y2"])]))
                bucket = "lines"
            elif t == "ARC":
                c, rad = e.dxf.center, _r(e.dxf.radius * vscale)
                cx, cy = _r((c.x - x0) * vscale), _r((c.y - y0) * vscale)
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
                p = {"cx": _r((c.x - x0) * vscale), "cy": _r((c.y - y0) * vscale),
                     "d": _r(2 * e.dxf.radius * vscale), "c": k}
                sig = ("c", k, p["cx"], p["cy"], p["d"])
                bucket = "circles"
            elif t == "ELLIPSE":
                # The one curve the exporter keeps PARAMETRIC. Same record the fitter emits for
                # a collapsed fan, minus n/fit -- their absence is the provenance.
                c, rx, ry, rot, p1, p2, ccw = ellipse_params(e)
                el = ((c[0] - x0) * vscale, (c[1] - y0) * vscale, rx * vscale, ry * vscale, rot)
                if p1 is not None:
                    p1 = ((p1[0] - x0) * vscale, (p1[1] - y0) * vscale)
                    p2 = ((p2[0] - x0) * vscale, (p2[1] - y0) * vscale)
                p = curvefit.make_record(el, k, p1, p2, ccw)
                sig = ("e", k, p["cx"], p["cy"], p["rx"], p["ry"], p["rot"], p.get("t1"), p.get("t2"))
                bucket = "ellipses"
            else:
                continue
            if sig in seen:
                dropped += 1
                continue
            seen.add(sig)
            prims[bucket].append(p)

        # EXPLODED CURVE FANS -> parametric ellipses. Runs of short chords are fitted, and a run is
        # replaced only when the whole polyline lies within fit_eps (PAPER mm x this view's scale)
        # of the fitted curve -- the fitter proposes, the residual decides. Refused runs stay raw.
        # Done BEFORE chaining so loops reference the collapsed arrays the model receives.
        cf = cfg.get("curve_fit") or curvefit.DEFAULTS
        prims["lines"], fitted = curvefit.collapse_fans(
            prims["lines"], prims["arcs"], prims["ellipses"],
            fit_eps=cf["fit_eps_paper_mm"] * vscale, chain_eps=chain_eps,
            min_segments=cf["min_segments"], max_turn_deg=cf["max_turn_deg"],
            min_sweep_deg=cf.get("min_sweep_deg", curvefit.DEFAULTS["min_sweep_deg"]))
        prims["ellipses"] += fitted
        if not prims["ellipses"]:
            del prims["ellipses"]                 # omitted when empty: most views have none

        # paper_box is the CLUSTER box and stays the local-coordinate origin. It over-reports the
        # view (overhanging centre lines, an arc's circle box), so the view's SIZE and its
        # projection-pair test use the geometry box instead.
        gb = geom_bbox(doc, v["ents"]) or (x0, y0, x1, y1)
        ov = {
            "vid": "v%d" % vi,
            "paper_box": [_r(x0, 2), _r(y0, 2), _r(x1, 2), _r(y1, 2)],
            "geom_box": [_r(gb[0], 2), _r(gb[1], 2), _r(gb[2], 2), _r(gb[3], 2)],
            "size": [_r((gb[2] - gb[0]) * vscale), _r((gb[3] - gb[1]) * vscale)],
            "geometry": prims,
        }
        if vscale != sheet_scale:
            # Say so loudly: this view is NOT at the sheet scale, and the label it was read from
            # is the only evidence for that.
            ov["scale_factor"] = vscale
        if dropped:
            ov["dropped_duplicates"] = dropped
        # CONTOUR CHAINING: closed loops (outer + inner) and open chains, as index references into
        # `geometry`. An open chain is a segment that is NOT part of any contour -- a bend line, a
        # centre line, or (in an ortho view) a silhouette fragment stopped by a T-junction.
        ov.update(contour.chain_view(ov, chain_eps))
        out_views.append(ov)

    # view ALIGNMENT is computed AFTER the roles are known (it must not pair title-block furniture)
    # -- see the align block near the end of this function.

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
    view_scale = {}
    for v in out_views:
        x0, y0 = v["paper_box"][0], v["paper_box"][1]
        vs = v.get("scale_factor", sheet_scale)     # un-scale with the SAME factor that scaled it
        view_scale[v["vid"]] = vs
        for i, a in enumerate(v["geometry"]["arcs"]):
            round_prims.append((x0 + a["cx"] / vs, y0 + a["cy"] / vs,
                                a["r"] / vs, "%s:a%d" % (v["vid"], i)))
        for i, c in enumerate(v["geometry"]["circles"]):
            round_prims.append((x0 + c["cx"] / vs, y0 + c["cy"] / vs,
                                c["d"] / vs / 2.0, "%s:c%d" % (v["vid"], i)))
    dims = []
    for e in ents:
        if e.dxftype() != "DIMENSION":
            continue
        try:
            raw = float(e.get_measurement())
        except Exception:
            continue
        pts = []
        for a in ("defpoint", "defpoint2", "defpoint3", "defpoint4", "defpoint5"):
            if e.dxf.hasattr(a):
                p = getattr(e.dxf, a)
                pts.append([_r(p.x, 2), _r(p.y, 2)])
        try:
            kind = DIMKIND.get(int(e.dxf.dimtype) & 7, "?")
        except Exception:
            kind = "?"
        # A HAND-FAKED DIAMETER. 4 of the 10 sample drawings dimension a hole with a plain LINEAR
        # dimension across the circle and type the diameter symbol in front of it by hand, as the
        # override '%%c<>'. The DXF entity then says "linear" while the drawing says "diameter 60",
        # and a reverse read that believes the entity puts a 60 mm LENGTH where a 60 mm HOLE
        # belongs. R18 -- what the drawing PRINTS arbitrates -- so the printed symbol decides the
        # kind. The signature is mechanical (dimtype base 0 + an override opening with %%c), not a
        # guess, and it is VALUE-NEUTRAL: the measured length across a circle already IS the
        # diameter, so only `kind` moves. The value therefore keeps the LINEAR path below --
        # linear scaling, and the linear printed-arbitration branch that prefers the computed
        # value's extra decimals -- and is relabelled only where the dimension is emitted.
        override = getattr(e.dxf, "text", "") or ""
        faked_diameter = kind == "linear" and override.lstrip().lower().startswith("%%c")
        # The OWNING VIEW's scale wins over the dimstyle's DIMLFAC: s-7 puts every dimension,
        # including the 1:1 section's, on a dimstyle carrying the sheet's factor of 10.
        owner = which_view(pts[-1][0], pts[-1][1]) if pts else None
        f = view_scale.get(owner) or dimlfac.get(getattr(e.dxf, "dimstyle", ""), 1.0)
        # DIMLFAC is a LENGTH factor. An ANGULAR dimension is dimensionless and must never be
        # scaled by it — s-7 is a 1:10 sheet and reported 2250 for a 225 deg angle.
        value = _r(raw if kind in ("angular", "angular3p") else raw * f)
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
                vs = view_scale.get(best[1].split(":")[0], sheet_scale)
                value = _r(best[0] * vs * (2.0 if kind == "diameter" else 1.0))
                measures = best[1]
        # The PRINTED text ARBITRATES. For a radius/diameter dim it simply wins -- both are
        # unreliable through the DWG route, and the printed string is what the drafter signed off
        # on. An ANGULAR dim is the exception: EITHER candidate can be the corrupted one, so it
        # goes through `reconcile_angular` (see there for the measurement that settled the rule).
        # For a linear dim the computed value stays (it carries more decimals than the printed
        # rounding), but a disagreement beyond rounding is REPORTED, never swallowed.
        printed = printed_text(doc, e)
        pv = printed_value(printed)
        mismatch = False
        if pv is not None:
            if kind in ("angular", "angular3p"):
                value, mismatch = reconcile_angular(value, pv)
                value = _r(value)
            elif kind in ("radius", "diameter"):
                mismatch = value is not None and abs(value - pv) > max(0.01, 0.01 * abs(pv))
                value = _r(pv)
            elif value is not None:
                mismatch = abs(value - pv) > max(0.05, 0.01 * abs(pv))
        d = {"value": value, "kind": "diameter" if faked_diameter else kind, "defpts": pts}
        if faked_diameter:
            d["kind_from"] = "printed"     # the ENTITY said linear; the printed symbol overruled it
        if printed:
            d["printed"] = printed
        if mismatch:
            d["printed_mismatch"] = True
        if measures:
            d["measures"] = measures
        if owner:
            d["view"] = owner
        if override not in ("<>", ""):
            d["text"] = override     # RAW code-1, undecoded: '8x <>' (count prefix), '%%c<>'
        dims.append(d)

    # ---- notes: bend annotations, free notes, section labels ---------------------------
    bend_re = re.compile(cfg["sheet_metal"]["bend_note_pattern"], re.I)
    notes, frame_notes, bends = [], [], []

    class _P:                                     # the insert point, top-level or block row
        __slots__ = ("x", "y")

        def __init__(self, x, y):
            self.x, self.y = x, y

    for txt, px, py, rot in texts:
        t = re.sub(r"\s+", " ", txt.replace("\n", " ")).strip()
        p = _P(px, py)
        if not t:
            continue
        # Inside the title block / sheet margin. This used to be a DELETE, and it was wrong: on a
        # bare SolidWorks template the title block really is furniture, but on a real industrial one
        # it IS the parameter table. s-3 lost 104 of its 134 texts that way -- among them the part's
        # LENGTH (`l=338`, the only source, since the drawing is a break view), the projection
        # standard (`ISO-E`), the sheet scale (`1:2`), the description (`Blech`) and the weight
        # (`0,464 kg`, a free independent check on the whole reading). So: separated, never dropped.
        in_frame = frame is not None and (
            p.y < fb[1] + 0.25 * sheet_h or p.y > fb[3] - 0.03 * sheet_h
            or p.x < fb[0] + 0.03 * sheet_w or p.x > fb[2] - 0.03 * sheet_w)
        m = bend_re.match(t.replace("°", " ").replace("  ", " ").strip()) or bend_re.match(t)
        if m:
            bends.append({"dir": m.group(1).upper(), "angle_deg": float(m.group(2)),
                          "radius": float(m.group(3)), "rot": _r(rot, 2),
                          "at": [_r(p.x, 2), _r(p.y, 2)], "view": which_view(p.x, p.y)})
        elif not in_frame:
            notes.append({"text": t, "at": [_r(p.x, 2), _r(p.y, 2)], "view": which_view(p.x, p.y)})
        else:
            frame_notes.append({"text": t, "at": [_r(p.x, 2), _r(p.y, 2)]})
    # Reading order (top row first, left to right): a title block is a TABLE, and its rows are what
    # pair a label with its value -- 'Länge' at x=78.73 with '338' at x=78.57 one row below.
    frame_notes.sort(key=lambda n: (-n["at"][1], n["at"][0]))

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
        # A cluster lying WHOLLY inside the title-block band that no dimension points at is
        # administrative furniture: the projection-method symbol, a weld symbol, the oval around a
        # check dimension. The no-dimension guard is what keeps a real (if small) view safe.
        in_title_block = (title_block_top is not None
                          and b[3] <= title_block_top + gap and b[0] >= title_block_left - gap)
        if v["vid"] in dimmed:
            v["role"] = "view"
        elif in_title_block:
            v["role"] = "frame_item"
        elif touches_end:
            v["role"] = "annotation"
        else:
            v["role"] = "view"

    # ---- view ALIGNMENT: shared paper-X = a vertical projection pair (same width axis),
    #      shared paper-Y = a horizontal pair. Convention-independent; only the SIGN needs
    #      config['projection']. Compared on GEOM_BOX, not paper_box: the cluster box carries
    #      overhanging centre lines and whole-circle arc boxes, which moved s-3's front view 20 mm
    #      off its own side view and lost a pair that is exact to 0.01 mm. Furniture never pairs.
    real = [v for v in out_views if v["role"] == "view"]
    align = viewgraph.compute_alignment(real)

    # The solved view-axis graph -- {view paper axis -> anonymous part axis}, emitted only when the
    # pair evidence exists. Axes stay ax0/ax1/ax2 on purpose: WHICH is width/height/depth and which
    # view is "front" is the projection convention's sign + the model's call (recipe R4), never the
    # reader's. A detail/section at its own scale re-shows existing geometry and stays out.
    # No view_graph on a flat-pattern sheet (bend notes present): those drawings belong to the
    # direct-build gate, and a bent-state companion view is another STATE of the part, not another
    # projection -- s-2's 30x60 bent view midpoint-aligns with its 20x99 flat and would "solve"
    # to a wrong axis graph presented as fact.
    view_graph = None
    if align and not bends:
        view_graph = viewgraph.solve_view_graph(
            [{"vid": v["vid"], "size": v["size"]} for v in real if "scale_factor" not in v],
            align)

    # Section LABELS pair a section view to the view carrying its cut line even where box math
    # cannot (a cross-scale section never shares a span). Facts stack: a same-scale aligned section
    # may appear both as a span pair and as a label pair.
    def _has_cut(v):
        return any(p["c"] == "cut_line"
                   for arr in v["geometry"].values() for p in arr)
    align += viewgraph.pair_sections(
        [{"vid": v["vid"], "role": v["role"], "has_cut_line": _has_cut(v)} for v in out_views],
        notes)

    # ---- the FRAME cluster is not emitted as a view (it is the border + title block), but it must
    #      not VANISH either: on s-3 it silently swallowed the projection symbol's two concentric
    #      circles, so the model could see the symbol's cone and never its circles. Counted, and its
    #      round primitives -- always few, always meaningful -- reported in PAPER mm.
    fcount = Counter(e.dxftype() for e, _b in (frame or []))
    fcircles = [{"cx": _r(e.dxf.center.x, 2), "cy": _r(e.dxf.center.y, 2),
                 "d": _r(2 * e.dxf.radius, 2), "c": edge_class(doc, e)}
                for e, _b in (frame or []) if e.dxftype() == "CIRCLE"]

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
            # entity classes seen and NOT emitted (block text IS read; block geometry is not)
            "not_read": {**{k: not_read[k] for k in sorted(not_read)},
                         **({"INSERT": {k: block_geom[k] for k in sorted(block_geom)}}
                            if block_geom else {})},
        },
        "frame": {
            # paper_box None = NO border/title block on this sheet (nothing contained the other
            # clusters, so the big-cluster candidate stayed a view -- see frame_plausible).
            "paper_box": ([_r(fb[0], 2), _r(fb[1], 2), _r(fb[2], 2), _r(fb[3], 2)]
                          if frame is not None else None),
            "title_block_top": _r(title_block_top, 2) if title_block_top is not None else None,
            "title_block_left": _r(title_block_left, 2) if title_block_left is not None else None,
            "primitives": {"lines": fcount.get("LINE", 0), "arcs": fcount.get("ARC", 0),
                           "circles": fcount.get("CIRCLE", 0)},
            "circles": fcircles,
            "note_count": len(frame_notes),
        },
        "views": out_views,
        "alignment": align,
        **({"view_graph": view_graph} if view_graph is not None else {}),
        "dimensions": dims,
        "bend_notes": bends,
        "notes": notes,
        "frame_notes": frame_notes,
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
