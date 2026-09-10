import hashlib
import inspect
import json
import os
import sys
import uuid
from datetime import datetime, timezone
from typing import Annotated, Literal, Optional
from pydantic import Field
from mcp.server import MCPServer
from mcp.server.caching import CacheHint
from mcp.server.mcpserver.exceptions import ResourceError
from execution_client import call_tool, get_state, ensure_ready as _ensure_ready, ExecutionLayerError
from response_mapper import map_response
# NOTE: pycompiler is reached via `from ir_execution_port import run_feature_graph` imported
# LAZILY inside rebuild_from_ir and submit_feature_graph — a missing compiler tree degrades to a
# clean tool error instead of killing the whole MCP server at startup.

# P0.4 MCP hardening: discriminators are `Literal[...]` (invalid values rejected at the
# MCP schema level, before any REST/COM round-trip) and numeric params carry Pydantic
# `Field` constraints (units/ranges). Coordinates stay plain float (negative/zero are
# legitimate); open-ended selector strings (entity1_type, plane/material names) stay str
# so valid selectors are never rejected. Editing this file requires reconnecting the
# `solidworks` MCP server (no hot-reload — KNOWN-LIMITATIONS #4).

# Every tool is registered with `structured_output=False`. All 46 return a `str`, so the
# SDK's structured mirror is `{"result": "<the same string>"}` — the whole payload a second
# time, for zero added information (measured: exactly 2.00x on both SDKs). Switching it off
# halves the wire cost of every reader (the 33 KB `schema://feature-graph` read went over at
# 66 KB before this) and drops the per-tool `outputSchema` from the tools/list prompt. Restore
# it only for a tool that returns real structure, never for a `-> str` one.

# Cache hints for the resource surface (SEP-2549, protocol 2026-07-28). NOTE: `ttl_ms`/
# `cache_scope` are set PER METHOD at construction, NOT per resource — the high-level
# `@mcp.resource` decorator returns str/bytes, so an individual handler cannot carry its own TTL.
# One value therefore covers every resource. `scope="public"`: the recipe rules and contract
# schemas are byte-identical for every caller, with nothing authorization-dependent in them.
# ttl_ms=1h is deliberately shorter than it could be — recipe-usage.md is read FRESH from disk on
# every read so an edit goes live without a reconnect, and a long TTL would defeat that.
# MEASURED 2026-07-30, and it settles the matter: the host does NOT honor these hints. A marker
# appended to recipe-usage.md showed up in `recipe://usage/coverage` immediately, with no
# reconnect, on a URI already read minutes earlier in the same session — a TTL-honoring client
# would have served the stale copy. Good news (reads are genuinely fresh), but the caching win is
# ZERO. And no host setting can change the deeper limit: a cached body STILL enters the model's
# context on every read, so TTL caching can only save a round-trip, never tokens. The whole
# measured saving of this migration is the per-turn `tools/list` shrinkage. Hints stay declared —
# spec-correct, free, and a future host may use them.
_RECIPE_CACHE = CacheHint(ttl_ms=3_600_000, scope="public")  # 1 h
mcp = MCPServer(
    "solidworks-execution-adapter",
    cache_hints={
        "resources/read": _RECIPE_CACHE,
        "resources/list": _RECIPE_CACHE,
        "resources/templates/list": _RECIPE_CACHE,
    },
)

# Tracks state_version in memory. Starts at 0, updated after every response.
# Auto-resyncs from the execution layer on an INVALID_STATE_VERSION mismatch
# (e.g. the execution server was restarted after a rebuild).
_state_version: int = 0


def _next_operation_id() -> str:
    return str(uuid.uuid4())


def _update_state_version(response: dict) -> None:
    global _state_version
    sv = response.get("stateVersion")
    if sv is not None:
        _state_version = sv


def _is_state_mismatch(response: dict) -> bool:
    if response.get("status") != "FAILED":
        return False
    return (response.get("error") or {}).get("code") == "INVALID_STATE_VERSION"


def _call(tool_name: str, params: dict) -> str:
    """Send a tool call to the execution layer.

    If the server reports an INVALID_STATE_VERSION mismatch (it was restarted and
    its state_version reset while this adapter kept its old value), fetch the
    authoritative state_version and retry once with a fresh operation_id. This
    removes the need to restart the adapter after every execution-layer rebuild.
    """
    global _state_version
    response = call_tool(tool_name, _next_operation_id(),
                         _state_version, params)
    if _is_state_mismatch(response):
        _state_version = get_state()
        response = call_tool(
            tool_name, _next_operation_id(), _state_version, params)
    _update_state_version(response)
    return map_response(response)


# ---------------------------------------------------------------------------
# Tool: ensure_ready  (lifecycle / bootstrap — not a state-versioned CAD op)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def ensure_ready() -> str:
    """Bring the SolidWorks environment up and confirm it is ready to use.

    Starts the execution server if it isn't running, and launches SolidWorks if it is closed
    (just attaches if it's already open). Does NOT open or create any document — use
    open_new_part / create_drawing for that (so assembly/drawing workflows aren't forced into a
    part). Safe to call anytime; idempotent. Call this first at the start of a session, or
    whenever a tool fails with a connection / COM-attach error."""
    global _state_version
    body = _ensure_ready()
    if not body.get("comAttached"):
        raise RuntimeError(
            "SolidWorks is not ready | "
            f"launch_error={body.get('launchError') or body.get('ensureError')}"
        )
    sv = body.get("stateVersion")
    if sv is not None:
        _state_version = sv
    return (
        f"READY | server=UP | "
        f"com_attached={body.get('comAttached')} | "
        f"sw_launched={body.get('swLaunched')} | "
        f"active_document={body.get('activeDocument')} | "
        f"sw_version={body.get('swVersion')} | "
        f"state_version={body.get('stateVersion')}"
    )


# ---------------------------------------------------------------------------
# Tool: open_new_part
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def open_new_part(template_path: str = "") -> str:
    """Open a new SolidWorks part document."""
    params = {}
    if template_path:
        params["template_path"] = template_path
    return _call("open_new_part", params)


# ---------------------------------------------------------------------------
# Tool: open_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def open_document(file_path: str, as_assembly: bool = False) -> str:
    """Open an EXISTING document from disk (counterpart to open_new_part, which makes a BLANK doc).
    file_path: full path to the file.
      - Native .sldprt / .sldasm / .slddrw open directly.
      - Foreign .step / .iges / .x_t / NX .prt / .ipt / .CATPart import via a STAGED path: the
        CLASSIC translator (LoadFile4) first — proven for STEP/IGES and NX .prt — with a
        3D-Interconnect retry as the last resort. A clear OPEN_FAILED means no usable translator:
        convert to STEP or defer that file.
      - as_assembly=True (foreign files only): the file is an ASSEMBLY STEP — the classic import
        would flatten it to a MULTIBODY PART; this asks for a real assembly import instead.
    Makes the opened document active and bumps state_version."""
    params = {"file_path": file_path}
    if as_assembly:
        params["as_assembly"] = True
    return _call("open_document", params)


# ---------------------------------------------------------------------------
# Tool: open_new_assembly  (Phase B, ADR-047)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def open_new_assembly(template_path: str = "") -> str:
    """Open a new blank SolidWorks ASSEMBLY document (the twin of open_new_part).
    Use before insert_component / add_mate. template_path: optional .asmdot template
    (default: the user-configured assembly template)."""
    params = {}
    if template_path:
        params["template_path"] = template_path
    return _call("open_new_assembly", params)


# ---------------------------------------------------------------------------
# Tool: analyze_assembly  (Phase B read tool, ADR-047 B1)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def analyze_assembly(
    analysis_type: Literal["components", "components_flat", "mates", "faces", "edges"],
    component: str = "",
) -> str:
    """Analyze the active ASSEMBLY document (read-only, does NOT change state).
    analysis_type='components': top-level component occurrences in FEATURE-TREE order — per
        instance: name (instance-numbered, e.g. 'Shaft-1'), source file path, config, fixed /
        suppressed flags, children count (a subassembly — out of scope, recorded as a gap), and
        the FULL transform (13 numbers: 3x3 rotation row-major + translation in meters + scale)
        — the position ground truth for round-trip verification.
    analysis_type='components_flat': LEAF occurrences across ALL nesting levels (depth-first
        tree order, transforms in ROOT space, each with its parent's name) — flattens a
        wrapper/subassembly level for ground-truth acquisition; the IR itself stays flat.
    analysis_type='mates': all mates in CREATION order — type and alignment from the SolidWorks
        ENUMS (locale-proof), the SI value for distance/angle mates, and per mate entity: owning
        component, entity kind (plane/cylinder/circle/...), and its geometric params in ASSEMBLY
        space (location + direction + radii).
    analysis_type='faces' / 'edges': ONE component's face/edge list with stable indices — feeds
        add_mate's index-based entity selection. Requires `component` (the Name2 from
        'components'). Coordinates are COMPONENT-LOCAL (the payload carries the transform)."""
    params = {"analysis_type": analysis_type}
    if component:
        params["component"] = component
    return _call("analyze_assembly", params)


# ---------------------------------------------------------------------------
# Tool: insert_component  (Phase B build tool, ADR-047 B3)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def insert_component(
    file_path: str,
    x: float = 0.0,
    y: float = 0.0,
    z: float = 0.0,
    config: str = "",
    fixed: bool = False,
    transform_json: str = "[]",
) -> str:
    """Insert a part file as a component of the ACTIVE assembly (open_new_assembly first).
    file_path: the .SLDPRT to insert (meters for all coordinates).
    x/y/z: insertion point — used when transform_json is not given.
    transform_json: OPTIONAL full placement as a JSON array STRING of 13 numbers
        (3x3 rotation row-major + translation xyz + scale — exactly the layout
        analyze_assembly(components) reports). Needed for rotated placements.
    fixed: True fixes the occurrence (its transform becomes authoritative); False leaves it
        floating for mates to position. The state is applied explicitly either way.
    Returns the runtime component name (e.g. 'Shaft-2') + fixed/transform readbacks."""
    params = {"file_path": file_path, "x": x, "y": y, "z": z, "fixed": fixed}
    if config:
        params["config"] = config
    if transform_json and transform_json != "[]":
        params["transform_json"] = transform_json
    return _call("insert_component", params)


# ---------------------------------------------------------------------------
# Tool: add_mate  (Phase B build tool, ADR-047 B3)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_mate(
    mate_type: Literal["coincident", "concentric", "perpendicular", "parallel",
                       "tangent", "distance", "angle", "lock"],
    a_component: str,
    a_index: int,
    b_component: str,
    b_index: int,
    a_kind: Literal["face", "edge"] = "face",
    b_kind: Literal["face", "edge"] = "face",
    alignment: Literal["aligned", "anti_aligned", "closest"] = "closest",
    flip: bool = False,
    value: float = 0.0,
) -> str:
    """Add a mate between two component entities in the ACTIVE assembly.
    Entity selection is INDEX-based (robust — coordinate picks miss real geometry): each side is
    (component name from analyze_assembly(components), kind 'face'|'edge', index from
    analyze_assembly(faces|edges, component=...)).
    value: METERS for distance mates, DEGREES for angle mates (ignored otherwise).
    alignment 'closest' lets SolidWorks pick the nearer solution; the mates reader reports the
    RESOLVED alignment afterwards.
    Returns the mate name + both components' post-rebuild transforms — compare against the
    original's readback after EVERY mate (the stop-check discipline)."""
    params = {"mate_type": mate_type, "alignment": alignment,
              "a_component": a_component, "a_kind": a_kind, "a_index": a_index,
              "b_component": b_component, "b_kind": b_kind, "b_index": b_index}
    if flip:
        params["flip"] = True
    if mate_type in ("distance", "angle"):
        params["value"] = value
    return _call("add_mate", params)


# ---------------------------------------------------------------------------
# Tool: save_body_as_part  (Phase B — flattened-assembly reconstruction, ADR-048)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def save_body_as_part(body_index: int, file_path: str) -> str:
    """Extract ONE solid body of the ACTIVE multibody part into its own .SLDPRT file.
    body_index: from analyze_model(analysis_type='bodies'). The body's geometry stays in the
    SOURCE coordinates (recover the instance transform separately); the new part is saved to
    file_path and closed, and the source document is re-activated. Use for flattened assembly
    imports whose per-part files don't exist: one file per DISTINCT body (fingerprint-deduped),
    then instances reference it with recovered transforms."""
    return _call("save_body_as_part", {"body_index": body_index, "file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: create_sketch
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_sketch(
    plane: str = "",
    on_face: bool = False,
    face_index: int = -1,
    face_x: float = 0.0,
    face_y: float = 0.0,
    face_z: float = 0.0,
) -> str:
    """Create a new sketch on a plane OR on an existing model face.
    Default (on_face=False): provide plane (e.g. 'Front Plane', 'Top Plane', or a named ref plane like 'Plane1').
    On a model face (on_face=True), pick the face one of two ways:
      • face_index (PREFERRED) — the integer index of the target face from analyze_model(analysis_type='faces').
        Robust: it selects the face directly, so it works on a revolve end-cap / shaft end where a coordinate
        pick is ambiguous (the flat circular face's centre lies on the revolve axis → FACE_NOT_FOUND).
      • face_x/face_y/face_z — a point in METERS lying ON the target planar face (interior, NOT on an edge).
    Use on_face for features on existing geometry (recess/hub/keyway on a part face, a bore on a shaft end);
    plane names cannot reach model faces. face_index takes priority over face_x/y/z when >= 0."""
    params = {"plane": plane, "on_face": on_face,
              "face_x": face_x, "face_y": face_y, "face_z": face_z}
    if face_index >= 0:
        params["face_index"] = face_index
    return _call("create_sketch", params)


# ---------------------------------------------------------------------------
# Tool: add_sketch_entity
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_sketch_entity(
    entity_type: Literal["rectangle", "circle", "line", "arc", "arc_center", "ellipse", "spline", "fillet", "chamfer"],
    x1: float = 0.0,
    y1: float = 0.0,
    x2: float = 0.0,
    y2: float = 0.0,
    xm: float = 0.0,
    ym: float = 0.0,
    cx: float = 0.0,
    cy: float = 0.0,
    radius: float = 0.0,
    vx: float = 0.0,
    vy: float = 0.0,
    distance: float = 0.0,
    direction: float = 0.0,
    points: str = "[]",
    construction: bool = False,
) -> str:
    """Add a sketch entity to the active sketch.
    entity_type='rectangle':  uses x1,y1 (first corner) and x2,y2 (opposite corner).
    entity_type='circle':     uses cx,cy (center) and radius.
    entity_type='line':       uses x1,y1 (start) and x2,y2 (end).
    entity_type='arc':        uses x1,y1 (start), x2,y2 (end), xm,ym (mid-arc point) — a 3-point arc.
    entity_type='arc_center': uses cx,cy (exact center), x1,y1 (start), x2,y2 (end) and optional
                              direction. PREFER this over 'arc' whenever the exact centre/radius are
                              known: it guarantees the radius, and the 3-point fit is unreliable on
                              shallow or near-collinear arcs.
    entity_type='ellipse':    uses cx,cy (center), x1,y1 (a point on the MAJOR axis), x2,y2 (a point
                              on the MINOR axis).
    entity_type='spline':     uses points — a JSON array STRING of flat through-points
                              '[x1,y1,x2,y2,...]' (>= 2 points), round-tripped exactly.
    entity_type='fillet':     uses vx,vy (vertex to round) and radius.
    entity_type='chamfer':    uses vx,vy (vertex to cut) and distance.
    direction: only for 'arc_center' — sweep sense from start to end. OMIT IT (or pass 0) for the
               MINOR (<=180°) arc, which a corner round virtually always is; a wrong explicit sign
               SILENTLY draws the >180° complement. Pass +1 (CCW) or -1 (CW) only for a deliberate
               major sweep (a round-trip replays analyze's 'dir' verbatim).
    construction: True makes the entity CONSTRUCTION/reference geometry — it guides the profile but
               adds no edge. Mirrors analyze_model's per-segment 'construction' flag.

    This tool MIRRORS analyze_model 1:1 — every segment it emits (line, arc/circle, ellipse, spline,
    with cx/cy/x1/y1/x2/y2/radius/points/construction, and 'dir' on partial arcs) maps onto an
    entity_type here, so an analyzed sketch rebuilds without dropping or simplifying a curve.
    On COMPLETED the result carries result_geometry: what SolidWorks actually created, READ BACK
    rather than echoed — check the radius/endpoints against your intent before moving on.
    Rebuilding from exact coordinates: do NOT add coincident constraints between segments. Shared
    endpoint coordinates already close the profile, and a constraint that fails rolls the sketch back.
    All coordinates in METERS."""
    return _call(
        "add_sketch_entity",
        {
            "entity_type": entity_type,
            "x1": x1, "y1": y1, "x2": x2, "y2": y2,
            "xm": xm, "ym": ym,
            "cx": cx, "cy": cy, "radius": radius,
            "vx": vx, "vy": vy,
            "distance": distance,
            "direction": direction,
            "points": json.loads(points) if points else [],
            "construction": construction,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_sketch_entities (batch)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_sketch_entities(segments: str) -> str:
    """Add MANY sketch entities to the active sketch in ONE call (the batch form of
    add_sketch_entity — prefer this whenever a profile has more than ~3 segments).
    segments: a JSON array STRING of entity records, each record = add_sketch_entity's
    parameters for one entity, e.g.
      '[{"entity_type":"line","x1":0,"y1":0,"x2":0.05,"y2":0},
        {"entity_type":"arc_center","cx":0.05,"cy":0.005,"x1":0.05,"y1":0,"x2":0.055,"y2":0.005},
        {"entity_type":"circle","cx":0.02,"cy":0.02,"radius":0.004,"construction":true}]'
    Corner rounds / fillet arcs: use arc_center WITHOUT "direction" — the tool then draws the
    MINOR (<=180°) arc deterministically (an explicit wrong sign silently draws the 270°
    complement); pass direction ±1 only for a deliberate major arc.
    Supported entity_type values and their parameters are IDENTICAL to add_sketch_entity
    (rectangle/circle/line/arc/arc_center/ellipse/spline/fillet/chamfer; per-record
    'construction': true supported; one difference: a 'spline' record's "points" is a plain
    JSON array [x1,y1,x2,y2,...], not a nested string). Segments are created in array order —
    list a profile's segments in contour order with EXACT shared endpoints (no coincident
    constraints needed).
    NOT transactional: on the first failing record the call returns FAILED naming segments[i]
    and how many earlier records were created — those remain in the sketch.
    On COMPLETED, result_geometry echoes {segment_count, counts per entity_type} (compact —
    NO per-segment geometry echo; read back with analyze_model(sketch, name=...) if needed).
    All coordinates in document units (meters)."""
    return _call("add_sketch_entities", {"segments": json.loads(segments) if segments else []})


# ---------------------------------------------------------------------------
# Tool: add_sketch_constraint
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_sketch_constraint(
    constraint_type: Literal[
        "horizontal", "vertical", "coincident", "parallel",
        "perpendicular", "tangent", "equal", "midpoint",
    ],
    px1: float,
    py1: float,
    px2: float = 0.0,
    py2: float = 0.0,
    entity_type1: Literal["SKETCHSEGMENT", "SKETCHPOINT"] = "SKETCHSEGMENT",
    entity_type2: Literal["SKETCHSEGMENT", "SKETCHPOINT"] = "SKETCHSEGMENT",
) -> str:
    """Apply a geometric constraint to sketch entities in the active sketch.
    constraint_type: 'horizontal', 'vertical' — single entity (px1, py1 only).
    constraint_type: 'coincident', 'parallel', 'perpendicular', 'tangent', 'equal', 'midpoint' — requires two entities (px2, py2).
    px1/py1: point on the first entity. px2/py2: point on the second entity (two-entity constraints).
    entity_type1/2: 'SKETCHSEGMENT' (default) or 'SKETCHPOINT' for point-based constraints like coincident/midpoint.
    All coordinates in document units (meters)."""
    return _call(
        "add_sketch_constraint",
        {
            "constraint_type": constraint_type,
            "px1": px1, "py1": py1,
            "px2": px2, "py2": py2,
            "entity_type1": entity_type1,
            "entity_type2": entity_type2,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_dimension
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_dimension(px: float, py: float, value: Annotated[float, Field(gt=0, description="Dimension value in METERS (e.g. 0.05 = 50mm)")], label_offset_x: float = 0.0, label_offset_y: float = -0.015) -> str:
    """Apply a smart dimension to the sketch segment nearest to point (px, py).
    px/py should be on or very near the target segment — e.g. the midpoint of a line.
    label_offset_x/y control where the dimension label is placed relative to the segment point.
    Works for any sketch geometry (rectangles, polygons, complex profiles)."""
    return _call(
        "add_dimension",
        {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
            "label_offset_y": label_offset_y},
    )


# ---------------------------------------------------------------------------
# Tool: extrude_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def extrude_feature(
    depth: Annotated[float, Field(
        ge=0, description="Extrusion depth in METERS (required > 0 for boss/cut)")] = 0.0,
    feature_type: Literal["boss", "cut", "revolve", "sweep", "loft"] = "boss",
    angle: Annotated[float, Field(
        gt=0, le=360, description="Revolve angle in DEGREES")] = 360.0,
    axis_x1: float = 0.0,
    axis_y1: float = 0.0,
    axis_x2: float = 0.0,
    axis_y2: float = 0.001,
    path_sketch: str = "",
    profiles: str = "[]",
    reverse: bool = False,
    through: bool = False,
    up_to_face_index: int = -1,
    mid_plane: bool = False,
) -> str:
    """Extrude/feature the active sketch profile.
    feature_type='boss' (default): solid extrusion, requires depth.
    feature_type='cut': material removal, requires depth and existing solid body.
    feature_type='revolve': solid of revolution — angle in DEGREES (default 360 = full revolve); axis defined by axis_x1/y1 to axis_x2/y2 (midpoint of that segment selects the centerline).
    feature_type='sweep': sweeps profile along a path — requires path_sketch (name of the path sketch).
    feature_type='loft': lofts through multiple profiles — requires profiles (JSON array of sketch names, e.g. '[\"Sketch1\",\"Sketch2\"]').
    END CONDITION (boss/cut) — pick ONE; depth is required only for BLIND or mid_plane:
      through=True          through-all; depth ignored. Use it instead of guessing a depth.
      up_to_face_index >= 0 terminates exactly ON that model face (index from
                            analyze_model('faces'), same indexing as create_sketch face_index);
                            depth ignored. -1 (default) = off.
      mid_plane=True        SYMMETRIC about the sketch plane; depth is the TOTAL width.
    reverse (boss/cut): flip the direction — needed when a cut/boss sketched on a part FACE has its
        material on the opposite side from the default. DIRECTION IS THE SILENT ONE: a feature built
        on the wrong side leaves volume, area and topology identical (recipe R6b/R14), so bind the
        sketch plane to a face explicitly and read one coordinate back afterwards.
    Sheet metal: a 'cut' on a sheet-metal body (holes after a bend included) is handled
        automatically — Normal Cut, inserted before the Flat-Pattern; no special params.
    On COMPLETED the result carries result_geometry {volume, faces, edges} after the feature — verify
        the step from that rather than a separate analyze_model.
    Exits sketch mode automatically before executing."""
    return _call(
        "extrude_feature",
        {
            "depth": depth,
            "feature_type": feature_type,
            "angle": angle,
            "axis_x1": axis_x1, "axis_y1": axis_y1,
            "axis_x2": axis_x2, "axis_y2": axis_y2,
            "path_sketch": path_sketch,
            "profiles": profiles,
            "reverse": reverse,
            "through": through,
            "up_to_face_index": up_to_face_index,
            "mid_plane": mid_plane,
        },
    )


# ---------------------------------------------------------------------------
# Tool: create_rib
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_rib(
    thickness: Annotated[float, Field(gt=0, description="Rib thickness in METERS (e.g. 0.005 = 5mm)")],
    two_sided: bool = True,
    reverse_thickness_dir: bool = False,
    reverse_material_dir: bool = False,
    is_norm_to_sketch: bool = False,
) -> str:
    """Create a RIB from the ACTIVE sketch (consumes it, like extrude_feature). The rib profile
    is OPEN geometry — typically a single line (e.g. the diagonal between an L-bracket's legs);
    SolidWorks extends it to the surrounding walls and thickens it.
    thickness: rib thickness in METERS.
    two_sided: True (default) thickens symmetrically on both sides of the sketch; False = single
        side (then reverse_thickness_dir picks which side).
    reverse_material_dir: flip which side of the profile the rib material FILLS toward. Wrong
        direction is auto-recovered: if no feature results, the tool retries flipped (your
        explicit value is tried first).
    is_norm_to_sketch: False (default) = extrusion parallel to the sketch (the classic line-rib
        on a mid/symmetry plane); True = normal to the sketch.
    Draft is deliberately not exposed (grow on demand). On COMPLETED the result includes
    result_geometry {volume, faces, edges} — verify the step from this. Requires an existing
    solid body for the rib to attach to."""
    return _call(
        "create_rib",
        {
            "thickness": thickness,
            "two_sided": two_sided,
            "reverse_thickness_dir": reverse_thickness_dir,
            "reverse_material_dir": reverse_material_dir,
            "is_norm_to_sketch": is_norm_to_sketch,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_edge_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_edge_feature(
    feature_type: Literal["fillet", "chamfer"],
    radius_or_distance: Annotated[float, Field(gt=0, description="Fillet radius / chamfer FIRST-face setback (D1) in METERS (e.g. 0.01 = 10mm)")],
    edge_indices: str = "",
    ex: float = 0.0,
    ey: float = 0.0,
    ez: float = 0.0,
    edges_json: str = "[]",
    chamfer_type: Literal["distance_angle", "distance_distance"] = "distance_angle",
    angle: Annotated[float, Field(gt=0, lt=90, description="Chamfer angle in DEGREES (distance_angle mode only; default 45)")] = 45.0,
    distance2: Annotated[float, Field(ge=0, description="Chamfer SECOND-face setback (D2) in METERS (distance_distance mode only; must be > 0 there)")] = 0.0,
    chamfer_flip: bool = False,
) -> str:
    """Apply a 3D edge modifier to solid body edges (post-extrusion). Distinct from sketch fillet/chamfer.
    feature_type='fillet': rounds selected edge(s) with given radius_or_distance.
    feature_type='chamfer': cuts a chamfer on selected edge(s). Two modes via chamfer_type:
      - 'distance_angle' (default): radius_or_distance = setback (D1), angle = the chamfer angle in DEGREES
        (default 45 → the classic equal 45° chamfer). angle is ignored in the other mode.
      - 'distance_distance': radius_or_distance = first-face setback (D1), distance2 = second-face setback
        (D2, METERS, required > 0). A distance-distance chamfer is DIRECTIONAL — if D1/D2 land on the wrong
        faces (the chamfer leans the wrong way), set chamfer_flip=True to swap the sides (FlipDirection).
    PREFERRED edge selection: edge_indices — a JSON array of integer indices from analyze_model(analysis_type='edges'),
    e.g. '[3,5]'. This selects edges directly (no coordinate pick), so it works on crowded or CONCAVE edges
    (inner-corner / small-radius step edges) that a coordinate pick can't disambiguate. Takes priority over
    edges_json / ex-ey-ez.
    Fallback — single edge: ex/ey/ez (3D point on the edge); multiple edges: edges_json e.g. '[{\"ex\":0.05,\"ey\":0.05,\"ez\":0.05}]'.
    All coordinates/distances in METERS. Requires an active part document with an existing solid body."""
    params = {
        "feature_type": feature_type,
        "radius_or_distance": radius_or_distance,
        "ex": ex, "ey": ey, "ez": ez,
        "edges_json": edges_json,
    }
    if edge_indices:
        params["edge_indices"] = edge_indices
    if feature_type == "chamfer":
        params["chamfer_type"] = chamfer_type
        params["angle"] = angle
        params["distance2"] = distance2
        params["chamfer_flip"] = chamfer_flip
    return _call("add_edge_feature", params)


# ---------------------------------------------------------------------------
# Tool: create_drawing
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_drawing(model_path: str = "") -> str:
    """Open a new SolidWorks drawing document (A3 sheet, 1:1 scale).
    model_path: optional path to the part/assembly to reference. If omitted, an empty drawing is created.
    Note: add_drawing_view requires the referenced part to be saved to disk."""
    return _call("create_drawing", {"model_path": model_path})


# ---------------------------------------------------------------------------
# Tool: add_drawing_view
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_drawing_view(
    view_type: Literal["front", "top", "right", "isometric", "back", "bottom", "left"],
    pos_x: float = 0.1,
    pos_y: float = 0.1,
    scale: Annotated[float, Field(
        gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
    model_path: str = "",
    display_mode: Literal["", "hlv", "hlr", "wireframe", "shaded", "shaded_edges", "default"] = "",
) -> str:
    """Add a model view to the active drawing sheet.
    view_type: 'front', 'top', 'right', 'isometric', 'back', 'bottom', 'left'.
    pos_x/pos_y: position on the drawing sheet in meters.
    scale: view scale (default 1.0 = 1:1).
    model_path: path to the part file; if omitted, uses the first open part document.
    display_mode: view display style — 'hlv' (Hidden Lines Visible), 'hlr' (Hidden Lines Removed),
        'wireframe', 'shaded', 'shaded_edges', or 'default' (document default). Leave '' to apply the
        DRAFTING-CONVENTION default: orthographic views (front/top/right/back/bottom/left) become HLV
        (hidden lines shown for reference — but never dimension to them), isometric keeps the document
        default. Section views (add_section_view) are separate and show the cut, not hidden lines.
    Requires the part to be saved to disk (in-memory parts cannot be projected)."""
    return _call(
        "add_drawing_view",
        {"view_type": view_type, "pos_x": pos_x, "pos_y": pos_y,
            "scale": scale, "model_path": model_path, "display_mode": display_mode},
    )


# ---------------------------------------------------------------------------
# Tool: add_flat_pattern_view
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_flat_pattern_view(
    pos_x: float = 0.1,
    pos_y: float = 0.1,
    scale: Annotated[float, Field(
        gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
    model_path: str = "",
    config_name: str = "",
    hide_bend_lines: bool = False,
    flip_view: bool = False,
) -> str:
    """Add a FLAT PATTERN view of a SHEET-METAL part to the active drawing — the unfolded blank with
    bend lines/notes. Sheet metal is dimensioned on its FLAT PATTERN (blank size, hole positions,
    bend lines), not on the folded orthographic views, so use this rather than them; then call
    auto_dimension_drawing.

    pos_x/pos_y: position on the drawing sheet in meters.
    scale: view scale (default 1.0 = 1:1).
    model_path: path to the sheet-metal part; if omitted, uses the first open part document.
    config_name: configuration to flatten; if omitted, the part's active configuration is used
        (falling back to 'Default').
    hide_bend_lines: hide the bend lines in the flat pattern (default False).
    flip_view: flip the flat pattern view (default False).

    The part must be sheet metal (have a Flat-Pattern feature) and saved to disk, else returns
    FLAT_PATTERN_VIEW_FAILED. Bumps state_version; result_geometry echoes {view_name, config}."""
    return _call(
        "add_flat_pattern_view",
        {"pos_x": pos_x, "pos_y": pos_y, "scale": scale, "model_path": model_path,
            "config_name": config_name, "hide_bend_lines": hide_bend_lines,
            "flip_view": flip_view},
    )


# ---------------------------------------------------------------------------
# Tool: add_drawing_dimension
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_drawing_dimension(
    px: float,
    py: float,
    value: Annotated[float, Field(gt=0, description="Dimension value in METERS")],
    label_offset_x: float = 0.0,
    label_offset_y: float = -0.015,
) -> str:
    """Add a smart dimension to a drawing view segment nearest to point (px, py).
    Works the same way as add_dimension but operates in a drawing document context.
    px/py should be on or very near the target segment in drawing sheet coordinates (meters).
    Requires an active drawing document."""
    return _call(
        "add_drawing_dimension",
        {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
            "label_offset_y": label_offset_y},
    )


# ---------------------------------------------------------------------------
# Tool: auto_dimension_drawing
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def auto_dimension_drawing(
    all_views: bool = True,
    include_unmarked: bool = False,
    eliminate_duplicates: bool = True,
) -> str:
    """Transfer the MODEL's driving dimensions into the active drawing's views (SolidWorks 'Insert
    Model Items > Dimensions'). PREFER this over add_drawing_dimension's coordinate pick — the
    values come from the model's own parametric dimensions and are placed for you. Call it AFTER
    create_drawing + add_drawing_view(s), then verify with analyze_slddrw_test (dimension_count > 0,
    values matching the model).

    all_views: insert into all drawing views (True) or only the currently selected view (False). Default True.
    include_unmarked: also insert driving dimensions NOT marked for drawing (i.e. ALL driving dims), not just
        those flagged 'marked for drawing'. More complete but noisier. Default False — if a marked-only pass
        inserts 0 dimensions (the part's dims weren't marked for drawing), re-call with include_unmarked=True.
    eliminate_duplicates: avoid inserting the same model dimension into more than one view. Default True.

    Returns COMPLETED with result_geometry.inserted_count (the number of dimensions placed; 0 is a valid
    outcome). Requires an active drawing with at least one model view. Bumps state_version."""
    return _call(
        "auto_dimension_drawing",
        {"all_views": all_views, "include_unmarked": include_unmarked,
            "eliminate_duplicates": eliminate_duplicates},
    )


# ---------------------------------------------------------------------------
# Tool: auto_center_marks
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def auto_center_marks(
    include_slots: bool = True,
    extended_lines: bool = True,
) -> str:
    """Automatically place center marks (with extended centerlines) on every hole/slot in every
    model view of the active drawing (SolidWorks 'Auto Insert > Center Marks'). This is the robust,
    automatic way to add centerlines to a holed part's drawing — no coordinate picking. Call it
    after the views exist (create_drawing + add_drawing_view), typically alongside
    auto_dimension_drawing, for a difficulty-2-grade drawing of a bracket/flange with holes.

    include_slots: also add slot center marks/centerlines for linear & arc slots (not just round holes). Default True.
    extended_lines: draw the extended centerlines through each hole rather than a small cross. Default True.

    Returns COMPLETED with result_geometry.center_marks (total marks placed across all views; 0 if the
    part has no holes/slots). Bumps state_version. Requires an active drawing with model views."""
    return _call(
        "auto_center_marks",
        {"include_slots": include_slots, "extended_lines": extended_lines},
    )


# ---------------------------------------------------------------------------
# Tool: add_hole_callout
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_hole_callout(px: float, py: float) -> str:
    """Insert a hole callout (e.g. '4× Ø8 THRU') on the hole edge nearest the sheet point (px, py)
    in the active drawing. Coordinate-based selection — same fragile point pick as add_drawing_dimension
    (KNOWN-LIMITATIONS #6), so place px/py right on the hole's projected circular edge. For centerlines
    prefer auto_center_marks (robust/automatic); use this for explicit per-hole callouts where wanted.
    px/py: a point on the target hole's projected circle, in drawing sheet coordinates (meters).
    Requires an active drawing document. Bumps state_version."""
    return _call("add_hole_callout", {"px": px, "py": py})


# ---------------------------------------------------------------------------
# Tool: add_section_view
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_section_view(
    px: float,
    py: float,
    edge_x: Optional[float] = None,
    edge_y: Optional[float] = None,
    x1: Optional[float] = None,
    y1: Optional[float] = None,
    x2: Optional[float] = None,
    y2: Optional[float] = None,
    label: str = "A",
    flip: bool = False,
    scale: float = 0.0,
) -> str:
    """Create a SECTION VIEW that cuts the model so internal/blind features become visible, dimensionable
    edges. Use this when a feature's size can't be shown in a projected view — most importantly a BLIND
    POCKET's DEPTH (its floor is a hidden edge in an orthographic view, so auto_dimension_drawing can't
    place the depth).

    Two ways to define the cut (provide ONE pair-set):
    - EDGE mode (edge_x, edge_y): cut collinear with an existing straight edge already projected in
      a view — point at it.
    - LINE mode (x1,y1 → x2,y2): draw the cut line, for cutting THROUGH a feature's interior where
      no edge exists. It must fully cross the view through the feature.
    All coordinates are drawing sheet coordinates in meters (KNOWN-LIMITATIONS #6 — coordinate-based).

    px,py: where to place the section view (meters) — also picks which side the section projects toward.
    label: section label (default 'A' → 'SECTION A-A'). flip: reverse the cut/viewing direction.
    scale: optional section view scale; inherits the parent view's scale if 0/omitted.

    After the section lands, call auto_dimension_drawing or add_drawing_dimension to dimension the
    now-visible depth. Bumps state_version. Requires an active drawing with a parent model view. If it
    returns SECTION_VIEW_FAILED, the drawing state may be wedged by prior failed attempts — a fresh
    server/drawing state clears it."""
    params: dict = {"px": px, "py": py, "label": label, "flip": flip}
    for k, v in (("edge_x", edge_x), ("edge_y", edge_y),
                 ("x1", x1), ("y1", y1), ("x2", x2), ("y2", y2)):
        if v is not None:
            params[k] = v
    if scale and scale > 0:
        params["scale"] = scale
    return _call("add_section_view", params)


# ---------------------------------------------------------------------------
# Tool: export_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def export_document(format: Literal["STEP", "IGES", "STL", "PDF", "DWG", "DXF"], file_path: str) -> str:
    """Export the active SolidWorks document to a file. The document remains open after export.
    format: 'STEP', 'IGES', 'STL', 'PDF', 'DWG', 'DXF'.
    file_path: full output path including filename and extension (e.g. 'C:/output/part.step').
    Note: DWG and DXF require an active drawing document (use create_drawing first).
    PDF uses SolidWorks PDF export options. STEP/IGES/STL work with part or assembly documents.
    IGES must use a .igs extension (.iges is auto-corrected to .igs)."""
    # export does NOT change CAD state; the execution layer returns the same state_version.
    return _call("export_document", {"format": format, "file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: batch_export
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def batch_export(file_path_base: str, formats_json: str) -> str:
    """Export the active document to multiple formats in one call.
    file_path_base: output path without extension (e.g. 'C:/output/mypart').
    formats_json: JSON array of format strings (e.g. '[\"STEP\",\"STL\",\"PDF\"]').
    Each format is exported as file_path_base.<ext>. DWG/DXF skipped if active doc is not a drawing.
    Partial success is reported in the response Features list."""
    return _call("batch_export", {"file_path_base": file_path_base, "formats_json": formats_json})


# ---------------------------------------------------------------------------
# Tool: verify_state
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def verify_state() -> str:
    """Read and return the current CAD state without modifying it."""
    return _call("verify_state", {})


# ---------------------------------------------------------------------------
# Tool: close_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def close_document(save: bool = False, close_all: bool = False) -> str:
    """Close the active SolidWorks document. Set save=True to save before closing.
    close_all=True closes ALL open documents (incl. invisibly-loaded component docs that a
    per-document close loop can never reach), DISCARDING unsaved changes — use to fully reset
    the document space between assembly jobs."""
    params = {"save": save}
    if close_all:
        params["close_all"] = True
    return _call("close_document", params)


# ---------------------------------------------------------------------------
# Tool: save_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def save_document(file_path: str = "") -> str:
    """Save the active SolidWorks document to disk.
    file_path: full output path including the document extension
    (.sldprt for parts, .sldasm for assemblies, .slddrw for drawings).
    If omitted, saves in place (only works if the document was saved before).
    A part must be saved to disk before it can be referenced by a drawing view."""
    return _call("save_document", {"file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: analyze_model
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def analyze_model(
    analysis_type: Literal["mass_properties", "geometry", "bodies", "edges", "faces", "features", "sketch", "feature_map"],
    name: str = "",
    from_feature: str = "",
    to_feature: str = "",
    near: str = "",
    k: int = 0,
    axis: str = "",
) -> str:
    """Analyze the active SolidWorks part document (read-only, does NOT change state).
    analysis_type='mass_properties': returns volume, surface_area, center of gravity (cx, cy, cz) in document units.
    analysis_type='geometry': returns bodies, faces, edges, vertices counts of all solid bodies.
    analysis_type='bodies': PER-BODY fingerprints for multibody parts (e.g. a flattened assembly
        STEP import): volume, area, centroid, edge/vertex counts, and the body's face-index RANGE
        in the same enumeration 'faces' walks — segment the faces list per body with it.
    analysis_type='edges': returns a JSON object listing EVERY solid edge with its start/end/MIDPOINT 3D coords
        in meters (rounded to 6 decimals; closed/circular edges also report length) and a stable index `i`. Use
        the index `i` for add_edge_feature(edge_indices=...) — robust for crowded/concave edges — or a MIDPOINT
        for coordinate-based selection (e.g. edge_flange's ex/ey/ez). On a very large part (many hundreds of edges)
        this is the slowest mode; prefer 'features' for a general understanding. TARGETED (recommended when you
        already know roughly WHERE): pass near='[x,y,z]' (+ optional k, axis) to get only the k nearest edges to
        a point instead of the full dump — see near/k/axis below.
    analysis_type='faces': returns a JSON object listing EVERY solid face with a stable index `i`; planar faces
        also carry normal, area, and a representative on-plane point (meters, rounded to 6 decimals). Use the
        index `i` for create_sketch(on_face=True, face_index=i) — robust where a coordinate pick is ambiguous
        (e.g. a revolve end-cap / shaft end whose centre lies on the axis). TARGETED: pass near='[x,y,z]'
        (+ optional k, axis) for only the k nearest faces to a point — see near/k/axis below.
    analysis_type='features': the part's COMPACT RECIPE — this is the default "understand the part" read. Returns
        the ordered feature tree (name/type; suppressed shown only when true), each feature's driving dimensions
        (deduped, SI meters/radians, rounded to 6 decimals), a per-sketch SUMMARY (segment counts + an inline
        {cx,cy,r} for single-full-circle profiles), pattern semantics (instances / spacing_deg / distinct_instances
        / wraps), and equations / globals. Each sketch reports its plane as {ref, offset} where ref is the
        canonical English default plane ('Front Plane'/'Top Plane'/'Right Plane') and offset is the signed
        distance along its normal (offset 0 = that default plane itself; offset != 0 = a parallel face/plane
        at that height — sketch on the face there). Each extrude/cut reports extrude:{end (blind/through_all/
        ...), depth (blind only), reversed (present only when the direction is flipped)} so you know HOW it
        was built; for direction use the sketch's plane.offset (offset != 0 ⇒ a face, so a cut goes into the
        part). Features are listed in tree (history) ORDER — reproduce them in that exact order (order
        matters wherever features overlap). It does NOT dump every sketch segment's coordinates — that keeps the payload small
        and is enough to understand the part and build dimension/pattern variants. Use this first.
    analysis_type='sketch' (requires name=...): ONE sketch's FULL geometry — every segment's coordinates (rounded
        to 6 decimals) plus its plane. Use this only when you actually need exact sketch geometry (e.g. to reproduce
        an irregular profile); get the sketch name from the 'features' read first. name = the sketch feature name,
        e.g. 'Sketch2'.
    analysis_type='feature_map': per-feature geometry ATTRIBUTION — deterministically answers "which edges/faces
        did each feature act on?" (e.g. WHICH edge a fillet/chamfer was applied to — never guess an anchor). Walks
        the tree base→end with the rollback bar and diffs the topology between stops INSIDE the tool; returns one
        compact JSON {feature_count, map:[{feature, type, delta:{faces,edges,vertices}, consumed_edges:[{mid,len}],
        created_faces:[{point, normal?, planar, area}]}]} (6-decimal meters). consumed_edges = edges that existed
        BEFORE the feature and were consumed by it (both endpoints gone; merely trimmed neighbours excluded) — use
        a consumed edge's `mid` as the fillet/chamfer anchor `near` point (it references the PRE-feature geometry,
        exactly what the IR anchor needs). created_faces = genuinely new surfaces (trimmed existing planes excluded).
        Sketches/planes are listed without a delta; suppressed features are skipped. Optional from_feature/to_feature
        (tree names) limit the walk to a range. NON-DESTRUCTIVE: the rollback bar is restored to the end, nothing is
        saved — but it does rebuild the model feature-by-feature, so it is the slowest mode on big trees.

    near / k / axis (edges and faces modes ONLY — a TARGETED read instead of the full dump): when you already
        know approximately where the geometry is (the common build-time case — you almost always do), pass
        near='[x,y,z]' (a JSON string, meters) to return only the nearest entities to that point, each annotated
        with `dist` (meters) and still carrying its STABLE full-enumeration index `i` (so selection is unchanged).
        k (default 0 = no cap): keep only the k nearest. axis='[x,y,z]' (optional JSON string): keep only entities
        whose direction is ~parallel to axis — for faces that's the planar-face NORMAL (e.g. axis='[0,0,1]' → only
        faces on +Z planes), for edges the chord direction. The response adds total_edge_count/total_face_count so
        you know how many were filtered out. Omitting near preserves the exact historical full-dump behavior.
    Does NOT increment state_version."""
    return _call("analyze_model", {"analysis_type": analysis_type, "name": name,
                                   "from_feature": from_feature, "to_feature": to_feature,
                                   "near": near, "k": k, "axis": axis})


# ---------------------------------------------------------------------------
# Tool: analyze_drawing  (2026-07-27 — the SHIPPING drawing reader: DXF/DWG)
# ---------------------------------------------------------------------------
# The reader is a permanent module of the CAD-neutral planner layer (ADR-064): a drawing is design
# intent expressed in 2D, so it belongs beside the IR schema and the recipe, not in a research dir.
_CAD_PLANNER_PKG_DIR = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "cad-planner"))

# Imported EAGERLY, at startup, in the MAIN thread -- deliberately, and NOT lazily inside the tool
# like pycompiler is. ezdxf pulls in numpy, and loading numpy's C extensions from an MCP worker
# thread DEADLOCKS on Windows: the tool hung forever with no error and no traceback (diagnosed by
# faulthandler thread dump, 2026-07-27 -- stuck in importlib create_module for numpy._core.
# multiarray). A failure here must still never kill the server, so it degrades to a clean per-call
# error instead.
try:
    if _CAD_PLANNER_PKG_DIR not in sys.path:
        sys.path.insert(0, _CAD_PLANNER_PKG_DIR)
    import drawing as _draw
    _DXF_READER_IMPORT_ERROR = None
except Exception as _exc:                     # noqa: BLE001 - startup must survive anything
    _draw = None
    _DXF_READER_IMPORT_ERROR = f"{type(_exc).__name__}: {_exc} (pip install ezdxf)"


def _summarise_direct(art, summary, graph):
    """The DIRECT_BUILDABLE result: everything needed to JUDGE the build, and nothing else.

    The contour deliberately stays out. A real laser outline is scores of segments; showing it and
    then having the model echo it back into submit_feature_graph would cost the tokens twice over
    and invite a transcription error in between — which is the whole reason the lowering exists."""
    b, th = summary["blank"], summary["thickness"]
    cuts = ", ".join("%gx%g @ (%g,%g)"
                     % (c["bbox"][2] - c["bbox"][0], c["bbox"][3] - c["bbox"][1],
                        round((c["bbox"][0] + c["bbox"][2]) / 2, 2),
                        round((c["bbox"][1] + c["bbox"][3]) / 2, 2))
                     for c in b["cutouts"]) or "none"
    lines = [
        "DIRECT_BUILDABLE | sheet-metal flat pattern | %s | sha256 %s"
        % (art["source"]["file"], art["source"]["sha256"][:12]),
        "  blank      %g x %g mm | outer loop %d segments | cutouts: %s"
        % (b["size"][0], b["size"][1], b["outer_segments"], cuts),
        "  thickness  %g mm  (%s)   bend radius %g mm   K %g"
        % (th["value_mm"], th["source"], summary["bends"][0]["radius_mm"], summary["k_factor"]),
        "  bends      %d, each corroborated by its line's edge class:" % len(summary["bends"]),
    ]
    for r in summary["bends"]:
        x1, y1, x2, y2 = r["line"]
        lines.append("               %-4s %g deg R%g  line %s  midpoint (%g, %g)%s"
                     % (r["dir"], r["angle_deg"], r["radius_mm"], r["class"],
                        round((x1 + x2) / 2, 4), round((y1 + y2) / 2, 4),
                        "  [WARNING: in a closed loop]" if r["in_loop"] else ""))
    for r in summary["skipped_bends"]:
        lines.append("  SKIPPED    %s note at %s — %s (candidates %s). It will NOT be built."
                     % (r["dir"], r["at"], r["reason"], r["candidates"]))
    lines += [
        "  expected   blank area %g mm^2 -> V = %r m^3  (bending does not change it at K=0.5)"
        % (b["area_mm2"], summary["expected"]["volume_m3"]),
        "  graph      %d IR nodes: sketch + sheet_metal, then sketch + sketched_bend per direction"
        % len(graph["nodes"]),
        "  NEXT       mode='build' builds it | mode='ir' shows the graph first.",
        "             AFTER building, verify: analyze_model('mass_properties')",
        "             against the expected volume above, and read one bend face back — a mirrored",
        "             fold is invisible to volume, area and topology alike (recipe R14).",
    ]
    if summary["skipped_bends"]:
        lines.append("             A skipped bend is invisible to ALL of those (the blank volume is "
                     "unchanged): add it by hand or declare it as a gap (R15).")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# ADVISORIES — the third home (payload trim phase 5, 2026-09-07)
# ---------------------------------------------------------------------------
# Three channels carry what the model needs, and each costs differently: a tool docstring is paid
# EVERY session whatever the job, a recipe section once per job that fetches it, and a payload note
# only when the thing it warns about actually happens. So guidance that applies to ONE flag belongs
# on that flag: `reverse` used to spend a paragraph on "if you see printed_mismatch, do X" in every
# read, whether or not any dimension carried it.
#
# Computed HERE, in the adapter, deliberately NOT in the reader: the saved artifact stays pure DATA
# (the research pool is not polluted, ANALYSIS_VERSION does not move, the draw-dialect contract and
# gate 4 are untouched). Only the ANSWER gains a short advisory block.
#
# A closed set, one line each, emitted ONCE per condition with the places it fired.
def _advisories(art) -> str:
    """A short plain-text block naming the conditions this read actually raised. '' when none."""
    out = []
    views = art.get("views") or []

    scaled = [v["vid"] for v in views if v.get("scale_factor") is not None]
    if scaled:
        out.append("view scale_factor @ %s — this view is drawn at a DIFFERENT ratio from the "
                   "sheet; say which factor produced a number before you quote it."
                   % ", ".join(scaled))

    not_read = (art.get("sheet") or {}).get("not_read") or {}
    if not_read:
        out.append("not_read @ sheet (%s) — the reader saw these and did not emit them; a named "
                   "block you cannot account for is evidence that never reached you."
                   % ", ".join(sorted(not_read)))

    ang, lin = [], []
    for k, d in enumerate(art.get("dimensions") or []):
        if d.get("printed_mismatch"):
            (ang if d.get("kind") == "angular" else lin).append("dim[%d]" % k)
    if ang:
        out.append("printed_mismatch/ANGULAR @ %s — the value is a JUDGEMENT, not a reading; "
                   "corroborate it against the geometry or an ellipse ratio before building."
                   % ", ".join(ang))
    if lin:
        out.append("printed_mismatch @ %s — the scale, the arc match or the per-view factor is "
                   "wrong. Reading the flag is free; ignoring it is not." % ", ".join(lin))

    tier_b = ["%s/%s" % (v["vid"], lp.get("id"))
              for v in views for lp in (v.get("loops") or []) if lp.get("tier") == "B"]
    if tier_b:
        out.append("tier:B @ %s — this boundary was WALKED, not chained; check its area against a "
                   "stated length or width before you build on it." % ", ".join(tier_b))

    unpaired = [b for b in (art.get("bend_notes") or []) if b.get("unpaired")]
    if unpaired:
        out.append("unpaired bend x%d — the reader refused to guess, so those bends are NOT built. "
                   "Resolve them from the drawing or declare a gap; never split the difference."
                   % len(unpaired))
    in_loop = [b for b in (art.get("bend_notes") or [])
               if (b.get("bend_line") or {}).get("in_loop")]
    if in_loop:
        out.append("bend_line in_loop x%d — the match landed on OUTLINE geometry, and a bend line "
                   "belongs to no closed contour. Say so." % len(in_loop))

    vg = art.get("view_graph")
    if isinstance(vg, dict) and vg.get("solved") is False:
        out.append("view_graph.solved:false (%s) — the axes did not join; that joining is yours."
                   % vg.get("reason", "reason in the field"))

    if not out:
        return ""
    return "\nADVISORIES (%d) — only what actually fired in THIS read:\n  %s" % (
        len(out), "\n  ".join(out))


def _wire_json(obj) -> str:
    """The drawing payload, serialized as tightly as JSON allows.

    `json.dumps` defaults to ', ' and ': ' — two bytes of whitespace per comma and colon, which on
    an artifact made mostly of short numeric arrays is 18,854 B across the 10 samples (11 points of
    payload, measured). Nothing is lost: JSON whitespace is not data.
    """
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


@mcp.tool(structured_output=False)
def analyze_drawing(file_path: str, save_analysis: bool = True,
                    mode: Literal["auto", "ir", "build"] = "auto") -> str:
    """Read a 2D technical drawing — **.DXF or .DWG** — and either BUILD the part from it or hand
    you the evidence to build it yourself. This is the drawing→part front end: a real drawing
    arrives as DXF/DWG (a .SLDDRW is model-linked and nobody ships one), so start here for ANY
    "build the part from this drawing" job. A .DWG is converted automatically.

    **mode='auto' (default) returns ONE OF TWO SHAPES — the reader decides, not you:**

    (A) `DIRECT_BUILDABLE` — a short plain-text summary. Every decision this drawing needs is
        forced by the drawing itself (today: sheet-metal flat patterns), so the whole part has
        already been lowered to Feature Graph IR deterministically. You get the blank size, the
        thickness and its source, every bend with its edge-class corroboration, and the EXPECTED
        VOLUME — enough to judge the build — but NOT the contour, on purpose: a real outline is
        scores of segments and echoing it back would cost the tokens twice. Read it, then call
        `mode='build'`. Anything the reader could not attribute is listed as SKIPPED and will not
        be built; completing or declaring it is then yours (recipe R15).

    (B) `NOT_DIRECT | <reason>` followed by the WHOLE analysis — the drawing needs real reading
        (the normal path for machined parts); the reason names exactly which decision the drawing
        left open. You get sheet/frame, every view complete (boxes, size, geometry, loops with
        their `seq`, open chains), alignment, view_graph, every dimension, bend note and note, in
        one answer. Read it per get_recipe('reverse') and build with submit_feature_graph.

    mode='ir'    — the lowered Feature Graph IR, without building. For inspection or saving.
    mode='build' — lower and BUILD, through the same deterministic pycompiler as
                   submit_feature_graph. **CHANGES GEOMETRY and bumps state_version.** Opens a new
                   part. Re-derives from the same file, so it needs no cached graph — but verify
                   the sha256 in the summary is the file you looked at.

    ⮕ Before interpreting a single number below, call `get_recipe(section='reverse')` — the TOOL,
      not the `recipe://usage/reverse` resource, which most hosts give the model no way to read
      (ADR-077). It holds the reading discipline (projection standard, face binding, contour gaps,
      sheet-metal bend arithmetic) and the self-verification rules. When you then need the IR
      vocabulary, `get_recipe('feature_graph_schema', profile='part')` is the right shape for this
      job — it drops the assembly vocabulary and keeps every part and sheet-metal feature type.

    The artifact — WHAT EVERY FIELD MEANS. (The DECISIONS you make with them are the recipe's;
    read get_recipe('reverse') for those. Nothing is documented in both places.)
      sheet — dxf_version, units, **scale_factor** and the sheet box. `scale_factor` is ALREADY
        applied to every size/coordinate/dimension below, so all numbers are TRUE model mm and
        multiplying again builds a half- or double-size part. An ANGLE is never scaled (DIMLFAC is
        a length factor).
        `not_read` counts what the reader saw and did NOT emit, by entity class and — for an
        INSERT — by block name, e.g. {"HATCH": 5, "INSERT": {"WALZRICHTUNG": 1}}. Nothing vanishes
        silently: a named block you cannot account for is evidence that exists on the sheet and
        did not reach you.
      frame — {paper_box, title_block_top, primitives, circles[], note_count}: the border/title
        block cluster, which is never emitted as a view. A candidate is accepted ONLY when it
        CONTAINS every other cluster, so `paper_box: null` means this sheet has no frame and every
        cluster was a view candidate. A cluster in the title-block corner that no dimension points
        at arrives as `role: "frame_item"` (a projection symbol, a weld symbol) — furniture.
      frame_notes[] / notes[] — the sheet's text in reading order, `frame_notes` being the title
        block's. Text INSIDE inserted blocks (SW_NOTE, BIEGETEILE, a company stamp) is included,
        one entry per ROW, nested to any depth. The DWG route splits a row into per-glyph
        fragments that are re-joined left to right, so match on content, not on exact spacing.
      views[] — one per detected view: `vid`, `role`, `paper_box` (sheet coords, for reading the
        LAYOUT), `geom_box`, `size` [w,h] in true mm, `dropped_duplicates`? (exact duplicates the
        DWG route re-emitted), `scale_factor`? — PRESENT ONLY when this view is drawn at a ratio
        DIFFERENT from the sheet's (a detail or section labelled 'A-A 1 : 1' on a 1:10 sheet); its
        geometry and dimensions are already scaled with THAT factor.
        Then `geometry` {lines, arcs, circles, ellipses} in view-local
        true mm with the view's own bottom-left as origin. The EDGE CLASS is carried on every
        primitive as `c`: 'visible' (near-side edge) | 'hidden' (obscured — a real feature seen
        through material) | 'cut_line' (a section's cutting line, not part geometry) | 'center'
        (an axis).
        `lines` is the one array grouped BY that class instead of carrying it per record:
        {"visible": [[index, x1, y1, x2, y2], …], "hidden": […]}. THE FIRST NUMBER IS THE INDEX —
        the one a `seq` entry names — written in rather than implied by position, so ['l', 12, 1]
        is found by looking for the row starting with 12, in whichever group, and never by
        recomputing an offset. Indices run 0..n-1 across all the groups together.
        `ellipses` {cx, cy, rx, ry, rot (+ t1, t2, x1..y2 for an arc)} are PARAMETRIC — a circle
        on a plane tilted by θ projects with ry/rx = cos θ and rx its true radius, so a Ø10 hole
        on a 20° face arrives as rx 5 / ry 4.699, not as 48 line segments. A record carrying
        `n` + `fit` was recovered from an exploded fan of n segments (fit = max deviation, mm);
        one without is a real DXF ellipse.
        The sheet border/title block is NOT a view; a cluster INSIDE a view is that view's feature.
      views[].loops[] — the CLOSED contours, already chained: {id, class, role outer|inner, parent,
        area (mm^2, arc bulges exact), bbox, seq, tier?}. `seq` is [[code, index, dir], …]
        referencing that view's own geometry arrays — the contour IN ORDER, ready to transcribe as
        a sketch path profile. Traverse it as given; consecutive entries share an endpoint exactly.
        `tier` is PRESENT ONLY when it is "B", so the common case costs nothing. Absent = Tier A,
        strict chaining, which stops dead at any junction of three or more and therefore never
        guesses. "B" = planar face traversal, run ONLY on a view whose visible graph has no free
        end — with no dangling edge the figure is a closed planar subdivision and its faces are
        DEFINED by the angular order of the edges at each vertex, so walking it is a reading. It
        appears only where Tier A found nothing, and the direct-build path ignores it by design.
      views[].open_singles[] — [position, code, index] triples: the SINGLE-segment open chains,
        which are most of them. Nothing is abbreviated away — the id is 'O<position>', the class is
        that primitive's own `c`, the direction is 1 — and a chain whose id/class/direction are not
        all recoverable stays a full record in `open_chains` instead. Read the two together: a
        primitive named here is exactly as unattributed as one in `open_chains`.
      views[].open_chains[] — the MULTI-segment chains belonging to no contour: bend lines, centre
        lines, and (in an ortho view) silhouette fragments the chainer refused to guess through
        a T-junction.
      alignment[] — GRADED view pairs: shares='x' is a vertical projection pair, 'y' a horizontal
        one, and `grade` says how strong the evidence is. 'span' = the two share that axis's span
        edge-for-edge, the strongest. 'mid' = only the midpoints coincide — still a measurement,
        but silhouette ends may genuinely differ, and on a bend-note sheet a mid pair may relate
        the FLAT and the BENT state (two states of the part, not two projections). 'label' = a
        section caption ties the section view `b` to the view `a` carrying its cut line, with
        shares=null, so it survives a cross-scale section.
      view_graph — {solved, views:{vid: [h_axis, v_axis]}} when the pairs pin every view's two
        paper axes onto the part's three. TOPOLOGY ONLY, over ANONYMOUS axes ax0/ax1/ax2: which
        axis is depth and which view is "front" is the projection convention's SIGN, not a field.
        `solved: false` names why, and the joining is yours again.
      dimensions[] — `value` (TRUE), `kind` (linear|aligned|angular|diameter|radius|ordinate),
        `defpts` (the dimension's own reference points, sheet coords) and the owning `view`.
        `printed` is the string the CAD system actually DREW, taken from the dimension's own
        block, with `%%c`→Ø, `%%d`→°, `%%p`→± decoded (the symbol is evidence about what was
        dimensioned); `text` beside it is the raw undecoded override (e.g. '8x <>' = a count).
        The DWG→DXF route corrupts the stored measurement in two whole classes — an ANGULAR value
        comes back as 180+θ, a RADIUS loses its arc side — so the reader ARBITRATES: for radius and
        diameter the printed value wins outright, and for an angle it takes the candidate ≤ 180°.
        `printed_mismatch: true` marks what it could not settle.
        `kind_from: "printed"` marks a dimension whose TYPE the drawn symbol overruled — a plain
        linear dim drawn across a circle with a hand-typed Ø is re-emitted as `kind: "diameter"`.
        `measures` links a dimension to the primitive the reader matched it to.
      bend_notes[] — sheet metal: {dir UP|DOWN, angle_deg, radius, view} plus either `bend_line`
        (the line it annotates, that line's edge class, and `in_loop` — true would mean the match
        landed on outline geometry, which a bend line never is) or `unpaired` with the reason and
        the candidates. Direction comes from the NOTE; the class corroborates it.
      notes[] — other free text on the sheet (e.g. a bare '2 mm' thickness note, 'SECTION C-C'),
        text inside note blocks included.

    file_path: absolute path to the .DXF or .DWG.
    save_analysis (default True): also write `<name>.analysis-v<version>.json` beside the source —
        a research artifact for cross-part study, versioned by the analyzer. Same version
        overwrites. You never need to read it back: this answer already carries everything in it."""
    src = os.path.abspath(file_path)
    if not os.path.exists(src):
        return f"FAILED | FILE_NOT_FOUND | {src}"
    ext = os.path.splitext(src)[1].lower()
    if ext not in (".dxf", ".dwg"):
        return ("FAILED | UNSUPPORTED_TYPE | analyze_drawing reads .DXF or .DWG. "
                "A native .SLDDRW is a test-only path (analyze_slddrw_test).")
    # DWG is binary; the reader parses DXF only. SolidWorks opens the DWG as a drawing and exports
    # DXF — measured lossless for geometry, dimensions, DIMLFAC, linetypes and notes.
    dxf_path = src if ext == ".dxf" else os.path.splitext(src)[0] + ".dwg2dxf.dxf"

    def _load():
        """Convert (DWG) + read + save -> (art, cfg, None) | (None, None, error string)."""
        converted_from = None
        if ext == ".dwg":
            opened = _call_raw("open_document", {"file_path": src})
            if opened.get("status") != "COMPLETED":
                err = opened.get("error") or {}
                return None, None, f"FAILED | DWG_OPEN_FAILED | {err.get('code')}: {err.get('message')}"
            exported = _call_raw("export_document", {"format": "DXF", "file_path": dxf_path})
            _call_raw("close_document", {})      # the import is scratch — discard, never save
            if exported.get("status") != "COMPLETED":
                err = exported.get("error") or {}
                return None, None, f"FAILED | DWG_CONVERT_FAILED | {err.get('code')}: {err.get('message')}"
            converted_from = os.path.basename(src)
        if _draw is None:
            return None, None, f"FAILED | READER_UNAVAILABLE | {_DXF_READER_IMPORT_ERROR}"
        try:
            cfg = _draw.load_config()
            art = _draw.read(dxf_path, cfg)
        except Exception as exc:
            return None, None, f"FAILED | DXF_READ_FAILED | {type(exc).__name__}: {exc}"
        if converted_from:
            art["source"]["converted_from"] = converted_from
        if save_analysis:
            out = os.path.join(os.path.dirname(dxf_path),
                               "%s.analysis-v%s.json" % (os.path.splitext(os.path.basename(src))[0],
                                                         art["analysis_version"]))
            try:
                with open(out, "w", encoding="utf-8") as fh:
                    json.dump(_draw.wire_encode(art), fh, indent=1, ensure_ascii=False)
            except OSError:
                pass                              # the artifact is research-only, never the result
        return art, cfg, None

    art, cfg, err = _load()
    if err:
        return err
    full = _wire_json(_draw.wire_encode(art))

    # The GATE decides the result shape. The model must never be asked to choose between "give me
    # the analysis" and "just build it" BEFORE it has seen any geometry — that was the flaw in
    # making this an opt-in flag (ADR-064).
    try:
        verdict = _draw.assess(art, cfg)
    except Exception as exc:                      # noqa: BLE001 - a gate bug must not lose the read
        return f"NOT_DIRECT | GATE_ERROR | {type(exc).__name__}: {exc}\n{full}"

    if not verdict["direct"]:
        if mode in ("ir", "build"):
            return (f"FAILED | NOT_DIRECTLY_BUILDABLE | {verdict['reason']} | {verdict['detail']} — "
                    f"call analyze_drawing(mode='auto') and build with submit_feature_graph.")
        # ONE SHAPE, the whole analysis (payload phase 1, 2026-09-07). This used to be a SUMMARY
        # with the geometry pulled back per view, and the split was retired on its own measurement:
        # once the wire form landed, the complete artifact came out smaller on all 10 samples than
        # the summary plus the pulls it made necessary (f-3: 14.0 KB against 28.7).
        return (f"NOT_DIRECT | {verdict['reason']} | {verdict['detail']}\n"
                f"The whole analysis is below. Read it per get_recipe(section='reverse') and build "
                f"with submit_feature_graph.{_advisories(art)}\n{full}")

    try:
        graph = _draw.lower_flat_pattern(art, cfg, verdict)
    except Exception as exc:                      # noqa: BLE001
        return f"NOT_DIRECT | LOWERING_ERROR | {type(exc).__name__}: {exc}\n{full}"

    if mode == "ir":
        return _wire_json(graph)
    if mode == "auto":
        # The direct path reports its own skipped bends; the advisories add what the READ raised
        # (unread entities, a walked boundary, a mismatched dimension) — equally true when the
        # drawing turned out to be buildable without you.
        return _summarise_direct(art, verdict["summary"], graph) + _advisories(art)

    # mode == "build": the THIRD IR door — same _run_graph, same pycompiler (IR-ADR-005).
    s = verdict["summary"]
    text, ok, sv = _run_graph(graph, fresh_document=True)
    if not ok:
        return text
    tail = ["", "built from %s (sha256 %s) | state_version=%s"
            % (art["source"]["file"], art["source"]["sha256"][:12], sv),
            "VERIFY: analyze_model('mass_properties') should read V = %r m^3 "
            "(blank %g mm^2 x %g mm)." % (s["expected"]["volume_m3"], s["blank"]["area_mm2"],
                                          s["thickness"]["value_mm"]),
            "        then read one bend face back — volume, area and topology cannot see a "
            "mirrored fold (recipe R14)."]
    for r in s["skipped_bends"]:
        tail.append("NOT BUILT: %s bend at %s (%s). The blank volume is unchanged by it, so no "
                    "measurement will catch it — add it or declare it (R15)."
                    % (r["dir"], r["at"], r["reason"]))
    return text + "\n".join(tail)


# ---------------------------------------------------------------------------
# Tool: analyze_slddrw_test  (TEST/REFERENCE ONLY — was analyze_drawing until 2026-07-27)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def analyze_slddrw_test(include_geometry: bool = False, include_relations: bool = False) -> str:
    """TEST/REFERENCE TOOL — reads a NATIVE SolidWorks drawing (.SLDDRW) through COM.

    ⚠ NOT the shipping drawing→part path. A .SLDDRW is SLDPRT-linked (its views go blank if the part
      is gone) and nobody ships one — real inputs are DXF/DWG (and PDF). This tool survives for two
      jobs ONLY: (a) checking a drawing YOU produced (do its dimensions match the model?), and
      (b) generating COM ground truth to compare a DXF reader against. For a DXF/DWG job do NOT use
      this tool and do NOT apply its discipline — the fields below (measures, section, normal_axis,
      relations, extent, center_marks) are COM constructs that DO NOT EXIST in a DXF.

    ⮕ Reconstructing a part from a NATIVE SLDDRW (test/comparison work only)? The FROZEN
      reverse-reading discipline is deliberately NOT on the MCP surface (ADR-069) — it is the repo
      file `cad-planner/slddrw-testing-recipe-usage.md`. Read it from disk when this tool is the
      job; port a rule out of it into the active recipe only as a conscious decision (ADR-063).

    Read-only, does NOT change state. Requires an active NATIVE drawing document. All lengths are
    METERS (angles radians), 6 decimals. Returns {view_count, dimension_count, views:[…]}, and the
    FIRST view is the drawing SHEET.

    view — {name, type (swDrawingViewTypes_e int), scale, pos [x,y], dimensions[], section?}
    dimension — {name, value_si, diametric?, attached?, anchors?}. `diametric` true means Ø is DATA,
      not a guess. `anchors` are the dim's reference points in MODEL space; an anchor beyond the base
      body's depth signals a feature that extends past it (offset-plane boss/loft), not bad data.
    section (SECTION views only) — {parent_view, cut_normal (the cutting plane's MODEL-space normal,
      i.e. the viewing direction), axis? (that normal snapped to X/Y/Z when axis-aligned), frame
      {origin, xdir, ydir}, label?}. A 2D coord maps to 3D as p = origin + u*xdir + v*ydir.

    include_geometry (default False) — each view's projected 2D geometry as clean primitives:
      {lines[{x1,y1,x2,y2}], curves[{n,x1,y1,xm,ym,x2,y2,cx?,cy?,r?}] (partial arcs as start/mid/end
      + fitted centre), circles[{cx,cy,r}] (FULL circles, true centre+radius), frame{origin,xdir,ydir,
      normal_axis}, extent? }. Source IView.GetPolylines7; heavier payload, so use it when you need
      the SHAPE, not for a dimension check. Two rules that fail silently if broken:
      · `normal_axis` is the view's viewing direction as a SIGNED principal axis. A circle is the
        cross-section of a feature whose axis runs along it, so circles in views with DIFFERENT
        normal_axis are DIFFERENT features — never chain them into one by radius alone.
      · `extent` {axis:[min,max]} is the SERVER-computed model-space span, frame signs applied. Read
        it before any "no material beyond X" claim; never re-derive a span from raw 2D coordinates,
        because a frame's direction components can be negative.

    include_relations (default False; forces include_geometry) — additive deterministic enrichment;
      pass BOTH for a reconstruction. Ids are positional per read: vid 'v<i>', and within a view
      c<i>/a<i>/l<i> for circles/curves/lines. Every relation carries `source` (closed enum) and
      `residual` (max deviation), so "why is this concentric?" is answerable by reading the field.
      · relations — concentric {members,center,radii} (TRUST these centres, never re-derive),
        equal_diameter {members,r,centers} (same Ø at distinct centres = twin bores), tangent.
      · measures — per dimension, the primitive id(s) it measures. `unattached:true` means it
        resolved to NO primitive: a loud gap to close from geometry, never to drop.
      · center_marks / centerlines — which circles carry marks (on:[ids]), which segments run
        through which (through:[ids]). A view that cannot expose its centerlines says so
        ({centerlines_reported, unreadable:true}) instead of guessing."""
    return _call("analyze_slddrw_test", {"include_geometry": include_geometry,
                                         "include_relations": include_relations})


# ---------------------------------------------------------------------------
# Tool: get_selection
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def get_selection() -> str:
    """Report what the USER currently has selected in the SolidWorks GUI — the inverse of index-based selection.
    When the user clicks geometry in the SolidWorks window (e.g. while telling you what to do — "put a hole on
    THIS face"), call this to learn exactly which entity they mean. Each selected item is mapped to the SAME
    stable index analyze_model(faces/edges) reports, so you can act on it immediately:
      • a FACE  → {type:'face', i, planar, normal?, area, point?} → create_sketch(on_face=True, face_index=i)
      • an EDGE → {type:'edge', i, start, end, mid}              → add_edge_feature(edge_indices='[i]')
      • a VERTEX → {type:'vertex', point:[x,y,z]};  a reference PLANE → {type:'plane', name}
    Returns {selected_count, selection:[...]}. i = -1 if a selected face/edge couldn't be matched to a
    solid-body index. Read-only: does NOT change state_version and does NOT clear the selection — but call it
    BEFORE any tool that clears the selection (create_sketch, add_edge_feature, extrude cut, etc.), otherwise
    the user's pick is gone. If selected_count is 0, ask the user to click the face/edge they mean."""
    return _call("get_selection", {})


# ---------------------------------------------------------------------------
# Tool: edit_sketch
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def edit_sketch(sketch_name: str) -> str:
    """Reopen an existing sketch for editing. Counterpart to create_sketch.
    sketch_name: exact name of the sketch feature in the feature tree (e.g. 'Sketch1').
    Returns COMPLETED with ActiveSketch set to sketch_name.
    After editing, call extrude_feature (or any feature) to exit the sketch."""
    return _call("edit_sketch", {"sketch_name": sketch_name})


# ---------------------------------------------------------------------------
# Tool: add_reference_geometry
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_reference_geometry(
    type: Literal["plane", "axis", "point"],
    ref_plane_name: str = "",
    offset: float = 0.0,
    entity1_name: str = "",
    entity1_type: str = "PLANE",
    entity2_name: str = "",
    entity2_type: str = "PLANE",
    px: float = 0.0,
    py: float = 0.0,
    pz: float = 0.0,
) -> str:
    """Create reference geometry (plane, axis, or point) in the active part.
    type='plane': offset plane from ref_plane_name by offset (meters). E.g. ref_plane_name='Front Plane', offset=0.05.
    type='axis': axis at intersection of two entities. Provide entity1_name, entity1_type, entity2_name, entity2_type (e.g. 'Top Plane'/'PLANE' and 'Right Plane'/'PLANE').
    type='point': reference point at vertex (px, py, pz). Coordinates must match an existing vertex exactly.
    Returns the created feature name (e.g. 'Plane1', 'Axis1', 'Point1')."""
    return _call(
        "add_reference_geometry",
        {
            "type": type,
            "ref_plane_name": ref_plane_name,
            "offset": offset,
            "entity1_name": entity1_name,
            "entity1_type": entity1_type,
            "entity2_name": entity2_name,
            "entity2_type": entity2_type,
            "px": px, "py": py, "pz": pz,
        },
    )


# ---------------------------------------------------------------------------
# Tool: create_pattern
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_pattern(
    pattern_type: Literal["linear", "circular", "mirror"],
    feature_name: str = "",
    spacing: Annotated[float, Field(
        gt=0, description="Linear pattern spacing in METERS")] = 0.01,
    count: Annotated[int, Field(
        ge=2, description="Total instances incl. seed (>= 2)")] = 2,
    direction: Literal["X", "Y", "Z"] = "X",
    count2: Annotated[int, Field(
        ge=1, description="Second-direction instances (1 = off)")] = 1,
    spacing2: Annotated[float, Field(
        gt=0, description="Second-direction spacing in METERS")] = 0.01,
    flip: bool = False,
    axis_name: str = "",
    angle: Annotated[float, Field(
        gt=0, le=360, description="Circular pattern angle in DEGREES")] = 90.0,
    equal_spacing: bool = True,
    features_json: str = "[]",
    plane: str = "",
    geometry_pattern: bool = False,
) -> str:
    """Create a linear, circular, or mirror feature pattern.
    pattern_type='linear': repeats feature_name along direction ('X', 'Y', or 'Z') with spacing (meters) and count instances.
        flip=True patterns toward the NEGATIVE axis (default False = positive).
        Direction maps to default planes: X→Right Plane normal, Y→Top Plane normal, Z→Front Plane normal.
        KNOWN LIMIT: count2/spacing2 are accepted but the second direction has no D2 entity wired —
        two-direction grids do NOT work in one call; compose a grid as a pattern OF a pattern instead.
    pattern_type='circular': repeats feature_name around axis_name (reference axis, e.g. 'Axis1') with count instances.
        Create the axis first with add_reference_geometry(type='axis', ...).
        equal_spacing=True (default): angle is the TOTAL spread (use 360 for a full ring); count instances are
            evenly divided across it and NEVER overlap (count distinct == count).
        equal_spacing=False: angle is the spacing BETWEEN adjacent instances. count*angle can exceed 360, in which
            case later instances WRAP and overlap earlier ones, so the number of DISTINCT instances < count.
        Round-trip: analyze_model reports a CirPattern's instances, equal_spacing, spacing_deg plus
        the EFFECTIVE distinct_instances and a `wraps` flag. Either replay the stored form verbatim,
        or — when it wraps — use the equivalent full ring (count=distinct_instances, angle=360,
        equal_spacing=True). Both give the same geometry.
    pattern_type='mirror': mirrors one or more FEATURES about a plane. features_json = a JSON array of
        feature tree names, e.g. '["Edge-Flange1","Sketched Bend2"]' (feature_name works for a single
        feature). plane = the mirror plane name ('Right Plane' default; canonical English default-plane
        names work on any localization, or a created reference plane's name). geometry_pattern=True
        mirrors the geometry without solving each feature (faster; default False = SW default).
        Round-trip: analyze_model(features) reports a MirrorPattern's mirror:{plane, features} — replay
        those values verbatim. feature_name/spacing/count/direction/axis params are ignored for mirror.
    On COMPLETED the result includes result_geometry {volume, faces, edges} after the pattern — verify from
        this instead of a separate analyze_model.
    Returns the created pattern feature name."""
    return _call(
        "create_pattern",
        {
            "pattern_type": pattern_type,
            "feature_name": feature_name,
            "spacing": spacing,
            "count": count,
            "direction": direction,
            "count2": count2,
            "spacing2": spacing2,
            "flip": flip,
            "axis_name": axis_name,
            "angle": angle,
            "equal_spacing": equal_spacing,
            "features_json": features_json,
            "plane": plane,
            "geometry_pattern": geometry_pattern,
        },
    )


# ---------------------------------------------------------------------------
# Tool: set_part_material
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def set_part_material(
    material_name: str,
    library: str = "SolidWorks Materials",
) -> str:
    """Assign a material to the active part document. Applied to all configurations.
    material_name: exact library name (e.g. '1060 Alloy', 'AISI 1020', 'ABS') — must match the SOLIDWORKS Materials database exactly (e.g. '1060 Alloy', NOT 'Aluminum 1060 Alloy').
    library: material library name (default 'SolidWorks Materials').
    The applied material is visible in Mass Properties and the feature tree.
    Requires an active part document — not a drawing."""
    return _call("set_part_material", {"material_name": material_name, "library": library})


# ---------------------------------------------------------------------------
# Tool: sheet_metal_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def sheet_metal_feature(
    feature_type: Literal["base_flange", "edge_flange", "edge_flange_sketch", "edge_flange_finish",
                          "flat_pattern", "sketched_bend"],
    thickness: Annotated[float, Field(
        gt=0, description="Sheet thickness in METERS")] = 0.001,
    bend_radius: Annotated[float, Field(
        gt=0, description="Bend radius in METERS")] = 0.001,
    k_factor: Annotated[float, Field(
        gt=0, lt=1, description="Neutral-axis K-factor (0..1)")] = 0.5,
    ex: float = 0.0,
    ey: float = 0.0,
    ez: float = 0.0,
    flange_length: Annotated[float, Field(
        gt=0, description="Edge flange length in METERS; use >= 2*thickness+1mm (KNOWN-LIMITATIONS #11)")] = 0.02,
    angle: Annotated[float, Field(
        gt=0, le=180, description="Bend angle in DEGREES (edge_flange and sketched_bend; default 90)")] = 90.0,
    use_default_radius: bool = False,
    flip: bool = False,
    bend_position: Literal["centerline", "material_inside", "material_outside", "bend_outside"] = "centerline",
    fixed_face_index: int = -1,
    fixed_x: float = 0.0,
    fixed_y: float = 0.0,
    fixed_z: float = 0.0,
    reverse_thickness: bool = False,
    symmetric_thickness: bool = False,
    clear_profile: bool = True,
    edge_index: int = -1,
) -> str:
    """Create sheet metal features on the active part. Lengths METERS, angles DEGREES.

    'base_flange' — a sheet metal base from the ACTIVE sketch profile; exits sketch mode.
        thickness · bend_radius (default = thickness) · k_factor (0.5).
        THICKENING SIDE IS A BUILD DECISION, and it is silent when wrong: `reverse_thickness`
        thickens to the OPPOSITE side of the sketch plane, `symmetric_thickness` splits ±t/2.
        Rebuilding an analyzed part, replay the original's own flags (analyze_model(features)
        reports them on the SMBaseFlange) — they also set the sheet orientation later bends fold
        against, so a wrong side fails downstream and less clearly.
    'edge_flange' — a DEFAULT-profile (full-edge-width) flange on an existing sheet metal edge.
        Select by edge_index (from analyze_model(edges), PREFERRED — a coordinate pick can miss a
        real edge) or a point ex/ey/ez on it. flange_length · angle (90).
    'edge_flange_sketch' + 'edge_flange_finish' — the CUSTOM-profile flange, in two calls. The
        sketch call takes the attach edge (edge_index preferred, else ex/ey/ez), generates the
        edge-linked profile sketch (the API accepts ONLY a sketch it generated), clears it
        (clear_profile) and leaves it ACTIVE, echoing the sketch's MEASURED frame — express your
        profile in THAT frame. Draw with add_sketch_entity, then call the finish with the SAME
        edge (+ angle, bend_radius or use_default_radius, bend_position). The profile defines the
        outline, so flange_length is ignored.
    'sketched_bend' — bends the sheet about the bend LINE(S) in the ACTIVE sketch (draw them on a
        sheet face with create_sketch + add_sketch_entity first; the sketch must still be active).
        The side that stays PUT is the fixed face: fixed_face_index (from analyze_model(faces),
        PREFERRED) or fixed_x/y/z, a 3D point ON that face. angle (90) · bend_radius, or
        use_default_radius=True to take the sheet's default and ignore it · flip reverses the fold
        direction · bend_position 'centerline' (default) | 'material_inside' | 'material_outside' |
        'bend_outside', matching the `position` analyze_model(features) reports on an SM3dBend, so a
        recipe value replays as-is. ONE sketch with SEVERAL bend lines makes one feature with one
        bend per line.
    'flat_pattern' — unfolds every bend. No extra parameters; needs an existing base_flange.

    A wrong fold is expensive: there is no sketch-entity move/delete tool, so repairing one costs
    deleting the feature and re-creating it. And a mirrored fold is invisible to volume, area AND
    topology alike — read one bend face back after building (recipe R14)."""
    params = {
        "feature_type": feature_type,
        "thickness": thickness,
        "bend_radius": bend_radius,
        "k_factor": k_factor,
        "ex": ex, "ey": ey, "ez": ez,
        "flange_length": flange_length,
        "angle": angle,
        "use_default_radius": use_default_radius,
        "flip": flip,
        "bend_position": bend_position,
        "fixed_x": fixed_x, "fixed_y": fixed_y, "fixed_z": fixed_z,
        "reverse_thickness": reverse_thickness,
        "symmetric_thickness": symmetric_thickness,
        "clear_profile": clear_profile,
    }
    if fixed_face_index >= 0:
        params["fixed_face_index"] = fixed_face_index
    if edge_index >= 0:
        params["edge_index"] = edge_index
    return _call("sheet_metal_feature", params)


# ---------------------------------------------------------------------------
# Tool: modify_dimension
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def modify_dimension(
    name: str,
    value: float,
) -> str:
    """Change a named display dimension's value — the UNIVERSAL parametric edit (variant keystone).
    name: the dimension full-name exactly as analyze_model(features) reports it, e.g.
          'D1@Boss-Extrude1@Part.Part' or 'D1@Sketch1'.
    value: the new value in SI units — METERS for a length/distance, RADIANS for an angle
          (e.g. 0.036 = 36mm; 0.5236 = 30°). NOT mm/degrees.
    Workflow: analyze_model(analysis_type='features') → read a feature's dimension name + value (SI) →
          modify_dimension(name, new_value) → analyze again to confirm. Bumps state_version (geometry
          changes, unlike analyze). On COMPLETED the result carries result_geometry
          {dimension, requested, effective}: the value read back from SolidWorks AFTER rebuild — verify
          it matches your intent (an equation/relation may have driven it elsewhere). This is how you
          build variants (e.g. resize a boss, widen a slot, change a gear's tooth-spacing)."""
    return _call("modify_dimension", {"name": name, "value": value})


# ---------------------------------------------------------------------------
# Tool: edit_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def edit_feature(
    feature_name: str,
    action: Literal["suppress", "unsuppress", "delete", "rename"],
    new_name: str = "",
) -> str:
    """Structurally edit an existing feature by name (suppress / unsuppress / delete / rename).
    feature_name: exact feature-tree name, e.g. 'Boss-Extrude1', 'Cut-Extrude1', 'CirPattern1'
          (get the names from analyze_model(analysis_type='features')).
    action='suppress':   removes the feature (and anything dependent on it) from the build WITHOUT deleting it.
    action='unsuppress': brings a suppressed feature back into the build.
    action='delete':     permanently removes the feature (works for ANY feature type, incl. sketches and reference geometry).
    action='rename':     renames the feature; requires new_name.
    new_name: required only for action='rename' (ignored otherwise).
    Bumps state_version. WARNING: suppress and delete CHANGE the model topology, so any coordinate /
    edge / face selection captured earlier may no longer resolve — re-run analyze_model
    (analysis_type='edges' or 'features') after a structural edit before selecting geometry again."""
    return _call(
        "edit_feature",
        {"feature_name": feature_name, "action": action, "new_name": new_name},
    )


# ---------------------------------------------------------------------------
# Tool: activate_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def activate_document(title: str) -> str:
    """Switch the ACTIVE SolidWorks document to an already-open one by its title (e.g. 'gear' or
    'gear.SLDPRT'). Use this to read/compare another open part — e.g. activate the original, analyze_model
    it, then activate your copy — without OS window switching. Does NOT change geometry or state_version.
    The document must already be OPEN. Returns the now-active document title."""
    return _call("activate_document", {"title": title})


# ---------------------------------------------------------------------------
# RESOURCE surface: the IR-generation recipe + the two cad-planner contract JSONs
#
# These were the `get_recipe(section=...)` tool until 2026-07-30 (ADR-069). They are now MCP
# *resources*: the content is large, near-static, identical for every caller and re-read
# constantly — exactly what `resources/read` + the 2026-07-28 `ttlMs`/`cacheScope` hints are for.
# MEASURED reason to move (not estimated): `get_recipe` cost 2,369 chars of `tools/list` on EVERY
# turn (2.97% of the 47-tool surface; 1,928 of it the docstring), paid whether or not any recipe
# was read. A resource read costs ~50 chars MORE than the equivalent tool result (the
# `uri`+`mimeType` wrapper), so the win is entirely the per-turn surface — which is why the tool
# had to GO, not sit beside the resources.
#
# Shape decided with the user (2026-07-30):
#   - EVERY section is its own STATIC resource. Probe finding: templated URIs are readable but
#     appear in NO listing (`resources/list` shows static entries only), so a template-only
#     surface would be undiscoverable. Static-per-section makes ONE `resources/list` the manifest.
#   - `recipe://usage/{section}` stays as a TEMPLATE ALIAS so a slug guessed from the index
#     still resolves. Concrete resources win over templates in the SDK's resolution order, so
#     the static entries above are never shadowed by it.
#   - Version lives in the BODY only (`Version: 0.21.0`), never in the URI: bumps must not
#     break the URI pointers embedded in the tool docstrings below.
#   - The FROZEN `slddrw-testing-recipe-usage.md` pair (ADR-063) is deliberately NOT served —
#     it stays a repo file; port from it consciously if a rule there is wanted again.
# The recipe's PRIVATE full edition (`recipe.md`) is NEVER served — it carries lesson history and
# provenance, is gitignored, and only the PUBLIC `-usage` twin appears on this surface.
# ---------------------------------------------------------------------------
_CAD_PLANNER_DIR = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "cad-planner"))

# The served section slugs, EXPLICIT (this replaces get_recipe's `Literal[...]`). The surface is
# under the user's control by decision: a new `## slug — title` in recipe-usage.md does NOT
# publish itself as a resource until it is added here, deliberately.
_RECIPE_SECTIONS = (
    "contract", "canonicalization", "forward", "mapping", "mapping_part",
    "mapping_sheet_metal", "mapping_assembly", "verification", "reverse", "coverage",
)


def _recipe_sections():
    """Parse cad-planner/recipe-usage.md into its version header + {slug: (title, body)} by the
    '## slug — title' section headers. Read fresh on every call — the file ships with the server
    and is small; no caching keeps edits live without a reconnect."""
    path = os.path.join(_CAD_PLANNER_DIR, "recipe-usage.md")
    with open(path, "r", encoding="utf-8-sig") as fh:
        text = fh.read()
    sections = {}
    current = None
    for line in text.splitlines():
        if line.startswith("## ") and " — " in line:
            slug, _, title = line[3:].partition(" — ")
            current = slug.strip()
            sections[current] = [title.strip(), []]
        elif current is not None:
            sections[current][1].append(line)
    header = text.split("\n## ", 1)[0]  # the version/preamble block above the first section
    return header, {k: (t, "\n".join(body).strip()) for k, (t, body) in sections.items()}


def _recipe_section_text(section: str) -> str:
    """One section, rendered with its own `## slug — title` header restored.

    Read FRESH from disk on every call (via _recipe_sections), so editing recipe-usage.md goes
    live with no reconnect — only the registration metadata below is snapshotted at import."""
    _header, sections = _recipe_sections()
    if section not in sections:
        # MUST be ResourceError: the SDK re-raises ResourceError/MCPError verbatim but replaces any
        # other exception's message with a generic "Error creating resource from template <uri>"
        # (templates.py). Verified live — a plain ValueError left the caller with no idea what the
        # valid slugs were, and a model that guesses (e.g. 'drawing', which was in the retired
        # get_recipe enum but is not a section) needs the list to self-correct in one step.
        raise ResourceError(
            f"'{section}' is not a served recipe section. Served: "
            f"{', '.join(_RECIPE_SECTIONS)}. Read recipe://usage/index for the full surface.")
    title, body = sections[section]
    return f"## {section} — {title}\n\n{body}"


def _contract_json(filename: str) -> str:
    with open(os.path.join(_CAD_PLANNER_DIR, "contracts", filename),
              "r", encoding="utf-8-sig") as fh:
        return fh.read()


# ---------------------------------------------------------------------------
# The IR schema, as the MODEL needs it (payload trim, 2026-09-07)
# ---------------------------------------------------------------------------
# `feature-graph.schema.json` serves two audiences at once and only one of them is the model.
# Gate 3 (`pycompiler/tests/test_ir_schema_contract.py`) machine-diffs every ARRAY under
# `covered_subset` against ir_schema.py's frozensets, and the prose beside those arrays documents
# that contract for whoever maintains it. So the FILE cannot be trimmed — the trim happens HERE,
# at serve time, on the copy the model receives.
#
# Dropped for every profile (measured; none of it is model-facing):
#   _meta.status                                2,135 B — the version narrative (IR-ADR references,
#                                                         "do NOT freeze before the MAT v1 set
#                                                         arrives"): a developer's changelog.
#   covered_subset._note/._grammar_note/.assumptions
#                                               1,529 B — half of it gate 3's own machine-diff
#                                                         instructions ("every ARRAY in this block
#                                                         is diffed against ir_schema.py").
# The covered_subset ARRAYS are left untouched: ~1,400 B carrying the entire capability list, the
# highest-value bytes in the file.
_SCHEMA_DEV_ONLY_SUBSET_KEYS = ("_note", "_grammar_note", "assumptions")

# What a PROFILE drops on top of that. Two values, because only two of them mean anything:
# `part` keeps every feature type ON PURPOSE — a drawing job does not know it is sheet metal until
# R21 decides, so the sheet-metal vocabulary must stay reachable. (A 'sheet_metal' profile would
# therefore be byte-identical to 'part', and an 'assembly' profile identical to 'all'; offering
# either would advertise a filter that does not exist.)
_SCHEMA_PROFILE_DROPS = {
    "all": (),
    "part": ("assembly_types_v06",),
}


def _feature_graph_schema_text(profile: str = "all") -> str:
    """`feature-graph.schema.json` with the developer-only blocks removed, re-serialized compactly.

    Falls back to the raw file if it will not parse: a malformed schema must still reach the caller
    (an unreadable capability registry is a loud failure, not a silent empty one).
    """
    raw = _contract_json("feature-graph.schema.json")
    try:
        doc = json.loads(raw)
    except ValueError:
        return raw
    meta = doc.get("_meta")
    if isinstance(meta, dict):
        meta.pop("status", None)
    subset = doc.get("covered_subset")
    if isinstance(subset, dict):
        for key in _SCHEMA_DEV_ONLY_SUBSET_KEYS:
            subset.pop(key, None)
    for key in _SCHEMA_PROFILE_DROPS.get(profile, ()):
        doc.pop(key, None)
    return json.dumps(doc, ensure_ascii=False, indent=1)


# ---------------------------------------------------------------------------
# Tool: get_recipe  (RESTORED 2026-09-03, ADR-077 — reverses ADR-069's retirement)
# ---------------------------------------------------------------------------
# ADR-069 moved the recipe from a TOOL to 13 MCP resources on a measured saving of 2,146 chars of
# `tools/list` per turn. The measurement was right; the assumption underneath it was not. MCP has
# three primitives and only ONE of them is in the MODEL's action space:
#
#     tool      tools/call       <- the model can call this
#     resource  resources/read   <- the CLIENT calls this; the model needs a host-provided bridge
#     prompt    prompts/get      <- the user invokes this
#
# ADR-069 probed the host and found it DID bridge resources for the model, so the migration looked
# safe. That bridge is gone: neither this host nor the one running the isolated tests exposes any
# `ReadMcpResource`-style tool, so the recipe left the model's action space entirely. Two isolated
# reconstructions of f-3 both reported they could not read `recipe://usage/reverse` and proceeded
# on their own judgement — which is exactly the "a resource no host reads is WORSE than a tool"
# risk ADR-069 named and accepted. A server cannot push a resource at a model; the only handle we
# can put in a model's hand is a TOOL. So the tool comes back.
#
# The RESOURCES STAY. They are spec-correct, the user can attach them by hand, and a host that
# does bridge them still benefits. Both channels read the same `recipe-usage.md` on every call, so
# there is exactly one copy of the rules and no sync burden.
@mcp.tool(structured_output=False)
def get_recipe(
    section: Literal["index", "contract", "canonicalization", "forward", "mapping",
                     "mapping_part", "mapping_sheet_metal", "mapping_assembly", "verification",
                     "reverse", "coverage", "feature_graph_schema",
                     "analysis_artifact_schema"] = "index",
    profile: Literal["all", "part"] = "all",
) -> str:
    """The IR-generation recipe — REQUIRED READING before writing any Feature Graph IR
    (an artifact's `ir.graph`), reconstructing a part from a drawing, or producing a drawing
    meant for reconstruction. Serves the rules section-by-section so token cost stays
    proportional to the task.

    Call order for the ARTIFACT→IR flow: 'contract' + 'canonicalization' + 'mapping' first,
    then the vocabulary section matching the document ('mapping_part' / 'mapping_sheet_metal' /
    'mapping_assembly'), then 'verification' before labeling anything. 'forward' holds the
    INTENT→IR authoring discipline (grammar cheat-sheet, anchor design without an original,
    computed-expectation self-verification) — read it FIRST when writing a graph for
    submit_feature_graph from design intent. 'reverse' holds the DXF/DWG drawing→part
    reconstruction discipline (scale/DIMLFAC, edge classes, view clustering, projection
    standard, contour tiers, sheet-metal bend arithmetic) — read it FIRST when rebuilding a part
    from a 2D drawing, before you interpret a single number of `analyze_drawing`'s answer.

    section='index' (default): the version header + a one-line table of contents.
    section='feature_graph_schema': the Feature Graph IR schema / capability registry JSON —
        the node types and params the compiler accepts (what is NOT in it cannot be built).
        `profile='part'` drops the ASSEMBLY vocabulary from the answer (~2.4 KB) and is what a
        part or drawing→part job wants; every feature type stays, sheet metal included, because a
        drawing is not known to be sheet metal until you decide it (R21). `profile='all'`
        (default) is the whole registry.
    section='analysis_artifact_schema': the persistent analysis-artifact contract JSON
        (identity/hash, recipe, parameters, ir block + the formal 'verified' definition).
    The same rules are also published as `recipe://usage/*` MCP resources; use whichever your
    host actually lets you reach. Read-only; does NOT touch SolidWorks or state_version."""
    if section == "feature_graph_schema":
        return _feature_graph_schema_text(profile)
    if section == "analysis_artifact_schema":
        return _contract_json("analysis-artifact.schema.json")
    header, sections = _recipe_sections()
    if section == "index":
        toc = "\n".join(f"- {slug} — {title}" for slug, (title, _b) in sections.items())
        return (f"{header.strip()}\n\nSections (pass as `section`):\n{toc}\n"
                "- feature_graph_schema — the IR schema / capability registry (JSON)\n"
                "- analysis_artifact_schema — the analysis-artifact contract (JSON)")
    return _recipe_section_text(section)


@mcp.resource("recipe://usage/index", name="recipe_index", mime_type="text/markdown",
              description="START HERE for IR work: the recipe version header + every section URI. "
                          "Rules for turning an analysis artifact or a 2D drawing into a Feature "
                          "Graph IR.")
def _recipe_index() -> str:
    """The table of contents, generated from the file so it can never drift from what is served."""
    header, sections = _recipe_sections()
    lines = []
    for slug in _RECIPE_SECTIONS:
        title = (sections.get(slug) or ("(missing from recipe-usage.md)",))[0]
        lines.append(f"- recipe://usage/{slug} — {title}")
    return (
        f"{header.strip()}\n\n"
        f"Sections (read by URI):\n" + "\n".join(lines) + "\n\n"
        "Contracts:\n"
        "- schema://feature-graph — the IR schema / capability registry (JSON). What is NOT in\n"
        "  it cannot be built; it doubles as the capability list.\n"
        "- schema://analysis-artifact — the persistent analysis-artifact contract (JSON):\n"
        "  identity/hash, recipe, parameter table, ir block + the formal 'verified' definition.\n\n"
        "Reading order — ARTIFACT→IR: contract + canonicalization + mapping, then the vocabulary\n"
        "section matching the document (mapping_part / mapping_sheet_metal / mapping_assembly),\n"
        "then verification BEFORE labeling anything. INTENT→IR (no original part): forward.\n"
        "DXF/DWG DRAWING→PART: reverse."
    )


def _register_recipe_sections() -> None:
    """Register one STATIC resource per served section.

    Static (not template-only) because a templated URI appears in NO listing — `resources/list`
    carries concrete resources only — so this is what makes the rule set DISCOVERABLE: one
    `resources/list` call returns every section with its title as the description.

    Titles are snapshotted at import for the descriptions (the listing metadata is fixed at
    registration time); the BODIES stay fresh per read. A missing/unreadable recipe-usage.md
    degrades to a generic description instead of killing the MCP server at startup — the same
    stance as the lazily-imported compiler.
    """
    try:
        _header, sections = _recipe_sections()
    except OSError:
        sections = {}
    for slug in _RECIPE_SECTIONS:
        title = (sections.get(slug) or ("",))[0]
        reader = (lambda s: lambda: _recipe_section_text(s))(slug)
        reader.__name__ = f"_recipe_{slug}"
        mcp.resource(
            f"recipe://usage/{slug}",
            name=f"recipe_{slug}",
            mime_type="text/markdown",
            description=(f"IR recipe — {title}" if title
                         else f"IR recipe section '{slug}' (see recipe://usage/index)"),
        )(reader)


_register_recipe_sections()


@mcp.resource("recipe://usage/{section}", name="recipe_section", mime_type="text/markdown",
              description="Alias: any recipe-usage.md section by slug. The per-section URIs above "
                          "are the discoverable form; this only catches a slug read off the index.")
def _recipe_section_alias(section: str) -> str:
    return _recipe_section_text(section)


@mcp.resource("schema://feature-graph", name="feature_graph_schema",
              mime_type="application/json",
              description="The CAD-neutral Feature Graph IR schema AND capability registry — the "
                          "node types and params the deterministic compiler accepts. What is not "
                          "in it cannot be built. Read before authoring any ir.graph.")
def _feature_graph_schema() -> str:
    # Same trimmed copy the tool serves at profile='all' — one shape, whichever channel reaches
    # the model. A resource URI takes no arguments, so the resource is always the full registry.
    return _feature_graph_schema_text("all")


@mcp.resource("schema://analysis-artifact", name="analysis_artifact_schema",
              mime_type="application/json",
              description="The persistent per-file analysis-artifact contract "
                          "(.solidpilot/*.analysis.json): identity/hash, recipe, lifted parameter "
                          "table, ir block + the formal 'verified' definition.")
def _analysis_artifact_schema() -> str:
    return _contract_json("analysis-artifact.schema.json")


# ---------------------------------------------------------------------------
# Tool: save_analysis  (Phase A analysis pipeline — ADAPTER-ONLY, no C#; ADR-040)
# ---------------------------------------------------------------------------
ANALYSIS_SCHEMA_VERSION = "0.1.0-draft"  # cad-planner/contracts/analysis-artifact.schema.json


def _call_raw(tool_name: str, params: dict) -> dict:
    """Like _call() but returns the RAW ExecutionResponse dict (no string mapping, no raise).

    Used by adapter-side orchestration (save_analysis) that needs the structured payloads the
    analyze tools carry in `result_geometry`. Same one-shot resync-retry on INVALID_STATE_VERSION."""
    global _state_version
    response = call_tool(tool_name, _next_operation_id(), _state_version, params)
    if _is_state_mismatch(response):
        _state_version = get_state()
        response = call_tool(tool_name, _next_operation_id(), _state_version, params)
    _update_state_version(response)
    return response


def _analysis_items(response: dict) -> list:
    """Return the `cadState.features` list an analyze_model call carries its payload in.

    Payload shapes (C#-owned): 'features' mode = ONE item holding the whole recipe as a JSON
    string; 'mass_properties'/'geometry' modes = 'key=value' strings. Raises RuntimeError on a
    FAILED response (mirrors map_response's error discipline)."""
    if response.get("status") != "COMPLETED":
        err = response.get("error") or {}
        raise RuntimeError(f"{err.get('code')}: {err.get('message')}")
    return (response.get("cadState") or {}).get("features") or []


def _kv_dict(items: list) -> dict:
    """Parse ['volume=4,22503E-06', 'faces=12', ...] into a dict with real numbers.

    Numeric values may use a COMMA decimal separator (localized SolidWorks formatting);
    normalize to float, and to int for plain integer counts."""
    out = {}
    for item in items:
        if not isinstance(item, str) or "=" not in item:
            continue
        key, _, raw = item.partition("=")
        raw = raw.strip()
        try:
            num = float(raw.replace(",", "."))
            is_plain_int = raw.isdigit() or (raw.startswith("-") and raw[1:].isdigit())
            out[key.strip()] = int(num) if is_plain_int else num
        except ValueError:
            out[key.strip()] = raw
    return out


def _collect_parameters(node, out: list, feature_name: str = "") -> None:
    """Walk the features recipe and lift dimension entries into the flat named-parameter table.

    A dimension entry is a dict whose 'name' is a FULL dimension name (contains '@', e.g.
    'D1@Boss-Extrude1@Part1.Part' — the modify_dimension target) with a numeric value under
    'value_si' or 'value'. Tolerant by design: the exact recipe keys are C#-owned; anything that
    doesn't match the shape is simply not lifted."""
    if isinstance(node, dict):
        owner = node.get("name") if node.get("type") else None
        value = node.get("value_si", node.get("value"))
        name = node.get("name")
        if isinstance(name, str) and "@" in name and isinstance(value, (int, float)):
            out.append({"name": name, "value_si": value, "feature": feature_name})
            return
        for child in node.values():
            _collect_parameters(child, out, owner or feature_name)
    elif isinstance(node, list):
        for child in node:
            _collect_parameters(child, out, feature_name)


@mcp.tool(structured_output=False)
def save_analysis(file_path: str) -> str:
    """Analyze a part FILE and persist its analysis ARTIFACT — the entry tool of the analysis
    pipeline. Opens the part (activates it if already open), runs the standard reads (features
    recipe + mass_properties + geometry), computes the file's sha256, and writes
    `<folder>/.solidpilot/<filename>.analysis.json` per
    cad-planner/contracts/analysis-artifact.schema.json.

    The artifact is a CACHE of the file's state at analysis time: consumers must compare
    identity.source_hash against the current file and re-analyze on a mismatch. The `ir` block
    is left null here — the AI/IR pass fills it later: BEFORE writing an ir.graph, read the MCP
    resource `recipe://usage/index` for the mapping rules. The part is left OPEN and
    ACTIVE for follow-up work.

    file_path: absolute path of the .SLDPRT part OR .SLDASM assembly to analyze (drawing
        artifacts arrive with later pipeline steps). An assembly artifact's recipe holds the
        component tree (tree order, full transforms) + mates (creation order, enum types,
        entity anchors) + assembly mass properties.
    Returns a token-frugal summary (artifact path + counts) — the artifact JSON stays on disk;
    read it from there when the full content is needed."""
    src = os.path.abspath(file_path)
    if not os.path.isfile(src):
        return f"FAILED | FILE_NOT_FOUND | {src}"
    is_assembly = src.lower().endswith(".sldasm")
    if not src.lower().endswith(".sldprt") and not is_assembly:
        return ("FAILED | UNSUPPORTED_TYPE | save_analysis analyzes .SLDPRT parts and .SLDASM "
                "assemblies (drawing artifacts come with later pipeline steps)")
    with open(src, "rb") as fh:
        sha = hashlib.sha256(fh.read()).hexdigest()

    # Open (or activate, if it is already open under its title).
    opened = _call_raw("open_document", {"file_path": src})
    if opened.get("status") == "FAILED":
        activated = _call_raw("activate_document", {"title": os.path.basename(src)})
        if activated.get("status") == "FAILED":
            err = opened.get("error") or {}
            return f"FAILED | OPEN_FAILED | {err.get('code')}: {err.get('message')}"

    if is_assembly:
        return _save_assembly_analysis(src, sha)

    try:
        features_items = _analysis_items(_call_raw("analyze_model", {"analysis_type": "features", "name": ""}))
        mass_kv = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        geometry = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "geometry", "name": ""})))
    except RuntimeError as ex:
        return f"FAILED | ANALYZE_FAILED | {ex}"

    notes = []
    if not features_items:
        return "FAILED | NO_PAYLOAD | analyze_model(features) returned an empty payload"
    try:
        features = json.loads(features_items[0])
    except (ValueError, TypeError):
        features = {"raw": features_items}
        notes.append("features payload was not parseable JSON — stored raw (reader refinement pending)")

    mass = {
        "volume_m3": mass_kv.get("volume"),
        "surface_area_m2": mass_kv.get("surface_area"),
        "cg": {"x": mass_kv.get("cx"), "y": mass_kv.get("cy"), "z": mass_kv.get("cz")},
    }

    parameters: list = []
    _collect_parameters(features, parameters)

    # V1 relationships: drawing files next to the part whose stem is the part's stem, optionally
    # followed by a suffix word (e.g. 'part-1 drawing.SLDDRW' for 'part-1.SLDPRT').
    folder = os.path.dirname(src)
    stem = os.path.splitext(os.path.basename(src))[0].lower()
    drawings = sorted(
        os.path.join(folder, f) for f in os.listdir(folder)
        if f.lower().endswith(".slddrw")
        and (os.path.splitext(f)[0].lower() == stem
             or os.path.splitext(f)[0].lower().startswith(stem + " "))
    )

    artifact = {
        "identity": {
            "source_path": src,
            "source_filename": os.path.basename(src),
            "source_hash": "sha256:" + sha,
            "source_mtime": datetime.fromtimestamp(os.path.getmtime(src), timezone.utc).isoformat(),
            "schema_version": ANALYSIS_SCHEMA_VERSION,
            "analyzed_at": datetime.now(timezone.utc).isoformat(),
            "generator": {"kind": "deterministic"},
        },
        "document_type": "part",
        "recipe": {
            "features": features,
            "mass_properties": mass,
            "geometry": geometry,
            "bbox": None,
            "material": None,
            "equations": [],
        },
        "parameters": parameters,
        "ir": None,
        "relationships": {"drawings": drawings, "category": None, "cluster_signals": {}},
        "notes": notes + ["bbox/material/equations extraction pending refinement (A2 live pass)"],
    }

    out_dir = os.path.join(folder, ".solidpilot")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, os.path.basename(src) + ".analysis.json")
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(artifact, fh, ensure_ascii=False, indent=2)

    geo = geometry if isinstance(geometry, dict) else {}
    feature_count = len(features.get("features", [])) if isinstance(features, dict) else "?"
    return (f"COMPLETED | artifact={out_path} | features={feature_count} | "
            f"parameters={len(parameters)} | geometry={{bodies:{geo.get('bodies')},"
            f"faces:{geo.get('faces')},edges:{geo.get('edges')},vertices:{geo.get('vertices')}}} | "
            f"drawings_linked={len(drawings)} | hash=sha256:{sha[:12]}")


def _save_assembly_analysis(src: str, sha: str) -> str:
    """The .SLDASM branch of save_analysis (Phase B): components (tree order, full transforms)
    + mates (creation order, enum types, entity anchors) + assembly mass properties. Component
    source files get their own sha256 in relationships so staleness is loud per part
    (ADR-047b: the assembly references part FILES — their artifacts live separately)."""
    try:
        comp_items = _analysis_items(_call_raw("analyze_assembly", {"analysis_type": "components"}))
        mate_items = _analysis_items(_call_raw("analyze_assembly", {"analysis_type": "mates"}))
        mass_kv = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
    except RuntimeError as ex:
        return f"FAILED | ANALYZE_FAILED | {ex}"
    try:
        components = json.loads(comp_items[0]) if comp_items else {}
        mates = json.loads(mate_items[0]) if mate_items else {}
    except (ValueError, TypeError) as ex:
        return f"FAILED | PAYLOAD_UNPARSEABLE | {ex}"

    part_files = []
    seen_paths = set()
    for c in components.get("components", []):
        p = c.get("path")
        if not p or p in seen_paths:
            continue
        seen_paths.add(p)
        entry = {"path": p}
        if os.path.isfile(p):
            with open(p, "rb") as fh:
                entry["hash"] = "sha256:" + hashlib.sha256(fh.read()).hexdigest()
        else:
            entry["missing"] = True
        part_files.append(entry)

    artifact = {
        "identity": {
            "source_path": src,
            "source_filename": os.path.basename(src),
            "source_hash": "sha256:" + sha,
            "source_mtime": datetime.fromtimestamp(os.path.getmtime(src), timezone.utc).isoformat(),
            "schema_version": ANALYSIS_SCHEMA_VERSION,
            "analyzed_at": datetime.now(timezone.utc).isoformat(),
            "generator": {"kind": "deterministic"},
        },
        "document_type": "assembly",
        "recipe": {
            "components": components,
            "mates": mates,
            "mass_properties": {
                "volume_m3": mass_kv.get("volume"),
                "surface_area_m2": mass_kv.get("surface_area"),
                "cg": {"x": mass_kv.get("cx"), "y": mass_kv.get("cy"), "z": mass_kv.get("cz")},
            },
        },
        "parameters": [],
        "ir": None,
        "relationships": {"part_files": part_files, "category": None, "cluster_signals": {}},
        "notes": [],
    }

    out_dir = os.path.join(os.path.dirname(src), ".solidpilot")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, os.path.basename(src) + ".analysis.json")
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(artifact, fh, ensure_ascii=False, indent=2)

    return (f"COMPLETED | artifact={out_path} | components={components.get('component_count')} | "
            f"mates={mates.get('mate_count')} | part_files={len(part_files)} | hash=sha256:{sha[:12]}")


# ---------------------------------------------------------------------------
# Tool: rebuild_from_ir  (adapter-only — the analysis pipeline's IR door, IR-ADR-005)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def rebuild_from_ir(artifact_path: str, fresh_document: bool = True) -> str:
    """Rebuild a part from its analysis artifact's Feature Graph IR — the verification half of
    the round-trip ("the LLM proposes, the round-trip decides", IR-ADR-006). Reads
    `<...>.analysis.json`, takes `ir.graph`, and executes it through the SAME deterministic
    pycompiler as the forward door submit_feature_graph (two doors, ONE compiler — never forked).

    fresh_document (default True): open a NEW blank part first — the normal round-trip flow
        (rebuild fresh, then compare_parts against the original). Pass False only if you have
        already prepared the target document yourself.

    The rebuild reproduces the part AS-ANALYZED. If the source file changed since the artifact
    was written (hash mismatch) the result carries source_stale=true — re-run save_analysis and
    regenerate the IR rather than trusting a stale graph.

    Returns the compiler's per-node summary (COMPLETED n/n, or the feature-level error and how
    far it got — partial geometry may remain; CAD ops are not transactional). Afterwards, verify
    with compare_parts and only then label the artifact's ir.verification. If you are GENERATING
    the ir.graph yourself, read the MCP resource `recipe://usage/index` first — it indexes the
    mapping/canonicalization rules the compiler expects."""
    global _state_version
    path = os.path.abspath(artifact_path)
    if not os.path.isfile(path):
        return f"FAILED | ARTIFACT_NOT_FOUND | {path}"
    try:
        # utf-8-sig: tolerate a BOM — artifacts hand-edited or written by other Windows tools
        # (e.g. PowerShell 5.1 Out-File) may carry one; save_analysis itself writes without.
        with open(path, "r", encoding="utf-8-sig") as fh:
            artifact = json.load(fh)
    except (ValueError, OSError) as ex:
        return f"FAILED | ARTIFACT_UNREADABLE | {ex}"

    ir = artifact.get("ir") or {}
    graph = ir.get("graph")
    if not isinstance(graph, dict) or not graph.get("nodes"):
        status = (ir.get("verification") or {}).get("status")
        reason = ((ir.get("verification") or {}).get("detail") or {}).get("reason")
        return (f"FAILED | NO_IR_GRAPH | the artifact carries no executable ir.graph"
                f"{f' (verification: {status} | {reason})' if status else ''} — run the AI/IR "
                f"pass per cad-planner/recipe.md first")

    # Stale-source signal (cache discipline, ADR-040): informative, not blocking — the graph
    # legitimately rebuilds the part AS-ANALYZED.
    source_stale = ""
    src = (artifact.get("identity") or {}).get("source_path")
    recorded = (artifact.get("identity") or {}).get("source_hash")
    if src and recorded and os.path.isfile(src):
        with open(src, "rb") as fh:
            current = "sha256:" + hashlib.sha256(fh.read()).hexdigest()
        if current != recorded:
            source_stale = " | source_stale=true (file changed since analysis — regenerate the artifact)"

    text, ok, _sv = _run_graph(graph, fresh_document)
    return text + (f" | artifact={os.path.basename(path)}{source_stale}" if ok else "")


# ---------------------------------------------------------------------------
# Tool: compare_parts  (adapter-only — the objective round-trip verifier, ADR-040/A0)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def compare_parts(doc_a: str, doc_b: str, detail: str = "") -> str:
    """Objectively diff two part documents — the round-trip verifier behind the artifact's
    `verified` label (and a general-purpose "are these the same part?" check).

    doc_a / doc_b: EITHER an absolute .SLDPRT path (opened/activated from disk) OR the TITLE of
        an already-open document (e.g. 'Part4' for an unsaved rebuild). doc_a is the REFERENCE
        (deltas are relative to it — normally the original; doc_b = the rebuild).

    For each doc it runs analyze_model(geometry + mass_properties) and reports topology
    (bodies/faces/edges/vertices), volume, surface area and CG side by side with deltas, plus
    the DECIDED verified-criteria verdict (analysis-artifact.schema.json): topology EXACT AND
    |ΔV| <= 1% AND |ΔA| <= 1%. The verdict is a REPORT — writing ir.verification into the
    artifact stays the caller's job. Read-only geometry-wise (activation may switch the active
    document; doc_b is left active). bbox comparison: pending (analyze doesn't expose it yet).

    detail='faces' (optional): ALSO run analyze_model(faces) on both and list the surfaces that
        are EXTRA (in doc_b only) or MISSING (in doc_a only), matched by supporting plane (normal
        + offset) for planar faces and by (axis, radius) for cylinders. This is the ONE diagnosis a
        point-query can't do — it turns "which face did my rebuild add/drop?" into one call instead
        of manually pairing two full face lists. Faces are reported with their key attributes + the
        stable index `i` in their own document."""
    def _read(doc: str, label: str, want_faces: bool):
        if os.path.isfile(doc):
            r = _call_raw("open_document", {"file_path": os.path.abspath(doc)})
            if r.get("status") != "COMPLETED":
                r = _call_raw("activate_document", {"title": os.path.splitext(os.path.basename(doc))[0]})
        else:
            r = _call_raw("activate_document", {"title": doc})
        if r.get("status") != "COMPLETED":
            err = r.get("error") or {}
            raise RuntimeError(f"DOC_{label}_UNAVAILABLE | {doc} | {err.get('code')}: {err.get('message')}")
        geo = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "geometry", "name": ""})))
        mass = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        faces = None
        if want_faces:
            items = _analysis_items(_call_raw("analyze_model", {"analysis_type": "faces", "name": ""}))
            faces = json.loads(items[0]).get("faces", []) if items else []
        return geo, mass, faces

    want_faces = detail == "faces"
    try:
        geo_a, mass_a, faces_a = _read(doc_a, "A", want_faces)
        geo_b, mass_b, faces_b = _read(doc_b, "B", want_faces)
    except RuntimeError as ex:
        return f"FAILED | {ex}"

    topo_keys = ("bodies", "faces", "edges", "vertices")
    topo_a = [geo_a.get(k) for k in topo_keys]
    topo_b = [geo_b.get(k) for k in topo_keys]
    topology_exact = topo_a == topo_b and None not in topo_a

    def _delta_pct(a, b):
        if not isinstance(a, (int, float)) or not isinstance(b, (int, float)) or a == 0:
            return None
        return (b - a) / a * 100.0

    dv = _delta_pct(mass_a.get("volume"), mass_b.get("volume"))
    da = _delta_pct(mass_a.get("surface_area"), mass_b.get("surface_area"))
    cg_dist = None
    if all(isinstance(mass_x.get(k), (int, float)) for mass_x in (mass_a, mass_b) for k in ("cx", "cy", "cz")):
        cg_dist = ((mass_a["cx"] - mass_b["cx"]) ** 2 + (mass_a["cy"] - mass_b["cy"]) ** 2
                   + (mass_a["cz"] - mass_b["cz"]) ** 2) ** 0.5

    verified = (topology_exact and dv is not None and da is not None
                and abs(dv) <= 1.0 and abs(da) <= 1.0)

    fmt = lambda v, spec=".6g": ("?" if v is None else format(v, spec))  # noqa: E731
    line = (f"COMPLETED | verified_criteria={'PASS' if verified else 'FAIL'} | "
            f"topology A={'-'.join(str(t) for t in topo_a)} B={'-'.join(str(t) for t in topo_b)} "
            f"{'EXACT' if topology_exact else 'DIFFER'} | "
            f"volume A={fmt(mass_a.get('volume'))} B={fmt(mass_b.get('volume'))} dV={fmt(dv, '.4f')}% | "
            f"area A={fmt(mass_a.get('surface_area'))} B={fmt(mass_b.get('surface_area'))} dA={fmt(da, '.4f')}% | "
            f"cg_distance={fmt(cg_dist)} m | reference=A ({doc_a})")

    if want_faces:
        missing, extra = _diff_faces(faces_a, faces_b)
        line += (f"\nfaces: matched={len(faces_a) - len(missing)} "
                 f"MISSING(in A only)={len(missing)} EXTRA(in B only)={len(extra)}")

        def _list(faces, cap=20):
            shown = "; ".join(_face_desc(f) for f in faces[:cap])
            if len(faces) > cap:
                shown += f"; …(+{len(faces) - cap} more)"
            return shown
        if missing:
            line += "\n  MISSING: " + _list(missing)
        if extra:
            line += "\n  EXTRA:   " + _list(extra)
    return line


_FACE_POS_TOL = 5e-5   # 50µm — supporting plane offset / point match
_FACE_RAD_TOL = 5e-5   # 50µm — cylinder radius match


def _faces_compatible(fa: dict, fb: dict) -> bool:
    """True if two faces (from analyze_model(faces) JSON) are the SAME SURFACE within tolerance —
    matched by supporting geometry, not by trim/area. Planar → same unit normal (sign-canonicalized)
    AND same signed offset from origin (±50µm). Cylinder → parallel axis AND same radius (±50µm).
    Other → same kind AND representative point within 50µm. This is deliberately looser than the
    <=1% verified gate: a face-DIFF looks for surfaces one part has and the other doesn't, so
    sub-µm/µm parameter drift on a shared surface must PAIR, leaving only true add/drops unmatched."""
    ka, kb = _face_cat(fa), _face_cat(fb)
    if ka != kb:
        return False
    if ka == "plane":
        na, oa = _plane_no(fa)
        nb, ob = _plane_no(fb)
        dot = na[0] * nb[0] + na[1] * nb[1] + na[2] * nb[2]
        return dot > 0.999 and abs(oa - ob) <= _FACE_POS_TOL
    if ka == "cyl":
        aa = [abs(float(x)) for x in fa["axis"]]
        ab = [abs(float(x)) for x in fb["axis"]]
        parallel = sum(x * y for x, y in zip(aa, ab)) > 0.999
        return parallel and abs(float(fa.get("radius", 0)) - float(fb.get("radius", 0))) <= _FACE_RAD_TOL
    pa, pb = fa.get("point"), fb.get("point")
    if isinstance(pa, list) and isinstance(pb, list):
        return all(abs(float(x) - float(y)) <= _FACE_POS_TOL for x, y in zip(pa, pb))
    return False


def _face_cat(f: dict) -> str:
    if f.get("planar") and isinstance(f.get("normal"), list) and isinstance(f.get("point"), list):
        return "plane"
    if f.get("kind") == "cylinder" and isinstance(f.get("axis"), list):
        return "cyl"
    return f.get("kind", "surf")


def _plane_no(f: dict):
    """Unit normal (sign-canonicalized so the dominant axis is positive) + signed offset from origin."""
    n = [float(x) for x in f["normal"]]
    p = [float(x) for x in f["point"]]
    offset = n[0] * p[0] + n[1] * p[1] + n[2] * p[2]
    dom = max(range(3), key=lambda i: abs(n[i]))
    if n[dom] < 0:
        n = [-x for x in n]
        offset = -offset
    return n, offset


def _diff_faces(faces_a: list, faces_b: list):
    """Greedy tolerance set-difference of two face lists. Returns (missing, extra): missing = faces
    in A that pair with no face in B; extra = faces in B that pair with none in A. Same-surface faces
    pair through sub-µm/µm parameter drift (_faces_compatible), so only genuine add/drops remain."""
    used_b = [False] * len(faces_b)
    missing = []
    for fa in faces_a:
        hit = -1
        for j, fb in enumerate(faces_b):
            if not used_b[j] and _faces_compatible(fa, fb):
                hit = j
                break
        if hit >= 0:
            used_b[hit] = True
        else:
            missing.append(fa)
    extra = [fb for j, fb in enumerate(faces_b) if not used_b[j]]
    return missing, extra


def _face_desc(f: dict) -> str:
    """Compact human description of a face for the diff report."""
    i = f.get("i")
    if f.get("planar"):
        n = f.get("normal")
        area = f.get("area")
        nstr = ("[" + ",".join(format(float(x), ".3g") for x in n) + "]") if isinstance(n, list) else "?"
        return f"i={i} plane n={nstr} area={format(float(area), '.4g') if isinstance(area, (int, float)) else '?'}"
    if f.get("kind") == "cylinder":
        return f"i={i} cyl r={format(float(f.get('radius', 0)), '.4g')}"
    return f"i={i} {f.get('kind', 'surf')}"


# ---------------------------------------------------------------------------
# Tool: compare_assemblies  (adapter-only — the assembly round-trip verifier, ADR-047 B4)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def compare_assemblies(doc_a: str, doc_b: str) -> str:
    """Objectively diff two ASSEMBLY documents against the RATIFIED verified criteria
    (ADR-047): component set EXACT (source file + config + instance counts) AND every
    component transform within tolerance (position <= 1µm, rotation <= 1e-6) AND mate
    count + types match AND mass properties within the V1 thresholds (|dV| <= 1%, |dA| <= 1%).

    doc_a / doc_b: EITHER an absolute .SLDASM path OR the TITLE of an already-open document
        (e.g. 'Assem2' for an unsaved rebuild). doc_a is the REFERENCE (the original).

    Components are paired by (source basename, instance ordinal in tree order) — instance
    numbering may legitimately differ between original and rebuild. The verdict is a REPORT;
    writing ir.verification into the artifact stays the caller's job. doc_b is left active."""
    def _read(doc: str, label: str):
        if os.path.isfile(doc):
            r = _call_raw("open_document", {"file_path": os.path.abspath(doc)})
            if r.get("status") != "COMPLETED":
                r = _call_raw("activate_document", {"title": os.path.splitext(os.path.basename(doc))[0]})
        else:
            r = _call_raw("activate_document", {"title": doc})
        if r.get("status") != "COMPLETED":
            err = r.get("error") or {}
            raise RuntimeError(f"DOC_{label}_UNAVAILABLE | {doc} | {err.get('code')}: {err.get('message')}")
        comps = json.loads(_analysis_items(_call_raw("analyze_assembly", {"analysis_type": "components"}))[0])
        mates = json.loads(_analysis_items(_call_raw("analyze_assembly", {"analysis_type": "mates"}))[0])
        mass = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        return comps.get("components", []), mates.get("mates", []), mass

    try:
        comps_a, mates_a, mass_a = _read(doc_a, "A")
        comps_b, mates_b, mass_b = _read(doc_b, "B")
    except (RuntimeError, ValueError, IndexError) as ex:
        return f"FAILED | {ex}"

    def _key_seq(comps):
        counters: dict = {}
        out = []
        for c in comps:
            base = os.path.basename(c.get("path") or "").lower()
            cfg = c.get("config") or ""
            n = counters.get((base, cfg), 0) + 1
            counters[(base, cfg)] = n
            out.append(((base, cfg, n), c))
        return dict(out), sorted(k for k, _c in out)

    map_a, keys_a = _key_seq(comps_a)
    map_b, keys_b = _key_seq(comps_b)
    set_exact = keys_a == keys_b

    # Transform comparison over the paired components (rotation elements vs 1e-6; translation
    # meters vs 1µm). Only meaningful when the sets pair up.
    max_rot = max_pos = 0.0
    transforms_ok = set_exact
    if set_exact:
        for k in keys_a:
            ta, tb = map_a[k].get("transform"), map_b[k].get("transform")
            if not (isinstance(ta, list) and isinstance(tb, list) and len(ta) >= 12 and len(tb) >= 12):
                transforms_ok = False
                break
            max_rot = max(max_rot, max(abs(ta[i] - tb[i]) for i in range(9)))
            max_pos = max(max_pos, max(abs(ta[i] - tb[i]) for i in range(9, 12)))
        transforms_ok = transforms_ok and max_rot <= 1e-6 and max_pos <= 1e-6

    types_a = sorted(m.get("type") or "" for m in mates_a)
    types_b = sorted(m.get("type") or "" for m in mates_b)
    mates_ok = len(mates_a) == len(mates_b) and types_a == types_b

    def _delta_pct(a, b):
        if not isinstance(a, (int, float)) or not isinstance(b, (int, float)) or a == 0:
            return None
        return (b - a) / a * 100.0

    dv = _delta_pct(mass_a.get("volume"), mass_b.get("volume"))
    da = _delta_pct(mass_a.get("surface_area"), mass_b.get("surface_area"))
    mass_ok = dv is not None and da is not None and abs(dv) <= 1.0 and abs(da) <= 1.0

    verified = set_exact and transforms_ok and mates_ok and mass_ok
    fmt = lambda v, spec=".6g": ("?" if v is None else format(v, spec))  # noqa: E731
    return (f"COMPLETED | verified_criteria={'PASS' if verified else 'FAIL'} | "
            f"components A={len(comps_a)} B={len(comps_b)} set={'EXACT' if set_exact else 'DIFFER'} | "
            f"transforms max_rot={fmt(max_rot, '.3g')} max_pos={fmt(max_pos, '.3g')} m "
            f"{'OK' if transforms_ok else 'FAIL'} | "
            f"mates A={len(mates_a)} B={len(mates_b)} types={'MATCH' if types_a == types_b else 'DIFFER'} | "
            f"volume dV={fmt(dv, '.4f')}% area dA={fmt(da, '.4f')}% | reference=A ({doc_a})")


# ---------------------------------------------------------------------------
# Tool: submit_feature_graph  (the FORWARD IR door — intent → IR → pycompiler → SolidWorks.
# Re-enabled GATE-FREE 2026-07-16 for the forward vocabulary effort (IR-ADR-017); the old
# experimental gates were deleted by IR-ADR-005. Two doors, ONE pycompiler: rebuild_from_ir
# replays an artifact's stored graph, this tool takes a graph directly — never fork the compiler.)
# ---------------------------------------------------------------------------
def _resync_state_version() -> int:
    """Realign the adapter's local state_version with the authoritative GET /state.

    A submit_feature_graph run performs MANY execution ops (each bumping state_version) OUTSIDE the
    normal _call() path, so afterwards — success OR failure — we resync the local value, or the NEXT
    normal tool call would fail INVALID_STATE_VERSION (KNOWN-LIMITATIONS #5)."""
    global _state_version
    try:
        _state_version = get_state()
    except Exception:  # noqa: BLE001
        pass
    return _state_version


def _run_graph(graph_obj, fresh_document):
    """The ONE path from a Feature Graph to live geometry — shared by every IR door.

    IR-ADR-005 forbids FORKING the compiler, not having several doors into it. There are three
    (submit_feature_graph takes a graph, rebuild_from_ir replays an artifact's stored one,
    analyze_drawing(mode='build') lowers a drawing), and they all land here, so the document
    choice, the compiler entry point and the post-run resync cannot diverge between them.

    Returns (text, ok, state_version). On failure `text` is already a complete FAILED line."""
    if fresh_document:
        # The graph type picks the document: an assembly graph (component/mate nodes) needs an
        # assembly, a part graph a part.
        nodes = graph_obj.get("nodes") or [] if isinstance(graph_obj, dict) else []
        is_assembly_graph = any(isinstance(n, dict) and n.get("type") in ("component", "mate")
                                for n in nodes)
        new_tool = "open_new_assembly" if is_assembly_graph else "open_new_part"
        opened = _call_raw(new_tool, {})
        if opened.get("status") != "COMPLETED":
            err = opened.get("error") or {}
            return (f"FAILED | {new_tool.upper()}_FAILED | {err.get('code')}: {err.get('message')}",
                    False, _state_version)

    # Lazy import: a missing compiler tree degrades to a clean tool error instead of killing the
    # whole MCP server at startup.
    try:
        from ir_execution_port import run_feature_graph
    except Exception as ex:  # noqa: BLE001
        return f"FAILED | COMPILER_UNAVAILABLE | {ex}", False, _state_version

    # NEVER let an exception crash the MCP server; resync state_version regardless (IR-ADR-001) —
    # one run performs MANY state-bumping sub-ops outside _call() (KNOWN-LIMITATIONS #5).
    try:
        result = run_feature_graph(graph_obj)
    except Exception as ex:  # noqa: BLE001
        sv = _resync_state_version()
        return (f"FAILED | UNEXPECTED | {type(ex).__name__}: {ex} | state_version resynced to {sv}",
                False, sv)
    return result.summary(), True, _resync_state_version()


@mcp.tool(structured_output=False)
def submit_feature_graph(graph: str, fresh_document: bool = True) -> str:
    """Build a part (or assembly) from a Feature Graph IR in ONE call — the deterministic
    compiler lowers each IR node to the right low-level tool sequence and resolves references
    (geometric anchors, runtime feature names) against live geometry.

    ★ PRIMARY BUILD PATH — prefer this whenever you are CREATING a part/assembly (including a
    reverse reconstruction from a drawing). Assemble the WHOLE feature graph and submit it ONCE;
    do NOT hand-build feature-by-feature with the individual low-level tools
    (create_sketch/extrude_feature/add_edge_feature/…). One graph keeps tree order intact, lets
    the compiler resolve anchors + runtime feature names deterministically, and takes far fewer
    round-trips. The low-level tools are for RECOVERY (re-doing one failed node), a genuine
    one-off edit, or a feature the IR vocabulary cannot yet express — not for routine
    construction. If you truly cannot express the whole part in one graph yet, still submit the
    LARGEST coherent batches (fresh_document=True for the first, append with fresh_document=False),
    never one node at a time.

    graph: the Feature Graph as a JSON STRING. Authoring from DESIGN INTENT: read the MCP resource
        `recipe://usage/forward` FIRST (grammar, anchor design, self-verification), plus
        `schema://feature-graph` — the schema IS the capability registry.
        Replaying an ANALYZED part instead: `recipe://usage/canonicalization` +
        `recipe://usage/mapping_part` (…/mapping_sheet_metal, …/mapping_assembly).
        Essentials: units METERS, angles RADIANS (the compiler converts at tool boundaries);
        nodes build in array order (tree order is law); extrude/revolve/rib/sweep/sheet_metal/
        sketched_bend consume the IMMEDIATELY preceding sketch node; loft profiles, a sweep's
        path, pattern seeds and mirror features reference EARLIER nodes by id; an optional
        graph-level material {name, library?} applies after the last node.
    fresh_document (default True): open a new blank document first — part graphs a part,
        assembly graphs (component/mate nodes) an assembly. Pass False to build into the
        CURRENT active document instead.

    Returns the compiler's per-node report: COMPLETED n/n, or FAILED with a feature-level error
    and how far it got. CAD ops are NOT transactional — on failure partial geometry remains
    (reported, never hidden). The adapter resyncs state_version after every run, so subsequent
    normal tools keep working. Verify the result objectively (analyze_model / compare_parts) —
    never assume."""
    try:
        graph_obj = json.loads(graph)
    except Exception as ex:  # noqa: BLE001
        return f"FAILED | INVALID_JSON | the graph is not valid JSON: {ex}"

    text, ok, sv = _run_graph(graph_obj, fresh_document)
    return text + (f" | state_version={sv}" if ok else "")


# ---------------------------------------------------------------------------
# Tool-surface normalisation (prompt cost)
# ---------------------------------------------------------------------------
def _normalize_tool_surface() -> None:
    """Undo three SDK artefacts that ride in the tools/list prompt on every turn.

    1. Docstrings keep their source indentation (the SDK does not dedent them).
    2. Every parameter gets an auto-generated `title` that just re-spells its own name.
    3. `additionalProperties` is absent, so a typo'd param name is not rejected by the
       schema — the P0.4 stance is to reject at the MCP layer, before any REST/COM
       round-trip, so put it back.

    `Tool.description` / `Tool.parameters` are plain mutable fields that `list_tools()`
    reads at call time, so one pass after registration is enough. Without this the payload
    is ~9% larger than the FastMCP surface it replaced; with it, ~2% smaller.

    Touches `mcp._tool_manager` (private). The public alternative is the `middleware`
    hook, which the SDK marks provisional — revisit if this breaks on an SDK bump; the
    schema-contract test is what catches it.
    """
    for tool in mcp._tool_manager.list_tools():
        if tool.description:
            tool.description = inspect.cleandoc(tool.description)
        schema = tool.parameters
        if isinstance(schema, dict):
            schema.pop("title", None)
            for prop in (schema.get("properties") or {}).values():
                if isinstance(prop, dict):
                    prop.pop("title", None)
            schema.setdefault("additionalProperties", False)


_normalize_tool_surface()


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    mcp.run()
