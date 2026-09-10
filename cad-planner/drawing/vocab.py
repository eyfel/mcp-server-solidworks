"""vocab.py -- every CLOSED vocabulary of the `draw` dialect, in one dependency-free place.

This is the drawing side's answer to pycompiler's ir_schema frozensets: the contract test
(tests/test_draw_contract.py) diffs each set here against the matching array in
cad-planner/contracts/draw-dialect.schema.json, both directions, exact equality. A token added on
one side alone fails the gate -- which is the point, since the schema is what the model is told
the dialect contains.

Kept free of imports on purpose: the contract test must run with no ezdxf, on any machine.
"""
from __future__ import annotations

# --- geometry / structure --------------------------------------------------------------------
EDGE_CLASSES = frozenset({"visible", "hidden", "cut_line", "center"})
VIEW_ROLES = frozenset({"view", "annotation", "frame_item"})
LOOP_ROLES = frozenset({"outer", "inner"})
# HOW a loop was resolved. A loop carries `tier` only when it is "B"; ABSENT means "A", so the
# overwhelmingly common case costs nothing. "A" = strict chaining, which stops dead at any
# junction of three or more and therefore never guesses. "B" = planar face traversal, used only
# where the view's visible graph has NO free end: the figure is then a closed subdivision whose
# faces are DEFINED by angular order at each vertex, so walking it is a reading, not a guess.
CHAIN_TIERS = frozenset({"A", "B"})
SEQ_CODES = frozenset({"l", "a", "c", "e"})                 # as used in a loop's `seq`
PRIMITIVE_KINDS = frozenset({"lines", "arcs", "circles", "ellipses"})   # view.geometry arrays
# "e"/"ellipses" since 0.5.0: a real DXF ELLIPSE entity, or an exploded curve fan the fitter
# collapsed (record carries `n` + `fit` then) -- see curvefit.py.

# --- view graph --------------------------------------------------------------------------------
# How strongly a projection pair is evidenced: full shared span > shared midpoints (a silhouette
# may genuinely be longer in one view -- s-7's bevelled beam) > a section label ("A-A", which even
# a cross-scale section keeps). The consumer decides what each grade licenses.
ALIGN_GRADES = frozenset({"span", "mid", "label"})

# --- sheet metal -------------------------------------------------------------------------------
BEND_DIRECTIONS = frozenset({"UP", "DOWN"})
UNPAIRED_REASONS = frozenset({"no_candidate", "ambiguous"})

# --- the direct-buildable gate ------------------------------------------------------------------
# Each one is a reason the DRAWING does not force a decision. None of them is an error: the caller
# falls back to handing the model the full analysis, which is the pre-existing behaviour.
GATE_REASONS = frozenset({
    "no_bend_notes",            # not a sheet-metal flat pattern (v1 scope)
    "bend_notes_split",         # bend notes spread over more than one view
    "no_outer_loop",            # the blank outline did not close
    "multiple_outer_loops",     # more than one candidate blank in the flat view
    "stray_loop",               # a closed loop that is neither the blank nor one of its cutouts
    "cut_line_in_flat_view",    # a section is cut through the flat pattern
    "unclaimed_open_chain",     # a real line in the flat view that nothing accounts for
    "no_bend_paired",           # not one bend note could be matched -- systematic, not a one-off
    "thickness_unresolved",     # no thickness source
    "thickness_ambiguous",      # more than one, and they disagree
    "fixed_point_ambiguous",    # no safe point on the blank that every bend can fold around
})

# The subset of gate reasons that are also `_resolve_thickness` states, so that helper's return
# values cannot drift away from the gate's vocabulary.
THICKNESS_STATES = frozenset({"ok", "thickness_unresolved", "thickness_ambiguous"})
