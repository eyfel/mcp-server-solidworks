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
    public class SolidWorksService
    {
        private readonly IOperationGuard _guard;
        private ISldWorks _solidWorks;

        public bool IsConnected { get; private set; }

        public SolidWorksService(IOperationGuard guard)
        {
            _guard = guard;
        }

        // SolidWorks COM must be called from an STA thread.
        // Ensure the calling thread is STA (e.g. [STAThread] on Main, or a dedicated STA thread).
        public bool Connect()
        {
            try
            {
                _solidWorks = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                IsConnected = true;
                EnsureVisible(); // the user must ALWAYS see what the automation does
                return true;
            }
            catch (COMException)
            {
                IsConnected = false;
                return false;
            }
        }

        // Force the attached SolidWorks instance to be on-screen and user-controllable.
        // GetActiveObject binds to whatever instance is first in the COM Running Object Table — which
        // can be a headless/COM-launched instance with NO window (Visible=false). If we silently drove
        // that one, the user would watch a different window and see nothing happen (observed live: two
        // SLDWORKS processes, the older one window-less). Policy (for now): every instance we touch is
        // made visible. Best-effort — a visibility failure must never break the attach or a tool call.
        private void EnsureVisible()
        {
            try
            {
                if (_solidWorks == null) return;
                if (!_solidWorks.Visible) _solidWorks.Visible = true;
                _solidWorks.UserControl = true;
            }
            catch { /* visibility is best-effort; never fail over it */ }
        }

        private bool EnsureConnected()
        {
            if (_solidWorks != null) return true;
            return Connect();
        }

        // Health probe (P0.6): attempts a fresh COM attach and reports whether SolidWorks is reachable
        // plus the active document title. Must run on the STA thread (it touches COM via Connect()).
        // Returns a dictionary so no new compiled type is needed (old-style csproj).
        public Dictionary<string, object> GetHealthInfo()
        {
            var result = new Dictionary<string, object>();
            bool attached = Connect();
            result["comAttached"] = attached;
            if (attached)
            {
                try
                {
                    var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                    result["activeDocument"] = d != null ? d.GetTitle() : null;
                }
                catch { /* attached but couldn't read the active doc — leave it unset */ }
            }
            return result;
        }

        // Lifecycle bootstrap (ensure_ready): unlike GetHealthInfo (read-only probe), this LAUNCHES
        // SolidWorks via COM when it isn't already running, then waits until a fresh ROT attach
        // succeeds — because every real tool call re-attaches on a new service instance via
        // GetActiveObject, that is the exact readiness gate that matters. Does NOT open/create any
        // document (assembly/drawing contexts come later — opening a part here would be wrong).
        // Must run on the STA thread (touches COM). Returns a plain dictionary (old-style csproj).
        public Dictionary<string, object> EnsureReady()
        {
            var result = new Dictionary<string, object>();
            bool launched = false;
            bool attached = Connect();

            if (!attached)
            {
                // SolidWorks is not running — start it out-of-process via COM and make it visible.
                try
                {
                    var progType = Type.GetTypeFromProgID("SldWorks.Application");
                    if (progType == null)
                        throw new Exception("SldWorks.Application ProgID is not registered. Is SolidWorks installed?");

                    _solidWorks = (ISldWorks)Activator.CreateInstance(progType);
                    EnsureVisible(); // visible + user-controllable from the moment it launches
                    launched = true;

                    // CreateInstance returns before the app is usable. Gate on a FRESH ROT attach
                    // (Marshal.GetActiveObject) succeeding + a responsive call — that's what the next
                    // tool call will do. Wait up to 90s (adapter ENSURE_TIMEOUT is 120s).
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            var probe = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                            var rev = probe.RevisionNumber(); // confirms responsive, not just registered
                            attached = true;
                            break;
                        }
                        catch
                        {
                            System.Threading.Thread.Sleep(1000); // app still spinning up / COM server busy
                        }
                    }
                    IsConnected = attached;
                }
                catch (Exception ex)
                {
                    result["comAttached"] = false;
                    result["swLaunched"] = false;
                    result["launchError"] = ex.Message;
                    return result;
                }
            }

            result["comAttached"] = attached;
            result["swLaunched"] = launched;
            if (attached)
            {
                try
                {
                    var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                    result["activeDocument"] = d != null ? d.GetTitle() : null;
                    result["swVersion"] = _solidWorks.RevisionNumber();
                }
                catch { /* attached but couldn't read doc/version — leave unset */ }
            }
            else if (!result.ContainsKey("launchError"))
            {
                // Launched but never became responsive within the wait window.
                result["launchError"] = "SolidWorks was launched but did not become responsive within the timeout.";
            }
            return result;
        }

        public ExecutionResponse OpenNewPart(ToolRequest request)
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
                string templatePath = p != null ? p.Value<string>("template_path") : null;

                if (string.IsNullOrEmpty(templatePath))
                {
                    // Ask SolidWorks for the user-configured default part template (locale-agnostic)
                    templatePath = _solidWorks.GetUserPreferenceStringValue(
                        (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                }

                if (string.IsNullOrEmpty(templatePath) || !System.IO.File.Exists(templatePath))
                {
                    // Fallback: scan the standard templates folder for any .prtdot file
                    string templatesDir = System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                        "SolidWorks");
                    var found = System.IO.Directory.GetFiles(templatesDir, "*.prtdot",
                        System.IO.SearchOption.AllDirectories);
                    if (found.Length == 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "TEMPLATE_NOT_FOUND",
                            "No part template (.prtdot) found. Set a default template in SolidWorks → Tools → Options → Default Templates.");
                    templatePath = found[0];
                }

                object doc = _solidWorks.NewDocument(templatePath, 0, 0, 0);
                var modelDoc = doc as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_CREATION_FAILED",
                        $"SolidWorks NewDocument returned null. Template used: '{templatePath}'. Ensure SolidWorks → Tools → Options → Default Templates has a valid part template configured.");

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
                        ActiveSketch = null,
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

                bool created = false;
                // Capture the created segment(s) so the response can echo the REAL resulting geometry
                // (Task 2 / result_geometry) — read back from SW, never the input, so snapping/inference
                // drift is visible. createdSeg = single-segment types; createdSegs = rectangle (4 lines).
                ISketchSegment createdSeg = null;
                object[] createdSegs = null;
                switch (entityType.ToLowerInvariant())
                {
                    case "rectangle":
                    {
                        double? x1 = p?.Value<double?>("x1");
                        double? y1 = p?.Value<double?>("y1");
                        double? x2 = p?.Value<double?>("x2");
                        double? y2 = p?.Value<double?>("y2");
                        if (x1 == null || y1 == null || x2 == null || y2 == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "rectangle requires x1, y1, x2, y2.");
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
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "circle requires cx, cy, radius.");
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
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "line requires x1, y1, x2, y2.");
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
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "arc requires x1, y1 (start), x2, y2 (end), xm, ym (mid-arc point).");
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
                        // Center-based arc: exact center (cx,cy) + radius, start (x1,y1), end (x2,y2),
                        // direction (+1 CCW, -1 CW). Unlike the 3-point arc, this guarantees the radius
                        // and is numerically stable for shallow arcs (no circle-fit through near-collinear
                        // points). Use when the exact center/radius are known (e.g. from analyze_model).
                        double? cx = p?.Value<double?>("cx");
                        double? cy = p?.Value<double?>("cy");
                        double? x1 = p?.Value<double?>("x1");
                        double? y1 = p?.Value<double?>("y1");
                        double? x2 = p?.Value<double?>("x2");
                        double? y2 = p?.Value<double?>("y2");
                        if (cx == null || cy == null || x1 == null || y1 == null || x2 == null || y2 == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "arc_center requires cx, cy (center), x1, y1 (start), x2, y2 (end).");
                        // Direction defaults to +1; pass -1 to sweep clockwise. The caller picks the
                        // direction that yields the intended (usually minor) arc.
                        short direction = (short)((p?.Value<double?>("direction") ?? 1.0) >= 0 ? 1 : -1);
                        // Endpoints are passed VERBATIM (they already lie on the radius circle and are
                        // shared EXACTLY with adjacent arcs, so the closed contour is preserved — any
                        // re-projection would nudge a shared junction off its neighbour and open the loop).
                        // The key to an exact radius is disabling SolidWorks input inference/snapping via
                        // AddToDB: otherwise SW snaps the new arc's endpoints to nearby existing geometry
                        // and skews the fitted radius (catastrophically for shallow, near-collinear arcs —
                        // e.g. a 7.6° root arc came out r=19mm instead of 68.75mm via the 3-point path).
                        bool prevAddToDB = modelDoc.SketchManager.AddToDB;
                        modelDoc.SketchManager.AddToDB = true;
                        ISketchSegment arc;
                        try
                        {
                            arc = modelDoc.SketchManager.CreateArc(
                                cx.Value, cy.Value, 0,
                                x1.Value, y1.Value, 0,
                                x2.Value, y2.Value, 0,
                                direction);
                        }
                        finally
                        {
                            modelDoc.SketchManager.AddToDB = prevAddToDB;
                        }
                        createdSeg = arc;
                        created = arc != null;
                        break;
                    }
                    case "ellipse":
                    {
                        // Center-based ellipse: cx,cy (center), x1,y1 (a point on the MAJOR axis),
                        // x2,y2 (a point on the MINOR axis). Reuses the existing scalar params (no new
                        // schema params) and round-trips exactly with analyze's ellipse segment read.
                        double? cx = p?.Value<double?>("cx");
                        double? cy = p?.Value<double?>("cy");
                        double? x1 = p?.Value<double?>("x1");
                        double? y1 = p?.Value<double?>("y1");
                        double? x2 = p?.Value<double?>("x2");
                        double? y2 = p?.Value<double?>("y2");
                        if (cx == null || cy == null || x1 == null || y1 == null || x2 == null || y2 == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "ellipse requires cx, cy (center), x1, y1 (major-axis point), x2, y2 (minor-axis point).");
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
                        // Through-point spline: 'points' is a flat list [x1,y1,x2,y2,...] (>= 2 points),
                        // all at z=0 in the active sketch plane. NOTE: a spline rebuilt from through-
                        // points is visually faithful but NOT bit-identical to one authored via control
                        // points (SW keeps tangency/curvature) — see KNOWN-LIMITATIONS. Use for rebuild,
                        // not for an exact round-trip guarantee.
                        var ptsTok = p?["points"] as JArray;
                        if (ptsTok == null || ptsTok.Count < 4 || ptsTok.Count % 2 != 0)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "spline requires 'points': a flat list [x1,y1,x2,y2,...] of >= 2 (x,y) pairs.");
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
                        // Sketch fillet: rounds the corner between two selected segments at vertex (vx, vy)
                        double? vx = p?.Value<double?>("vx");
                        double? vy = p?.Value<double?>("vy");
                        double? radius = p?.Value<double?>("radius");
                        if (vx == null || vy == null || radius == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "fillet requires vx, vy (vertex coordinates) and radius.");
                        // Select the vertex point
                        modelDoc.ClearSelection2(true);
                        bool vertexSelected = modelDoc.Extension.SelectByID2(
                            "", "SKETCHPOINT", vx.Value, vy.Value, 0, false, 0, null, 0);
                        if (!vertexSelected)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "VERTEX_NOT_FOUND", $"No sketch vertex found at or near ({vx.Value}, {vy.Value}).");
                        var fillet = modelDoc.SketchManager.CreateFillet(radius.Value,
                            (int)swConstrainedCornerAction_e.swConstrainedCornerDeleteGeometry);
                        createdSeg = fillet as ISketchSegment;
                        created = fillet != null;
                        if (created) RegenerateSketchInPlace(modelDoc, (activeSketch as IFeature)?.Name);
                        break;
                    }
                    case "chamfer":
                    {
                        // Sketch chamfer: cuts the corner between two adjacent segments at vertex (vx, vy)
                        double? vx = p?.Value<double?>("vx");
                        double? vy = p?.Value<double?>("vy");
                        double? distance = p?.Value<double?>("distance");
                        if (vx == null || vy == null || distance == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "chamfer requires vx, vy (vertex coordinates) and distance.");
                        modelDoc.ClearSelection2(true);
                        bool vertexSelected = modelDoc.Extension.SelectByID2(
                            "", "SKETCHPOINT", vx.Value, vy.Value, 0, false, 0, null, 0);
                        if (!vertexSelected)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "VERTEX_NOT_FOUND", $"No sketch vertex found at or near ({vx.Value}, {vy.Value}).");
                        var chamfer = modelDoc.SketchManager.CreateChamfer(
                            (int)swSketchChamferType_e.swSketchChamfer_DistanceEqual,
                            distance.Value, distance.Value);
                        createdSeg = chamfer as ISketchSegment;
                        created = chamfer != null;
                        if (created) RegenerateSketchInPlace(modelDoc, (activeSketch as IFeature)?.Name);
                        break;
                    }
                    default:
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "UNSUPPORTED_ENTITY_TYPE",
                            $"entity_type '{entityType}' is not supported. Supported: rectangle, circle, line, arc, arc_center, ellipse, spline, fillet, chamfer.");
                }

                if (!created)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ENTITY_CREATION_FAILED", $"Failed to create sketch entity of type '{entityType}'.");

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

        public ExecutionResponse ExtrudeFeature(ToolRequest request)
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
                double? depth = p?.Value<double?>("depth");

                string featureType = (p?.Value<string>("feature_type") ?? "boss").ToLowerInvariant();
                if (featureType != "boss" && featureType != "cut" && featureType != "revolve" && featureType != "sweep" && featureType != "loft")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_FEATURE_TYPE",
                        $"feature_type '{featureType}' is not supported. Supported: boss, cut, revolve, sweep, loft.");

                // depth (> 0) is required ONLY for a BLIND boss/cut. A through-all (through=true) ignores
                // depth, and revolve/sweep/loft don't use it — so don't demand it there (KNOWN-LIMITATIONS #13).
                bool throughAll = p?.Value<bool?>("through") ?? false;
                if ((featureType == "boss" || featureType == "cut") && !throughAll && (depth == null || depth.Value <= 0))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "depth (> 0 meters) is required for a blind boss/cut. Pass through=true for a through-all instead.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                // Direction + end-condition controls (boss/cut). reverse flips the feature direction
                // (needed e.g. for a cut sketched on a part FACE, where "into the body" is the opposite
                // side from a cut sketched on a boundary plane). through = through-all (depth ignored).
                bool reverse = p?.Value<bool?>("reverse") ?? false;
                int endCond = throughAll
                    ? (int)swEndConditions_e.swEndCondThroughAll
                    : (int)swEndConditions_e.swEndCondBlind;
                // Through-all ignores depth; a blind boss/cut already validated depth > 0 above.
                // Default a missing depth to 0 so the API call is safe (e.g. a REST through-cut with no depth).
                double depthVal = depth ?? 0.0;

                // Capture the active sketch name before exiting — it is the PROFILE for sweep (and is
                // useful context generally). Re-selected by name after exit (names resolve post-exit).
                string activeProfileSketchName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name;

                // Exit sketch mode if a sketch is still active. EXCEPTION: revolve selects its axis
                // line by coordinate, which only works while the sketch is still active (a closed
                // sketch's segments are not coordinate-pickable). Revolve handles its own selection
                // below with the sketch open; FeatureRevolve2 consumes the sketch as the profile.
                if (featureType != "revolve" && modelDoc.SketchManager.ActiveSketch != null)
                    modelDoc.SketchManager.InsertSketch(true);

                var featureMgr = modelDoc.FeatureManager;
                IFeature feature;

                if (featureType == "revolve")
                {
                    // Requires depth param ignored; needs axis defined by two coordinate points in the sketch
                    double ax1 = p?.Value<double?>("axis_x1") ?? 0.0;
                    double ay1 = p?.Value<double?>("axis_y1") ?? 0.0;
                    double ax2 = p?.Value<double?>("axis_x2") ?? 0.0;
                    double ay2 = p?.Value<double?>("axis_y2") ?? 0.001;
                    // angle arrives in DEGREES (model-facing convention, like create_pattern circular /
                    // sheet_metal); convert to radians for FeatureRevolve2. Default 360 = full revolve.
                    double angleDeg = p?.Value<double?>("angle") ?? 360.0;
                    double angle = angleDeg * Math.PI / 180.0;
                    // Snap anything within ~0.06° of a full turn to an exact 2π so "full revolve" is clean
                    // (guards float rounding). Partial revolves (e.g. 180°) are unaffected.
                    if (Math.Abs(angle - 2.0 * Math.PI) < 1e-3) angle = 2.0 * Math.PI;

                    // The sketch is still active here (the generic exit above skips revolve), so the
                    // axis line is coordinate-pickable. Select it at the midpoint of the two endpoints.
                    if (modelDoc.SketchManager.ActiveSketch == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_NOT_ACTIVE", "revolve needs the profile+axis sketch active. Call create_sketch and draw the profile + axis line first.");

                    modelDoc.ClearSelection2(true);
                    bool axSel = modelDoc.Extension.SelectByID2("", "SKETCHSEGMENT", (ax1 + ax2) / 2.0, (ay1 + ay2) / 2.0, 0, false, 0, null, 0);
                    if (!axSel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "AXIS_NOT_FOUND", $"No sketch segment found at midpoint of provided axis coords. Provide axis_x1/y1/x2/y2 as two endpoints of the centerline.");

                    // Convert the selected axis line to construction geometry (a centerline) so SolidWorks
                    // treats it as the axis of revolution and the remaining closed loop as the sole profile
                    // — mirrors the manual flow (axis-of-revolution = line, selected-contour = sketch region).
                    var revSelMgr = modelDoc.SelectionManager as ISelectionMgr;
                    var axisSeg = revSelMgr?.GetSelectedObject6(1, -1) as ISketchSegment;
                    if (axisSeg != null) axisSeg.ConstructionGeometry = true;

                    // Remember the profile sketch, then exit it (post-exit, re-select it by name — names
                    // resolve after exit even though coordinates do not).
                    string profileSketchName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name;
                    modelDoc.SketchManager.InsertSketch(true);
                    modelDoc.ClearSelection2(true);
                    if (!string.IsNullOrEmpty(profileSketchName))
                        modelDoc.Extension.SelectByID2(profileSketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);

                    feature = featureMgr.FeatureRevolve2(
                        true, true, false, false, false, false,
                        0, 0, angle, 0, false, false,
                        0.0, 0.0, 0, 0.0, 0.0, true, true, true) as IFeature;
                }
                else if (featureType == "sweep")
                {
                    string pathSketch = p?.Value<string>("path_sketch");
                    if (string.IsNullOrEmpty(pathSketch))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "sweep requires path_sketch (name of the sketch containing the sweep path).");

                    // The profile sketch was already exited by the generic exit above. A sweep needs BOTH
                    // selected: the profile (selection mark 1) and the path (mark 4). Selecting only the
                    // path left InsertProtrusionSwept4 with no profile → null.
                    if (string.IsNullOrEmpty(activeProfileSketchName))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "PROFILE_NOT_FOUND", "sweep needs an active profile sketch when called. Create the profile sketch last (it stays active) and pass the path sketch name.");

                    modelDoc.ClearSelection2(true);
                    bool profSel = modelDoc.Extension.SelectByID2(activeProfileSketchName, "SKETCH", 0, 0, 0, false, 1, null, 0);
                    if (!profSel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "PROFILE_NOT_FOUND", $"Profile sketch '{activeProfileSketchName}' not found.");
                    bool pathSel = modelDoc.Extension.SelectByID2(pathSketch, "SKETCH", 0, 0, 0, true, 4, null, 0);
                    if (!pathSel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_NOT_FOUND", $"Path sketch '{pathSketch}' not found. Ensure the sketch is named exactly as provided.");

                    feature = featureMgr.InsertProtrusionSwept4(
                        false, false, 0, false, false,
                        0, 0,
                        false, 0.0, 0.0, 0, 0,
                        true, true, true,
                        0.0, false, false, 0.0, 0) as IFeature;
                }
                else if (featureType == "loft")
                {
                    string profilesJson = p?.Value<string>("profiles");
                    if (string.IsNullOrEmpty(profilesJson))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "loft requires profiles (JSON array of sketch names, e.g. [\"Sketch1\",\"Sketch2\"]).");

                    JArray profileArray;
                    try { profileArray = JArray.Parse(profilesJson); }
                    catch { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "profiles must be a valid JSON array of sketch name strings."); }

                    if (profileArray.Count < 2)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "loft requires at least 2 profiles.");

                    if (modelDoc.SketchManager.ActiveSketch != null)
                        modelDoc.SketchManager.InsertSketch(true);

                    modelDoc.ClearSelection2(true);
                    for (int i = 0; i < profileArray.Count; i++)
                    {
                        string skName = profileArray[i].Value<string>();
                        bool sel = modelDoc.Extension.SelectByID2(skName, "SKETCH", 0, 0, 0, i > 0, 0, null, 0);
                        if (!sel)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "SKETCH_NOT_FOUND", $"Profile sketch '{skName}' not found.");
                    }

                    feature = featureMgr.InsertProtrusionBlend2(
                        false, false, false, 0.1,
                        0, 0,
                        0.0, 0.0, false, false,
                        false, 0.0, 0.0, 0,
                        true, true, true, 0) as IFeature;
                }
                else if (featureType == "cut")
                {
                    // SHEET-METAL fix: a sheet-metal part keeps a (usually suppressed) Flat-Pattern as the
                    // LAST top-level feature, and the rollback bar sits AFTER it — so FeatureCut4 inserts
                    // the cut after Flat-Pattern (illegal; it must stay last) and silently returns null.
                    // Roll the bar to just before Flat-Pattern, make the cut (the profile sketch already
                    // sits before Flat-Pattern, so it stays available), then roll forward so Flat-Pattern
                    // re-applies on top of the cut — exactly what the SW UI does automatically.
                    IFeature flatPat = FindFlatPattern(modelDoc);
                    bool rolledBack = false;
                    if (flatPat != null)
                    {
                        try
                        {
                            rolledBack = modelDoc.FeatureManager.EditRollback(
                                (int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, flatPat.Name);
                        }
                        catch { rolledBack = false; }
                    }

                    // Explicitly re-select the profile sketch by name. On a PLANE sketch the just-exited
                    // sketch stays implicitly selected as the profile, but on a FACE sketch the selected
                    // FACE remains the selection after exit, so FeatureCut4 found no profile → null.
                    // (Same post-exit re-select trick revolve uses.) Also required after a rollback, which
                    // clears the selection.
                    modelDoc.ClearSelection2(true);
                    if (!string.IsNullOrEmpty(activeProfileSketchName))
                        modelDoc.Extension.SelectByID2(activeProfileSketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);

                    // Dir (3rd arg) controls cut direction. Default true = INTO the body (opposite the
                    // sketch plane's outward normal) — correct for a cut on a boundary plane. For a cut
                    // sketched on a part FACE the material is on the other side, so pass reverse=true to
                    // flip it (Dir=false). through ⇒ swEndCondThroughAll (depth ignored).
                    // SHEET METAL (flatPat != null): a plain cut returns null — it needs the "Normal Cut"
                    // option (normalCut=true, 18th arg, cut perpendicular to the sheet faces) AND the
                    // OPPOSITE default direction (the sketch sits on a sheet FACE, like ADR-019's face cut),
                    // i.e. dir = reverse instead of !reverse. We then add a flipped-direction retry so the
                    // through-all works regardless of which face the profile was sketched on.
                    bool isSheetMetal = (flatPat != null);
                    bool normalCut = isSheetMetal;
                    bool dir = isSheetMetal ? reverse : !reverse;
                    feature = FeatureCutOnce(featureMgr, dir, endCond, depthVal, normalCut);
                    if (feature == null)
                    {
                        // A null cut is most often the WRONG direction: the default Dir sent the cut AWAY
                        // from material so nothing was removed. This happens whenever the sketch sits on a
                        // surface whose outward normal points into free space — an offset reference plane
                        // tangent to the body (e.g. a keyway plane) or a part FACE both behave like ADR-019's
                        // face cut, inverting the boundary-plane default. Retry the flipped direction before
                        // failing, for ANY body type (this generalizes the former sheet-metal-only retry;
                        // for sheet metal the flipped through-all also matters per the sketched face). The
                        // caller's explicit `reverse` is still tried FIRST — we only flip to recover a null,
                        // never to override intent. (KNOWN-LIMITATIONS #13 direction note.)
                        modelDoc.ClearSelection2(true);
                        if (!string.IsNullOrEmpty(activeProfileSketchName))
                            modelDoc.Extension.SelectByID2(activeProfileSketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);
                        feature = FeatureCutOnce(featureMgr, !dir, endCond, depthVal, normalCut);
                        ExecLog.Write($"cut: flipped-direction retry (sheetMetal={isSheetMetal}) → feature={(feature != null)}");
                    }

                    // Restore the rollback bar to the end so Flat-Pattern (and its sub-features) re-apply.
                    if (rolledBack)
                    {
                        try { modelDoc.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); }
                        catch { }
                    }
                }
                else
                {
                    // Re-select the profile sketch by name (see cut branch — needed for FACE sketches,
                    // harmless for plane sketches).
                    modelDoc.ClearSelection2(true);
                    if (!string.IsNullOrEmpty(activeProfileSketchName))
                        modelDoc.Extension.SelectByID2(activeProfileSketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);

                    feature = featureMgr.FeatureExtrusion3(
                        true, false, reverse,
                        endCond, 0,
                        depthVal, 0,
                        false, false, false, false,
                        0, 0,
                        false, false, false, false,
                        true, true, true,
                        (int)swStartConditions_e.swStartSketchPlane, 0,
                        false) as IFeature;
                }

                if (feature == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "EXTRUSION_FAILED", $"Feature creation returned null for feature_type '{featureType}'. Check the profile is a closed/valid sketch, the cut direction hits existing material, and (cut) a solid body exists.");

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
                        ActiveSketch = null,
                        Features = new List<string> { feature.Name },
                        Dimensions = new List<string>()
                    },
                    // In-band body summary so the caller can verify the step without a separate analyze.
                    ResultGeometry = BuildBodySummary(modelDoc),
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

        public ExecutionResponse VerifyState(ToolRequest request)
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
                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var featureNames = new List<string>();
                object[] rawFeatures = modelDoc.FeatureManager.GetFeatures(true) as object[];
                if (rawFeatures != null)
                {
                    foreach (var f in rawFeatures)
                    {
                        var feat = f as IFeature;
                        if (feat != null)
                            featureNames.Add(feat.Name);
                    }
                }

                var activeSketch = modelDoc.SketchManager.ActiveSketch;

                // VerifyState does NOT increment StateVersion and does NOT call RegisterCompleted
                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = activeSketch != null ? (activeSketch as IFeature)?.Name : null,
                        Features = featureNames,
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
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

        public ExecutionResponse CloseDocument(ToolRequest request)
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
                bool save = p != null && (p.Value<bool?>("save") ?? false);

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                // IModelDoc2.Close() is not implemented in the interop (throws NotImplementedException);
                // close via ISldWorks.CloseDoc(title). CloseDoc discards unsaved changes silently, so
                // when save=true we must persist first. GetTitle() works for unsaved docs too (e.g. "Part2").
                string docTitle = modelDoc.GetTitle();
                if (save)
                {
                    int saveErrors = 0, saveWarnings = 0;
                    modelDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
                }
                _solidWorks.CloseDoc(docTitle);

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = null,
                        DocumentType = null,
                        ActiveSketch = null,
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

        public ExecutionResponse SaveDocument(ToolRequest request)
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
                string filePath = p?.Value<string>("file_path");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                int saveErrors = 0, saveWarnings = 0;
                bool success;

                if (string.IsNullOrEmpty(filePath))
                {
                    // Save in place — requires the document to already have a path on disk.
                    if (string.IsNullOrEmpty(modelDoc.GetPathName()))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "Document has never been saved; file_path is required for the first save (full path including .sldprt/.sldasm/.slddrw extension).");
                    success = modelDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
                }
                else
                {
                    // SaveAs to an explicit path. Ensure output directory exists.
                    string dir = System.IO.Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);

                    success = modelDoc.Extension.SaveAs3(filePath,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        null, null,
                        ref saveErrors, ref saveWarnings);
                }

                if (!success)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SAVE_FAILED", $"Save failed. Errors: {saveErrors}, Warnings: {saveWarnings}. Ensure the output path is writable and the extension matches the document type (.sldprt/.sldasm/.slddrw).");

                // Saving does not change CAD geometry — state_version is unchanged (same as export).
                string savedPath = modelDoc.GetPathName();
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = modelDoc is IDrawingDoc ? "DRAWING" : (modelDoc is IAssemblyDoc ? "ASSEMBLY" : "PART"),
                        ActiveSketch = null,
                        Features = new List<string> { savedPath },
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

        public ExecutionResponse ExportDocument(ToolRequest request)
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
                string format = p?.Value<string>("format")?.ToUpperInvariant();
                string filePath = p?.Value<string>("file_path");

                if (string.IsNullOrEmpty(format))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "format is required: STEP, IGES, STL, PDF, DWG, DXF.");
                if (string.IsNullOrEmpty(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path is required (full output path including filename and extension).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                bool isDrawing = modelDoc is IDrawingDoc;

                // DWG/DXF require a drawing document
                if ((format == "DWG" || format == "DXF") && !isDrawing)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", $"Export format '{format}' requires an active drawing document (not a part). Call create_drawing first.");

                // SolidWorks infers the format from the extension; its IGES translator only recognizes
                // .igs (not .iges → SaveAs error 256). Correct it so an explicit .iges path still works.
                if (format == "IGES" && !filePath.ToLowerInvariant().EndsWith(".igs"))
                    filePath = System.IO.Path.ChangeExtension(filePath, "igs");

                // Ensure output directory exists
                string dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                int exportErrors = 0;
                int exportWarnings = 0;
                bool success;

                if (format == "PDF")
                {
                    // PDF requires ExportPdfData options object
                    var exportData = _solidWorks.GetExportFileData(
                        (int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                    success = modelDoc.Extension.SaveAs3(filePath,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        exportData, null,
                        ref exportErrors, ref exportWarnings);
                }
                else
                {
                    success = modelDoc.Extension.SaveAs3(filePath,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        null, null,
                        ref exportErrors, ref exportWarnings);
                }

                if (!success)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "EXPORT_FAILED", $"SaveAs3 failed for format '{format}'. Errors: {exportErrors}, Warnings: {exportWarnings}. Ensure the output path is writable and the format matches the document type.");

                // ExportDocument does NOT increment state_version — no CAD state change
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = isDrawing ? "DRAWING" : "PART",
                        ActiveSketch = null,
                        Features = new List<string> { filePath },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };

                // Register as completed for idempotency but with same state_version
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

        public ExecutionResponse BatchExport(ToolRequest request)
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
                string filePathBase = p?.Value<string>("file_path_base");
                string formatsJson = p?.Value<string>("formats_json");

                if (string.IsNullOrEmpty(filePathBase))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path_base is required (output path without extension, e.g. 'C:/output/mypart').");
                if (string.IsNullOrEmpty(formatsJson))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "formats_json is required (JSON array of format strings, e.g. '[\"STEP\",\"STL\"]').");

                JArray formatArray;
                try { formatArray = JArray.Parse(formatsJson); }
                catch { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "formats_json must be a valid JSON array of format strings."); }

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                bool isDrawing = modelDoc is IDrawingDoc;

                string dir = System.IO.Path.GetDirectoryName(filePathBase);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                var exported = new List<string>();
                var failed = new List<string>();

                foreach (var fToken in formatArray)
                {
                    string fmt = fToken.Value<string>()?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(fmt)) continue;

                    if ((fmt == "DWG" || fmt == "DXF") && !isDrawing)
                    {
                        failed.Add($"{fmt}:WRONG_DOCUMENT_TYPE");
                        continue;
                    }

                    // SolidWorks infers the format from the extension; its IGES translator only
                    // recognizes .igs (not .iges → SaveAs error 256 swFileSaveAsNotSupported).
                    string ext = fmt == "IGES" ? "igs" : fmt.ToLowerInvariant();
                    string outPath = filePathBase + "." + ext;

                    int exportErrors = 0;
                    int exportWarnings = 0;
                    bool success;

                    if (fmt == "PDF")
                    {
                        var exportData = _solidWorks.GetExportFileData(
                            (int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                        success = modelDoc.Extension.SaveAs3(outPath,
                            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                            exportData, null, ref exportErrors, ref exportWarnings);
                    }
                    else
                    {
                        success = modelDoc.Extension.SaveAs3(outPath,
                            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                            null, null, ref exportErrors, ref exportWarnings);
                    }

                    if (success) exported.Add(outPath);
                    else failed.Add($"{fmt}:errors={exportErrors}");
                }

                if (exported.Count == 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "BATCH_EXPORT_FAILED", $"All exports failed. Details: {string.Join(", ", failed)}");

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = isDrawing ? "DRAWING" : "PART",
                        ActiveSketch = null,
                        Features = exported,
                        Dimensions = failed.Count > 0 ? new List<string> { $"partial_failures: {string.Join(", ", failed)}" } : new List<string>()
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

        public ExecutionResponse CreateDrawing(ToolRequest request)
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
                string modelPath = p?.Value<string>("model_path") ?? "";

                // Discover drawing template (.drwdot) — same pattern as OpenNewPart
                string templatePath = _solidWorks.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);

                if (string.IsNullOrEmpty(templatePath) || !System.IO.File.Exists(templatePath))
                {
                    string templatesDir = System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                        "SolidWorks");
                    var found = System.IO.Directory.GetFiles(templatesDir, "*.drwdot",
                        System.IO.SearchOption.AllDirectories);
                    if (found.Length == 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "TEMPLATE_NOT_FOUND",
                            "No drawing template (.drwdot) found. Set a default drawing template in SolidWorks → Tools → Options → Default Templates.");
                    templatePath = found[0];
                }

                // A3 paper (297x420mm), scale 1:1
                object doc = _solidWorks.NewDocument(templatePath,
                    (int)swDwgPaperSizes_e.swDwgPapersUserDefined, 0.420, 0.297);
                var modelDoc = doc as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_CREATION_FAILED", "SolidWorks NewDocument returned null for drawing template.");

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
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

        public ExecutionResponse AddDrawingView(ToolRequest request)
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
                string viewType = (p?.Value<string>("view_type") ?? "front").ToLowerInvariant();
                double posX = p?.Value<double?>("pos_x") ?? 0.1;
                double posY = p?.Value<double?>("pos_y") ?? 0.1;
                double scale = p?.Value<double?>("scale") ?? 1.0;
                string modelPath = p?.Value<string>("model_path") ?? "";

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active drawing document found in SolidWorks.");

                var drawingDoc = modelDoc as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Call create_drawing first.");

                // Map view_type to SolidWorks named view string
                string swViewName;
                switch (viewType)
                {
                    case "front":      swViewName = "*Front";     break;
                    case "top":        swViewName = "*Top";       break;
                    case "right":      swViewName = "*Right";     break;
                    case "isometric":  swViewName = "*Isometric"; break;
                    case "back":       swViewName = "*Back";      break;
                    case "bottom":     swViewName = "*Bottom";    break;
                    case "left":       swViewName = "*Left";      break;
                    default:
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "UNSUPPORTED_VIEW_TYPE",
                            $"view_type '{viewType}' is not supported. Supported: front, top, right, isometric, back, bottom, left.");
                }

                // If model_path not provided, attempt to use the first open part
                if (string.IsNullOrEmpty(modelPath))
                {
                    object[] docs = _solidWorks.GetDocuments() as object[];
                    if (docs != null)
                    {
                        foreach (var d in docs)
                        {
                            var md = d as IModelDoc2;
                            if (md != null && md is IPartDoc)
                            {
                                modelPath = md.GetPathName();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(modelPath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MODEL_NOT_FOUND", "model_path not provided and no open part document found. Provide model_path or open a part first.");

                var view = drawingDoc.CreateDrawViewFromModelView3(modelPath, swViewName, posX, posY, 0.0) as IView;
                if (view == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "VIEW_CREATION_FAILED", $"CreateDrawViewFromModelView3 returned null for view '{viewType}'. Ensure model_path is saved to disk.");

                // Apply scale
                view.ScaleDecimal = scale;
                modelDoc.GraphicsRedraw2();

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
                        Features = new List<string> { view.Name },
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

        public ExecutionResponse AddDrawingDimension(ToolRequest request)
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

                if (px == null || py == null || value == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "px, py, and value are required.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (!(modelDoc is IDrawingDoc))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Use add_dimension for part/sketch dimensions.");

                modelDoc.ClearSelection2(true);
                modelDoc.GraphicsRedraw2();

                bool prevInputDimVal = _solidWorks.GetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate);
                _solidWorks.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);

                // Drawing views project the model as EDGE entities (not SKETCHSEGMENT). Try EDGE first,
                // then SILHOUETTE (curved-body outlines), then SKETCHSEGMENT for sketched drawing geometry.
                bool selected = modelDoc.Extension.SelectByID2(
                    "", "EDGE", px.Value, py.Value, 0, false, 0, null, 0);
                if (!selected)
                    selected = modelDoc.Extension.SelectByID2(
                        "", "SILHOUETTE", px.Value, py.Value, 0, false, 0, null, 0);
                if (!selected)
                    selected = modelDoc.Extension.SelectByID2(
                        "", "SKETCHSEGMENT", px.Value, py.Value, 0, false, 0, null, 0);

                if (!selected)
                {
                    _solidWorks.SetUserPreferenceToggle(
                        (int)swUserPreferenceToggle_e.swInputDimValOnCreate, prevInputDimVal);
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SEGMENT_NOT_FOUND", $"No drawing edge/segment found at or near ({px.Value}, {py.Value}). Ensure the point lies on a projected model edge in a view.");
                }

                var dim = modelDoc.AddDimension2(px.Value + labelOffsetX, py.Value + labelOffsetY, 0) as IDisplayDimension;
                _solidWorks.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate, prevInputDimVal);

                if (dim == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DIMENSION_CREATION_FAILED", "AddDimension2 returned null in drawing context.");

                var swDim = dim.GetDimension2(0) as SolidWorks.Interop.sldworks.IDimension;
                if (swDim != null)
                    swDim.SystemValue = value.Value;

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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
                        Features = new List<string>(),
                        Dimensions = new List<string> { $"px={px.Value},py={py.Value},value={value.Value}" }
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

        public ExecutionResponse AddEdgeFeature(ToolRequest request)
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
                string featureType = p?.Value<string>("feature_type")?.ToLowerInvariant();
                if (string.IsNullOrEmpty(featureType) || (featureType != "fillet" && featureType != "chamfer"))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "feature_type is required: 'fillet' or 'chamfer'.");

                double? radiusOrDist = p?.Value<double?>("radius_or_distance");
                if (radiusOrDist == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "radius_or_distance is required.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                // Ensure we are NOT in sketch mode (edge features work on solid body)
                if (modelDoc.SketchManager.ActiveSketch != null)
                    modelDoc.SketchManager.InsertSketch(true);

                modelDoc.ClearSelection2(true);

                // Selection priority: edge_indices (robust) → edges_json (coords) → ex/ey/ez (single coord).
                // edge_indices is a JSON array of integers matching the `i` field from analyze_model(edges).
                // It reuses the EXACT same enumeration (GetBodies2(swSolidBody,true) → GetEdges()), so the
                // index is stable and selection bypasses the coordinate pick tolerance entirely — the fix for
                // crowded / concave (inner-corner, small-radius) edges that SelectByID2 can't disambiguate
                // (KNOWN-LIMITATIONS #6).
                string edgeIndicesJson = p?.Value<string>("edge_indices");
                JArray indexArray = null;
                if (!string.IsNullOrEmpty(edgeIndicesJson))
                {
                    try { indexArray = JArray.Parse(edgeIndicesJson); }
                    catch { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "edge_indices must be a valid JSON array of integers, e.g. \"[3,5]\"."); }
                }

                // Support single edge via ex/ey/ez or multiple via edges_json array
                string edgesJson = p?.Value<string>("edges_json");
                JArray edgeArray = null;
                if (!string.IsNullOrEmpty(edgesJson))
                {
                    try { edgeArray = JArray.Parse(edgesJson); }
                    catch { return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "INVALID_PARAMETER", "edges_json must be a valid JSON array of {ex,ey,ez} objects."); }
                }

                if (indexArray != null && indexArray.Count > 0)
                {
                    // Flatten all solid-body edges in the SAME order analyze_model(edges) reports, then select
                    // the i-th IEdge directly (no coordinate pick).
                    var partDoc = modelDoc as IPartDoc;
                    var allEdges = new List<IEdge>();
                    object[] bodies = partDoc?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                    if (bodies != null)
                        foreach (var b in bodies)
                        {
                            var body = b as IBody2;
                            if (body == null) continue;
                            object[] es = body.GetEdges() as object[];
                            if (es == null) continue;
                            foreach (var e in es) { var ed = e as IEdge; if (ed != null) allEdges.Add(ed); }
                        }
                    for (int i = 0; i < indexArray.Count; i++)
                    {
                        int ei = indexArray[i].Value<int>();
                        if (ei < 0 || ei >= allEdges.Count)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "EDGE_NOT_FOUND", $"Edge index {ei} out of range (part has {allEdges.Count} edges). Call analyze_model(edges) for valid indices.");
                        var edgeEnt = allEdges[ei] as IEntity;
                        bool sel = edgeEnt != null && edgeEnt.Select4(i > 0, null);
                        if (!sel)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "EDGE_NOT_FOUND", $"Failed to select edge index {ei}.");
                    }
                }
                else if (edgeArray != null && edgeArray.Count > 0)
                {
                    for (int i = 0; i < edgeArray.Count; i++)
                    {
                        double ex = edgeArray[i].Value<double>("ex");
                        double ey = edgeArray[i].Value<double>("ey");
                        double ez = edgeArray[i].Value<double>("ez");
                        bool sel = modelDoc.Extension.SelectByID2("", "EDGE", ex, ey, ez, i > 0, 0, null, 0);
                        if (!sel)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "EDGE_NOT_FOUND", $"No edge found at or near ({ex}, {ey}, {ez}) (edge index {i}).");
                    }
                }
                else
                {
                    double? ex = p?.Value<double?>("ex");
                    double? ey = p?.Value<double?>("ey");
                    double? ez = p?.Value<double?>("ez");
                    if (ex == null || ey == null || ez == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER", "ex, ey, ez are required (3D coordinates of a point on the target edge), or provide edges_json for multiple edges.");
                    bool sel = modelDoc.Extension.SelectByID2("", "EDGE", ex.Value, ey.Value, ez.Value, false, 0, null, 0);
                    if (!sel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDGE_NOT_FOUND", $"No edge found at or near ({ex.Value}, {ey.Value}, {ez.Value}).");
                }

                IFeature feature;
                if (featureType == "fillet")
                {
                    // Options must include swFeatureFilletUniformRadius (2) for a constant-radius fillet;
                    // Propagate (1) alone left R1 unapplied and FeatureFillet3 returned null on SW2026.
                    int filletOpts = (int)(swFeatureFilletOptions_e.swFeatureFilletPropagate
                                         | swFeatureFilletOptions_e.swFeatureFilletUniformRadius);
                    feature = modelDoc.FeatureManager.FeatureFillet3(
                        filletOpts,
                        radiusOrDist.Value,   // R1
                        0.0,                  // R2
                        0.0,                  // Rho
                        (int)swFeatureFilletType_e.swFeatureFilletType_Simple,
                        0,                    // OverflowType
                        0,                    // ConicRhoType
                        null, null, null,     // Radii, Dist2Arr, RhoArr
                        null, null, null, null // SetBackDistances, PointRadiusArray, PointDist2Array, PointRhoArray
                    ) as IFeature;
                    ExecLog.Write($"add_edge_feature fillet: opts={filletOpts} r={radiusOrDist.Value} -> {(feature == null ? "NULL" : feature.Name)}");
                }
                else // chamfer
                {
                    // Was InsertFeatureChamfer(1, 1, dist, 0, …): Options=1 is actually
                    // swFeatureChamferFlipDirection, and ChamferType=1 (AngleDistance) was given Angle=0 →
                    // degenerate → null on SW2026. Use a proper 45° distance-angle chamfer (equal setback).
                    double chamferAngle = 45.0 * Math.PI / 180.0;
                    feature = modelDoc.FeatureManager.InsertFeatureChamfer(
                        0,                                               // Options: none
                        (int)swChamferType_e.swChamferAngleDistance,     // distance + angle
                        radiusOrDist.Value,                              // Width (setback distance)
                        chamferAngle,                                    // 45°
                        0.0, 0.0, 0.0, 0.0) as IFeature;
                    ExecLog.Write($"add_edge_feature chamfer: dist={radiusOrDist.Value} angle=45 -> {(feature == null ? "NULL" : feature.Name)}");
                }

                if (feature == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FEATURE_CREATION_FAILED", $"add_edge_feature '{featureType}' returned null. Ensure the selected edge(s) belong to an existing solid body.");

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
                        ActiveSketch = null,
                        Features = new List<string> { feature.Name },
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
        // M16 — analyze_model
        // -----------------------------------------------------------------------
        public ExecutionResponse AnalyzeModel(ToolRequest request)
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
                string analysisType = (p?.Value<string>("analysis_type") ?? "mass_properties").ToLowerInvariant();

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var results = new List<string>();

                if (analysisType == "mass_properties")
                {
                    // IModelDoc2.GetMassProperties2 returns double[]:
                    // [0]=CG.x, [1]=CG.y, [2]=CG.z, [3]=volume, [4]=surface_area, [5]=mass, [6+]=moments.
                    // (Verified empirically against a 50mm cube: [0.025,0.025,0.025,0.000125,0.015,...].)
                    int massStatus = 0;
                    object rawProps = modelDoc.GetMassProperties2(ref massStatus);
                    double[] props = rawProps as double[];
                    if (props == null && rawProps is object[] objArr)
                        props = System.Array.ConvertAll(objArr, o => (double)o);

                    if (props == null || props.Length < 5)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "ANALYSIS_FAILED",
                            "GetMassProperties2 returned null or insufficient data. Ensure the document has at least one solid body.");

                    // GetMassProperties2 layout: [0]=cx, [1]=cy, [2]=cz, [3]=volume, [4]=surface_area
                    results.Add($"volume={props[3]:G6}");
                    results.Add($"surface_area={props[4]:G6}");
                    results.Add($"cx={props[0]:G6}");
                    results.Add($"cy={props[1]:G6}");
                    results.Add($"cz={props[2]:G6}");
                }
                else if (analysisType == "geometry")
                {
                    var partDoc = modelDoc as IPartDoc;
                    if (partDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE",
                            "analysis_type='geometry' requires a part document.");

                    object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                    int bodyCount = bodies != null ? bodies.Length : 0;
                    int totalFaces = 0, totalEdges = 0, totalVerts = 0;

                    if (bodies != null)
                    {
                        foreach (var b in bodies)
                        {
                            var body = b as IBody2;
                            if (body == null) continue;
                            totalFaces += body.GetFaceCount();
                            totalEdges += body.GetEdgeCount();
                            totalVerts += body.GetVertexCount();
                        }
                    }

                    results.Add($"bodies={bodyCount}");
                    results.Add($"faces={totalFaces}");
                    results.Add($"edges={totalEdges}");
                    results.Add($"vertices={totalVerts}");
                }
                else if (analysisType == "edges")
                {
                    // Edge list with start/end/MIDPOINT 3D coords — so a caller can pick an edge to
                    // select (e.g. edge_flange's ex/ey/ez = an edge midpoint) WITHOUT blind-guessing
                    // coordinates or reverse-deriving the extrude direction from mass_properties.
                    var partDoc = modelDoc as IPartDoc;
                    if (partDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE",
                            "analysis_type='edges' requires a part document.");
                    JObject edgeAnalysis = ReadEdges(partDoc);
                    results.Add(edgeAnalysis.ToString(Newtonsoft.Json.Formatting.None));
                }
                else if (analysisType == "faces")
                {
                    // Planar-face list with centroid/normal/area + a stable index — the twin of 'edges'
                    // (ADR-027). Lets a caller pick a face to sketch on (create_sketch on_face + face_index)
                    // WITHOUT a coordinate pick, which is fragile on a revolve end-cap (the face centroid can
                    // sit on the revolve axis/origin → ambiguous SelectByID2). Baby reference-resolver → P1.3.
                    var partDoc = modelDoc as IPartDoc;
                    if (partDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE",
                            "analysis_type='faces' requires a part document.");
                    JObject faceAnalysis = ReadFaces(partDoc);
                    results.Add(faceAnalysis.ToString(Newtonsoft.Json.Formatting.None));
                }
                else if (analysisType == "features")
                {
                    // Feature-tree read for the "analyze -> build a variant" loop. Read-only.
                    // Packs a single consolidated JSON string into Features (the only field the
                    // adapter's response_mapper surfaces to the host). A COMPACT recipe: the ordered
                    // feature tree (name/type, suppressed only when true), each feature's driving
                    // display-dimensions (deduped, rounded SI), a SKETCH SUMMARY (counts + an inline
                    // circle for single-full-circle profiles — NO per-segment coordinate dump),
                    // pattern semantics, and equations / globals. For one sketch's exact geometry use
                    // analysis_type='sketch'.
                    JObject analysis = BuildFeatureTreeAnalysis(modelDoc);
                    results.Add(analysis.ToString(Newtonsoft.Json.Formatting.None));
                }
                else if (analysisType == "sketch")
                {
                    // Detail mode: ONE sketch's full geometry (every segment, rounded), on demand — so
                    // the common 'features' recipe stays cheap and you pay for coordinates only where
                    // you actually need them. Find the sketch feature by its exact tree name.
                    string sketchName = p?.Value<string>("name");
                    if (string.IsNullOrEmpty(sketchName))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER",
                            "analysis_type='sketch' requires 'name' (sketch feature name, e.g. 'Sketch2'). " +
                            "Call analysis_type='features' first to list sketch names.");

                    var allNames = new List<string>();
                    var sketchFeat = FindFeatureByName(modelDoc, sketchName, allNames);
                    if (sketchFeat == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FEATURE_NOT_FOUND",
                            $"No feature named '{sketchName}'. Available: {string.Join(", ", allNames)}");

                    var theSketch = sketchFeat.GetSpecificFeature2() as ISketch;
                    if (theSketch == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_FEATURE_TYPE",
                            $"Feature '{sketchName}' is not a sketch (type '{sketchFeat.GetTypeName2()}'). " +
                            "Pass a sketch/ProfileFeature name.");

                    var sObj = new JObject();
                    sObj["name"] = sketchFeat.Name;
                    sObj["sketch"] = ReadSketch(theSketch, true); // FULL geometry, rounded
                    var pl = ReadSketchPlane(theSketch);
                    if (pl != null) sObj["plane"] = pl;
                    results.Add(sObj.ToString(Newtonsoft.Json.Formatting.None));
                }
                else
                {
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_ANALYSIS_TYPE",
                        $"analysis_type '{analysisType}' is not supported. Supported: mass_properties, geometry, edges, faces, features, sketch.");
                }

                // AnalyzeModel does NOT increment state_version — read-only, same pattern as VerifyState
                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = results,
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
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

        // get_selection — report what the USER currently has selected in the SolidWorks GUI, mapped to the
        // SAME stable indices analyze_model(faces/edges) reports. The INVERSE of index-based selection
        // (ADR-033): instead of the AI telling SW "select face i", the human picks in the GUI and the AI
        // reads back which face/edge index it was — so a user can point at geometry mid-conversation and the
        // AI acts on it (face → create_sketch(on_face, face_index); edge → add_edge_feature(edge_indices)).
        // Read-only: does NOT clear the selection (would wipe the user's pick) and does NOT bump
        // state_version (same pattern as AnalyzeModel/VerifyState). Baby reference-resolver → feeds P1.3.
        public ExecutionResponse GetSelection(ToolRequest request)
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
                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var selMgr = modelDoc.SelectionManager as ISelectionMgr;
                int count = selMgr?.GetSelectedObjectCount2(-1) ?? 0;

                // Enumerate part faces/edges LAZILY — only when a face/edge is actually in the selection — so
                // a vertex/plane/empty selection costs no body-walk. Same enumeration analyze_model uses, so
                // the matched index is the one create_sketch(face_index)/add_edge_feature(edge_indices) expect.
                var partDoc = modelDoc as IPartDoc;
                List<IFace2> allFaces = null;
                List<IEdge> allEdges = null;

                var selArr = new JArray();
                for (int s = 1; s <= count; s++)
                {
                    // GetSelectedObjectType3 → swSelectType_e (inlined ints, ADR-018): 1=EDGES, 2=FACES,
                    // 3=VERTICES, 4=DATUMPLANES. (Never cast to the swconst enum TYPE → would load swconst.dll.)
                    int t = selMgr.GetSelectedObjectType3(s, -1);
                    object obj = selMgr.GetSelectedObject6(s, -1);
                    JObject so;
                    if (t == 2 && obj is IFace2 selFace)
                    {
                        if (allFaces == null) allFaces = FlattenFaces(partDoc);
                        int i = IndexByJson(allFaces, BuildFaceJson(selFace, -1), BuildFaceJson);
                        so = BuildFaceJson(selFace, i);
                        so["type"] = "face";
                    }
                    else if (t == 1 && obj is IEdge selEdge)
                    {
                        if (allEdges == null) allEdges = FlattenEdges(partDoc);
                        int i = IndexByJson(allEdges, BuildEdgeJson(selEdge, -1), BuildEdgeJson);
                        so = BuildEdgeJson(selEdge, i);
                        so["type"] = "edge";
                    }
                    else if (t == 3 && obj is IVertex selVert)
                    {
                        so = new JObject { ["type"] = "vertex" };
                        var pt = selVert.GetPoint() as double[];
                        if (pt != null && pt.Length >= 3)
                            so["point"] = new JArray { R6(pt[0]), R6(pt[1]), R6(pt[2]) };
                    }
                    else if (t == 4)
                    {
                        so = new JObject { ["type"] = "plane", ["name"] = (obj as IFeature)?.Name };
                    }
                    else
                    {
                        so = new JObject { ["type"] = "other", ["sw_type"] = t };
                    }
                    selArr.Add(so);
                }

                var root = new JObject();
                root["selected_count"] = count;
                root["selection"] = selArr;

                // Read-only, same response shape as AnalyzeModel — does NOT bump state_version, no MarkComplete.
                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name,
                        Features = new List<string> { root.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
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

        // Flatten all solid-body faces / edges in the SAME order analyze_model walks (GetBodies2(swSolidBody,
        // true) → GetFaces()/GetEdges()), so a list index here equals the `i` analyze_model reports.
        private List<IFace2> FlattenFaces(IPartDoc partDoc)
        {
            var list = new List<IFace2>();
            object[] bodies = partDoc?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies != null)
                foreach (var b in bodies)
                {
                    var body = b as IBody2;
                    if (body == null) continue;
                    object[] fs = body.GetFaces() as object[];
                    if (fs == null) continue;
                    foreach (var f in fs) { var fa = f as IFace2; if (fa != null) list.Add(fa); }
                }
            return list;
        }

        private List<IEdge> FlattenEdges(IPartDoc partDoc)
        {
            var list = new List<IEdge>();
            object[] bodies = partDoc?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies != null)
                foreach (var b in bodies)
                {
                    var body = b as IBody2;
                    if (body == null) continue;
                    object[] es = body.GetEdges() as object[];
                    if (es == null) continue;
                    foreach (var e in es) { var ed = e as IEdge; if (ed != null) list.Add(ed); }
                }
            return list;
        }

        // Match a selected entity to its index in the enumerated list by GEOMETRY: build each candidate's
        // JSON (idx -1, so the `i` field doesn't interfere) and DeepEquals it against the selected entity's
        // JSON. Reuses the SAME BuildFaceJson/BuildEdgeJson readers, so the match key is exactly the reported
        // geometry. (We can't use IEntity.IsSameAs — not exposed by this interop version — and RCW reference
        // equality is unreliable across separate COM calls.) Collisions need two geometrically identical
        // entities, which a valid solid doesn't have. -1 if no match.
        private static int IndexByJson<T>(List<T> items, JObject target, Func<T, int, JObject> build)
        {
            for (int i = 0; i < items.Count; i++)
                if (JToken.DeepEquals(build(items[i], -1), target)) return i;
            return -1;
        }

        // -----------------------------------------------------------------------
        // activate_document — switch the active SolidWorks document by title.
        // Lifecycle-ish: changes which open doc subsequent ops target, not geometry. Like ensure_ready it
        // does NOT check/bump state_version (it's process-global and the activated doc's geometry is
        // unchanged). Lets a caller compare against another open part without OS window hacks.
        // -----------------------------------------------------------------------
        public ExecutionResponse ActivateDocument(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string title = p?.Value<string>("title");
                if (string.IsNullOrEmpty(title))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "title is required (the open document's title, e.g. 'gear' or 'gear.SLDPRT').");

                int errors = 0;
                // 3rd arg = swRebuildOnActivation_e; 1 = swDontRebuildActiveDoc (literal, don't load swconst).
                var doc = _solidWorks.ActivateDoc3(title, false, 1, ref errors) as IModelDoc2;
                if (doc == null &&
                    !title.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase) &&
                    !title.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) &&
                    !title.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase))
                {
                    // Retry with the part extension if the bare title didn't resolve.
                    doc = _solidWorks.ActivateDoc3(title + ".SLDPRT", false, 1, ref errors) as IModelDoc2;
                }
                if (doc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_NOT_FOUND",
                        $"Could not activate '{title}' (errors={errors}). Pass the exact title of an OPEN document.");

                ExecLog.Write($"activate_document: '{title}' -> active '{doc.GetTitle()}'");
                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = doc.GetTitle(),
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = new List<string>(),
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
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

        // -----------------------------------------------------------------------
        // M19 — add_reference_geometry
        // -----------------------------------------------------------------------
        public ExecutionResponse AddReferenceGeometry(ToolRequest request)
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
                string geoType = (p?.Value<string>("type") ?? "").ToLowerInvariant();

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var featureMgr = modelDoc.FeatureManager;
                IFeature feature = null;

                if (geoType == "plane")
                {
                    string refPlaneName = p?.Value<string>("ref_plane_name");
                    double offset = p?.Value<double?>("offset") ?? 0.0;
                    if (string.IsNullOrEmpty(refPlaneName))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER",
                            "plane requires ref_plane_name (e.g. 'Front Plane') and offset (distance in meters).");

                    modelDoc.ClearSelection2(true);
                    bool sel = SelectPlaneFlexible(modelDoc, refPlaneName, false, 0); // language-independent (ADR-007)
                    if (!sel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "PLANE_NOT_FOUND", $"Plane '{refPlaneName}' not found.");

                    feature = featureMgr.InsertRefPlane(
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance,
                        offset, 0, 0, 0, 0) as IFeature;
                }
                else if (geoType == "axis")
                {
                    string entity1Name = p?.Value<string>("entity1_name");
                    string entity1Type = (p?.Value<string>("entity1_type") ?? "PLANE").ToUpperInvariant();
                    string entity2Name = p?.Value<string>("entity2_name");
                    string entity2Type = (p?.Value<string>("entity2_type") ?? "PLANE").ToUpperInvariant();

                    if (string.IsNullOrEmpty(entity1Name) || string.IsNullOrEmpty(entity2Name))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER",
                            "axis requires entity1_name and entity2_name (e.g. 'Top Plane' and 'Right Plane').");

                    modelDoc.ClearSelection2(true);
                    // For PLANE-type entities use the language-independent resolver (ADR-007);
                    // other types (EDGE, etc.) keep exact-name selection.
                    bool sel1 = entity1Type == "PLANE"
                        ? SelectPlaneFlexible(modelDoc, entity1Name, false, 1)
                        : modelDoc.Extension.SelectByID2(entity1Name, entity1Type, 0, 0, 0, false, 1, null, 0);
                    if (!sel1)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "ENTITY_NOT_FOUND",
                            $"Entity '{entity1Name}' (type '{entity1Type}') not found.");

                    bool sel2 = entity2Type == "PLANE"
                        ? SelectPlaneFlexible(modelDoc, entity2Name, true, 2)
                        : modelDoc.Extension.SelectByID2(entity2Name, entity2Type, 0, 0, 0, true, 2, null, 0);
                    if (!sel2)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "ENTITY_NOT_FOUND",
                            $"Entity '{entity2Name}' (type '{entity2Type}') not found.");

                    bool axisInserted = modelDoc.InsertAxis2(false);
                    if (!axisInserted)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "REFERENCE_GEOMETRY_FAILED",
                            "InsertAxis2 failed. Verify the two selected planes/edges are valid.");
                    // Get the last inserted feature (the reference axis just created)
                    object[] allFeaturesAxis = modelDoc.FeatureManager.GetFeatures(true) as object[];
                    feature = allFeaturesAxis != null && allFeaturesAxis.Length > 0
                        ? allFeaturesAxis[allFeaturesAxis.Length - 1] as IFeature
                        : null;
                }
                else if (geoType == "point")
                {
                    double px = p?.Value<double?>("px") ?? 0.0;
                    double py = p?.Value<double?>("py") ?? 0.0;
                    double pz = p?.Value<double?>("pz") ?? 0.0;

                    modelDoc.ClearSelection2(true);
                    bool sel = modelDoc.Extension.SelectByID2("", "VERTEX", px, py, pz, false, 0, null, 0);
                    if (!sel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "VERTEX_NOT_FOUND",
                            $"No vertex found at ({px}, {py}, {pz}). Coordinates must match an existing vertex exactly.");

                    feature = featureMgr.InsertReferencePoint(
                        (int)swRefPointType_e.swRefPointIntersection, 0, 0.0, 1) as IFeature;
                }
                else
                {
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_GEOMETRY_TYPE",
                        $"type '{geoType}' is not supported. Supported: plane, axis, point.");
                }

                if (feature == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "REFERENCE_GEOMETRY_FAILED",
                        $"Failed to create reference {geoType}. Verify the referenced entities are valid and the document has no active sketch.");

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
                        ActiveSketch = null,
                        Features = new List<string> { feature.Name },
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
        // M20 — create_pattern
        // -----------------------------------------------------------------------
        public ExecutionResponse CreatePattern(ToolRequest request)
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
                string patternType = (p?.Value<string>("pattern_type") ?? "").ToLowerInvariant();
                string featureName = p?.Value<string>("feature_name");

                if (patternType != "linear" && patternType != "circular")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "pattern_type is required: 'linear' or 'circular'.");
                if (string.IsNullOrEmpty(featureName))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "feature_name is required (name of the feature to pattern, e.g. 'Boss-Extrude1').");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (modelDoc.SketchManager.ActiveSketch != null)
                    modelDoc.SketchManager.InsertSketch(true);

                var featureMgr = modelDoc.FeatureManager;
                IFeature feature = null;

                if (patternType == "linear")
                {
                    string direction = (p?.Value<string>("direction") ?? "X").ToUpperInvariant();
                    double spacing = p?.Value<double?>("spacing") ?? 0.01;
                    int count = p?.Value<int?>("count") ?? 2;
                    int count2 = p?.Value<int?>("count2") ?? 1;
                    double spacing2 = p?.Value<double?>("spacing2") ?? 0.01;

                    // Select seed feature with mark=4
                    modelDoc.ClearSelection2(true);
                    bool featSel = modelDoc.Extension.SelectByID2(featureName, "BODYFEATURE", 0, 0, 0, false, 4, null, 0);
                    if (!featSel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FEATURE_NOT_FOUND",
                            $"Feature '{featureName}' not found. Ensure the name matches the feature tree entry exactly.");

                    // Select direction entity with mark=1:
                    // X→Right Plane (normal=X), Y→Top Plane (normal=Y), Z→Front Plane (normal=Z)
                    string dirPlane;
                    switch (direction)
                    {
                        case "Y": dirPlane = "Top Plane";   break;
                        case "Z": dirPlane = "Front Plane"; break;
                        default:  dirPlane = "Right Plane"; break; // X
                    }
                    // append=true keeps the seed feature (mark=4) selected; language-independent (ADR-007)
                    bool dirSel = SelectPlaneFlexible(modelDoc, dirPlane, true, 1);
                    if (!dirSel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "DIRECTION_NOT_FOUND",
                            $"Direction plane '{dirPlane}' not found. Ensure default planes exist in the document.");

                    feature = featureMgr.FeatureLinearPattern3(
                        count, spacing,
                        count2, spacing2,
                        false, false,
                        null, null,
                        false, false) as IFeature; // geometryPattern=false (SW default). NOTE: disjoint instances warn & are skipped — see KNOWN-LIMITATIONS.md; geometryPattern=true returns null here, not a safe blanket default.
                }
                else // circular
                {
                    string axisName = p?.Value<string>("axis_name");
                    int count = p?.Value<int?>("count") ?? 4;
                    double angleDeg = p?.Value<double?>("angle") ?? 90.0;
                    double angleRad = angleDeg * Math.PI / 180.0;
                    // equal_spacing=true (default): angle = TOTAL angle spread across all instances (pass
                    // 360 for a full ring). equal_spacing=false: angle = the angle BETWEEN adjacent
                    // instances (matches a model authored that way; can exceed 360 and wrap/overlap).
                    bool equalSpacing = p?.Value<bool?>("equal_spacing") ?? true;

                    if (string.IsNullOrEmpty(axisName))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER",
                            "circular pattern requires axis_name (name of the rotation axis, e.g. 'Axis1'). Create one first with add_reference_geometry.");

                    // Select seed feature with mark=4
                    modelDoc.ClearSelection2(true);
                    bool featSel = modelDoc.Extension.SelectByID2(featureName, "BODYFEATURE", 0, 0, 0, false, 4, null, 0);
                    if (!featSel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FEATURE_NOT_FOUND",
                            $"Feature '{featureName}' not found.");

                    // Select axis with mark=1 — try AXIS type first, then REFERENCECURVES
                    bool axisSel = modelDoc.Extension.SelectByID2(axisName, "AXIS", 0, 0, 0, true, 1, null, 0);
                    if (!axisSel)
                        axisSel = modelDoc.Extension.SelectByID2(axisName, "REFERENCECURVES", 0, 0, 0, true, 1, null, 0);
                    if (!axisSel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "AXIS_NOT_FOUND",
                            $"Axis '{axisName}' not found. Create a reference axis with add_reference_geometry first.");

                    feature = featureMgr.FeatureCircularPattern4(
                        count, angleRad,
                        false,
                        null,
                        false, equalSpacing, false) as IFeature; // geometryPattern=false (SW default); see KNOWN-LIMITATIONS.md re: disjoint instances
                }

                if (feature == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "PATTERN_FAILED",
                        $"Pattern feature creation returned null. Verify seed feature and direction/axis are valid.");

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
                        ActiveSketch = null,
                        Features = new List<string> { feature.Name },
                        Dimensions = new List<string>()
                    },
                    // In-band body summary so the caller can verify the pattern without a separate analyze.
                    ResultGeometry = BuildBodySummary(modelDoc),
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
        // M21 — set_part_material
        // -----------------------------------------------------------------------
        public ExecutionResponse SetPartMaterial(ToolRequest request)
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
                string materialName = p?.Value<string>("material_name");
                string library = p?.Value<string>("library") ?? "SolidWorks Materials";

                if (string.IsNullOrEmpty(materialName))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "material_name is required (e.g. 'AISI 1020 Steel (SS)', 'Aluminum 1060 Alloy', 'ABS').");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (!(modelDoc is IPartDoc))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE",
                        "set_part_material requires an active part document, not a drawing or assembly.");

                var partDoc = modelDoc as IPartDoc;
                if (partDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_CAST_FAILED", "Active document is not castable to IPartDoc.");

                // Apply material — "" config name = all configurations
                partDoc.SetMaterialPropertyName2("", library, materialName);

                // Verify material was applied
                string appliedMaterial = partDoc.GetMaterialPropertyName2("", out string appliedDb);
                if (string.IsNullOrEmpty(appliedMaterial))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MATERIAL_NOT_FOUND",
                        $"Material '{materialName}' in library '{library}' was not found or could not be applied. Check spelling and ensure the material library is installed.");

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
                        ActiveSketch = null,
                        Features = new List<string> { $"material={appliedMaterial}", $"library={appliedDb}" },
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
        // M22 — sheet_metal_feature
        // -----------------------------------------------------------------------
        public ExecutionResponse SheetMetalFeature(ToolRequest request)
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
                string featureType = (p?.Value<string>("feature_type") ?? "").ToLowerInvariant();

                if (featureType != "base_flange" && featureType != "edge_flange" && featureType != "flat_pattern")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "feature_type is required: 'base_flange', 'edge_flange', or 'flat_pattern'.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var featureMgr = modelDoc.FeatureManager;
                IFeature feature = null;

                if (featureType == "base_flange")
                {
                    double thickness = p?.Value<double?>("thickness") ?? 0.001;
                    double bendRadius = p?.Value<double?>("bend_radius") ?? thickness;
                    double kFactor = p?.Value<double?>("k_factor") ?? 0.5;

                    // Base flange needs the profile sketch SELECTED. After exiting a sketch nothing is
                    // selected, so InsertSheetMetalBaseFlange2 had no profile and returned null. Capture the
                    // active sketch name before exiting, then re-select it by name (mirrors revolve/sweep,
                    // ADR-011/012). See ADR-015.
                    // A closed-profile base flange (flat tab) needs the WHOLE closed sketch contour selected,
                    // not a single segment (selecting one segment makes SW treat it as an open profile, which
                    // needs ExtrudeDist1 > 0 and otherwise returns null). Capture the active sketch name, exit
                    // the sketch, then select the sketch FEATURE by name (the whole contour). Runs on STA (P0.1).
                    string baseProfileName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name;
                    if (modelDoc.SketchManager.ActiveSketch != null)
                        modelDoc.SketchManager.InsertSketch(true);
                    modelDoc.ClearSelection2(true);
                    if (!string.IsNullOrEmpty(baseProfileName))
                        modelDoc.Extension.SelectByID2(baseProfileName, "SKETCH", 0, 0, 0, false, 0, null, 0);

                    // InsertSheetMetalBaseFlange2 signature:
                    // (Thickness, ThickenDir, Radius, ExtrudeDist1, ExtrudeDist2, FlipExtruDir,
                    //  EndCondition1, EndCondition2, DirToUse, CustomBendAllowance PCBA,
                    //  UseDefaultRelief, ReliefType, ReliefWidth, ReliefDepth, ReliefRatio,
                    //  UseReliefRatio, Merge, UseFeatScope, UseAutoSelect)
                    // The legacy InsertSheetMetalBaseFlange2 returned null on SW2026 even with a correctly
                    // selected closed contour on the STA thread (verified live). The modern, reliable pattern
                    // is CreateDefinition (gives a pre-populated IBaseFlangeFeatureData) → set params →
                    // CreateFeature, with the closed sketch contour selected. (ADR-015/016)
                    var bfData = featureMgr.CreateDefinition((int)swFeatureNameID_e.swFmBaseFlange) as IBaseFlangeFeatureData;
                    if (bfData == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SHEET_METAL_FAILED", "CreateDefinition(swFmBaseFlange) returned null.");

                    bfData.OverrideThickness = true;
                    bfData.Thickness = thickness;
                    bfData.ReverseThickness = false;
                    bfData.OverrideRadius = true;
                    bfData.BendRadius = bendRadius;
                    bfData.OverrideKFactor = true;
                    bfData.KFactor = kFactor;

                    feature = featureMgr.CreateFeature(bfData) as IFeature;
                }
                else if (featureType == "edge_flange")
                {
                    // Legacy InsertSheetMetalEdgeFlange2 returns null silently on SW2026 (same fragility as
                    // base flange — separate from the MTA/STA issue). Implemented via the modern pattern:
                    // CreateDefinition(swFmEdgeFlange) -> IEdgeFlangeFeatureData -> AddEdges -> set params ->
                    // CreateFeature (ADR-016). Param values reverse-engineered from a manual edge flange:
                    // OffsetType/OffsetDistance is the Flange LENGTH group (OffsetDistance = flange_length);
                    // PositionType=1 = swFlangePositionTypeMaterialInside.
                    double? ex = p?.Value<double?>("ex");
                    double? ey = p?.Value<double?>("ey");
                    double? ez = p?.Value<double?>("ez");
                    double flangeLength = p?.Value<double?>("flange_length") ?? 0.02;
                    double angleDeg = p?.Value<double?>("angle") ?? 90.0;
                    double angleRad = angleDeg * Math.PI / 180.0;

                    if (ex == null || ey == null || ez == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER",
                            "edge_flange requires ex, ey, ez (3D coordinates of a point on the target edge).");

                    modelDoc.ClearSelection2(true);
                    bool sel = modelDoc.Extension.SelectByID2("", "EDGE", ex.Value, ey.Value, ez.Value, false, 0, null, 0);
                    if (!sel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDGE_NOT_FOUND",
                            $"No edge found at ({ex.Value}, {ey.Value}, {ez.Value}). Coordinates must be on or very near the edge.");

                    var efSelMgr = modelDoc.SelectionManager as ISelectionMgr;
                    var flangeEdge = efSelMgr?.GetSelectedObject6(1, -1) as Edge;
                    if (flangeEdge == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDGE_NOT_FOUND",
                            $"Entity at ({ex.Value}, {ey.Value}, {ez.Value}) is not an edge.");

                    // Edge flange is a TWO-step API: (1) IModelDoc2.InsertSketchForEdgeFlange generates the
                    // default flange profile sketch on the selected edge, then (2) InsertSheetMetalEdgeFlange2
                    // consumes the edge + that sketch. The legacy/CreateDefinition one-shots all returned null
                    // or 'SketchNotSpecified' precisely because no profile sketch existed (the API does NOT
                    // auto-generate it). BooleanOptions requests default radius + relief ratio + default relief.
                    int boolOpts = (int)(swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRadius
                                       | swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseReliefRatio
                                       | swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief);

                    var efSketch = modelDoc.InsertSketchForEdgeFlange(flangeEdge, angleRad, false) as IFeature;
                    ExecLog.Write($"edge_flange: InsertSketchForEdgeFlange -> {(efSketch == null ? "NULL" : efSketch.Name)}");
                    // The profile sketch is left open/active — exit it before creating the flange feature.
                    if (modelDoc.SketchManager.ActiveSketch != null)
                        modelDoc.SketchManager.InsertSketch(true);

                    if (efSketch != null)
                    {
                        // Re-select the edge (sketch creation/exit can clear the selection list).
                        modelDoc.ClearSelection2(true);
                        modelDoc.Extension.SelectByID2("", "EDGE", ex.Value, ey.Value, ez.Value, false, 0, null, 0);

                        feature = featureMgr.InsertSheetMetalEdgeFlange2(
                            new Edge[] { flangeEdge }, new Feature[] { (Feature)efSketch },
                            boolOpts, angleRad, 0.0,
                            (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialInside,
                            flangeLength,
                            (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular,
                            0.5, 0.001, 0.001, 0, null) as IFeature;
                        ExecLog.Write($"edge_flange: InsertSheetMetalEdgeFlange2 -> {(feature == null ? "NULL" : feature.Name)}");

                        // The flange length comes from the auto-generated profile sketch (a small default),
                        // NOT from FlangeOffsetDist when a sketch is supplied. Force the requested length by
                        // editing the created feature's definition: AccessSelections (valid for an existing
                        // feature, unlike a fresh CreateDefinition) -> set OffsetDistance -> ModifyDefinition.
                        if (feature != null)
                        {
                            var efDef = feature.GetDefinition() as IEdgeFlangeFeatureData;
                            if (efDef != null)
                            {
                                bool acc = efDef.AccessSelections(modelDoc, null);
                                ExecLog.Write($"edge_flange: pre-modify OffsetType={efDef.OffsetType} OffsetDistance={efDef.OffsetDistance} acc={acc}");
                                efDef.OffsetDistance = flangeLength;
                                bool mod = feature.ModifyDefinition(efDef, modelDoc, null);
                                ExecLog.Write($"edge_flange: ModifyDefinition len={flangeLength} -> mod={mod}");
                            }
                        }

                        // Commit the geometry so analyze_model/verify_state read the rebuilt body.
                        modelDoc.EditRebuild3();
                    }
                }
                else // flat_pattern
                {
                    // Flatten = UNSUPPRESS the Flat-Pattern feature (exactly what the UI "Flatten" button
                    // does). `SetBendState(Flattened)` only flips a state flag — the displayed/B-rep body
                    // re-folds on rebuild (verified live: after SetBendState+rebuild the CG was unchanged
                    // from the bent state, i.e. still folded). Locate Flat-Pattern1 by type, unsuppress it,
                    // then EditRebuild3 to commit the unfolded body so it shows and analyze_model reads flat.
                    object[] allFeatures = modelDoc.FeatureManager.GetFeatures(true) as object[];
                    IFeature flatFeat = null;
                    if (allFeatures != null)
                    {
                        foreach (var f in allFeatures)
                        {
                            var ft = f as IFeature;
                            if (ft != null && ft.GetTypeName2() == "FlatPattern") { flatFeat = ft; break; }
                        }
                    }
                    if (flatFeat == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FLAT_PATTERN_FAILED",
                            "No Flat-Pattern feature found. Ensure the part has a sheet metal base flange.");

                    bool unsup = flatFeat.SetSuppression2(
                        (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                        (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    modelDoc.EditRebuild3();
                    int bendStateAfter = modelDoc.GetBendState();
                    ExecLog.Write($"flat_pattern: unsuppress={unsup} bendState={bendStateAfter}");
                    feature = flatFeat;
                }

                if (feature == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SHEET_METAL_FAILED",
                        $"Sheet metal feature '{featureType}' creation returned null. Verify the sketch profile (for base_flange) or edge selection (for edge_flange) is correct.");

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
                        ActiveSketch = null,
                        Features = new List<string> { feature.Name },
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
        // modify_dimension — UNIVERSAL parametric value edit (the variants keystone).
        // Sets a named display dimension's value (e.g. "D1@Boss-Extrude1@Part.Part") in SI units
        // (meters for length, radians for angle), rebuilds, then echoes the EFFECTIVE value back in
        // result_geometry for in-band verification (ADR-023 spirit). Bumps state_version (geometry
        // changes — unlike analyze). Flow: analyze_model(features) reports every feature's dimension
        // full-names + SI values → pick a name → modify_dimension(name, value).
        // -----------------------------------------------------------------------
        public ExecutionResponse ModifyDimension(ToolRequest request)
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
                string name = p != null ? p.Value<string>("name") : null;
                double? value = p != null ? p.Value<double?>("value") : null;

                if (string.IsNullOrEmpty(name))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "name is required (the dimension full-name, e.g. 'D1@Boss-Extrude1@Part.Part' — get it from analyze_model(features)).");
                if (value == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "value is required (SI units: meters for length, radians for angle).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (modelDoc.SketchManager.ActiveSketch != null)
                    modelDoc.SketchManager.InsertSketch(true);

                var dim = modelDoc.Parameter(name) as IDimension;
                if (dim == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DIMENSION_NOT_FOUND",
                        $"Dimension '{name}' not found. Use the exact full-name reported by analyze_model(features) (e.g. 'D1@Boss-Extrude1@Part.Part').");

                // SI setter (meters/radians). SystemValue writes the document-units (SI) value.
                dim.SystemValue = value.Value;
                modelDoc.EditRebuild3(); // commit so analyze_model/verify_state read the new geometry

                // Read the effective value back (a rebuild may clamp it or an equation may drive it).
                // Same reader analyze uses: GetSystemValue3(1=swThisConfiguration, ...) — inlined int (ADR-018).
                double effective = value.Value;
                try
                {
                    var sv = dim.GetSystemValue3(1, null) as double[];
                    if (sv != null && sv.Length > 0) effective = sv[0];
                    else effective = dim.SystemValue;
                }
                catch { try { effective = dim.SystemValue; } catch { } }

                ExecLog.Write($"modify_dimension: '{name}' requested={value.Value} effective={effective}");

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
                        ActiveSketch = null,
                        Features = new List<string>(),
                        Dimensions = new List<string> { name }
                    },
                    ResultGeometry = new JObject
                    {
                        ["dimension"] = name,
                        ["requested"] = value.Value,
                        ["effective"] = effective
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
        // edit_feature — discriminated STRUCTURAL edit of a named feature.
        // action: suppress | unsuppress | delete | rename (rename needs new_name). Each rebuilds and
        // bumps state_version. RESOLVER PROBE (P1.3): suppress/delete CHANGE topology, so selectors or
        // coordinates captured earlier may no longer resolve — re-query with analyze_model afterwards.
        // -----------------------------------------------------------------------
        public ExecutionResponse EditFeature(ToolRequest request)
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
                string featureName = p != null ? p.Value<string>("feature_name") : null;
                string action = ((p != null ? p.Value<string>("action") : null) ?? "").ToLowerInvariant();
                string newName = p != null ? p.Value<string>("new_name") : null;

                if (string.IsNullOrEmpty(featureName))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "feature_name is required (exact feature-tree name, e.g. 'Boss-Extrude1').");
                if (action != "suppress" && action != "unsuppress" && action != "delete" && action != "rename")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "action is required: 'suppress', 'unsuppress', 'delete', or 'rename'.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (modelDoc.SketchManager.ActiveSketch != null)
                    modelDoc.SketchManager.InsertSketch(true);

                var allNames = new List<string>();
                var feature = FindFeatureByName(modelDoc, featureName, allNames);
                if (feature == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FEATURE_NOT_FOUND",
                        $"Feature '{featureName}' not found. Available features: [{string.Join(", ", allNames)}].");

                string resultName = featureName;
                switch (action)
                {
                    case "suppress":
                        // swSuppressFeature=0, swThisConfiguration=1 (inlined enum constants, ADR-018).
                        feature.SetSuppression2(
                            (int)swFeatureSuppressionAction_e.swSuppressFeature,
                            (int)swInConfigurationOpts_e.swThisConfiguration, null);
                        break;
                    case "unsuppress":
                        feature.SetSuppression2(
                            (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                            (int)swInConfigurationOpts_e.swThisConfiguration, null);
                        break;
                    case "rename":
                        if (string.IsNullOrEmpty(newName))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "new_name is required for action='rename'.");
                        feature.Name = newName;
                        resultName = newName;
                        break;
                    case "delete":
                        // Select the FOUND IFeature directly (type-agnostic) then EditDelete. The old
                        // SelectByID2(..., "BODYFEATURE", ...) only matched body features and FAILED on
                        // sketches / reference geometry (a sketch is "SKETCH", not "BODYFEATURE"); selecting
                        // the feature object we already resolved via FindFeatureByName deletes ANY type.
                        modelDoc.ClearSelection2(true);
                        bool sel = feature.Select2(false, 0);
                        if (!sel)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "FEATURE_NOT_FOUND",
                                $"Feature '{featureName}' could not be selected for deletion.");
                        modelDoc.EditDelete();
                        break;
                }

                modelDoc.EditRebuild3(); // commit so analyze_model/verify_state reflect the edit
                ExecLog.Write($"edit_feature: {action} '{featureName}'" + (action == "rename" ? $" -> '{newName}'" : ""));

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
                        ActiveSketch = null,
                        Features = action == "delete" ? new List<string>() : new List<string> { resultName },
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
        /// Language-independent default-plane selection. SolidWorks localizes the default
        /// plane names (e.g. Turkish "Ön Düzlem", Italian "Piano frontale"), so SelectByID2
        /// by the English name fails on non-English installs. We first try the given name
        /// (English installs keep working), then fall back to mapping Front/Top/Right to the
        /// first three RefPlane features in tree order (the default planes), selecting the
        /// feature object directly — no name involved.
        /// </summary>
        private bool SelectPlaneFlexible(IModelDoc2 modelDoc, string planeName, bool append, int mark)
        {
            // 1) Try by name (works on English installs, and for explicitly-named planes).
            if (modelDoc.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, append, mark, null, 0))
                return true;

            // 2) Map a canonical default-plane name to its ordinal among the default planes.
            string n = (planeName ?? string.Empty).ToLowerInvariant();
            int ordinal;
            if (n.Contains("front")) ordinal = 0;
            else if (n.Contains("top")) ordinal = 1;
            else if (n.Contains("right")) ordinal = 2;
            else return false; // not a default plane name we can resolve language-independently

            // 3) Walk the feature tree, collect RefPlane features in order; the first three are
            //    the default planes (Front, Top, Right). Select the Nth directly.
            var features = modelDoc.FeatureManager.GetFeatures(true) as object[];
            if (features == null) return false;

            int seen = 0;
            foreach (var obj in features)
            {
                var feat = obj as IFeature;
                if (feat == null) continue;
                if (feat.GetTypeName2() != "RefPlane") continue;
                if (seen == ordinal)
                {
                    if (!append) modelDoc.ClearSelection2(true);
                    return feat.Select2(append, mark);
                }
                seen++;
            }
            return false;
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

        // -----------------------------------------------------------------------
        // analyze_model "features" helpers — read-only feature-tree extraction.
        // -----------------------------------------------------------------------

        // Tree folders / lights carry no design intent; skip them so the output is just
        // the meaningful feature tree (sketches, solid features, patterns, planes, equations).
        private static readonly HashSet<string> NoiseFeatureTypes = new HashSet<string>
        {
            "HistoryFolder", "SensorFolder", "CommentsFolder", "FavoriteFolder", "DetailCabinet",
            "MaterialFolder", "EnvironmentFolder", "DocsFolder", "BlocksFolder", "SunFolder",
            "AmbientLight", "DirectionLight", "PointLight", "SpotLight",
            // UI / annotation / body-container folders that carry no design intent — they leaked into
            // the feature list (8 of the gear's 25 "features" were these). Equations have their own
            // section (ReadEquations), so the EqnFolder entry is redundant too.
            "SelectionSetFolder", "NotesAreaFtrFolder", "SurfaceBodyFolder", "SolidBodyFolder",
            "EnvFolder", "InkMarkupFolder", "EqnFolder"
        };

        private JObject BuildFeatureTreeAnalysis(IModelDoc2 modelDoc)
        {
            var root = new JObject();
            var featArr = new JArray();
            int meaningful = 0;

            // Complete tree walk: FirstFeature/GetNextFeature (top-level, in tree order) PLUS each
            // feature's sub-features (folder contents). GetFeatures(true) skips features grouped into
            // folders, which is how a real Cut-Extrude went missing from the analysis.
            var seen = new HashSet<string>();
            // Dedup driving dimensions across the whole tree by full-name (first feature to report a
            // dim owns it) — kills the repeat where e.g. D1@Sketch1 showed under BOTH the sketch and
            // the extrude that consumes it.
            var seenDims = new HashSet<string>();
            var feat = modelDoc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 5000)
            {
                string type = feat.GetTypeName2() ?? "";
                if (!NoiseFeatureTypes.Contains(type) && seen.Add(feat.Name))
                {
                    featArr.Add(BuildFeatureJson(feat, modelDoc, seenDims, false));
                    meaningful++;
                }

                // One level of sub-features (covers folders that group real features). Sketches consumed
                // by an extrude also appear here — already counted as top-level, so dedup by name skips them;
                // genuinely folder-hidden features are NOT top-level, so they get added.
                var sub = feat.GetFirstSubFeature() as IFeature;
                int subGuard = 0;
                while (sub != null && subGuard++ < 2000)
                {
                    string stype = sub.GetTypeName2() ?? "";
                    if (!NoiseFeatureTypes.Contains(stype) && seen.Add(sub.Name))
                    {
                        var sj = BuildFeatureJson(sub, modelDoc, seenDims, false);
                        sj["in_folder"] = feat.Name;
                        featArr.Add(sj);
                        meaningful++;
                    }
                    sub = sub.GetNextSubFeature() as IFeature;
                }

                feat = feat.GetNextFeature() as IFeature;
            }

            root["feature_count"] = meaningful;
            root["features"] = featArr;
            root["equations"] = ReadEquations(modelDoc);
            return root;
        }

        // seenDims: cross-feature dimension dedup (null = no dedup, e.g. a single-sketch read).
        // fullSketch: true = emit every sketch segment (the 'sketch' detail mode); false = a compact
        // summary (counts + an inline circle for the common single-full-circle profile, no segments).
        private JObject BuildFeatureJson(IFeature feat, IModelDoc2 modelDoc, HashSet<string> seenDims, bool fullSketch)
        {
            var fj = new JObject();
            fj["name"] = feat.Name;
            string type = feat.GetTypeName2() ?? "";
            fj["type"] = type;
            // Only emit suppressed when TRUE — 'suppressed:false' on every feature was pure noise.
            try { if (feat.IsSuppressed()) fj["suppressed"] = true; } catch { }

            var dims = ReadDisplayDimensions(feat, seenDims);
            if (dims.Count > 0) fj["dimensions"] = dims;

            var sketch = feat.GetSpecificFeature2() as ISketch;
            if (sketch != null)
            {
                var sk = ReadSketch(sketch, fullSketch);
                // Skip empty sketches (e.g. the Origin's degenerate sketch emitted "counts":{} noise).
                var counts = sk["counts"] as JObject;
                bool hasContent = (counts != null && counts.Count > 0)
                                  || sk["circle"] != null
                                  || (sk["segments"] is JArray segs && segs.Count > 0);
                if (hasContent)
                {
                    fj["sketch"] = sk;
                    var plane = ReadSketchPlane(sketch);
                    if (plane != null) fj["plane"] = plane;
                }
            }

            if (type == "CirPattern" || type == "LPattern")
                TryReadPattern(feat, type, fj);

            // Extrude/cut end-condition + depth (blind) + reverse flag, so a rebuild knows HOW it was made.
            var ex = ReadExtrude(feat);
            if (ex != null) fj["extrude"] = ex;

            return fj;
        }

        // Read an extrude/cut feature's end-condition + depth (blind only) + the raw ReverseDirection flag.
        // We deliberately do NOT synthesize an absolute axis ("+Z"/"-Z"): tested on the gear, SW's cut
        // direction is "into material", which a simple sketch-normal × reverse does NOT capture (a top-face
        // pocket reads reverse=false yet cuts -Z), so a computed axis would be WRONG for cuts. The sketch
        // plane's offset (≠0 ⇒ a face) already gives the real direction cue; `reversed` is ground truth.
        // A guaranteed-correct absolute direction is deferred (KNOWN-LIMITATIONS #19).
        private JObject ReadExtrude(IFeature feat)
        {
            try
            {
                var ed = feat.GetDefinition() as IExtrudeFeatureData;
                if (ed == null) return null;
                var ej = new JObject();
                int endc = ed.GetEndCondition(true);
                ej["end"] = EndConditionName(endc);
                if (endc == (int)swEndConditions_e.swEndCondBlind)
                    ej["depth"] = R6(ed.GetDepth(true));
                if (ed.ReverseDirection) ej["reversed"] = true;
                return ej;
            }
            catch { return null; }
        }

        // swEndConditions_e → short label. The enum constants in the comparisons are compile-time folded to
        // ints, so the swconst assembly is never loaded at runtime (same safe pattern as line ~1012/ADR-018).
        private static string EndConditionName(int e)
        {
            if (e == (int)swEndConditions_e.swEndCondBlind) return "blind";
            if (e == (int)swEndConditions_e.swEndCondThroughAll) return "through_all";
            if (e == (int)swEndConditions_e.swEndCondThroughNext) return "through_next";
            if (e == (int)swEndConditions_e.swEndCondUpToVertex) return "up_to_vertex";
            if (e == (int)swEndConditions_e.swEndCondUpToSurface) return "up_to_surface";
            if (e == (int)swEndConditions_e.swEndCondOffsetFromSurface) return "offset_from_surface";
            if (e == (int)swEndConditions_e.swEndCondUpToBody) return "up_to_body";
            if (e == (int)swEndConditions_e.swEndCondMidPlane) return "mid_plane";
            return "end_" + e;
        }

        // Sketch plane/face in MODEL space — origin + normal — so a rebuild knows WHICH plane/face a
        // sketch sits on (e.g. Front Plane z=0 vs a part face at z=0.02). Derived from the sketch
        // transform's inverse (sketch→model): translation = origin, mapped Z-axis = normal. Best-effort.
        private JObject ReadSketchPlane(ISketch sketch)
        {
            try
            {
                var m2s = sketch.ModelToSketchTransform;
                if (m2s == null) return null;
                var s2m = m2s.IInverse();
                if (s2m == null) return null;
                double[] d = s2m.ArrayData as double[];
                if (d == null || d.Length < 12) return null;
                double ox = d[9], oy = d[10], oz = d[11];
                double nx = d[2], ny = d[5], nz = d[8];
                var pj = new JObject();
                string refName = DefaultPlaneForAxis(PrincipalAxis(nx, ny, nz));
                if (refName != null)
                {
                    // Canonical English default-plane name, computed from the NORMAL — never read from the
                    // localized plane name, so it's correct on a Turkish/any-language SW (create_sketch maps
                    // the English name language-independently via SelectPlaneFlexible, ADR-007). offset =
                    // signed perpendicular distance from the global origin: 0 ⇒ the default plane itself;
                    // ≠0 ⇒ a parallel surface at that height (sketch on the face there, or an offset plane).
                    pj["ref"] = refName;
                    double off = R6(ox * nx + oy * ny + oz * nz);
                    if (Math.Abs(off) > 1e-9) pj["offset"] = off;
                }
                else
                {
                    // Non-axis-aligned (angled face) — fall back to raw origin + normal.
                    pj["origin"] = new JArray { R6(ox), R6(oy), R6(oz) };
                    pj["normal"] = new JArray { R6(nx), R6(ny), R6(nz) };
                }
                return pj;
            }
            catch { return null; }
        }

        // Map an (approximately) axis-aligned unit vector to its principal axis "X"/"Y"/"Z", else null.
        private static string PrincipalAxis(double x, double y, double z)
        {
            double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
            const double near1 = 0.999, near0 = 0.01;
            if (az >= near1 && ax <= near0 && ay <= near0) return "Z";
            if (ay >= near1 && ax <= near0 && az <= near0) return "Y";
            if (ax >= near1 && ay <= near0 && az <= near0) return "X";
            return null;
        }

        // Canonical English default plane whose normal is the given axis (standard SW part template:
        // Front=Z, Top=Y, Right=X — confirmed live).
        private static string DefaultPlaneForAxis(string axis)
        {
            switch (axis)
            {
                case "Z": return "Front Plane";
                case "Y": return "Top Plane";
                case "X": return "Right Plane";
                default: return null;
            }
        }

        // Generic numeric-parameter recovery: every feature's driving dimensions (extrude depth,
        // fillet radius, hole diameter, revolve angle, ...) without per-type casting. SI units
        // (meters for length, radians for angle).
        private JArray ReadDisplayDimensions(IFeature feat, HashSet<string> seenDims)
        {
            var arr = new JArray();
            object dispObj = feat.GetFirstDisplayDimension();
            int guard = 0;
            while (dispObj != null && guard++ < 500)
            {
                var disp = dispObj as IDisplayDimension;
                if (disp != null)
                {
                    var dim = disp.GetDimension2(0) as IDimension;
                    if (dim != null)
                    {
                        string fullName = dim.FullName;
                        // First feature to report a dimension owns it; skip the repeat that shows up on
                        // a later feature (a sketch's dim re-appears on the extrude that consumes it).
                        if (seenDims == null || seenDims.Add(fullName))
                        {
                            var dj = new JObject();
                            dj["name"] = fullName;
                            double val = dim.Value;
                            try
                            {
                                // 1 = swThisConfiguration. Literal, not the swconst enum: using a swconst
                                // type at runtime forces loading SolidWorks.Interop.swconst (not deployed),
                                // which fails — the codebase relies on enum constants being inlined instead.
                                var sv = dim.GetSystemValue3(1, null) as double[];
                                if (sv != null && sv.Length > 0) val = sv[0];
                            }
                            catch { }
                            dj["value_si"] = R6(val);
                            arr.Add(dj);
                        }
                    }
                }
                dispObj = feat.GetNextDisplayDimension(dispObj);
            }
            return arr;
        }

        // full=false (recipe/summary): counts (zero buckets dropped) + an inline {cx,cy,r} when the
        //   profile is a single full circle (the common bore/boss case — fully defined, lossless, tiny).
        //   No per-segment coordinate dump.
        // full=true (the 'sketch' detail mode): every segment's geometry (rounded), for an exact rebuild.
        private JObject ReadSketch(ISketch sketch, bool full)
        {
            var sj = new JObject();
            var segArr = new JArray();
            int lines = 0, arcs = 0, splines = 0, ellipses = 0, other = 0, construction = 0;
            int realCount = 0; JObject lastReal = null;

            object[] segs = sketch.GetSketchSegments() as object[];
            if (segs != null)
            {
                foreach (var s in segs)
                {
                    var seg = s as ISketchSegment;
                    if (seg == null) continue;
                    var ej = ReadSegment(seg);
                    if (ej == null) continue;
                    if (ej.Value<bool?>("construction") == true) construction++;
                    else { realCount++; lastReal = ej; }
                    switch (ej.Value<string>("kind"))
                    {
                        case "line": lines++; break;
                        case "arc/circle": arcs++; break;
                        case "spline": splines++; break;
                        case "ellipse": ellipses++; break;
                        default: other++; break;
                    }
                    if (full) segArr.Add(ej);
                }
            }

            // Compact counts — omit the zero buckets that bloated the old payload.
            var counts = new JObject();
            if (lines > 0) counts["line"] = lines;
            if (arcs > 0) counts["arc_circle"] = arcs;
            if (splines > 0) counts["spline"] = splines;
            if (ellipses > 0) counts["ellipse"] = ellipses;
            if (other > 0) counts["other"] = other;
            if (construction > 0) counts["construction"] = construction;
            sj["counts"] = counts;

            if (full)
            {
                sj["segments"] = segArr;
            }
            else if (realCount == 1 && lastReal != null
                     && lastReal.Value<string>("kind") == "arc/circle"
                     && lastReal["radius"] != null && lastReal["x1"] == null)
            {
                // Single full circle (ReadSegment drops start/end for a full circle, so x1 is absent):
                // center + radius fully define it — emit inline, no detail-mode round-trip needed.
                sj["circle"] = new JObject
                {
                    ["cx"] = lastReal["cx"],
                    ["cy"] = lastReal["cy"],
                    ["r"] = lastReal["radius"]
                };
            }
            return sj;
        }

        // Read ONE sketch segment's geometry into a JObject. Shared by analyze_model (ReadSketch) and
        // the create-tool in-band echo (AddSketchEntity result_geometry) so analyze's output and a
        // create's reported geometry are byte-for-byte the same shape — the round-trip invariant
        // ("analyze ⊆ create") is enforced by construction, not by two parallel readers.
        private JObject ReadSegment(ISketchSegment seg)
        {
            if (seg == null) return null;
            var ej = new JObject();
            try
            {
                // Construction flag carries design intent (reference scaffolding). Without it,
                // a rebuild would draw construction lines as real profile edges.
                bool isCon = false;
                try { isCon = seg.ConstructionGeometry; } catch { }
                ej["construction"] = isCon;

                // swSketchSegments_e as plain ints (0=line 1=arc/circle 2=ellipse 3=spline)
                // so the runtime never loads SolidWorks.Interop.swconst as a type.
                int segType = seg.GetType();
                switch (segType)
                {
                    case 0:
                        ej["kind"] = "line";
                        var ln = seg as ISketchLine;
                        var a = ln.IGetStartPoint2();
                        var b = ln.IGetEndPoint2();
                        if (a != null) { ej["x1"] = R6(a.X); ej["y1"] = R6(a.Y); }
                        if (b != null) { ej["x2"] = R6(b.X); ej["y2"] = R6(b.Y); }
                        break;
                    case 1:
                        ej["kind"] = "arc/circle";
                        var ar = seg as ISketchArc;
                        var c = ar.IGetCenterPoint2();
                        if (c != null) { ej["cx"] = R6(c.X); ej["cy"] = R6(c.Y); }
                        ej["radius"] = R6(ar.GetRadius());
                        // Start/end points let a rebuild recreate a PARTIAL arc via
                        // add_sketch_entity(arc/arc_center). For a FULL circle start==end, so they're
                        // redundant (center+radius suffice) — omit them; the absence of x1 marks a full circle.
                        var asp = ar.IGetStartPoint2();
                        var aep = ar.IGetEndPoint2();
                        bool fullCircle = asp != null && aep != null
                            && Math.Abs(asp.X - aep.X) < 1e-9 && Math.Abs(asp.Y - aep.Y) < 1e-9;
                        if (!fullCircle)
                        {
                            if (asp != null) { ej["x1"] = R6(asp.X); ej["y1"] = R6(asp.Y); }
                            if (aep != null) { ej["x2"] = R6(aep.X); ej["y2"] = R6(aep.Y); }
                        }
                        break;
                    case 2:
                        // Ellipse: center + major-axis point + minor-axis point — exactly the inputs
                        // add_sketch_entity(ellipse) consumes (cx,cy / x1,y1 / x2,y2), so it round-trips.
                        ej["kind"] = "ellipse";
                        var el = seg as ISketchEllipse;
                        var ec = el.IGetCenterPoint2();
                        var emaj = el.IGetMajorPoint2();
                        var emin = el.IGetMinorPoint2();
                        if (ec != null) { ej["cx"] = R6(ec.X); ej["cy"] = R6(ec.Y); }
                        if (emaj != null) { ej["x1"] = R6(emaj.X); ej["y1"] = R6(emaj.Y); }
                        if (emin != null) { ej["x2"] = R6(emin.X); ej["y2"] = R6(emin.Y); }
                        break;
                    case 3:
                        // Spline: best-effort through-points (x,y per point). NOTE: recreating a spline
                        // from through-points yields a VISUALLY-equivalent but not bit-identical curve
                        // (SW stores control points + tangency/curvature, not just interpolation points)
                        // — see KNOWN-LIMITATIONS. Enough for a faithful rebuild, not a lossless one.
                        ej["kind"] = "spline";
                        var sp = seg as ISketchSpline;
                        try
                        {
                            // GetPoints2 returns an array of ISketchPoint COM objects (the spline's
                            // through/interpolation points), NOT a flat double[] — read X,Y from each.
                            var rawArr = sp.GetPoints2() as Array;
                            if (rawArr != null && rawArr.Length > 0)
                            {
                                var pts = new JArray();
                                foreach (var o in rawArr)
                                {
                                    var skPt = o as ISketchPoint;
                                    if (skPt == null) continue;
                                    pts.Add(R6(skPt.X));
                                    pts.Add(R6(skPt.Y));
                                }
                                if (pts.Count > 0) ej["points"] = pts;
                            }
                        }
                        catch { }
                        break;
                    default:
                        ej["kind"] = "other";
                        break;
                }
            }
            catch { return null; }
            return ej;
        }

        // Pattern instance count is a feature parameter, NOT a display dimension — read it from
        // the feature definition. The gear case ("36 teeth") depends on this. Best-effort.
        private void TryReadPattern(IFeature feat, string type, JObject fj)
        {
            try
            {
                object def = feat.GetDefinition();
                if (type == "CirPattern")
                {
                    var cd = def as ICircularPatternFeatureData;
                    if (cd != null)
                    {
                        int instances = cd.TotalInstances;
                        double spacingRad = cd.Spacing;
                        bool equalSpacing = cd.EqualSpacing;
                        double spacingDeg = spacingRad * 180.0 / Math.PI;

                        // Disambiguated form — kills the "30@15° vs 24@15°" guesswork (the gear case).
                        // SW stores 'Spacing' as the angle BETWEEN adjacent instances when EqualSpacing
                        // is FALSE (it can exceed 360 and WRAP, overlapping earlier instances), and as
                        // the TOTAL spread when EqualSpacing is TRUE (instances are evenly distributed,
                        // so they never overlap). We report spacing in DEGREES (rounded) + the EFFECTIVE
                        // distinct-instance count + a wrapping flag. The raw radian 'angle_rad' and the
                        // 'inter_angle_deg' (== spacing_deg) duplicates were dropped as redundant.
                        fj["instances"] = instances;
                        fj["equal_spacing"] = equalSpacing;
                        fj["spacing_deg"] = R6(spacingDeg);
                        if (equalSpacing)
                        {
                            // Evenly distributed across 'spacingDeg' total → no overlap by construction.
                            fj["total_angle_deg"] = R6(spacingDeg);
                            fj["distinct_instances"] = instances;
                            fj["wraps"] = false;
                        }
                        else
                        {
                            // Per-instance angle. Instances sit at i*spacing (i=0..N-1); two coincide when
                            // their angles differ by a multiple of 360 → fewer DISTINCT teeth than stored.
                            fj["total_angle_deg"] = R6(spacingDeg * instances);
                            int distinct = CountDistinctAngles(instances, spacingDeg);
                            fj["distinct_instances"] = distinct;
                            fj["wraps"] = distinct < instances;
                        }
                    }
                }
                else
                {
                    var ld = def as ILinearPatternFeatureData;
                    if (ld != null)
                    {
                        fj["d1_instances"] = ld.D1TotalInstances;
                        fj["d1_spacing_si"] = R6(ld.D1Spacing);
                    }
                }
            }
            catch { }
        }

        // Count how many of N circular-pattern instances land on DISTINCT angular positions when each
        // is placed 'interDeg' degrees from the previous. Two instances coincide when their angles are
        // equal mod 360 (within tolerance) — this is what collapses "30 stored @ 15°" to "24 teeth".
        private static int CountDistinctAngles(int instances, double interDeg)
        {
            const double tol = 1e-4; // degrees
            var seen = new List<double>();
            for (int i = 0; i < instances; i++)
            {
                double ang = (i * interDeg) % 360.0;
                if (ang < 0) ang += 360.0;
                bool dup = false;
                foreach (var s in seen)
                {
                    double d = Math.Abs(s - ang);
                    if (d < tol || Math.Abs(d - 360.0) < tol) { dup = true; break; }
                }
                if (!dup) seen.Add(ang);
            }
            return seen.Count;
        }

        // Enumerate every solid body's edges with start/end/MIDPOINT 3D coords (+ length). The midpoint
        // is the natural pick-point for coordinate-based selection (edge_flange ex/ey/ez), so the caller
        // never has to guess where an edge is or reverse-derive the extrude direction. Best-effort per edge.
        // One FeatureCut4 invocation with the full (verbose) parameter list factored out, so the cut path
        // can try a direction / normalCut combination cleanly and retry the flipped direction.
        private IFeature FeatureCutOnce(IFeatureManager featureMgr, bool dir, int endCond, double depthVal, bool normalCut)
        {
            return featureMgr.FeatureCut4(
                true, false, dir,
                endCond, 0,
                depthVal, 0,
                false, false, false, false,
                0, 0,
                false, false, false, false,
                normalCut, true, true,
                false, false, false,
                (int)swStartConditions_e.swStartSketchPlane, 0,
                false, false) as IFeature;
        }

        // Locate the sheet-metal Flat-Pattern feature (always the last top-level feature in a sheet-metal
        // part, usually suppressed), or null for a non-sheet-metal part. Used to roll the bar before it so
        // new features insert ahead of it.
        private IFeature FindFlatPattern(IModelDoc2 modelDoc)
        {
            try
            {
                object[] allFeatures = modelDoc.FeatureManager.GetFeatures(true) as object[];
                if (allFeatures != null)
                {
                    foreach (var f in allFeatures)
                    {
                        var ft = f as IFeature;
                        if (ft != null && ft.GetTypeName2() == "FlatPattern") return ft;
                    }
                }
            }
            catch { }
            return null;
        }

        // Locate a feature by its exact tree name, walking top-level features AND one level of
        // sub-features (folder contents) — same traversal as analyze's tree walk, so a feature
        // analyze reports can always be found here. Collects every visited name into namesOut for a
        // helpful "available features" error list. Returns null if not found.
        private IFeature FindFeatureByName(IModelDoc2 modelDoc, string name, List<string> namesOut)
        {
            IFeature found = null;
            var feat = modelDoc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 5000)
            {
                if (namesOut != null) namesOut.Add(feat.Name);
                if (found == null && feat.Name == name) found = feat;

                var sub = feat.GetFirstSubFeature() as IFeature;
                int subGuard = 0;
                while (sub != null && subGuard++ < 2000)
                {
                    if (namesOut != null) namesOut.Add(sub.Name);
                    if (found == null && sub.Name == name) found = sub;
                    sub = sub.GetNextSubFeature() as IFeature;
                }

                feat = feat.GetNextFeature() as IFeature;
            }
            return found;
        }

        // Small post-build body summary for in-band verification (ADR-023 spirit): volume + face/edge
        // counts, so a caller can confirm an extrude/cut/pattern step from the response itself instead of
        // a separate analyze_model + manual volume arithmetic. Read-only; not part of state/idempotency.
        private JObject BuildBodySummary(IModelDoc2 modelDoc)
        {
            try
            {
                var s = new JObject();
                int st = 0;
                object raw = modelDoc.GetMassProperties2(ref st);
                double[] mp = raw as double[];
                if (mp == null && raw is object[] oa) mp = Array.ConvertAll(oa, o => (double)o);
                if (mp != null && mp.Length >= 4) s["volume"] = R6(mp[3]);
                var partDoc = modelDoc as IPartDoc;
                if (partDoc != null)
                {
                    object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                    int faces = 0, edges = 0;
                    if (bodies != null)
                        foreach (var b in bodies)
                        {
                            var body = b as IBody2;
                            if (body == null) continue;
                            faces += body.GetFaceCount();
                            edges += body.GetEdgeCount();
                        }
                    s["faces"] = faces;
                    s["edges"] = edges;
                }
                return s;
            }
            catch { return null; }
        }

        // Round a coordinate/length (meters) to 6 decimals = 1µm. Below SW's selection tolerance,
        // so pick-points stay valid, while the edges JSON stays small enough for the MCP result cap.
        private static double R6(double v) { return Math.Round(v, 6); }

        // Single-edge JSON (start/end/MIDPOINT, +length for closed edges) — the shared reader behind both
        // ReadEdges and get_selection, so an analyzed edge and a user-selected edge report identical fields
        // (the "analyze ⊆ everything else" invariant, ADR-023 spirit). `idx` is the edge's stable index.
        private JObject BuildEdgeJson(IEdge edge, int idx)
        {
            var ej = new JObject();
            ej["i"] = idx;
            try
            {
                // GetCurveParams2 layout: [0..2]=start xyz, [3..5]=end xyz, [6]=uStart, [7]=uEnd, [8]=sense.
                var cp = edge.GetCurveParams2() as double[];
                if (cp != null && cp.Length >= 8)
                {
                    // Round to 6 decimals (1µm — far below selection tolerance). Keeps the JSON compact
                    // (a 304-edge part otherwise blows the MCP result token cap) AND kills near-zero noise.
                    ej["start"] = new JArray { R6(cp[0]), R6(cp[1]), R6(cp[2]) };
                    ej["end"] = new JArray { R6(cp[3]), R6(cp[4]), R6(cp[5]) };
                    double dx = cp[0] - cp[3], dy = cp[1] - cp[4], dz = cp[2] - cp[5];
                    double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (chord > 1e-9)
                    {
                        // Open edge (the common case): the chord midpoint (start+end)/2 is EXACT for a line
                        // and a robust pick-point for an arc. We do NOT call IGetCurve()/GetLength3() here —
                        // on a large part those per-edge COM round-trips dominate (gear: 26s of a 58s total).
                        ej["mid"] = new JArray { R6((cp[0] + cp[3]) / 2.0), R6((cp[1] + cp[4]) / 2.0), R6((cp[2] + cp[5]) / 2.0) };
                    }
                    else
                    {
                        // Closed edge (full circle, start==end): the chord midpoint is the center, not on the
                        // edge — need the curve for an on-edge midpoint. Rare (a handful per part), so the
                        // IGetCurve cost is negligible; we also report true length since we hold the curve.
                        var curve = edge.IGetCurve();
                        if (curve != null)
                        {
                            var mid = curve.Evaluate2((cp[6] + cp[7]) / 2.0, 0) as double[];
                            if (mid != null && mid.Length >= 3)
                                ej["mid"] = new JArray { R6(mid[0]), R6(mid[1]), R6(mid[2]) };
                            try { ej["length"] = R6(curve.GetLength3(cp[6], cp[7])); } catch { }
                        }
                    }
                }
            }
            catch { }
            return ej;
        }

        // Single-face JSON (planar flag + normal/area/representative point for planar faces) — the shared
        // reader behind both ReadFaces and get_selection. `idx` is the face's stable index.
        private JObject BuildFaceJson(IFace2 face, int idx)
        {
            var fj = new JObject();
            fj["i"] = idx;
            try
            {
                var surf = face.IGetSurface();
                bool planar = surf != null && surf.IsPlane();
                fj["planar"] = planar;
                try { fj["area"] = R6(face.GetArea()); } catch { }
                if (planar)
                {
                    // Plane normal (model space) — the sketch-plane direction for on_face.
                    var n = face.Normal as double[];
                    if (n != null && n.Length >= 3)
                        fj["normal"] = new JArray { R6(n[0]), R6(n[1]), R6(n[2]) };
                    // A representative point ON the plane (UV-mid of the trimmed face). Informational —
                    // selection is by index, so this need not be strictly inside a face with holes.
                    var uv = face.GetUVBounds() as double[];
                    if (uv != null && uv.Length >= 4)
                    {
                        var pt = surf.Evaluate((uv[0] + uv[1]) / 2.0, (uv[2] + uv[3]) / 2.0, 0, 0) as double[];
                        if (pt != null && pt.Length >= 3)
                            fj["point"] = new JArray { R6(pt[0]), R6(pt[1]), R6(pt[2]) };
                    }
                }
            }
            catch { }
            return fj;
        }

        private JObject ReadEdges(IPartDoc partDoc)
        {
            var root = new JObject();
            var edgesArr = new JArray();
            int idx = 0;
            // Edge enumeration is COM-round-trip-bound (SolidWorks is out-of-process). On a large part
            // (gear = 304 edges) the per-edge curve calls dominated, so we keep them off the open-edge
            // path (see below) and log the total for visibility on big models.
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies != null)
            {
                foreach (var b in bodies)
                {
                    var body = b as IBody2;
                    if (body == null) continue;
                    object[] edges = body.GetEdges() as object[];
                    if (edges == null) continue;
                    foreach (var e in edges)
                    {
                        var edge = e as IEdge;
                        if (edge == null) continue;
                        edgesArr.Add(BuildEdgeJson(edge, idx++));
                    }
                }
            }
            swTotal.Stop();
            ExecLog.Write($"ReadEdges: edges={edgesArr.Count} total={swTotal.ElapsedMilliseconds}ms");
            root["edge_count"] = edgesArr.Count;
            root["edges"] = edgesArr;
            return root;
        }

        // Face list with a stable index — the twin of ReadEdges (ADR-027). The `i` matches the same
        // GetBodies2(swSolidBody,true) → GetFaces() enumeration create_sketch(on_face, face_index) walks,
        // so a caller picks a face to sketch on by index instead of a fragile coordinate pick. Per planar
        // face we report normal + area + a representative on-plane point; non-planar faces still get an
        // index (so the numbering stays aligned) but no normal.
        private JObject ReadFaces(IPartDoc partDoc)
        {
            var root = new JObject();
            var facesArr = new JArray();
            int idx = 0;
            object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies != null)
            {
                foreach (var b in bodies)
                {
                    var body = b as IBody2;
                    if (body == null) continue;
                    object[] faces = body.GetFaces() as object[];
                    if (faces == null) continue;
                    foreach (var f in faces)
                    {
                        var face = f as IFace2;
                        if (face == null) continue;
                        facesArr.Add(BuildFaceJson(face, idx++));
                    }
                }
            }
            ExecLog.Write($"ReadFaces: faces={facesArr.Count}");
            root["face_count"] = facesArr.Count;
            root["faces"] = facesArr;
            return root;
        }

        private JArray ReadEquations(IModelDoc2 modelDoc)
        {
            var arr = new JArray();
            try
            {
                var em = modelDoc.GetEquationMgr();
                if (em != null)
                {
                    int n = em.GetCount();
                    for (int i = 0; i < n; i++)
                    {
                        var ej = new JObject();
                        try { ej["equation"] = em.get_Equation(i); } catch { }
                        try { ej["value"] = em.get_Value(i); } catch { }
                        try { ej["global"] = em.get_GlobalVariable(i); } catch { }
                        arr.Add(ej);
                    }
                }
            }
            catch { }
            return arr;
        }

        private ExecutionResponse BuildFailed(string operationId, int stateVersion, string code, string message)
        {
            return new ExecutionResponse
            {
                OperationId = operationId,
                Status = "FAILED",
                Verified = false,
                StateVersion = stateVersion,
                CadState = null,
                Error = new ExecutionError
                {
                    Code = code,
                    Message = message
                }
            };
        }
    }
}
