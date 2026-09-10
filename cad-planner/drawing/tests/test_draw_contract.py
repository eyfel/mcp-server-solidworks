"""Offline gate for the drawing front end: contract drift + chaining/pairing/gate/lowering goldens.

Three things are checked, in order of how expensive the bug would be:

  1. CONTRACT DRIFT -- every array in draw-dialect.schema.json's `covered` block vs the matching
     frozenset in vocab.py, both directions, exact set equality. The schema is what the model is
     told the dialect contains, so a token on one side alone means either the model is promised
     something the reader never emits, or the reader emits something the model was never told about.
     (Same discipline, and the same contract-vs-prose split, as pycompiler's
     test_ir_schema_contract.py.)

  2. GOLDENS on frozen `draw`-dialect fixtures -- chaining is re-run and must reproduce the stored
     loops exactly; bend pairing is stripped and re-derived and must reproduce the stored matches;
     the gate must return the recorded verdict. The pairing case matters most: the rule this test
     pins is the one that replaced "nearest parallel line", which had silently mis-paired s-1's
     fourth bend note to the blank's OUTLINE edge.

  3. CROSS-CONTRACT -- the IR this module emits must validate against pycompiler's ir_schema. That
     is the check that stops the lowering from inventing vocabulary the compiler cannot build.

NO ezdxf DEPENDENCY, by construction: dxf_read is the only module that touches DXF, and it is not
imported here. The fixtures are committed `draw`-dialect JSON, so this runs anywhere, CI included.

Run two ways:
  - standalone:  python -m tests.test_draw_contract     (from cad-planner/drawing/)
  - pytest:      pytest cad-planner/drawing/tests/test_draw_contract.py
"""
import json
import math
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_DRAWING_ROOT = os.path.dirname(_HERE)                       # cad-planner/drawing/
_REPO_ROOT = os.path.dirname(os.path.dirname(_DRAWING_ROOT))  # cad-planner/ -> repo root
_FIXTURES = os.path.join(_HERE, "fixtures")
for _p in (_DRAWING_ROOT, os.path.join(_REPO_ROOT, "compiler", "solidworks")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import contour  # noqa: E402
import curvefit  # noqa: E402
import dxf_read  # noqa: E402  -- ezdxf is imported LAZILY inside read(), so this is CI-safe
import lowering  # noqa: E402
import pairing  # noqa: E402
import viewgraph  # noqa: E402
import vocab  # noqa: E402
import wire  # noqa: E402

_SCHEMA_PATH = os.path.join(_REPO_ROOT, "cad-planner", "contracts", "draw-dialect.schema.json")
_CONFIG_PATH = os.path.join(_DRAWING_ROOT, "config.json")

# Every array under `covered` -> the frozenset that must equal it, exactly.
_PAIRS = {
    "edge_classes":     vocab.EDGE_CLASSES,
    "view_roles":       vocab.VIEW_ROLES,
    "alignment_grades": vocab.ALIGN_GRADES,
    "loop_roles":       vocab.LOOP_ROLES,
    "chain_tiers":      vocab.CHAIN_TIERS,
    "seq_codes":        vocab.SEQ_CODES,
    "primitive_kinds":  vocab.PRIMITIVE_KINDS,
    "bend_directions":  vocab.BEND_DIRECTIONS,
    "unpaired_reasons": vocab.UNPAIRED_REASONS,
    "gate_reasons":     vocab.GATE_REASONS,
}
_PROSE_KEYS = frozenset(("_note",))

# The recorded verdict for every fixture: (direct, reason, bends_built, bends_skipped, ir_nodes).
# A fixture whose verdict changes is either a fix or a regression -- either way it must be seen.
_EXPECTED = {
    "s1_flat.json":        (True, None, 4, 0, 6),
    "s2_flat.json":        (True, None, 2, 0, 4),
    "ambiguous_bend.json": (True, None, 1, 1, 4),
    "f2_ortho.json":       (False, "no_bend_notes", 0, 0, 0),
    "open_contour.json":   (False, "no_outer_loop", 0, 0, 0),
    "no_thickness.json":   (False, "thickness_unresolved", 0, 0, 0),
}

# s-1's blank is the load-bearing number on this path: 80 x 118.5664 minus two 10x10 cutouts, at
# 2 mm. It is asserted explicitly so an area/units regression cannot hide behind a loop count.
_S1_BLANK_AREA_MM2 = 9285.312
_S1_VOLUME_M3 = 1.8570624e-05


def _load(name):
    with open(os.path.join(_FIXTURES, name), encoding="utf-8") as fh:
        return json.load(fh)


def _config():
    with open(_CONFIG_PATH, encoding="utf-8") as fh:
        return json.load(fh)


def _covered():
    if not os.path.isfile(_SCHEMA_PATH):
        raise AssertionError("draw-dialect schema not found at %s — the contract test's anchor is "
                             "gone." % _SCHEMA_PATH)
    with open(_SCHEMA_PATH, encoding="utf-8") as fh:
        schema = json.load(fh)
    covered = schema.get("covered")
    if not isinstance(covered, dict):
        raise AssertionError("draw-dialect.schema.json has no 'covered' object (renamed?).")
    return covered


# --------------------------------------------------------------------------- 1. contract drift
def check_contract():
    covered = _covered()
    errors = []
    for key in sorted(set(covered) - set(_PAIRS) - _PROSE_KEYS):
        errors.append("covered.%s is neither mapped to a vocab frozenset nor declared prose — add "
                      "it to _PAIRS or _PROSE_KEYS in this test." % key)
    for key in sorted(_PAIRS):
        expected = _PAIRS[key]
        if key not in covered:
            errors.append("covered.%s is MISSING from the schema but vocab.py registers %s."
                          % (key, sorted(expected)))
            continue
        advertised = covered[key]
        if not isinstance(advertised, list) or not all(isinstance(v, str) for v in advertised):
            errors.append("covered.%s must be an array of token strings (got %r)." % (key, advertised))
            continue
        if len(set(advertised)) != len(advertised):
            errors.append("covered.%s contains duplicate tokens." % key)
        only_schema = sorted(set(advertised) - expected)
        only_code = sorted(expected - set(advertised))
        if only_schema:
            errors.append("covered.%s advertises %s, which the reader never emits." % (key, only_schema))
        if only_code:
            errors.append("vocab.py registers %s for %s, which the schema does NOT advertise (the "
                          "model is never told about it)." % (only_code, key))
    # vocab-internal invariant: _resolve_thickness's states must stay inside the gate vocabulary.
    stray = vocab.THICKNESS_STATES - vocab.GATE_REASONS - {"ok"}
    if stray:
        errors.append("vocab.THICKNESS_STATES has %s outside GATE_REASONS — assess() would return a "
                      "reason the schema does not advertise." % sorted(stray))
    return errors


# --------------------------------------------------------------------------- 2. goldens
def check_chaining():
    """Re-chain every fixture view from its raw primitives; the stored loops must come back."""
    errors = []
    for name in sorted(_EXPECTED):
        art = _load(name)
        eps = _config()["tolerance"]["chain_eps_mm"]
        for v in art["views"]:
            got = contour.chain_view({"geometry": v["geometry"]}, eps)
            if got["loops"] != v["loops"]:
                errors.append("%s/%s: chaining drifted — stored %d loops %s, recomputed %d loops %s"
                              % (name, v["vid"], len(v["loops"]), [lp["id"] for lp in v["loops"]],
                                 len(got["loops"]), [lp["id"] for lp in got["loops"]]))
            if got["open_chains"] != v["open_chains"]:
                errors.append("%s/%s: open chains drifted — stored %d, recomputed %d"
                              % (name, v["vid"], len(v["open_chains"]), len(got["open_chains"])))
    return errors


def check_pairing():
    """Strip every stored bend match and re-derive it. This is the regression test for the rule
    that replaced 'nearest parallel line' — the one that took s-1's fourth DOWN note to the
    blank's top OUTLINE edge (visible) instead of the hidden bend line below it."""
    errors = []
    cfg = _config()
    for name in sorted(_EXPECTED):
        art = _load(name)
        if not art.get("bend_notes"):
            continue
        stored = [(b.get("bend_line"), b.get("unpaired")) for b in art["bend_notes"]]
        stripped = []
        for b in art["bend_notes"]:
            b = dict(b)
            b.pop("bend_line", None)
            b.pop("unpaired", None)
            stripped.append(b)
        pairing.pair_bend_notes(stripped, art["views"], art["sheet"]["scale_factor"], cfg)
        got = [(b.get("bend_line"), b.get("unpaired")) for b in stripped]
        if got != stored:
            errors.append("%s: bend pairing drifted\n      stored %s\n      got    %s"
                          % (name, stored, got))
        # Every match must corroborate: the class the note implies is the class of the line found.
        cmap = cfg["sheet_metal"]["bend_class_map"]
        for b in stripped:
            bl = b.get("bend_line")
            if bl and bl["class"] != cmap.get(b["dir"]):
                errors.append("%s: %s note matched a %r line (bend_class_map says %r)"
                              % (name, b["dir"], bl["class"], cmap.get(b["dir"])))
            if bl and bl["in_loop"]:
                errors.append("%s: %s note matched line %s which belongs to a CLOSED loop — a bend "
                              "line never should" % (name, b["dir"], bl["seg"]))
    return errors


def check_gate_and_lowering():
    errors = []
    cfg = _config()
    for name, (direct, reason, n_built, n_skipped, n_nodes) in sorted(_EXPECTED.items()):
        art = _load(name)
        a = lowering.assess(art, cfg)
        if a["direct"] is not direct or a.get("reason") != reason:
            errors.append("%s: gate said direct=%s reason=%s, expected direct=%s reason=%s (%s)"
                          % (name, a["direct"], a.get("reason"), direct, reason, a.get("detail", "")))
            continue
        if not direct:
            continue
        s = a["summary"]
        if len(s["bends"]) != n_built or len(s["skipped_bends"]) != n_skipped:
            errors.append("%s: %d built / %d skipped bends, expected %d / %d"
                          % (name, len(s["bends"]), len(s["skipped_bends"]), n_built, n_skipped))
        graph = lowering.lower_flat_pattern(art, cfg, a)
        if len(graph["nodes"]) != n_nodes:
            errors.append("%s: lowered to %d IR nodes, expected %d"
                          % (name, len(graph["nodes"]), n_nodes))
        # A sheet_metal graph is sketch -> sheet_metal, then {sketch -> sketched_bend} per group.
        types = [n["type"] for n in graph["nodes"]]
        if types[:2] != ["sketch", "sheet_metal"] or any(
                types[i] != "sketch" or types[i + 1] != "sketched_bend"
                for i in range(2, len(types), 2)):
            errors.append("%s: node order %s is not sketch/sheet_metal + sketch/sketched_bend pairs"
                          % (name, types))
        # Bend groups fold OUTER-FIRST (recipe R11): each group's greatest reach from the fixed
        # point must not increase down the graph.
        reach = [max(lowering._point_to_segment(
                     s["fixed_point_mm"], (p["x1"], p["y1"]), (p["x2"], p["y2"]))
                     for p in graph["nodes"][i]["profile"])
                 for i in range(2, len(graph["nodes"]), 2)]
        if reach != sorted(reach, reverse=True):
            errors.append("%s: bend groups are not ordered outer-first (reach %s)" % (name, reach))
    return errors


def check_s1_numbers():
    """The one fixture whose absolute numbers are pinned: a units or arc-area regression must not
    be able to hide behind matching loop counts."""
    a = lowering.assess(_load("s1_flat.json"), _config())
    errors = []
    got = a["summary"]["blank"]["area_mm2"]
    if abs(got - _S1_BLANK_AREA_MM2) > 1e-3:
        errors.append("s-1 blank area %.4f mm^2, expected %.4f (80 x 118.5664 minus two 10x10)"
                      % (got, _S1_BLANK_AREA_MM2))
    got = a["summary"]["expected"]["volume_m3"]
    if abs(got - _S1_VOLUME_M3) > 1e-12:
        errors.append("s-1 expected volume %r m^3, expected %r" % (got, _S1_VOLUME_M3))
    return errors


# --------------------------------------------------------------------------- 2b. view graph
def check_viewgraph():
    """The pure view-graph layer, pinned on the two drawings that motivated it: f-3 (borderless —
    the old 'biggest cluster is the frame' heuristic ate its front view) and s-7 (a bevelled beam
    whose middle view breaks the full-span test but not the midpoint one; and a stack that must
    REFUSE axis solving, because one of the three stacked views is not a plain projection)."""
    errors = []

    # frame_plausible: a real border contains everything; f-3's candidate does not; a lone cluster
    # proves nothing (a borderless single-view sheet must not have its only view eaten).
    if not viewgraph.frame_plausible((0, 0, 300, 200), [(20, 20, 120, 80), (150, 30, 250, 90)], 1.0):
        errors.append("frame_plausible refused a border that contains every cluster")
    f3_cand, f3_others = (22.82, 126.58, 146.82, 176.18), \
        [(34.82, 45.27, 134.82, 103.14), (175.21, 120.29, 237.38, 182.46)]
    if viewgraph.frame_plausible(f3_cand, f3_others, 1.0):
        errors.append("frame_plausible accepted f-3's front view as a frame (v1 lies outside it)")
    if viewgraph.frame_plausible((0, 0, 100, 100), [], 1.0):
        errors.append("frame_plausible accepted a lone cluster (nothing contained = nothing proven)")

    # graded alignment on s-7's real boxes: v0-v2 span, v0-v1 and v1-v2 midpoint (0.25 mm).
    s7 = [{"vid": "v0", "geom_box": (105.01, 388.55, 762.04, 481.28)},
          {"vid": "v1", "geom_box": (99.31, 169.62, 768.24, 213.81)},
          {"vid": "v2", "geom_box": (105.01, 307.42, 762.04, 348.01)}]
    got = {(p["a"], p["b"]): (p["shares"], p["grade"]) for p in viewgraph.compute_alignment(s7)}
    want = {("v0", "v1"): ("x", "mid"), ("v0", "v2"): ("x", "span"), ("v1", "v2"): ("x", "mid")}
    if got != want:
        errors.append("s-7 alignment drifted: want %s, got %s" % (want, got))

    # the axis solver SOLVES f-3 (unique leftover merge: 57.8665 ~ 57.8874) ...
    f3_views = [{"vid": "front", "size": [100.0, 40.0]},
                {"vid": "v0", "size": [100.0, 57.8665]},
                {"vid": "v1", "size": [57.8874, 40.0]}]
    f3_pairs = [{"a": "front", "b": "v0", "shares": "x", "grade": "span"},
                {"a": "front", "b": "v1", "shares": "y", "grade": "span"}]
    g = viewgraph.solve_view_graph(f3_views, f3_pairs)
    if not g["solved"] or g["views"] != {"front": ["ax0", "ax2"], "v0": ["ax0", "ax1"],
                                         "v1": ["ax1", "ax2"]}:
        errors.append("f-3 view graph did not solve to front(ax0,ax2)/v0(ax0,ax1)/v1(ax1,ax2): %r" % g)

    # ... REFUSES s-7 (three stacked views whose own axes are three different extents: one of them
    # is not a plain projection of the same box) ...
    s7_views = [{"vid": "v0", "size": [6570.3042, 927.2231]},
                {"vid": "v1", "size": [6689.3272, 441.9169]},
                {"vid": "v2", "size": [6570.3042, 405.8958]}]
    s7_pairs = [{"a": "v0", "b": "v1", "shares": "x", "grade": "mid"},
                {"a": "v0", "b": "v2", "shares": "x", "grade": "span"},
                {"a": "v1", "b": "v2", "shares": "x", "grade": "mid"}]
    g = viewgraph.solve_view_graph(s7_views, s7_pairs)
    if g["solved"] or g.get("reason") != "axes_unmerged":
        errors.append("s-7's stack must refuse with axes_unmerged, got %r" % g)

    # ... SOLVES f-1's four views, where TWO independent merges are each forced (100~100 for the
    # bottom view's width, 40~40 for the side view's h) and must both apply in one pass ...
    f1_views = [{"vid": "v0", "size": [100.0, 60.0]}, {"vid": "v1", "size": [100.0, 40.0]},
                {"vid": "v2", "size": [100.0, 40.0]}, {"vid": "v3", "size": [40.0, 60.0]}]
    f1_pairs = [{"a": "v0", "b": "v1", "shares": "x", "grade": "span"},
                {"a": "v0", "b": "v3", "shares": "y", "grade": "span"},
                {"a": "v1", "b": "v2", "shares": "y", "grade": "span"}]
    g = viewgraph.solve_view_graph(f1_views, f1_pairs)
    if not g["solved"] or g["views"] != {"v0": ["ax0", "ax1"], "v1": ["ax0", "ax2"],
                                         "v2": ["ax0", "ax2"], "v3": ["ax2", "ax1"]}:
        errors.append("f-1's two independent forced merges did not both apply: %r" % g)

    # ... and REFUSES a cube (every merge fits: nothing is forced).
    cube_views = [{"vid": "f", "size": [50.0, 50.0]}, {"vid": "t", "size": [50.0, 50.0]},
                  {"vid": "s", "size": [50.0, 50.0]}]
    cube_pairs = [{"a": "f", "b": "t", "shares": "x", "grade": "span"},
                  {"a": "f", "b": "s", "shares": "y", "grade": "span"}]
    g = viewgraph.solve_view_graph(cube_views, cube_pairs)
    if g["solved"] or g.get("reason") != "axes_ambiguous":
        errors.append("a cube's axes must refuse as axes_ambiguous, got %r" % g)

    # section labels: the caption names the section; the cut-line owner is the parent. Both f-3's
    # 'SECTION A-A' caption form and s-7's leading 'A-A 1 : 1' form must pair; prose and a
    # caption with no cut owner must not.
    vs = [{"vid": "v0", "role": "view", "has_cut_line": True},
          {"vid": "v1", "role": "view", "has_cut_line": False},
          {"vid": "v2", "role": "view", "has_cut_line": False}]
    for caption in ("SECTION A-A", "A-A 1 : 1"):
        got = viewgraph.pair_sections(vs, [{"text": caption, "view": "v1"},
                                           {"text": "A", "view": "v0"}])
        if got != [{"a": "v0", "b": "v1", "shares": None, "grade": "label", "label": "A-A"}]:
            errors.append("section caption %r did not pair v0->v1: %r" % (caption, got))
    if viewgraph.pair_sections(vs, [{"text": "see note A-A below", "view": "v1"}]):
        errors.append("prose containing 'A-A' must not become a section pair")
    if viewgraph.pair_sections(
            [{"vid": "v0", "role": "view", "has_cut_line": False}],
            [{"text": "SECTION B-B", "view": "v0"}]):
        errors.append("a caption with no cut-line owner must pair nothing")
    return errors


# --------------------------------------------------------------------------- 2c. curve fit
def _fan(el, t_from, t_to, n, cls="visible", reverse=False):
    """A KNOWN ellipse (arc) tessellated into n chords, 3-decimal like the reader emits."""
    pts = [curvefit.point_at(el, t_from + (t_to - t_from) * k / n) for k in range(n + 1)]
    pts = [(round(x, 3), round(y, 3)) for x, y in pts]
    if reverse:
        pts.reverse()
    return [{"x1": a[0], "y1": a[1], "x2": b[0], "y2": b[1], "c": cls}
            for a, b in zip(pts, pts[1:])]


def check_curvefit():
    """Parametric ellipses (0.5.0) pinned on SYNTHETIC fans built from KNOWN ellipses -- so a
    regression cannot hide behind a real sample's own tessellation noise. The fitter proposes,
    the residual decides: a true fan collapses with its parameters recovered, a polygon stays a
    polygon, a straight flank absorbed into a run is trimmed off, and the chained loop's area is
    exact through the elliptical bulge."""
    errors = []
    cfg = _config()
    cf, chain_eps = cfg["curve_fit"], cfg["tolerance"]["chain_eps_mm"]
    eps, ms, mt = cf["fit_eps_paper_mm"], cf["min_segments"], cf["max_turn_deg"]

    def collapse(lines):
        return curvefit.collapse_fans(lines, [], [], eps, chain_eps, ms, mt, cf["min_sweep_deg"])

    # f-3's hole: a FULL ellipse rx 5 / ry 4.6985 (= 5 cos 20 deg), rot 90, 48 chords (sag 0.0107)
    el = (30.0, 20.0, 5.0, 4.6985, 90.0)
    left, fitted = collapse(_fan(el, 0, 360, 48))
    if left or len(fitted) != 1 or "t1" in fitted[0]:
        errors.append("a 48-chord full ellipse did not collapse to ONE full record: %d lines left, %r"
                      % (len(left), fitted))
    else:
        e = fitted[0]
        off = max(abs(e["cx"] - 30), abs(e["cy"] - 20), abs(e["rx"] - 5), abs(e["ry"] - 4.6985))
        if off > 2e-3 or abs(e["rot"] - 90) > 0.5 or e["n"] != 48 or not 0 < e["fit"] <= eps:
            errors.append("full-ellipse fit drifted (off %.4f): %r" % (off, e))

    # a half-ellipse ARC stored BACKWARDS normalises to CCW t1 -> t2 with its OWN end vertices
    left, fitted = collapse(_fan(el, 0, 180, 24, reverse=True))
    if left or len(fitted) != 1 or "t1" not in fitted[0]:
        errors.append("a 24-chord half ellipse did not collapse to ONE arc record: %r" % fitted)
    else:
        e = fitted[0]
        p0, p1 = curvefit.point_at(el, 0.0), curvefit.point_at(el, 180.0)
        if (min(abs(e["t1"]), abs(e["t1"] - 360)) > 0.5 or abs(e["t2"] - 180) > 0.5
                or e["dir"] != 1
                or math.hypot(e["x1"] - p0[0], e["y1"] - p0[1]) > 2e-3
                or math.hypot(e["x2"] - p1[0], e["y2"] - p1[1]) > 2e-3):
            errors.append("ellipse ARC normalisation drifted: %r" % e)

    # polygons stay polygons: a 12-gon of r 5 (sag 0.17 >> eps) and a hexagon (n < min_segments)
    for n in (12, 6):
        left, fitted = collapse(_fan((10.0, 10.0, 5.0, 5.0, 0.0), 0, 360, n))
        if fitted or len(left) != n:
            errors.append("a %d-gon was collapsed into an ellipse: %r" % (n, fitted))

    # a SHORT arc stays raw: 60 deg of f-3's boss ellipse (rx 12 / ry 4.104) in 13 chords fits
    # within the sag yet pins rx 2% wrong -- below min_sweep it is not a measurement
    boss = (58.0, 31.0, 12.0, 4.104, 90.0)
    left, fitted = collapse(_fan(boss, 300, 360, 13))
    if fitted or len(left) != 13:
        errors.append("a 60-degree arc was emitted as an ellipse (parameters undetermined): %r" % fitted)
    left, fitted = collapse(_fan(boss, 270, 360, 19))    # 90 deg: allowed, rx within 0.65%
    if len(fitted) != 1 or abs(fitted[0]["rx"] - 12.0) > 0.08:
        errors.append("a 90-degree arc did not fit within the measured 0.65%% bound: %r" % fitted)

    # a straight flank tangent to the arc is absorbed by the turn test, then TRIMMED by the fit
    lines = _fan(el, 90, 270, 24)                    # top -> left -> bottom (rot 90: ends at x 34.7)
    end = (lines[-1]["x2"], lines[-1]["y2"])
    lines.append({"x1": end[0], "y1": end[1], "x2": end[0], "y2": end[1] - 15.0, "c": "visible"})
    left, fitted = collapse(lines)
    if len(left) != 1 or len(fitted) != 1 or fitted[0]["n"] != 24:
        errors.append("the tangent flank was not trimmed off the run: %d lines left, %r"
                      % (len(left), fitted))

    # chaining + exact area: a stadium of two lines and two half-ellipse arcs (rx 5, ry 3), with a
    # small full ellipse inside it -> outer loop 20*6 + pi*5*3, inner loop pi*2*1, codes 'e'
    el_l, el_r, el_in = (0.0, 0.0, 5.0, 3.0, 0.0), (20.0, 0.0, 5.0, 3.0, 0.0), (10.0, 0.0, 2.0, 1.0, 30.0)
    view = {"geometry": {
        "lines": [{"x1": 0.0, "y1": 3.0, "x2": 20.0, "y2": 3.0, "c": "visible"},
                  {"x1": 0.0, "y1": -3.0, "x2": 20.0, "y2": -3.0, "c": "visible"}],
        "arcs": [], "circles": [],
        "ellipses": [curvefit.make_record(el_l, "visible", (0.0, 3.0), (0.0, -3.0), True),
                     curvefit.make_record(el_r, "visible", (20.0, -3.0), (20.0, 3.0), True),
                     curvefit.make_record(el_in, "visible")]}}
    ch = contour.chain_view(view, chain_eps)
    want = 20 * 6 + math.pi * 15, math.pi * 2
    if (len(ch["loops"]) != 2 or ch["open_chains"]
            or abs(ch["loops"][0]["area"] - want[0]) > 1e-3 or abs(ch["loops"][1]["area"] - want[1]) > 1e-3
            or ch["loops"][1]["parent"] != "L0" or len(ch["loops"][0]["seq"]) != 4
            or sorted(c for c, _i, _d in ch["loops"][0]["seq"]) != ["e", "e", "l", "l"]):
        errors.append("stadium with ellipse arcs did not chain to areas %.4f / %.4f: %r"
                      % (want[0], want[1], ch["loops"]))
    else:
        # lowering: a full ellipse -> the IR `ellipse` (centre + a point on each axis); an arc ->
        # a `spline` through sampled points whose ENDS are the record's exact vertices
        to_m = lambda x, y: (round(x / 1000.0, 9), round(y / 1000.0, 9))  # noqa: E731
        prof = lowering._loop_profile(view, ch["loops"][1], to_m)
        if (len(prof) != 1 or prof[0]["kind"] != "ellipse"
                or abs(math.hypot(prof[0]["x1"] - prof[0]["cx"], prof[0]["y1"] - prof[0]["cy"]) - 0.002) > 1e-6
                or abs(math.hypot(prof[0]["x2"] - prof[0]["cx"], prof[0]["y2"] - prof[0]["cy"]) - 0.001) > 1e-6):
            errors.append("a full ellipse did not lower to the IR ellipse primitive: %r" % prof)
        prof = lowering._loop_profile(view, ch["loops"][0], to_m)
        kinds = sorted(p["kind"] for p in prof)
        if kinds != ["line", "line", "spline", "spline"]:
            errors.append("the stadium did not lower to 2 lines + 2 splines: %s" % kinds)
        else:
            for k in range(4):                     # consecutive primitives share an endpoint
                a, b = prof[k], prof[(k + 1) % 4]
                ea = (a["x2"], a["y2"]) if a["kind"] == "line" else tuple(a["points"][-2:])
                sb = (b["x1"], b["y1"]) if b["kind"] == "line" else tuple(b["points"][:2])
                if math.hypot(ea[0] - sb[0], ea[1] - sb[1]) > 1e-6:
                    errors.append("lowered stadium is not endpoint-continuous at %d: %r -> %r" % (k, ea, sb))
    return errors


def check_wire():
    """The wire form must be EXACTLY reversible, and it must actually be smaller.

    Reversibility is the whole safety argument for compacting the payload: the pipeline keeps the
    record shape, only the boundary encodes, and an artifact read back off disk has to re-enter the
    pipeline identical to the one that was written. So `decode(encode(x)) == x`, deeply, on every
    fixture -- and idempotence too, because a pre-0.7.0 artifact must pass through untouched.
    """
    errors = []
    for name in sorted(_EXPECTED):
        art = _load(name)
        try:
            enc = wire.encode(art)
            back = wire.decode(enc)
        except ValueError as exc:
            errors.append("%s: the wire round trip RAISED: %s" % (name, exc))
            continue
        if back != art:
            errors.append("%s: decode(encode(art)) != art -- the wire form is LOSSY" % name)
        if wire.decode(art) != art:
            errors.append("%s: decode is not idempotent on an already-decoded artifact" % name)
        if wire.encode(enc) != enc:
            errors.append("%s: encode is not idempotent on an already-encoded artifact" % name)
        # The input must not have been mutated -- callers keep using it after encoding.
        if _load(name) != art:
            errors.append("%s: encode MUTATED its input" % name)
        for v in enc.get("views", []):
            lines = (v.get("geometry") or {}).get("lines")
            if lines is None:
                continue
            if not isinstance(lines, dict):
                errors.append("%s/%s: encoded lines is not grouped by class" % (name, v["vid"]))
                continue
            if set(lines) - set(vocab.EDGE_CLASSES):
                errors.append("%s/%s: encoded lines group by a class outside the contract: %s"
                              % (name, v["vid"], sorted(set(lines) - set(vocab.EDGE_CLASSES))))
            idx = sorted(row[0] for rows in lines.values() for row in rows)
            if idx != list(range(len(idx))):
                errors.append("%s/%s: encoded line indices are not contiguous 0..n-1"
                              % (name, v["vid"]))
        # Every `seq` reference must still resolve to the SAME primitive after the round trip.
        for v0, v1 in zip(art.get("views", []), back.get("views", [])):
            g0, g1 = v0.get("geometry") or {}, v1.get("geometry") or {}
            for lp in list(v0.get("loops") or []) + list(v0.get("open_chains") or []):
                for code, i, _d in lp["seq"]:
                    kind = wire._CODE_TO_KIND[code]
                    if (g0.get(kind) or [])[i] != (g1.get(kind) or [])[i]:
                        errors.append("%s/%s: seq ref %s%d resolves to a DIFFERENT primitive after "
                                      "the round trip" % (name, v0["vid"], code, i))
    return errors


# --------------------------------------------------------------------------- 3. cross-contract
def check_ir_validates():
    """The emitted graph must pass pycompiler's own validator — the check that keeps this module
    from inventing IR the compiler cannot build."""
    try:
        from pycompiler import ir_schema
    except ImportError as exc:
        return ["pycompiler is not importable (%s) — the drawing lowering's IR cannot be verified "
                "against the compiler that has to build it." % exc]
    errors = []
    cfg = _config()
    for name, (direct, _r, _b, _s, _n) in sorted(_EXPECTED.items()):
        if not direct:
            continue
        graph = lowering.lower_flat_pattern(_load(name), cfg)
        bad = ir_schema.validate(graph)
        if bad:
            errors.append("%s: lowered IR is invalid: %s" % (name, bad))
    return errors


# --------------------------------------------------------------------------- entry points
def check_angular_arbitration():
    """R18's angular reconciliation, pinned on EVERY angular dimension in the sample set.

    The DWG->DXF route loses an angular dim's SIDE and corrupts EITHER candidate -- usually the
    stored measurement (180+theta), but on f-3 the rendered block text (360-theta). These seven
    (measured, printed) pairs are the real ones read out of the sample DXFs; the expected value is
    the true angle, corroborated independently (f-3's 110 by its ray geometry AND by the view's
    ellipse foreshortening of 70/20 deg). The invariant the rule rests on: a mechanical drawing
    does not dimension a reflex angle."""
    errors = []
    cases = [
        # (measured, printed, expected value, expected mismatch, provenance)
        (110.000, 250.0, 110.0, True,  "f-3: PRINTED corrupt (360-110)"),
        (30.000,   30.0,  30.0, False, "s-6: agree"),
        (187.876,   7.9,   7.9, True,  "s-7: MEASURED corrupt (180+7.9)"),
        (12.018,  12.02, 12.02, False, "s-7: agree"),
        (204.401,  24.4,  24.4, True,  "s-7: MEASURED corrupt"),
        (180.991,   1.0,   1.0, True,  "s-7: MEASURED corrupt, barely over 180"),
        (225.000,  45.0,  45.0, True,  "s-7: MEASURED corrupt"),
    ]
    for measured, printed, want, want_mm, why in cases:
        got, mm = dxf_read.reconcile_angular(measured, printed)
        if abs(got - want) > 1e-9:
            errors.append("angular %s: expected %s, got %s (%s)" % (why, want, got, printed))
        if mm != want_mm:
            errors.append("angular %s: mismatch flag %s, expected %s" % (why, mm, want_mm))

    # Both candidates reflex => NO evidence to choose on: keep printed, still flag the conflict.
    got, mm = dxf_read.reconcile_angular(200.0, 300.0)
    if got != 300.0 or not mm:
        errors.append("both-reflex must keep printed and flag: got %s / %s" % (got, mm))
    # A missing candidate must never crash or invent a value.
    if dxf_read.reconcile_angular(None, 45.0) != (45.0, False):
        errors.append("printed-only case must pass printed through")
    if dxf_read.reconcile_angular(45.0, None) != (45.0, False):
        errors.append("measured-only case must pass measured through")
    return errors


_CHECKS = (("contract drift", check_contract),
           ("angular arbitration", check_angular_arbitration),
           ("contour chaining", check_chaining),
           ("bend pairing", check_pairing),
           ("gate + lowering", check_gate_and_lowering),
           ("s-1 absolute numbers", check_s1_numbers),
           ("view graph", check_viewgraph),
           ("curve fit", check_curvefit),
           ("wire round-trip", check_wire),
           ("IR validity", check_ir_validates))


def find_drift():
    out = []
    for label, fn in _CHECKS:
        out += ["[%s] %s" % (label, e) for e in fn()]
    return out


def test_draw_dialect_contract_in_sync():
    """pytest entry point."""
    errors = find_drift()
    assert not errors, "draw-dialect drift detected:\n  - " + "\n  - ".join(errors)


if __name__ == "__main__":
    errs = find_drift()
    if errs:
        print("DRAW-DIALECT CONTRACT DRIFT DETECTED:")
        for e in errs:
            print("  -", e)
        sys.exit(1)
    print("OK - %d vocabulary sets / %d tokens in sync (draw-dialect.schema.json <-> vocab.py); "
          "%d fixtures: chaining, pairing, gate, lowering and IR validity all match; view-graph, "
          "curve-fit goldens hold; the wire form round-trips exactly"
          % (len(_PAIRS), sum(len(v) for v in _PAIRS.values()), len(_EXPECTED)))
    sys.exit(0)
