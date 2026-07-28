using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: sketch lifecycle and 2D geometry (create/edit sketch, entities, dimensions, constraints).
    public partial class SolidWorksService
    {

        public ExecutionResponse CreateSketch(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string planeName = p != null ? p.Value<string>("plane") : null;
                bool onFace = p?.Value<bool?>("on_face") ?? false;

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                // Auto-exit any active sketch so multi-sketch workflows (sweep needs a path + a profile,
                // loft needs multiple profiles) work without a dedicated exit-sketch tool. Previously this
                // returned SKETCH_ALREADY_ACTIVE; finishing the open sketch before starting a new one is the
                // sensible behaviour and all existing flows call create_sketch with no sketch active anyway.
                if (modelDoc.SketchManager.ActiveSketch != null)
                    modelDoc.SketchManager.InsertSketch(true);

                modelDoc.ClearSelection2(true);
                if (onFace)
                {
                    // Sketch on a model FACE. Two ways to pick the face:
                    //   (a) face_index — the i-th face from analyze_model(faces). ROBUST: reuses the same
                    //       GetBodies2(swSolidBody,true) → GetFaces() enumeration and selects the IFace2
                    //       directly, bypassing the coordinate pick that is ambiguous on a revolve end-cap
                    //       (the flat circular cap's centroid sits on the revolve axis/origin → FACE_NOT_FOUND).
                    //   (b) face_x/y/z — a 3D point lying ON the planar face (interior, not on an edge).
                    // Plane-name selection can't reach model faces, so this is what enables features on
                    // existing geometry (e.g. a recess/hub/keyway on a gear face, or a bore on a shaft end).
                    int? faceIndex = p?.Value<int?>("face_index");
                    if (faceIndex != null)
                    {
                        var partDoc = modelDoc as IPartDoc;
                        var allFaces = new List<IFace2>();
                        object[] bodies = partDoc?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                        if (bodies != null)
                            foreach (var b in bodies)
                            {
                                var body = b as IBody2;
                                if (body == null) continue;
                                object[] fs = body.GetFaces() as object[];
                                if (fs == null) continue;
                                foreach (var f in fs) { var fa = f as IFace2; if (fa != null) allFaces.Add(fa); }
                            }
                        int fi = faceIndex.Value;
                        if (fi < 0 || fi >= allFaces.Count)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "FACE_NOT_FOUND",
                                $"Face index {fi} out of range (part has {allFaces.Count} faces). Call analyze_model(faces) for valid indices.");
                        var ent = allFaces[fi] as IEntity;
                        bool faceSel = ent != null && ent.Select4(false, null);
                        if (!faceSel)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "FACE_NOT_FOUND", $"Failed to select face index {fi}.");
                    }
                    else
                    {
                        double fx = p?.Value<double?>("face_x") ?? 0.0;
                        double fy = p?.Value<double?>("face_y") ?? 0.0;
                        double fz = p?.Value<double?>("face_z") ?? 0.0;
                        bool faceSel = modelDoc.Extension.SelectByID2("", "FACE", fx, fy, fz, false, 0, null, 0);
                        if (!faceSel)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "FACE_NOT_FOUND",
                                $"No face found at ({fx}, {fy}, {fz}). Provide a point lying ON the target planar face (interior, not on an edge), or use face_index from analyze_model(faces).");
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(planeName))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "plane is required (or set on_face=true with face_x/face_y/face_z).");
                    bool selected = SelectPlaneFlexible(modelDoc, planeName, false, 0);
                    if (!selected)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "PLANE_NOT_FOUND", $"Plane '{planeName}' not found");
                }

                if (onFace)
                {
                    try
                    {
                        var selMgr = modelDoc.SelectionManager as ISelectionMgr;
                        int selCount = selMgr?.GetSelectedObjectCount2(-1) ?? -1;
                        ExecLog.Write($"create_sketch on_face: selectedCount={selCount} before InsertSketch");
                    }
                    catch { }
                }

                modelDoc.SketchManager.InsertSketch(true);

                var activeSketch = modelDoc.SketchManager.ActiveSketch;
                if (onFace)
                    ExecLog.Write($"create_sketch on_face: activeSketch={(activeSketch != null)} after InsertSketch");
                if (activeSketch == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_CREATION_FAILED", "Sketch was not created or is not active after InsertSketch. The face may be non-planar, or the selection was lost — try analyze_model(faces) and a different face_index.");

                string activeSketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

                // Echo the NEW sketch's MEASURED plane + frame (same reader analyze uses). The
                // caller (IR compiler) compares this against the original sketch's recorded frame
                // and transforms 2D coordinates — the deterministic fix for the support-normal /
                // in-plane-axis mismatch class (2-1 Sketch5). Best-effort: never fails the create.
                object sketchPlaneEcho = null;
                try { sketchPlaneEcho = ReadSketchPlane(activeSketch); } catch { }

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = activeSketchName,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = sketchPlaneEcho,
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // The single-entity CREATE core shared by add_sketch_entity and add_sketch_entities (Batch-3):
        // one switch, never forked. Creates ONE entity record (entity_type + its params, incl. the
        // per-record construction flag) in the ACTIVE sketch. Returns null on success (createdSeg /
        // createdSegs populated) or "CODE|message" on failure — the caller maps it into its own
        // response shape. The caller owns the AddToDB bracket (ADR-042) and the guard preamble.
        private string CreateSketchEntityCore(IModelDoc2 modelDoc, JObject p, string entityType,
            string sketchName, out ISketchSegment createdSeg, out object[] createdSegs)
        {
            createdSeg = null;
            createdSegs = null;
            bool created = false;
            switch (entityType.ToLowerInvariant())
            {
                case "rectangle":
                {
                    double? x1 = p?.Value<double?>("x1");
                    double? y1 = p?.Value<double?>("y1");
                    double? x2 = p?.Value<double?>("x2");
                    double? y2 = p?.Value<double?>("y2");
                    if (x1 == null || y1 == null || x2 == null || y2 == null)
                        return "MISSING_PARAMETER|rectangle requires x1, y1, x2, y2.";
                    var segs = modelDoc.SketchManager.CreateCornerRectangle(
                        x1.Value, y1.Value, 0, x2.Value, y2.Value, 0) as object[];
                    createdSegs = segs;
                    created = segs != null && segs.Length > 0;
                    break;
                }
                case "circle":
                {
                    double? cx = p?.Value<double?>("cx");
                    double? cy = p?.Value<double?>("cy");
                    double? radius = p?.Value<double?>("radius");
                    if (cx == null || cy == null || radius == null)
                        return "MISSING_PARAMETER|circle requires cx, cy, radius.";
                    var arc = modelDoc.SketchManager.CreateCircleByRadius(cx.Value, cy.Value, 0, radius.Value);
                    createdSeg = arc as ISketchSegment;
                    created = arc != null;
                    break;
                }
                case "line":
                {
                    double? x1 = p?.Value<double?>("x1");
                    double? y1 = p?.Value<double?>("y1");
                    double? x2 = p?.Value<double?>("x2");
                    double? y2 = p?.Value<double?>("y2");
                    if (x1 == null || y1 == null || x2 == null || y2 == null)
                        return "MISSING_PARAMETER|line requires x1, y1, x2, y2.";
                    var seg = modelDoc.SketchManager.CreateLine(x1.Value, y1.Value, 0, x2.Value, y2.Value, 0);
                    createdSeg = seg as ISketchSegment;
                    created = seg != null;
                    break;
                }
                case "arc":
                {
                    // 3-point arc: start (x1,y1), end (x2,y2), mid-arc point (xm,ym)
                    double? x1 = p?.Value<double?>("x1");
                    double? y1 = p?.Value<double?>("y1");
                    double? x2 = p?.Value<double?>("x2");
                    double? y2 = p?.Value<double?>("y2");
                    double? xm = p?.Value<double?>("xm");
                    double? ym = p?.Value<double?>("ym");
                    if (x1 == null || y1 == null || x2 == null || y2 == null || xm == null || ym == null)
                        return "MISSING_PARAMETER|arc requires x1, y1 (start), x2, y2 (end), xm, ym (mid-arc point).";
                    var arc = modelDoc.SketchManager.Create3PointArc(
                        x1.Value, y1.Value, 0,
                        x2.Value, y2.Value, 0,
                        xm.Value, ym.Value, 0);
                    createdSeg = arc as ISketchSegment;
                    created = arc != null;
                    break;
                }
                case "arc_center":
                {
                    // Center-based arc: exact center (cx,cy) + start (x1,y1), end (x2,y2), direction
                    // (+1 CCW, -1 CW). Guarantees the radius; numerically stable for shallow arcs.
                    double? cx = p?.Value<double?>("cx");
                    double? cy = p?.Value<double?>("cy");
                    double? x1 = p?.Value<double?>("x1");
                    double? y1 = p?.Value<double?>("y1");
                    double? x2 = p?.Value<double?>("x2");
                    double? y2 = p?.Value<double?>("y2");
                    if (cx == null || cy == null || x1 == null || y1 == null || x2 == null || y2 == null)
                        return "MISSING_PARAMETER|arc_center requires cx, cy (center), x1, y1 (start), x2, y2 (end).";
                    // direction: +1 CCW / -1 CW, applied VERBATIM when given. When OMITTED (or 0),
                    // pick the MINOR (<=180°) arc deterministically — a corner round / fillet arc is
                    // virtually always the minor one, and a wrong sign silently draws the 270°
                    // complement (live benchmark failure: R2 90° corner came out as the other side).
                    // Callers that really want the major arc pass the explicit sign (the analyze/IR
                    // round-trip always does — its 'dir' is read from the original geometry).
                    double dirRaw = p?.Value<double?>("direction") ?? 0.0;
                    short direction;
                    if (Math.Abs(dirRaw) >= 0.5)
                        direction = (short)(dirRaw >= 0 ? 1 : -1);
                    else
                    {
                        double a1 = Math.Atan2(y1.Value - cy.Value, x1.Value - cx.Value);
                        double a2 = Math.Atan2(y2.Value - cy.Value, x2.Value - cx.Value);
                        double ccwSweep = a2 - a1;
                        while (ccwSweep < 0) ccwSweep += 2 * Math.PI;
                        while (ccwSweep >= 2 * Math.PI) ccwSweep -= 2 * Math.PI;
                        direction = (short)(ccwSweep <= Math.PI ? 1 : -1);
                    }
                    // Endpoints passed VERBATIM (shared exactly with neighbours — the closed contour is
                    // preserved). The caller's AddToDB bracket keeps SW inference off the exact radius.
                    var arc = modelDoc.SketchManager.CreateArc(
                        cx.Value, cy.Value, 0,
                        x1.Value, y1.Value, 0,
                        x2.Value, y2.Value, 0,
                        direction);
                    createdSeg = arc;
                    created = arc != null;
                    break;
                }
                case "ellipse":
                {
                    // Center-based ellipse: cx,cy (center), x1,y1 (major-axis point), x2,y2 (minor-axis point).
                    double? cx = p?.Value<double?>("cx");
                    double? cy = p?.Value<double?>("cy");
                    double? x1 = p?.Value<double?>("x1");
                    double? y1 = p?.Value<double?>("y1");
                    double? x2 = p?.Value<double?>("x2");
                    double? y2 = p?.Value<double?>("y2");
                    if (cx == null || cy == null || x1 == null || y1 == null || x2 == null || y2 == null)
                        return "MISSING_PARAMETER|ellipse requires cx, cy (center), x1, y1 (major-axis point), x2, y2 (minor-axis point).";
                    var ell = modelDoc.SketchManager.CreateEllipse(
                        cx.Value, cy.Value, 0,
                        x1.Value, y1.Value, 0,
                        x2.Value, y2.Value, 0);
                    createdSeg = ell as ISketchSegment;
                    created = ell != null;
                    break;
                }
                case "spline":
                {
                    // Through-point spline: 'points' = flat [x1,y1,x2,y2,...] (>= 2 points), z=0.
                    var ptsTok = p?["points"] as JArray;
                    if (ptsTok == null || ptsTok.Count < 4 || ptsTok.Count % 2 != 0)
                        return "MISSING_PARAMETER|spline requires 'points': a flat list [x1,y1,x2,y2,...] of >= 2 (x,y) pairs.";
                    int nPts = ptsTok.Count / 2;
                    var pointData = new double[nPts * 3];
                    for (int i = 0; i < nPts; i++)
                    {
                        pointData[i * 3] = ptsTok[i * 2].Value<double>();
                        pointData[i * 3 + 1] = ptsTok[i * 2 + 1].Value<double>();
                        pointData[i * 3 + 2] = 0.0;
                    }
                    var spl = modelDoc.SketchManager.CreateSpline(pointData);
                    createdSeg = spl as ISketchSegment;
                    created = spl != null;
                    break;
                }
                case "fillet":
                {
                    // Sketch fillet: rounds the corner between two segments meeting at vertex (vx, vy).
                    double? vx = p?.Value<double?>("vx");
                    double? vy = p?.Value<double?>("vy");
                    double? radius = p?.Value<double?>("radius");
                    if (vx == null || vy == null || radius == null)
                        return "MISSING_PARAMETER|fillet requires vx, vy (vertex coordinates) and radius.";
                    modelDoc.ClearSelection2(true);
                    bool vertexSelected = modelDoc.Extension.SelectByID2(
                        "", "SKETCHPOINT", vx.Value, vy.Value, 0, false, 0, null, 0);
                    if (!vertexSelected)
                        return $"VERTEX_NOT_FOUND|No sketch vertex found at or near ({vx.Value}, {vy.Value}).";
                    var fillet = modelDoc.SketchManager.CreateFillet(radius.Value,
                        (int)swConstrainedCornerAction_e.swConstrainedCornerDeleteGeometry);
                    createdSeg = fillet as ISketchSegment;
                    created = fillet != null;
                    if (created) RegenerateSketchInPlace(modelDoc, sketchName);
                    break;
                }
                case "chamfer":
                {
                    // Sketch chamfer: cuts the corner between two adjacent segments at vertex (vx, vy).
                    double? vx = p?.Value<double?>("vx");
                    double? vy = p?.Value<double?>("vy");
                    double? distance = p?.Value<double?>("distance");
                    if (vx == null || vy == null || distance == null)
                        return "MISSING_PARAMETER|chamfer requires vx, vy (vertex coordinates) and distance.";
                    modelDoc.ClearSelection2(true);
                    bool vertexSelected = modelDoc.Extension.SelectByID2(
                        "", "SKETCHPOINT", vx.Value, vy.Value, 0, false, 0, null, 0);
                    if (!vertexSelected)
                        return $"VERTEX_NOT_FOUND|No sketch vertex found at or near ({vx.Value}, {vy.Value}).";
                    var chamfer = modelDoc.SketchManager.CreateChamfer(
                        (int)swSketchChamferType_e.swSketchChamfer_DistanceEqual,
                        distance.Value, distance.Value);
                    createdSeg = chamfer as ISketchSegment;
                    created = chamfer != null;
                    if (created) RegenerateSketchInPlace(modelDoc, sketchName);
                    break;
                }
                default:
                    return $"UNSUPPORTED_ENTITY_TYPE|entity_type '{entityType}' is not supported. Supported: rectangle, circle, line, arc, arc_center, ellipse, spline, fillet, chamfer.";
            }

            if (!created)
                return $"ENTITY_CREATION_FAILED|Failed to create sketch entity of type '{entityType}'.";

            // construction=true converts the created segment(s) to construction/reference geometry
            // (centerlines, symmetry axes, hole-position scaffolding). Applied post-create because
            // SketchManager has no construction-aware create calls.
            bool asConstruction = p?.Value<bool?>("construction") ?? false;
            if (asConstruction)
            {
                try
                {
                    if (createdSeg != null) createdSeg.ConstructionGeometry = true;
                    if (createdSegs != null)
                        foreach (var o in createdSegs)
                        {
                            var s = o as ISketchSegment;
                            if (s != null) s.ConstructionGeometry = true;
                        }
                }
                catch (Exception ex)
                {
                    return $"CONSTRUCTION_FLAG_FAILED|Entity created but could not be converted to construction geometry: {ex.Message}";
                }
            }
            return null;
        }

        // Batch-3: add_sketch_entities — N entity records in ONE call. The single-entity tool's calls
        // were the largest call-count item in both level-2-2 benchmark runs (62 and 16 calls); a batch
        // cuts both the round-trips and the per-call context echo. Same JSON-string idiom as ADR-022
        // (the adapter parses `segments` into a JArray of records, each = add_sketch_entity's params).
        // NOT transactional (IR-ADR-001 failure isolation): on the first failing record the run STOPS
        // and reports FAILED with the record index + how many records were already created (they remain
        // in the sketch — the caller decides whether to continue, fix, or discard). state_version bumps
        // only on COMPLETED. The echo is a compact per-type count, NOT per-segment result_geometry —
        // read the sketch back with analyze_model(sketch) when exact readback is needed.
        public ExecutionResponse AddSketchEntities(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                var segsTok = p?["segments"] as JArray;
                if (segsTok == null || segsTok.Count == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "segments is required: a JSON array of entity records, each with entity_type + its add_sketch_entity params.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var activeSketch = modelDoc.SketchManager.ActiveSketch;
                if (activeSketch == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first.");
                string sketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

                int done = 0;
                var counts = new Dictionary<string, int>();
                // ONE AddToDB bracket around the whole batch (ADR-042 — frozen-exact coordinates, no
                // pixel-tolerance snapping) and ONE redraw at the end, instead of per segment.
                bool prevAddToDb = modelDoc.SketchManager.AddToDB;
                modelDoc.SketchManager.AddToDB = true;
                try
                {
                    for (int i = 0; i < segsTok.Count; i++)
                    {
                        var sp = segsTok[i] as JObject;
                        string et = sp?.Value<string>("entity_type");
                        if (sp == null || string.IsNullOrEmpty(et))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER",
                                $"segments[{i}] must be an object with entity_type. {done} segment(s) before it were created and remain in the sketch.");

                        ISketchSegment seg; object[] segs;
                        string err = CreateSketchEntityCore(modelDoc, sp, et, sketchName, out seg, out segs);
                        if (err != null)
                        {
                            var parts = err.Split(new[] { '|' }, 2);
                            string code = parts[0];
                            string msg = parts.Length > 1 ? parts[1] : err;
                            ExecLog.Write($"add_sketch_entities: FAILED at segments[{i}] ({et}) after {done} created — {code}");
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), code,
                                $"segments[{i}] ({et}): {msg} {done} segment(s) before it were created and remain in the sketch.");
                        }
                        done++;
                        string key = et.ToLowerInvariant();
                        counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                    }
                }
                finally
                {
                    modelDoc.SketchManager.AddToDB = prevAddToDb;
                }
                modelDoc.GraphicsRedraw2();

                ExecLog.Write($"add_sketch_entities: created {done} segment(s) in {sketchName}");

                var countsJson = new JObject();
                foreach (var kv in counts) countsJson[kv.Key] = kv.Value;

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = sketchName,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["segment_count"] = done,
                        ["counts"] = countsJson
                    },
                    Error = null
                };
                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        public ExecutionResponse AddSketchEntity(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string entityType = p?.Value<string>("entity_type");
                if (string.IsNullOrEmpty(entityType))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "entity_type is required (rectangle, circle, line).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var activeSketch = modelDoc.SketchManager.ActiveSketch;
                if (activeSketch == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first.");

                // The entity switch lives in CreateSketchEntityCore — ONE switch shared with
                // add_sketch_entities (Batch-3), never forked. The core also applies the per-record
                // construction flag. Captured segment(s) feed the result_geometry echo below
                // (read back from SW, never the input, so snapping/inference drift is visible).
                //
                // AddToDB for the WHOLE create (generalizes ADR-022's arc_center-only toggle): all
                // coordinates arrive frozen-exact (analyze round-trip / IR lowering), so SW's input
                // inference/snapping must never touch them. Found live on the 1-2 flange rebuild: a
                // 1.59mm line (raised-face step) built fine in one document, then CreateLine returned
                // NULL for the same coords in the next — the pixel-based snap tolerance depends on the
                // window's zoom, so short segments nondeterministically collapse without AddToDB.
                ISketchSegment createdSeg;
                object[] createdSegs;
                string coreErr;
                bool prevAddToDbAll = modelDoc.SketchManager.AddToDB;
                modelDoc.SketchManager.AddToDB = true;
                try
                {
                    coreErr = CreateSketchEntityCore(modelDoc, p, entityType,
                        (activeSketch as IFeature)?.Name, out createdSeg, out createdSegs);
                }
                finally
                {
                    modelDoc.SketchManager.AddToDB = prevAddToDbAll;
                }
                if (coreErr != null)
                {
                    var coreParts = coreErr.Split(new[] { '|' }, 2);
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        coreParts[0], coreParts.Length > 1 ? coreParts[1] : coreErr);
                }

                string activeSketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

                // In-band echo of the REAL created geometry (Task 2). Read back from SW (never the
                // input), so AddToDB snapping / inference drift is visible to the caller without a
                // separate analyze pass. Best-effort; a read failure must not fail the create.
                object resultGeom = null;
                try
                {
                    if (createdSeg != null)
                    {
                        resultGeom = ReadSegment(createdSeg);
                    }
                    else if (createdSegs != null)
                    {
                        var arr = new JArray();
                        foreach (var o in createdSegs)
                        {
                            var rs = ReadSegment(o as ISketchSegment);
                            if (rs != null) arr.Add(rs);
                        }
                        resultGeom = new JObject { ["kind"] = "rectangle", ["segments"] = arr };
                    }
                }
                catch { resultGeom = null; }

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = activeSketchName,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = resultGeom,
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        public ExecutionResponse AddDimension(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                double? px = p?.Value<double?>("px");
                double? py = p?.Value<double?>("py");
                double? value = p?.Value<double?>("value");
                double labelOffsetX = p?.Value<double?>("label_offset_x") ?? 0.0;
                double labelOffsetY = p?.Value<double?>("label_offset_y") ?? -0.015;

                if (px == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "px is required (X coordinate of a point on the target segment).");
                if (py == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "py is required (Y coordinate of a point on the target segment).");
                if (value == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "value is required.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var activeSketch = modelDoc.SketchManager.ActiveSketch;
                if (activeSketch == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first.");

                string activeSketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

                // Force SolidWorks to commit and refresh sketch geometry before attempting selection.
                // Without this, segments created by add_rectangle may not yet be queryable via SelectByID2.
                modelDoc.ClearSelection2(true);
                modelDoc.GraphicsRedraw2();

                // Suppress the "input dimension value" dialog — otherwise SolidWorks blocks waiting for user input
                bool prevInputDimVal = _solidWorks.GetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate);
                _solidWorks.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);

                // SelectByID2 with coordinates selects the nearest segment — robust for any sketch geometry
                bool selected = modelDoc.Extension.SelectByID2(
                    "", "SKETCHSEGMENT", px.Value, py.Value, 0, false, 0, null, 0);

                if (!selected)
                {
                    _solidWorks.SetUserPreferenceToggle(
                        (int)swUserPreferenceToggle_e.swInputDimValOnCreate, prevInputDimVal);
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SEGMENT_NOT_FOUND", $"No sketch segment found at or near ({px.Value}, {py.Value}).");
                }

                var dim = modelDoc.AddDimension2(px.Value + labelOffsetX, py.Value + labelOffsetY, 0) as IDisplayDimension;

                // Restore preference regardless of outcome
                _solidWorks.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate, prevInputDimVal);

                if (dim == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DIMENSION_CREATION_FAILED", "AddDimension2 returned null.");

                // Drive the dimension to the requested value
                var swDim = dim.GetDimension2(0) as SolidWorks.Interop.sldworks.IDimension;
                if (swDim != null)
                    swDim.SystemValue = value.Value;

                var existingDims = new List<string>();
                existingDims.Add($"px={px.Value},py={py.Value},value={value.Value}");

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = activeSketchName,
                        Features = new List<string>(),
                        Dimensions = existingDims
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        public ExecutionResponse AddSketchConstraint(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string constraintType = p?.Value<string>("constraint_type");
                if (string.IsNullOrEmpty(constraintType))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "constraint_type is required (horizontal, vertical, coincident, parallel, perpendicular, tangent, equal, midpoint).");

                double? px1 = p?.Value<double?>("px1");
                double? py1 = p?.Value<double?>("py1");
                if (px1 == null || py1 == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "px1 and py1 are required (point on the first entity).");

                double? px2 = p?.Value<double?>("px2");
                double? py2 = p?.Value<double?>("py2");
                string entityType1 = p?.Value<string>("entity_type1") ?? "SKETCHSEGMENT";
                string entityType2 = p?.Value<string>("entity_type2") ?? "SKETCHSEGMENT";

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (modelDoc.SketchManager.ActiveSketch == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first.");

                string swConstraintId;
                bool needsTwoEntities = false;
                switch (constraintType.ToLowerInvariant())
                {
                    case "horizontal":    swConstraintId = "sgHORIZONTAL";    break;
                    case "vertical":      swConstraintId = "sgVERTICAL";      break;
                    case "coincident":    swConstraintId = "sgCOINCIDENT";    needsTwoEntities = true; break;
                    case "parallel":      swConstraintId = "sgPARALLEL";      needsTwoEntities = true; break;
                    case "perpendicular": swConstraintId = "sgPERPENDICULAR"; needsTwoEntities = true; break;
                    case "tangent":       swConstraintId = "sgTANGENT";       needsTwoEntities = true; break;
                    case "equal":         swConstraintId = "sgSAMELENGTH";    needsTwoEntities = true; break;
                    case "midpoint":      swConstraintId = "sgATMIDDLE";      needsTwoEntities = true; break;
                    default:
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "UNSUPPORTED_CONSTRAINT_TYPE",
                            $"constraint_type '{constraintType}' is not supported. Supported: horizontal, vertical, coincident, parallel, perpendicular, tangent, equal, midpoint.");
                }

                if (needsTwoEntities && (px2 == null || py2 == null))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", $"constraint_type '{constraintType}' requires px2 and py2 (point on the second entity).");

                modelDoc.ClearSelection2(true);
                bool sel1 = modelDoc.Extension.SelectByID2("", entityType1, px1.Value, py1.Value, 0, false, 0, null, 0);
                if (!sel1)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ENTITY_NOT_FOUND", $"No {entityType1} found at or near ({px1.Value}, {py1.Value}).");

                if (needsTwoEntities)
                {
                    bool sel2 = modelDoc.Extension.SelectByID2("", entityType2, px2.Value, py2.Value, 0, true, 0, null, 0);
                    if (!sel2)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "ENTITY_NOT_FOUND", $"No {entityType2} found at or near ({px2.Value}, {py2.Value}).");
                }

                modelDoc.SketchAddConstraints(swConstraintId);

                string activeSketchName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name ?? "Sketch";

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = activeSketchName,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        // -----------------------------------------------------------------------
        // M18 — edit_sketch
        // -----------------------------------------------------------------------
        public ExecutionResponse EditSketch(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string sketchName = p?.Value<string>("sketch_name");
                if (string.IsNullOrEmpty(sketchName))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "sketch_name is required (name of existing sketch feature, e.g. 'Sketch1').");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (modelDoc.SketchManager.ActiveSketch != null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_ALREADY_ACTIVE",
                        "A sketch is already active. Exit the current sketch first (call extrude_feature or close the sketch).");

                bool selected = modelDoc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);
                if (!selected)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_NOT_FOUND",
                        $"Sketch '{sketchName}' not found. Ensure the name matches exactly (e.g. 'Sketch1').");

                modelDoc.EditSketch();

                var activeSketch = modelDoc.SketchManager.ActiveSketch;
                if (activeSketch == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_EDIT_FAILED",
                        $"EditSketch() was called but no sketch became active. Ensure '{sketchName}' is a valid sketch feature.");

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = sketchName,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// CreateFillet/CreateChamfer apply the corner to the model, but the new sketch segment
        /// isn't regenerated/painted until the user clicks in the view. EditRebuild3 (Ctrl+B)
        /// regenerates it, but exits the active sketch — so we re-enter edit mode on the same
        /// sketch by name. Net effect: the fillet/chamfer appears immediately and the user stays
        /// in the sketch.
        /// </summary>
        private void RegenerateSketchInPlace(IModelDoc2 modelDoc, string sketchName)
        {
            modelDoc.EditRebuild3();
            if (!string.IsNullOrEmpty(sketchName) && modelDoc.SketchManager.ActiveSketch == null)
            {
                modelDoc.ClearSelection2(true);
                if (modelDoc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0))
                    modelDoc.EditSketch();
                modelDoc.ClearSelection2(true);
            }
            modelDoc.GraphicsRedraw2();
        }
    }
}
