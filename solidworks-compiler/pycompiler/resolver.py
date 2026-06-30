"""Reference resolver — the make-or-break module. Semantic refs -> concrete selectors.

Resolves 'top_face' and 'center' against LIVE geometry using the existing read-only
analyze_model(faces) tool (ReadFaces/BuildFaceJson). This is index-based selection (ADR-033,
KNOWN-LIMITATIONS #6): the resolver returns a face INDEX that create_sketch(on_face, face_index)
selects directly — no fragile coordinate pick.

v0-exp definitions (deliberately narrow; B = general 3D->sketch-2D mapping is P1.5):
  - top_face: among PLANAR, upward-facing faces (normal . +Y >= 0.9), the one with the greatest
    centroid Y. Deterministic and independent of the actual extrude/cut direction (so it is not
    fooled by SolidWorks' direction quirks, KNOWN-LIMITATIONS #19). v0 has a single body, so
    "the top face of node n" == the body's top face.
  - center: the box is built CENTERED ON THE MODEL ORIGIN, so the origin projects onto the
    top-face plane at the face centre, and a sketch-on-face seeds its 2D origin there -> center
    maps to sketch (0, 0).
"""
import json

from .errors import FeatureError

_UP = (0.0, 1.0, 0.0)   # SolidWorks default "up" axis (+Y). v0-exp binds 'top' to global +Y.
_NORMAL_TOL = 0.9       # a face counts as "upward" when normal . up >= this
_TIE_EPS = 1e-6         # two upward faces this close in height => ambiguous


def resolve_top_face(port, state_version, node_id=None):
    """Return (face_index, info) for the semantic 'top' face. Raises FeatureError on
    REFERENCE_UNRESOLVED (no upward face / analyze failed) or REFERENCE_AMBIGUOUS (a tie)."""
    resp = port.execute("analyze_model", {"analysis_type": "faces", "name": ""}, state_version)
    if resp.get("status") != "COMPLETED":
        err = resp.get("error") or {}
        raise FeatureError("analyze_model(faces) failed: %s" % err.get("message"),
                           code="REFERENCE_UNRESOLVED", node_id=node_id, step="resolve_top_face")

    feats = (resp.get("cadState") or {}).get("features") or []
    if not feats:
        raise FeatureError("analyze_model(faces) returned no face data (is there a solid body?)",
                           code="REFERENCE_UNRESOLVED", node_id=node_id, step="resolve_top_face")
    try:
        data = json.loads(feats[0])
    except Exception as ex:
        raise FeatureError("could not parse analyze_model(faces) payload: %s" % ex,
                           code="REFERENCE_UNRESOLVED", node_id=node_id, step="resolve_top_face")

    candidates = []  # (centroid_y, face_index, face)
    for f in data.get("faces") or []:
        if not f.get("planar"):
            continue
        n = f.get("normal")
        p = f.get("point")
        if not (isinstance(n, list) and isinstance(p, list) and len(n) >= 3 and len(p) >= 3):
            continue
        dot = n[0] * _UP[0] + n[1] * _UP[1] + n[2] * _UP[2]
        if dot >= _NORMAL_TOL:
            candidates.append((p[1], int(f.get("i", -1)), f))

    if not candidates:
        raise FeatureError("no upward-facing planar face found — a 'top face' could not be resolved",
                           code="REFERENCE_UNRESOLVED", node_id=node_id, step="resolve_top_face")

    candidates.sort(key=lambda c: c[0], reverse=True)
    top_y, top_i, top_face = candidates[0]
    if top_i < 0:
        raise FeatureError("resolved top face has no valid index",
                           code="REFERENCE_UNRESOLVED", node_id=node_id, step="resolve_top_face")
    if len(candidates) > 1 and abs(candidates[1][0] - top_y) < _TIE_EPS:
        raise FeatureError("top face is ambiguous — multiple upward faces at the same height",
                           code="REFERENCE_AMBIGUOUS", node_id=node_id, step="resolve_top_face")

    return top_i, {"face_index": top_i, "centroid_y": top_y, "point": top_face.get("point")}


def resolve_center_on_face(face_info):
    """Resolve position 'center' to sketch-2D coords. v0-exp: origin-centered box -> sketch (0, 0).
    (General 3D point -> sketch-2D projection for non-origin faces is deferred to P1.5 / option B.)"""
    return (0.0, 0.0)
