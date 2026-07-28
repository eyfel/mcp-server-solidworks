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
import lowering  # noqa: E402
import pairing  # noqa: E402
import vocab  # noqa: E402

_SCHEMA_PATH = os.path.join(_REPO_ROOT, "cad-planner", "contracts", "draw-dialect.schema.json")
_CONFIG_PATH = os.path.join(_DRAWING_ROOT, "config.json")

# Every array under `covered` -> the frozenset that must equal it, exactly.
_PAIRS = {
    "edge_classes":     vocab.EDGE_CLASSES,
    "view_roles":       vocab.VIEW_ROLES,
    "loop_roles":       vocab.LOOP_ROLES,
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
_CHECKS = (("contract drift", check_contract),
           ("contour chaining", check_chaining),
           ("bend pairing", check_pairing),
           ("gate + lowering", check_gate_and_lowering),
           ("s-1 absolute numbers", check_s1_numbers),
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
          "%d fixtures: chaining, pairing, gate, lowering and IR validity all match"
          % (len(_PAIRS), sum(len(v) for v in _PAIRS.values()), len(_EXPECTED)))
    sys.exit(0)
