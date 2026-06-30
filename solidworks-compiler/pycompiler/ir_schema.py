"""Structural (plan-time) validation of a Feature Graph IR — v0-exp subset.

Validates STRUCTURE only (registered types, required params present, node references resolvable,
units correct) — NOT geometry. Geometric validity (does the top face exist, does the hole fit) is
the compiler/resolver/execution's job at compile/exec time (ADR-004). If this returns errors, the
compiler performs ZERO execution calls.

v0-exp covered vocabulary (kept deliberately minimal — grow from real MAT parts, not ahead of them):
  box, sketch, extrude (boss|cut), hole-on-face (selector 'top', position 'center', depth 'through_all').
Anything outside the covered set is rejected here with an explicit 'unsupported in v0-exp' message —
never silently ignored.
"""

DATUMS = frozenset(("front", "top", "right"))
NODE_TYPES = frozenset(("box", "sketch", "extrude", "hole"))
PROFILE_KINDS = frozenset(("rectangle", "circle"))
FACE_SELECTORS = frozenset(("top",))          # v0-exp: only 'top'
POSITIONS = frozenset(("center",))            # v0-exp: only 'center'
THROUGH_DEPTHS = frozenset(("through_all", "through"))
BODY_PRODUCERS = frozenset(("box", "extrude"))  # node types whose body a 'hole' may reference


def _is_pos_number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool) and v > 0


def validate(graph):
    """Return a list of human-readable error strings ([] == structurally valid)."""
    errors = []
    if not isinstance(graph, dict):
        return ["graph must be a JSON object."]

    if graph.get("units") != "meters":
        errors.append("units must be 'meters' (got %r)." % graph.get("units"))

    nodes = graph.get("nodes")
    if not isinstance(nodes, list) or len(nodes) == 0:
        errors.append("graph.nodes must be a non-empty array.")
        return errors  # nothing more to check without nodes

    seen = {}  # id -> type (in declaration order)
    for pos, node in enumerate(nodes):
        where = "nodes[%d]" % pos
        if not isinstance(node, dict):
            errors.append("%s must be an object." % where)
            continue

        nid = node.get("id")
        if not isinstance(nid, str) or not nid:
            errors.append("%s.id is required (non-empty string)." % where)
            nid = None
        elif nid in seen:
            errors.append("%s.id '%s' is duplicated." % (where, nid))

        ntype = node.get("type")
        if ntype not in NODE_TYPES:
            errors.append("%s.type '%s' is not a registered v0-exp type %s."
                          % (where, ntype, sorted(NODE_TYPES)))
            if nid:
                seen[nid] = ntype
            continue

        label = "%s (%s '%s')" % (where, ntype, nid)

        if ntype == "box":
            _check_datum(node, label, errors)
            for k in ("width", "depth", "height"):
                if not _is_pos_number(node.get(k)):
                    errors.append("%s: '%s' must be a positive number (meters)." % (label, k))

        elif ntype == "sketch":
            _check_datum(node, label, errors)
            profile = node.get("profile")
            if not isinstance(profile, list) or len(profile) == 0:
                errors.append("%s: 'profile' must be a non-empty array of primitives." % label)
            else:
                for j, prim in enumerate(profile):
                    _check_profile_prim(prim, "%s.profile[%d]" % (label, j), errors)

        elif ntype == "extrude":
            sk = node.get("sketch")
            prev = nodes[pos - 1] if pos > 0 else None
            if not isinstance(sk, str) or not sk:
                errors.append("%s: 'sketch' (id of a sketch node) is required." % label)
            elif seen.get(sk) != "sketch":
                errors.append("%s: 'sketch' must reference an earlier sketch node (got '%s')." % (label, sk))
            elif not (isinstance(prev, dict) and prev.get("id") == sk):
                # v0-exp: extrude consumes the ACTIVE sketch, so it must immediately follow its sketch.
                errors.append("%s: in v0-exp an extrude must immediately follow its sketch node '%s'." % (label, sk))
            op = node.get("operation")
            if op not in ("boss", "cut"):
                errors.append("%s: 'operation' must be 'boss' or 'cut' (got %r)." % (label, op))
            if op == "boss" and not _is_pos_number(node.get("depth")):
                errors.append("%s: 'depth' (positive meters) is required for a boss extrude." % label)
            if op == "cut" and not (node.get("through") is True
                                    or node.get("depth") in THROUGH_DEPTHS
                                    or _is_pos_number(node.get("depth"))):
                errors.append("%s: a cut needs 'through': true, depth 'through_all', or a positive blind depth." % label)

        elif ntype == "hole":
            ref = node.get("ref") or {}
            nf = ref.get("node_face") or {}
            tgt = nf.get("node")
            if not isinstance(tgt, str) or not tgt:
                errors.append("%s: ref.node_face.node (an earlier node id) is required." % label)
            elif seen.get(tgt) not in BODY_PRODUCERS:
                errors.append("%s: ref.node_face.node '%s' must reference an earlier box/extrude node." % (label, tgt))
            if nf.get("selector") not in FACE_SELECTORS:
                errors.append("%s: ref.node_face.selector %r unsupported in v0-exp (only %s)."
                              % (label, nf.get("selector"), sorted(FACE_SELECTORS)))
            if ref.get("position") not in POSITIONS:
                errors.append("%s: ref.position %r unsupported in v0-exp (only %s)."
                              % (label, ref.get("position"), sorted(POSITIONS)))
            if not _is_pos_number(node.get("diameter")):
                errors.append("%s: 'diameter' must be a positive number (meters)." % label)
            depth = node.get("depth")
            if depth not in THROUGH_DEPTHS:
                errors.append("%s: 'depth' must be 'through_all' in v0-exp (got %r)." % (label, depth))

        if nid:
            seen[nid] = ntype

    return errors


def _check_datum(node, label, errors):
    ref = node.get("ref")
    if ref is None:
        return  # default datum 'top'
    if not isinstance(ref, dict):
        errors.append("%s: 'ref' must be an object." % label)
        return
    datum = ref.get("datum", "top")
    if datum not in DATUMS:
        errors.append("%s: ref.datum '%s' must be one of %s." % (label, datum, sorted(DATUMS)))


def _check_profile_prim(prim, label, errors):
    if not isinstance(prim, dict):
        errors.append("%s must be an object." % label)
        return
    kind = prim.get("kind")
    if kind not in PROFILE_KINDS:
        errors.append("%s.kind '%s' unsupported in v0-exp (only %s)." % (label, kind, sorted(PROFILE_KINDS)))
        return
    if kind == "rectangle":
        for k in ("width", "height"):
            if not _is_pos_number(prim.get(k)):
                errors.append("%s: rectangle '%s' must be a positive number." % (label, k))
    elif kind == "circle":
        if not _is_pos_number(prim.get("diameter")):
            errors.append("%s: circle 'diameter' must be a positive number." % label)
