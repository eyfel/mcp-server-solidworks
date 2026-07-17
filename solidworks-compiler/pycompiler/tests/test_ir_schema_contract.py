"""Contract test (audit-A1): the IR schema (cad-planner) vs the validator (pycompiler).

Guards against drift between the CAD-neutral capability registry at
`cad-planner/contracts/feature-graph.schema.json` (its `covered_subset` block) and the
frozensets in `pycompiler/ir_schema.py` that actually decide what compiles. The schema IS the
capability registry (ADR-005): if it advertises a token the validator rejects, an author writes a
graph that cannot build; if the validator accepts a token the schema never advertises, the
capability is invisible. Either direction is a bug, so every pair below is checked BOTH ways with
exact set equality.

Scope — CONTRACT vs PROSE (decided 2026-07-17 with the user):
  CONTRACT = every ARRAY in `covered_subset`; each is a closed set of vocabulary tokens and maps
      to exactly one frozenset. v0.7.1 promoted eight sets that had lived only in prose (the
      assembly vocabulary, pattern directions, chamfer types, bend/flange positions) plus the two
      grammar sets (body/feature producers) into arrays for this purpose — prose cannot be
      tested, and circular_pattern's seed list had silently rotted to five stale types to prove
      it.
  PROSE  = `_note` / `_grammar_note` / `datum_offset` / `assumptions`, and every per-param
      description elsewhere in the schema ("number - meters (> 0)"). Per-param presence, types
      and ranges live in ir_schema.py code and are exercised by test_compiler.py; string-parsing
      them here would be brittle for no gain. This mirrors the scope note the adapter's
      test_schema_contract.py makes for itself.

Run two ways:
  - standalone:  python -m pycompiler.tests.test_ir_schema_contract   (exits non-zero on drift)
  - pytest:      pytest solidworks-compiler/pycompiler/tests/test_ir_schema_contract.py
"""
import json
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_COMPILER_ROOT = os.path.dirname(os.path.dirname(_HERE))   # solidworks-compiler/
_REPO_ROOT = os.path.dirname(_COMPILER_ROOT)
if _COMPILER_ROOT not in sys.path:
    sys.path.insert(0, _COMPILER_ROOT)

from pycompiler import ir_schema  # noqa: E402

_SCHEMA_PATH = os.path.join(_REPO_ROOT, "cad-planner", "contracts", "feature-graph.schema.json")

# `hole.depth` accepts 'through' as a BACK-COMPAT ALIAS of the canonical 'through_all' (the
# schema's own hole prose says so, and its v0-exp example uses it). The registry advertises only
# the canonical token — advertising an alias would invite authors to use it. So the alias is
# subtracted here as a DECLARED, reviewed exception: any OTHER token added to THROUGH_DEPTHS
# still fails this test.
_HOLE_DEPTH_ALIASES = frozenset(("through",))

# Every array under covered_subset -> the frozenset that must equal it, exactly.
# NODE_TYPES holds the part AND assembly vocabularies; the schema splits them into two arrays
# (a graph may never mix them, ir_schema.validate enforces that), so the part array pairs with
# the difference.
_PAIRS = {
    "node_types":            ir_schema.NODE_TYPES - ir_schema.ASSEMBLY_NODE_TYPES,
    "assembly_node_types":   ir_schema.ASSEMBLY_NODE_TYPES,
    "profile_kinds":         ir_schema.PROFILE_KINDS,
    "profile_flags":         ir_schema.PROFILE_FLAGS,
    "datums":                ir_schema.DATUMS,
    "face_selectors":        ir_schema.FACE_SELECTORS,
    "positions":             ir_schema.POSITIONS,
    "hole_depth":            ir_schema.THROUGH_DEPTHS - _HOLE_DEPTH_ALIASES,
    "extrude_ends":          ir_schema.EXTRUDE_ENDS,
    "pattern_directions":    ir_schema.PATTERN_DIRECTIONS,
    "chamfer_types":         ir_schema.CHAMFER_TYPES,
    "bend_positions":        ir_schema.BEND_POSITIONS,
    "edge_flange_positions": ir_schema.EDGE_FLANGE_POSITIONS,
    "mate_types":            ir_schema.MATE_TYPES,
    "mate_alignments":       ir_schema.MATE_ALIGNMENTS,
    "anchor_kinds_face":     ir_schema.ANCHOR_KINDS_FACE,
    "anchor_kinds_edge":     ir_schema.ANCHOR_KINDS_EDGE,
    "body_producers":        ir_schema.BODY_PRODUCERS,
    "feature_producers":     ir_schema.FEATURE_PRODUCERS,
}

# covered_subset keys that are deliberately PROSE, not contract. Any key that is in neither this
# set nor _PAIRS is unclassified drift: someone added a subset key without deciding what it is.
_PROSE_KEYS = frozenset(("_note", "_grammar_note", "datum_offset", "assumptions"))


def _covered_subset():
    """The schema's covered_subset block. A missing/malformed schema is drift, not a skip."""
    if not os.path.isfile(_SCHEMA_PATH):
        raise AssertionError("IR schema not found at %s — the contract test cannot verify the "
                             "capability registry." % _SCHEMA_PATH)
    with open(_SCHEMA_PATH, encoding="utf-8") as fh:
        schema = json.load(fh)
    subset = schema.get("covered_subset")
    if not isinstance(subset, dict):
        raise AssertionError("feature-graph.schema.json has no 'covered_subset' object — the "
                             "contract test's anchor is gone (renamed?).")
    return subset


def find_drift():
    """Return a list of human-readable drift messages (empty == in sync)."""
    subset = _covered_subset()
    errors = []

    # Unclassified keys: a new covered_subset key must be declared contract (_PAIRS) or prose.
    for key in sorted(set(subset) - set(_PAIRS) - _PROSE_KEYS):
        errors.append(
            "covered_subset.%s is neither mapped to an ir_schema frozenset nor declared prose — "
            "add it to _PAIRS or _PROSE_KEYS in this test." % key)

    for key in sorted(_PAIRS):
        expected = _PAIRS[key]
        if key not in subset:
            errors.append("covered_subset.%s is MISSING from the schema but ir_schema.py "
                          "registers %s." % (key, sorted(expected)))
            continue
        advertised = subset[key]
        if not isinstance(advertised, list) or not all(isinstance(v, str) for v in advertised):
            errors.append("covered_subset.%s must be an array of vocabulary-token strings "
                          "(got %r) — contract keys are machine-diffed, not prose."
                          % (key, advertised))
            continue
        if len(set(advertised)) != len(advertised):
            errors.append("covered_subset.%s contains duplicate tokens: %s"
                          % (key, sorted(t for t in set(advertised)
                                         if advertised.count(t) > 1)))
        advertised = set(advertised)
        only_schema = sorted(advertised - expected)
        only_code = sorted(expected - advertised)
        if only_schema:
            errors.append("covered_subset.%s advertises %s, which ir_schema.py does NOT accept "
                          "(a graph using it would be rejected at validation)." % (key, only_schema))
        if only_code:
            errors.append("ir_schema.py accepts %s for %s, but the schema does NOT advertise it "
                          "(the capability is invisible to graph authors)." % (only_code, key))

    # ir_schema-internal invariant behind the two anchor_kinds arrays: ANCHOR_KINDS is what
    # validate() actually checks, so a kind added there alone would slip past both pairs above.
    union = ir_schema.ANCHOR_KINDS_FACE | ir_schema.ANCHOR_KINDS_EDGE
    if ir_schema.ANCHOR_KINDS != union:
        errors.append("ir_schema.ANCHOR_KINDS %s != ANCHOR_KINDS_FACE | ANCHOR_KINDS_EDGE %s — "
                      "the schema advertises the face/edge split, so the union must stay derived "
                      "from it." % (sorted(ir_schema.ANCHOR_KINDS), sorted(union)))
    return errors


def test_ir_schema_contract_in_sync():
    """pytest entry point."""
    errors = find_drift()
    assert not errors, "IR schema/validator drift detected:\n  - " + "\n  - ".join(errors)


if __name__ == "__main__":
    errs = find_drift()
    if errs:
        print("IR SCHEMA CONTRACT DRIFT DETECTED:")
        for e in errs:
            print("  -", e)
        sys.exit(1)
    tokens = sum(len(v) for v in _PAIRS.values())
    print("OK - %d vocabulary sets / %d tokens in sync "
          "(feature-graph.schema.json <-> ir_schema.py)" % (len(_PAIRS), tokens))
    sys.exit(0)
