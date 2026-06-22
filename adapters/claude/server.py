import json
import uuid
from typing import Annotated, Literal
from pydantic import Field
from fastmcp import FastMCP
from execution_client import call_tool, get_state, ensure_ready as _ensure_ready, ExecutionLayerError
from response_mapper import map_response

# P0.4 MCP hardening: discriminators are `Literal[...]` (invalid values rejected at the
# MCP schema level, before any REST/COM round-trip) and numeric params carry Pydantic
# `Field` constraints (units/ranges). Coordinates stay plain float (negative/zero are
# legitimate); open-ended selector strings (entity1_type, plane/material names) stay str
# so valid selectors are never rejected. Editing this file requires reconnecting the
# `solidworks` MCP server (no hot-reload — KNOWN-LIMITATIONS #4).

mcp = FastMCP("solidworks-execution-adapter")

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
@mcp.tool()
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
@mcp.tool()
def open_new_part(template_path: str = "") -> str:
    """Open a new SolidWorks part document."""
    params = {}
    if template_path:
        params["template_path"] = template_path
    return _call("open_new_part", params)


# ---------------------------------------------------------------------------
# Tool: create_sketch
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
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
    direction: float = 1.0,
    points: str = "[]",
) -> str:
    """Add a sketch entity to the active sketch.
    entity_type='rectangle':  uses x1,y1 (first corner) and x2,y2 (opposite corner).
    entity_type='circle':     uses cx,cy (center) and radius.
    entity_type='line':       uses x1,y1 (start) and x2,y2 (end).
    entity_type='arc':        uses x1,y1 (start), x2,y2 (end), xm,ym (mid-arc point) — a 3-point arc.
    entity_type='arc_center': uses cx,cy (exact center), x1,y1 (start), x2,y2 (end) and
                              direction (+1 CCW, -1 CW). Prefer this over 'arc' when the exact
                              center/radius are known (e.g. straight from analyze_model): it
                              guarantees the radius and is numerically stable for shallow/near-
                              collinear arcs where the 3-point circle-fit is unreliable.
    entity_type='ellipse':    uses cx,cy (center), x1,y1 (a point on the MAJOR axis), x2,y2 (a point
                              on the MINOR axis). Mirrors analyze_model's ellipse segment exactly.
    entity_type='spline':     uses points — a JSON array STRING of flat through-points
                              '[x1,y1,x2,y2,...]' (>= 2 points). Mirrors analyze_model's spline segment
                              (its 'points'). Round-trips its through-points exactly.
    entity_type='fillet':     uses vx,vy (vertex to round) and radius.
    entity_type='chamfer':    uses vx,vy (vertex to cut) and distance.
    direction: only for 'arc_center' — sweep sense from start to end (+1 CCW, -1 CW); pick the
               sign that yields the intended (usually minor) arc. Ignored by other types.
    points: only for 'spline' — a JSON array string of flat (x,y) through-points, e.g.
            '[-0.2,0.0,-0.18,0.04,-0.14,0.05]'. Ignored by other types.
    This tool MIRRORS analyze_model: every sketch segment analyze_model emits (line, arc/circle,
    ellipse, spline, with cx/cy/x1/y1/x2/y2/radius/points) maps 1:1 onto an entity_type here, so an
    analyzed sketch can be rebuilt without dropping/simplifying any curve.
    On COMPLETED the result includes result_geometry: the REAL geometry SolidWorks created (read back,
    not echoed) — use it to self-verify the radius/endpoints match your intent before moving on.
    Rebuilding from exact (frozen) coordinates: do NOT add coincident constraints between segments —
    segments that share identical endpoint coordinates already close the profile, so a cut/extrude finds
    the region automatically. Adding constraints is unnecessary and can fail and roll the sketch back.
    All coordinates in document units (meters)."""
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
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_sketch_constraint
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
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
@mcp.tool()
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
) -> str:
    """Extrude/feature the active sketch profile.
    feature_type='boss' (default): solid extrusion, requires depth.
    feature_type='cut': material removal, requires depth and existing solid body.
    feature_type='revolve': solid of revolution — angle in DEGREES (default 360 = full revolve); axis defined by axis_x1/y1 to axis_x2/y2 (midpoint of that segment selects the centerline).
    feature_type='sweep': sweeps profile along a path — requires path_sketch (name of the path sketch).
    feature_type='loft': lofts through multiple profiles — requires profiles (JSON array of sketch names, e.g. '[\"Sketch1\",\"Sketch2\"]').
    reverse (boss/cut): flip the feature direction. Needed e.g. for a cut/boss sketched on a part FACE, where the material is on the opposite side from the default.
    through (boss/cut): through-all end condition (depth is ignored). Use for through holes/cuts instead of guessing a depth.
    depth is required only for a BLIND boss/cut (not for through, revolve, sweep, or loft).
    Sheet metal: a 'cut' on a sheet-metal body (incl. holes after a bend) is handled automatically — it
        applies a Normal Cut and inserts before the Flat-Pattern; no special params needed. Use
        analyze_model(analysis_type='edges') to get real edge midpoints for selection.
    On COMPLETED the result includes result_geometry {volume, faces, edges} of the body after the feature
        — verify the step from this instead of a separate analyze_model + manual volume math.
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
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_edge_feature
# ---------------------------------------------------------------------------
@mcp.tool()
def add_edge_feature(
    feature_type: Literal["fillet", "chamfer"],
    radius_or_distance: Annotated[float, Field(gt=0, description="Fillet radius / chamfer setback in METERS (e.g. 0.01 = 10mm)")],
    edge_indices: str = "",
    ex: float = 0.0,
    ey: float = 0.0,
    ez: float = 0.0,
    edges_json: str = "[]",
) -> str:
    """Apply a 3D edge modifier to solid body edges (post-extrusion). Distinct from sketch fillet/chamfer.
    feature_type='fillet': rounds selected edge(s) with given radius_or_distance.
    feature_type='chamfer': cuts a 45° chamfer on selected edge(s) with given radius_or_distance as equal distance.
    PREFERRED edge selection: edge_indices — a JSON array of integer indices from analyze_model(analysis_type='edges'),
    e.g. '[3,5]'. This selects edges directly (no coordinate pick), so it works on crowded or CONCAVE edges
    (inner-corner / small-radius step edges) that a coordinate pick can't disambiguate. Takes priority over
    edges_json / ex-ey-ez.
    Fallback — single edge: ex/ey/ez (3D point on the edge); multiple edges: edges_json e.g. '[{\"ex\":0.05,\"ey\":0.05,\"ez\":0.05}]'.
    All coordinates in document units (meters). Requires an active part document with an existing solid body."""
    params = {
        "feature_type": feature_type,
        "radius_or_distance": radius_or_distance,
        "ex": ex, "ey": ey, "ez": ez,
        "edges_json": edges_json,
    }
    if edge_indices:
        params["edge_indices"] = edge_indices
    return _call("add_edge_feature", params)


# ---------------------------------------------------------------------------
# Tool: create_drawing
# ---------------------------------------------------------------------------
@mcp.tool()
def create_drawing(model_path: str = "") -> str:
    """Open a new SolidWorks drawing document (A3 sheet, 1:1 scale).
    model_path: optional path to the part/assembly to reference. If omitted, an empty drawing is created.
    Note: add_drawing_view requires the referenced part to be saved to disk."""
    return _call("create_drawing", {"model_path": model_path})


# ---------------------------------------------------------------------------
# Tool: add_drawing_view
# ---------------------------------------------------------------------------
@mcp.tool()
def add_drawing_view(
    view_type: Literal["front", "top", "right", "isometric", "back", "bottom", "left"],
    pos_x: float = 0.1,
    pos_y: float = 0.1,
    scale: Annotated[float, Field(
        gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
    model_path: str = "",
) -> str:
    """Add a model view to the active drawing sheet.
    view_type: 'front', 'top', 'right', 'isometric', 'back', 'bottom', 'left'.
    pos_x/pos_y: position on the drawing sheet in meters.
    scale: view scale (default 1.0 = 1:1).
    model_path: path to the part file; if omitted, uses the first open part document.
    Requires the part to be saved to disk (in-memory parts cannot be projected)."""
    return _call(
        "add_drawing_view",
        {"view_type": view_type, "pos_x": pos_x, "pos_y": pos_y,
            "scale": scale, "model_path": model_path},
    )


# ---------------------------------------------------------------------------
# Tool: add_drawing_dimension
# ---------------------------------------------------------------------------
@mcp.tool()
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
# Tool: export_document
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
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
@mcp.tool()
def verify_state() -> str:
    """Read and return the current CAD state without modifying it."""
    return _call("verify_state", {})


# ---------------------------------------------------------------------------
# Tool: close_document
# ---------------------------------------------------------------------------
@mcp.tool()
def close_document(save: bool = False) -> str:
    """Close the active SolidWorks document. Set save=True to save before closing."""
    return _call("close_document", {"save": save})


# ---------------------------------------------------------------------------
# Tool: save_document
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
def analyze_model(
    analysis_type: Literal["mass_properties", "geometry", "edges", "faces", "features", "sketch"],
    name: str = "",
) -> str:
    """Analyze the active SolidWorks part document (read-only, does NOT change state).
    analysis_type='mass_properties': returns volume, surface_area, center of gravity (cx, cy, cz) in document units.
    analysis_type='geometry': returns bodies, faces, edges, vertices counts of all solid bodies.
    analysis_type='edges': returns a JSON object listing EVERY solid edge with its start/end/MIDPOINT 3D coords
        in meters (rounded to 6 decimals; closed/circular edges also report length) and a stable index `i`. Use
        the index `i` for add_edge_feature(edge_indices=...) — robust for crowded/concave edges — or a MIDPOINT
        for coordinate-based selection (e.g. edge_flange's ex/ey/ez). On a very large part (many hundreds of edges)
        this is the slowest mode; prefer 'features' for a general understanding.
    analysis_type='faces': returns a JSON object listing EVERY solid face with a stable index `i`; planar faces
        also carry normal, area, and a representative on-plane point (meters, rounded to 6 decimals). Use the
        index `i` for create_sketch(on_face=True, face_index=i) — robust where a coordinate pick is ambiguous
        (e.g. a revolve end-cap / shaft end whose centre lies on the axis).
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
    Does NOT increment state_version."""
    return _call("analyze_model", {"analysis_type": analysis_type, "name": name})


# ---------------------------------------------------------------------------
# Tool: get_selection
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
def edit_sketch(sketch_name: str) -> str:
    """Reopen an existing sketch for editing. Counterpart to create_sketch.
    sketch_name: exact name of the sketch feature in the feature tree (e.g. 'Sketch1').
    Returns COMPLETED with ActiveSketch set to sketch_name.
    After editing, call extrude_feature (or any feature) to exit the sketch."""
    return _call("edit_sketch", {"sketch_name": sketch_name})


# ---------------------------------------------------------------------------
# Tool: add_reference_geometry
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
def create_pattern(
    pattern_type: Literal["linear", "circular"],
    feature_name: str,
    spacing: Annotated[float, Field(
        gt=0, description="Linear pattern spacing in METERS")] = 0.01,
    count: Annotated[int, Field(
        ge=2, description="Total instances incl. seed (>= 2)")] = 2,
    direction: Literal["X", "Y", "Z"] = "X",
    count2: Annotated[int, Field(
        ge=1, description="Second-direction instances (1 = off)")] = 1,
    spacing2: Annotated[float, Field(
        gt=0, description="Second-direction spacing in METERS")] = 0.01,
    axis_name: str = "",
    angle: Annotated[float, Field(
        gt=0, le=360, description="Circular pattern angle in DEGREES")] = 90.0,
    equal_spacing: bool = True,
) -> str:
    """Create a linear or circular feature pattern.
    pattern_type='linear': repeats feature_name along direction ('X', 'Y', or 'Z') with spacing (meters) and count instances.
        Optional second direction: count2>1 with spacing2.
        Direction maps to default planes: X→Right Plane normal, Y→Top Plane normal, Z→Front Plane normal.
    pattern_type='circular': repeats feature_name around axis_name (reference axis, e.g. 'Axis1') with count instances.
        Create the axis first with add_reference_geometry(type='axis', ...).
        equal_spacing=True (default): angle is the TOTAL spread (use 360 for a full ring); count instances are
            evenly divided across it and NEVER overlap (count distinct == count).
        equal_spacing=False: angle is the spacing BETWEEN adjacent instances. count*angle can exceed 360, in which
            case later instances WRAP and overlap earlier ones, so the number of DISTINCT instances < count.
    Round-trip with analyze_model: a CirPattern reports instances, equal_spacing, spacing_deg, plus the
        EFFECTIVE distinct_instances and a wraps flag. To reproduce a pattern faithfully, EITHER replay the
        stored form verbatim (count=instances, angle=spacing_deg, equal_spacing as reported) OR, when it wraps,
        use the simpler equivalent full ring: count=distinct_instances, angle=360, equal_spacing=True.
        (Example: a gear analyzed as 30 @ 15° equal_spacing=False has distinct_instances=24 → reproduce as
        either 30 @ 15° False or 24 @ 360° True; both yield the same 24 teeth.)
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
            "axis_name": axis_name,
            "angle": angle,
            "equal_spacing": equal_spacing,
        },
    )


# ---------------------------------------------------------------------------
# Tool: set_part_material
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
def sheet_metal_feature(
    feature_type: Literal["base_flange", "edge_flange", "flat_pattern"],
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
        gt=0, le=180, description="Edge flange bend angle in DEGREES")] = 90.0,
) -> str:
    """Create sheet metal features on the active part.
    feature_type='base_flange': creates a sheet metal base from the active sketch profile.
        thickness: sheet thickness (meters). bend_radius: bend radius (default = thickness). k_factor: default 0.5.
        Exits sketch mode automatically.
    feature_type='edge_flange': adds a flange to an existing sheet metal edge at (ex, ey, ez).
        flange_length: flange length (meters). angle: bend angle in degrees (default 90).
    feature_type='flat_pattern': unfolds all bends to create the flat pattern view.
        No additional parameters required. Requires an existing base_flange feature."""
    return _call(
        "sheet_metal_feature",
        {
            "feature_type": feature_type,
            "thickness": thickness,
            "bend_radius": bend_radius,
            "k_factor": k_factor,
            "ex": ex, "ey": ey, "ez": ez,
            "flange_length": flange_length,
            "angle": angle,
        },
    )


# ---------------------------------------------------------------------------
# Tool: modify_dimension
# ---------------------------------------------------------------------------
@mcp.tool()
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
@mcp.tool()
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
@mcp.tool()
def activate_document(title: str) -> str:
    """Switch the ACTIVE SolidWorks document to an already-open one by its title (e.g. 'gear' or
    'gear.SLDPRT'). Use this to read/compare another open part — e.g. activate the original, analyze_model
    it, then activate your copy — without OS window switching. Does NOT change geometry or state_version.
    The document must already be OPEN. Returns the now-active document title."""
    return _call("activate_document", {"title": title})


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    mcp.run()
