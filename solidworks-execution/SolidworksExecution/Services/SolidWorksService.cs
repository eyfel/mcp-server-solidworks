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

        // open_document — open an EXISTING file from disk (the counterpart to open_new_part, which only
        // makes a BLANK doc). Native .sldprt/.sldasm/.slddrw open directly; foreign .ipt/.CATPart/STEP/etc.
        // import as a PART via SolidWorks 3D Interconnect / translators (install/license dependent — a
        // failure returns a clear OPEN_FAILED so the caller can defer that sample, per the drawing plan).
        // Bumps state_version (a new active document is established), same effect class as open_new_part.
        public ExecutionResponse OpenDocument(ToolRequest request)
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
                string filePath = p != null ? p.Value<string>("file_path") : null;
                if (string.IsNullOrEmpty(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path is required.");
                if (!System.IO.File.Exists(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_NOT_FOUND", $"No file at '{filePath}'.");

                // Document type from the extension. swconst values inlined as constants (ADR-018).
                string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                int docType;
                switch (ext)
                {
                    case ".sldasm": docType = (int)swDocumentTypes_e.swDocASSEMBLY; break;
                    case ".slddrw": docType = (int)swDocumentTypes_e.swDocDRAWING; break;
                    // .sldprt AND foreign part formats (.ipt/.CATPart/.step/.stp/.iges/.igs/.x_t/.x_b/...)
                    // open as a PART (3D Interconnect imports the foreign ones).
                    default: docType = (int)swDocumentTypes_e.swDocPART; break;
                }

                int errors = 0, warnings = 0;
                var modelDoc = _solidWorks.OpenDoc6(filePath, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;

                // FOREIGN-FORMAT staged fallback (2026-07-09, proven live): with 3D Interconnect OFF,
                // OpenDoc6 does NOT import foreign files — it returns null with the misleading
                // errors=2097152 (swFileRequiresRepairError; observed on both .step and NX .prt).
                // Stage 2: LoadFile4 = the CLASSIC translator import (STEP/IGES; content decides
                // part vs assembly). Stage 3: NX/Creo/CATIA native formats have no classic
                // translator — they REQUIRE Interconnect, so enable it (documented prerequisite,
                // swMultiCAD_Enable3DInterconnect=691) and retry OpenDoc6 once; left ON afterwards
                // (the steady state for mixed-CAD sample folders).
                bool foreign = ext != ".sldprt" && ext != ".sldasm" && ext != ".slddrw";
                string openPath = "OpenDoc6";
                // as_assembly=true (foreign only): the file is an ASSEMBLY STEP/etc. The classic
                // translator (LoadFile4) imports an assembly STEP as a MULTIBODY PART (proven live
                // 2026-07-09 on Fixture Assembly.step) — only 3D Interconnect maps it to a real
                // assembly with components. Enable the toggle just for this open and RESTORE it
                // after, so foreign PART opens elsewhere keep their proven classic-import behavior.
                bool asAssembly = p.Value<bool?>("as_assembly") ?? false;
                if (modelDoc == null && foreign && asAssembly)
                {
                    bool prior = _solidWorks.GetUserPreferenceToggle(691); // swMultiCAD_Enable3DInterconnect
                    bool onlyParts = _solidWorks.GetUserPreferenceToggle(758); // swMultiCAD_ApplyOnlyToParts — excludes assemblies from Interconnect when ON
                    try
                    {
                        if (!prior) _solidWorks.SetUserPreferenceToggle(691, true);
                        if (onlyParts) _solidWorks.SetUserPreferenceToggle(758, false);
                        errors = 0; warnings = 0;
                        modelDoc = _solidWorks.OpenDoc6(filePath, (int)swDocumentTypes_e.swDocASSEMBLY,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
                        openPath = "OpenDoc6+3DInterconnect(as_assembly)";
                        ExecLog.Write($"open_document(as_assembly): prior691={prior} onlyParts758={onlyParts} errors={errors} warnings={warnings} null={modelDoc == null}");
                    }
                    finally
                    {
                        if (!prior) _solidWorks.SetUserPreferenceToggle(691, false);
                        if (onlyParts) _solidWorks.SetUserPreferenceToggle(758, true);
                    }
                }
                if (modelDoc == null && foreign)
                {
                    int lfErrors = 0;
                    modelDoc = _solidWorks.LoadFile4(filePath, "r", null, ref lfErrors) as IModelDoc2;
                    openPath = "LoadFile4";
                    if (modelDoc == null && !_solidWorks.GetUserPreferenceToggle(691))
                    {
                        _solidWorks.SetUserPreferenceToggle(691, true);
                        ExecLog.Write("open_document: enabled 3D Interconnect (toggle 691) for foreign-format import");
                        errors = 0; warnings = 0;
                        modelDoc = _solidWorks.OpenDoc6(filePath, docType,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
                        openPath = "OpenDoc6+3DInterconnect";
                    }
                }

                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "OPEN_FAILED",
                        $"Could not open '{filePath}' (OpenDoc6 errors={errors}, then LoadFile4 also null for foreign formats). " +
                        "errors=2097152 is swFileRequiresRepairError — for foreign files this usually means no usable " +
                        "translator; convert to STEP or defer this sample.");
                ExecLog.Write($"open_document: '{System.IO.Path.GetFileName(filePath)}' via {openPath}");

                string typeName = modelDoc is IDrawingDoc ? "DRAWING"
                                : modelDoc is IAssemblyDoc ? "ASSEMBLY" : "PART";

                // ALREADY-OPEN gotcha (the 2026-07-05 PENDING bug, fixed 2026-07-07): OpenDoc6 on an
                // already-open file returns its IModelDoc2 but does NOT activate it — the previously
                // active document silently stays active, so follow-up reads (analyze/save_analysis)
                // hit the WRONG doc. Explicitly activate what we just opened, then verify.
                int actErr = 0;
                _solidWorks.ActivateDoc3(modelDoc.GetTitle(), false, 1 /* swDontRebuildActiveDoc */, ref actErr);
                var nowActive = _solidWorks.IActiveDoc2 as IModelDoc2;
                // Title normalization: an imported doc's title can gain/lose its extension between
                // reads ('Butterfly_valve' vs 'Butterfly_valve.prt' — observed live on an NX
                // import). Compare extension-stripped, case-insensitive.
                Func<string, string> baseTitle = t =>
                    (System.IO.Path.GetFileNameWithoutExtension(t ?? "") ?? "").ToLowerInvariant();
                if (nowActive == null || baseTitle(nowActive.GetTitle()) != baseTitle(modelDoc.GetTitle()))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ACTIVATE_FAILED",
                        $"Opened '{modelDoc.GetTitle()}' but could not make it the ACTIVE document " +
                        $"(active is '{nowActive?.GetTitle()}').");

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
                        DocumentType = typeName,
                        ActiveSketch = null,
                        Features = new List<string> { System.IO.Path.GetFileName(filePath) },
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
                // AddToDB for the WHOLE create (generalizes ADR-022's arc_center-only toggle): all
                // coordinates arrive frozen-exact (analyze round-trip / IR lowering), so SW's input
                // inference/snapping must never touch them. Found live on the 1-2 flange rebuild: a
                // 1.59mm line (raised-face step) built fine in one document, then CreateLine returned
                // NULL for the same coords in the next — the pixel-based snap tolerance depends on the
                // window's zoom, so short segments nondeterministically collapse without AddToDB.
                bool prevAddToDbAll = modelDoc.SketchManager.AddToDB;
                modelDoc.SketchManager.AddToDB = true;
                try
                {
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
                }
                finally
                {
                    modelDoc.SketchManager.AddToDB = prevAddToDbAll;
                }

                if (!created)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ENTITY_CREATION_FAILED", $"Failed to create sketch entity of type '{entityType}'.");

                // construction=true converts the created segment(s) to construction/reference geometry
                // (centerlines, symmetry axes, hole-position scaffolding). Closes the "analyze ⊆ create"
                // gap: ReadSegment already REPORTS the flag, this lets a rebuild REPRODUCE it. Applied
                // post-create because SketchManager has no construction-aware create calls; the
                // result_geometry echo below reads the segment back AFTER the flip, so the caller sees it.
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
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "CONSTRUCTION_FLAG_FAILED",
                            $"Entity created but could not be converted to construction geometry: {ex.Message}");
                    }
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
                // up_to_face_index >= 0 ⇒ up-to-surface end condition: the extrude/cut terminates ON that
                // face (index from analyze_model(faces), same enumeration create_sketch(on_face) walks).
                // Depth is ignored, like through-all.
                int upToFaceIndex = p?.Value<int?>("up_to_face_index") ?? -1;
                bool upToSurface = upToFaceIndex >= 0;
                // mid_plane=true ⇒ swEndCondMidPlane: the feature extrudes SYMMETRICALLY about the
                // sketch plane; depth = the TOTAL width (SolidWorks midplane semantics).
                bool midPlane = p?.Value<bool?>("mid_plane") ?? false;
                if (upToSurface && featureType != "boss" && featureType != "cut")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "up_to_face_index is only valid for feature_type 'boss' or 'cut'.");
                if (midPlane && featureType != "boss" && featureType != "cut")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "mid_plane is only valid for feature_type 'boss' or 'cut'.");
                if (midPlane && (throughAll || upToSurface))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "mid_plane conflicts with through/up_to_face_index — pick ONE end condition.");
                if ((featureType == "boss" || featureType == "cut") && !throughAll && !upToSurface && (depth == null || depth.Value <= 0))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "depth (> 0 meters) is required for a blind or mid-plane boss/cut. Pass through=true for a through-all instead.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                // Direction + end-condition controls (boss/cut). reverse flips the feature direction
                // (needed e.g. for a cut sketched on a part FACE, where "into the body" is the opposite
                // side from a cut sketched on a boundary plane). through = through-all (depth ignored).
                bool reverse = p?.Value<bool?>("reverse") ?? false;
                int endCond = upToSurface
                    ? (int)swEndConditions_e.swEndCondUpToSurface
                    : midPlane
                        ? (int)swEndConditions_e.swEndCondMidPlane
                        : throughAll
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
                    if (upToSurface && !SelectFaceByIndexAppend(modelDoc, upToFaceIndex, 1))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FACE_NOT_FOUND",
                            $"up_to_face_index {upToFaceIndex} could not be selected. Call analyze_model(faces) for valid indices.");

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
                        if (upToSurface && !SelectFaceByIndexAppend(modelDoc, upToFaceIndex, 1))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "FACE_NOT_FOUND",
                                $"up_to_face_index {upToFaceIndex} could not be re-selected for the flipped-direction retry.");
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
                    if (upToSurface && !SelectFaceByIndexAppend(modelDoc, upToFaceIndex, 1))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "FACE_NOT_FOUND",
                            $"up_to_face_index {upToFaceIndex} could not be selected. Call analyze_model(faces) for valid indices.");

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

        public ExecutionResponse CreateRib(ToolRequest request)
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
                double? thickness = p?.Value<double?>("thickness");
                if (thickness == null || thickness.Value <= 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "thickness (> 0 meters) is required.");
                bool twoSided = p?.Value<bool?>("two_sided") ?? true;
                bool reverseThicknessDir = p?.Value<bool?>("reverse_thickness_dir") ?? false;
                bool reverseMaterialDir = p?.Value<bool?>("reverse_material_dir") ?? false;
                bool isNormToSketch = p?.Value<bool?>("is_norm_to_sketch") ?? false;

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                // The ACTIVE sketch is the rib profile (an OPEN sketch — typically a single line;
                // SolidWorks extends it to the surrounding walls). Same consume-the-active-sketch
                // contract as extrude_feature.
                var activeSketchFeat = modelDoc.SketchManager.ActiveSketch as IFeature;
                if (activeSketchFeat == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SKETCH_NOT_ACTIVE", "No active sketch — the rib profile sketch must be active. Call create_sketch + add_sketch_entity first.");
                string profileName = activeSketchFeat.Name;

                // Snapshot the last feature so the void-returning InsertRib can be verified from
                // the tree afterwards (no return value to check — unlike FeatureExtrusion3).
                string lastBefore = LastFeatureName(modelDoc);

                modelDoc.SketchManager.InsertSketch(true);  // exit the sketch
                modelDoc.ClearSelection2(true);
                modelDoc.Extension.SelectByID2(profileName, "SKETCH", 0, 0, 0, false, 0, null, 0);

                // InsertRib(Is2Sided, ReverseThicknessDir, Thickness, ReferenceEdgeIndex,
                //           ReverseMaterialDir, IsDrafted, DraftOutward, DraftAngle,
                //           IsNormToSketch, IsDraftedFromWall) — signature + parameter semantics
                // verified against the LOCAL sldworksapi.chm + interop reflection (ADR-035).
                // Draft deliberately not exposed in v1 (2-1's rib is undrafted; grow on demand).
                modelDoc.FeatureManager.InsertRib(
                    twoSided, reverseThicknessDir, thickness.Value, 0,
                    reverseMaterialDir, false, false, 0.0,
                    isNormToSketch, false);
                modelDoc.EditRebuild3();

                string lastAfter = LastFeatureName(modelDoc);
                if (lastAfter == lastBefore)
                {
                    // Most likely the WRONG material direction (nothing to fill on that side) —
                    // retry flipped, mirroring the cut path's flipped-direction recovery. The
                    // caller's explicit reverse_material_dir is still tried FIRST.
                    modelDoc.ClearSelection2(true);
                    modelDoc.Extension.SelectByID2(profileName, "SKETCH", 0, 0, 0, false, 0, null, 0);
                    modelDoc.FeatureManager.InsertRib(
                        twoSided, reverseThicknessDir, thickness.Value, 0,
                        !reverseMaterialDir, false, false, 0.0,
                        isNormToSketch, false);
                    modelDoc.EditRebuild3();
                    lastAfter = LastFeatureName(modelDoc);
                    ExecLog.Write($"create_rib: flipped-material retry → created={(lastAfter != lastBefore)}");
                }
                if (lastAfter == lastBefore)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "RIB_FAILED",
                        "InsertRib created no feature (tried both material directions). Check the profile sketch is open geometry whose extensions hit existing walls, and thickness is sensible.");

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
                        Features = new List<string> { lastAfter },
                        Dimensions = new List<string>()
                    },
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

        // Name of the LAST feature in the tree (creation-order detection for void-returning
        // feature APIs like InsertRib). Null-safe.
        private static string LastFeatureName(IModelDoc2 modelDoc)
        {
            try
            {
                object[] all = modelDoc.FeatureManager.GetFeatures(true) as object[];
                if (all == null || all.Length == 0) return null;
                return (all[all.Length - 1] as IFeature)?.Name;
            }
            catch { return null; }
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
                bool closeAll = p != null && (p.Value<bool?>("close_all") ?? false);

                if (closeAll)
                {
                    // Close EVERYTHING, including invisibly-loaded component docs that a
                    // per-active-doc close loop can never reach (they are never active).
                    // Live lesson: a lingering doc with the same TITLE blocks opening another
                    // folder's same-named part (swFileWithSameTitleAlreadyOpen = 65536) and
                    // keeps files locked on disk. Discards unsaved changes (true).
                    _solidWorks.CloseAllDocuments(true);
                }
                else
                {
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
                }

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
                string displayMode = (p?.Value<string>("display_mode") ?? "").Trim().ToLowerInvariant();

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

                // Apply display mode (drafting convention). swDisplayMode_e:
                //   0=WIREFRAME, 1=HIDDEN_GREYED (Hidden Lines Visible/HLV), 2=HIDDEN (Hidden Lines Removed/HLR),
                //   3=SHADED, 7=SHADED_EDGES. IView.SetDisplayMode3(UseParent, Mode, Facetted, Edges) — reflection-verified.
                // If display_mode is not given, DEFAULT orthographic views to HLV (drafting rule: show hidden lines
                // for reference, never dimension them); leave isometric at the document default (shaded/HLR).
                int? modeToSet = null;
                switch (displayMode)
                {
                    case "wireframe":                       modeToSet = 0; break;
                    case "hlv":
                    case "hidden_lines_visible":
                    case "hidden_greyed":                   modeToSet = 1; break;
                    case "hlr":
                    case "hidden_lines_removed":
                    case "hidden":                          modeToSet = 2; break;
                    case "shaded":                          modeToSet = 3; break;
                    case "shaded_edges":                    modeToSet = 7; break;
                    case "default":                         modeToSet = null; break;
                    case "":
                        // no explicit request → view-type default
                        if (viewType != "isometric") modeToSet = 1; // ortho views → HLV
                        break;
                    default:
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "UNSUPPORTED_DISPLAY_MODE",
                            $"display_mode '{displayMode}' is not supported. Use: hlv, hlr, wireframe, shaded, shaded_edges, default.");
                }
                if (modeToSet.HasValue)
                {
                    try { view.SetDisplayMode3(false, modeToSet.Value, false, false); } catch { /* best-effort; never break the view creation */ }
                }

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

        public ExecutionResponse AddFlatPatternView(ToolRequest request)
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
                double posX = p?.Value<double?>("pos_x") ?? 0.1;
                double posY = p?.Value<double?>("pos_y") ?? 0.1;
                double scale = p?.Value<double?>("scale") ?? 1.0;
                string modelPath = p?.Value<string>("model_path") ?? "";
                string configName = p?.Value<string>("config_name") ?? "";
                bool hideBendLines = p?.Value<bool?>("hide_bend_lines") ?? false;
                bool flipView = p?.Value<bool?>("flip_view") ?? false;

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active drawing document found in SolidWorks.");

                var drawingDoc = modelDoc as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Call create_drawing first.");

                // If model_path not provided, use the first open part
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

                // Resolve the configuration name (CreateFlatPatternViewFromModelView3 needs it).
                // If not supplied, read the referenced part's ACTIVE configuration; fall back to "Default".
                if (string.IsNullOrEmpty(configName))
                {
                    configName = "Default";
                    object[] docs2 = _solidWorks.GetDocuments() as object[];
                    if (docs2 != null)
                    {
                        foreach (var d in docs2)
                        {
                            var md = d as IModelDoc2;
                            if (md != null && string.Equals(md.GetPathName(), modelPath, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var cfg = md.ConfigurationManager?.ActiveConfiguration;
                                    if (cfg != null && !string.IsNullOrEmpty(cfg.Name))
                                        configName = cfg.Name;
                                }
                                catch { }
                                break;
                            }
                        }
                    }
                }

                // CreateFlatPatternViewFromModelView3(ModelName, ConfigName, LocX, LocY, LocZ, HideBendLines, FlipView)
                // -> IView. Internally flattens the sheet-metal body; the part must be sheet metal (have a Flat-Pattern).
                var view = drawingDoc.CreateFlatPatternViewFromModelView3(modelPath, configName, posX, posY, 0.0, hideBendLines, flipView) as IView;
                if (view == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FLAT_PATTERN_VIEW_FAILED",
                        $"CreateFlatPatternViewFromModelView3 returned null for '{modelPath}' (config '{configName}'). Ensure the part is SHEET METAL (has a Flat-Pattern feature) and is saved to disk.");

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
                    ResultGeometry = new JObject
                    {
                        ["view_name"] = view.Name,
                        ["config"] = configName,
                        ["hide_bend_lines"] = hideBendLines
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

        // analyze_drawing — read the ACTIVE drawing structurally (the drawing-side sibling of analyze_model):
        // every view's {name, type, scale, pos} + its dimensions {name, value_si}. Read-only, does NOT bump
        // state_version (same pattern as AnalyzeModel/VerifyState). Feeds the objective Stage-1 check (do the
        // drawing's dims match the model?) and the Stage-2 re-modeling input. Packs one JSON string into
        // Features (the field the adapter's response_mapper surfaces). NOTE: GetFirstView() returns the SHEET
        // as the first view; it is reported too (type int distinguishes it) — interpret view[0] as the sheet.
        public ExecutionResponse AnalyzeDrawing(ToolRequest request)
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

                var drawingDoc = modelDoc as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Use analyze_model for part documents.");

                var pAd = request.Params as JObject;
                bool includeGeometry = pAd?.Value<bool?>("include_geometry") ?? false;

                // A freshly-opened drawing has not computed its view DISPLAY geometry yet, so GetPolylines7
                // returns empty (per-view UpdateViewDisplayGeometry alone is insufficient on a cold doc that
                // has never been displayed this session). Force a rebuild AND a redraw/zoom so the views
                // actually render and the projected edges exist before we read them.
                if (includeGeometry)
                {
                    try { modelDoc.ForceRebuild3(false); } catch { }
                    try { modelDoc.EditRebuild3(); } catch { }
                    try { modelDoc.GraphicsRedraw2(); } catch { }
                    try { modelDoc.ViewZoomtofit2(); } catch { }
                }

                var root = new JObject();
                var viewsArr = new JArray();
                int totalDims = 0;

                object viewObj = drawingDoc.GetFirstView(); // first view = the sheet container
                int guard = 0;
                while (viewObj != null && guard++ < 2000)
                {
                    var view = viewObj as IView;
                    if (view != null)
                    {
                        var vj = new JObject();
                        vj["name"] = view.Name;
                        try { vj["type"] = view.Type; } catch { }              // swDrawingViewTypes_e (raw int)
                        try { vj["scale"] = R6(view.ScaleDecimal); } catch { }
                        try
                        {
                            var pos = view.Position as double[];
                            if (pos != null && pos.Length >= 2)
                                vj["pos"] = new JArray { R6(pos[0]), R6(pos[1]) };
                        }
                        catch { }
                        var dimsArr = ReadViewDimensions(view);
                        totalDims += dimsArr.Count;
                        vj["dimensions"] = dimsArr;
                        if (includeGeometry)
                            vj["geometry"] = ReadViewGeometry(view);
                        viewsArr.Add(vj);
                    }
                    viewObj = (view != null) ? view.GetNextView() : null;
                }

                root["view_count"] = viewsArr.Count;
                root["dimension_count"] = totalDims;
                root["views"] = viewsArr;

                // Read-only — does NOT bump state_version (mirrors AnalyzeModel).
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
                        DocumentType = "DRAWING",
                        ActiveSketch = null,
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

        // Per-view display dimensions, reusing the same IDisplayDimension→IDimension reader shape as the
        // feature-side ReadDisplayDimensions (FullName + SI value). View-level iteration uses
        // GetFirstDisplayDimension5()/GetNext5() (the IView/IDisplayDimension API). Best-effort + guarded.
        private JArray ReadViewDimensions(IView view)
        {
            var arr = new JArray();
            object dispObj = null;
            try { dispObj = view.GetFirstDisplayDimension5(); } catch { return arr; }
            int guard = 0;
            while (dispObj != null && guard++ < 1000)
            {
                var disp = dispObj as IDisplayDimension;
                if (disp != null)
                {
                    var dim = disp.GetDimension2(0) as IDimension;
                    if (dim != null)
                    {
                        var dj = new JObject();
                        dj["name"] = dim.FullName;
                        double val = dim.Value;
                        try
                        {
                            var sv = dim.GetSystemValue3(1, null) as double[]; // 1 = swThisConfiguration (inlined)
                            if (sv != null && sv.Length > 0) val = sv[0];
                        }
                        catch { }
                        dj["value_si"] = R6(val);
                        arr.Add(dj);
                    }
                }
                object next = null;
                try { next = disp != null ? disp.GetNext5() : null; } catch { next = null; }
                dispObj = next;
            }
            return arr;
        }

        // Read a drawing view's PROJECTED 2D geometry as clean primitives — the "clean shape" needed to
        // reverse-engineer a part from its drawing WITHOUT depending on a cluttered raster (dimension lines
        // overlapping the geometry). Source: IView.GetPolylines7 (the only view-geometry getter that returns
        // data here — GetLines3/GetArcs4/GetSplines3 come back empty, KNOWN-LIMITATIONS #21).
        // UpdateViewDisplayGeometry() is called first to force the display geometry to compute.
        //
        // GetPolylines7 returns a flat double[] of records; each record = [header][N][N*(x,y,z)], coords in
        // MODEL-scale meters CENTERED on the view centroid (z==0, planar). Decoded empirically (ADR-035):
        //  - straight edges: an 8-double header [0,0,0,0, nx,ny,nz, 0] then N=2 then 2 points -> a LINE.
        //    Extracted by a desync-proof SIGNATURE SCAN (cylindrical-wall silhouettes come out here — this is
        //    where a revolve profile's UP/DOWN steps live, the info a dimension value can't carry).
        //  - curved edges (fillets/chamfers, and the circular boundaries of annular faces): tessellated
        //    multi-point runs, captured best-effort as {start, mid, end} via a tolerant walk.
        private JObject ReadViewGeometry(IView view)
        {
            var g = new JObject();
            try { view.UpdateViewDisplayGeometry(); } catch { }
            double[] arr;
            try { object polys; view.GetPolylines7((short)0, out polys); arr = polys as double[]; }
            catch (Exception ex) { g["err"] = ex.Message; return g; }
            int nlen = (arr == null) ? 0 : arr.Length;

            var lines = new JArray();
            // Line-record signature: arr[i..i+3]==0, arr[i+7]==0, arr[i+8]==2.0, then two points. Each point
            // is (x, y, depth); the 3rd coord is the view DEPTH — CONSTANT per record (0 for some views,
            // non-zero for others), not necessarily 0. Require the two points to share the same depth.
            for (int i = 0; i + 15 <= nlen;)
            {
                if (Z(arr[i]) && Z(arr[i + 1]) && Z(arr[i + 2]) && Z(arr[i + 3]) && Z(arr[i + 7])
                    && arr[i + 8] == 2.0 && Math.Abs(arr[i + 11] - arr[i + 14]) < 1e-6 && InR(arr[i + 11])
                    && InR(arr[i + 9]) && InR(arr[i + 10]) && InR(arr[i + 12]) && InR(arr[i + 13]))
                {
                    var lj = new JObject();
                    lj["x1"] = R6(arr[i + 9]); lj["y1"] = R6(arr[i + 10]);
                    lj["x2"] = R6(arr[i + 12]); lj["y2"] = R6(arr[i + 13]);
                    lines.Add(lj);
                    i += 15;
                }
                else i++;
            }

            // Curves: tolerant walk for N>=3 point-runs (tessellated arcs/circles) -> start/mid/end.
            // Points are (x, y, depth); require all points in a run to share one depth (planar) — this is
            // the validity filter (a header's stray values are not consistently planar).
            var curves = new JArray();
            for (int j = 0; j < nlen - 1;)
            {
                int N = -1, basep = -1;
                for (int h = 0; h <= 13 && j + h < nlen; h++)
                {
                    double Nd = arr[j + h];
                    if (Nd != Math.Floor(Nd)) continue;
                    int Ni = (int)Nd;
                    if (Ni < 3 || Ni > 500) continue;
                    int b = j + h + 1;
                    if (b + 3 * Ni > nlen) continue;
                    double depth = arr[b + 2];
                    if (!InR(depth)) continue;
                    bool ok = true;
                    for (int k = 0; k < Ni; k++)
                        if (Math.Abs(arr[b + 3 * k + 2] - depth) > 1e-6 || !InR(arr[b + 3 * k]) || !InR(arr[b + 3 * k + 1])) { ok = false; break; }
                    if (ok) { N = Ni; basep = b; break; }
                }
                if (basep < 0) { j++; continue; }
                int mid = N / 2;
                var cj = new JObject();
                cj["n"] = N;
                cj["x1"] = R6(arr[basep]); cj["y1"] = R6(arr[basep + 1]);
                cj["xm"] = R6(arr[basep + 3 * mid]); cj["ym"] = R6(arr[basep + 3 * mid + 1]);
                cj["x2"] = R6(arr[basep + 3 * (N - 1)]); cj["y2"] = R6(arr[basep + 3 * (N - 1) + 1]);
                curves.Add(cj);
                j = basep + 3 * N;
            }

            g["lines"] = lines;
            g["curves"] = curves;
            g["frame"] = "model-scale meters (x,y in the view plane), centered on view centroid";
            return g;
        }

        private static bool Z(double v) { return Math.Abs(v) < 1e-6; }
        private static bool InR(double v) { return Math.Abs(v) < 2.0; }

        // auto_dimension_drawing — automatically transfer the MODEL's driving dimensions into the drawing
        // views (the SolidWorks "Insert Model Items > Dimensions" automation). This is the robust
        // alternative to add_drawing_dimension's fragile coordinate pick (KNOWN-LIMITATIONS #6, the drawing
        // counterpart of part index-based selection): the dimensions come straight from the model's
        // parametric dimensions, correctly placed, instead of being guessed from a sheet coordinate.
        // Uses IDrawingDoc.InsertModelAnnotations3(Option, Types, AllViews, DuplicateDims, HiddenFeatureDims,
        // UsePlacementInSketch) — signature verified against the interop (IView.InsertModelDimensions does
        // NOT exist; the insertion API lives on IDrawingDoc). All swconst values are inlined int constants
        // (ADR-018). Bumps state_version (the drawing gains annotations); echoes inserted_count in
        // result_geometry for in-band verification (ADR-023 spirit) so the caller can confirm dims landed
        // without a separate analyze_drawing round-trip.
        public ExecutionResponse AutoDimensionDrawing(ToolRequest request)
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
                bool allViews = p?.Value<bool?>("all_views") ?? true;
                bool includeUnmarked = p?.Value<bool?>("include_unmarked") ?? false;
                bool eliminateDuplicates = p?.Value<bool?>("eliminate_duplicates") ?? true;

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var drawingDoc = modelDoc as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Call create_drawing + add_drawing_view first.");

                // InsertModelAnnotations3 args (swconst inlined as ints — ADR-018):
                //   Option = swImportModelItemsSource_e.swImportModelItemsFromEntireModel (0)
                //   Types  = swInsertAnnotation_e bitmask: swInsertDimensionsMarkedForDrawing (32768),
                //            optionally | swInsertDimensionsNotMarkedForDrawing (524288) for ALL driving dims
                //   AllViews, DuplicateDims, HiddenFeatureDims(false), UsePlacementInSketch(true)
                int option = 0;
                int types = 32768;
                if (includeUnmarked) types |= 524288;
                bool duplicateDims = !eliminateDuplicates;

                modelDoc.ClearSelection2(true);

                object inserted = drawingDoc.InsertModelAnnotations3(
                    option, types, allViews, duplicateDims, false, true);

                modelDoc.GraphicsRedraw2();

                int insertedCount = 0;
                var arr = inserted as object[];
                if (arr != null) insertedCount = arr.Length;

                ExecLog.Write($"auto_dimension_drawing: inserted {insertedCount} dimension(s) " +
                    $"allViews={allViews} includeUnmarked={includeUnmarked} types={types}");

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
                    ResultGeometry = new JObject
                    {
                        ["inserted_count"] = insertedCount,
                        ["all_views"] = allViews,
                        ["include_unmarked"] = includeUnmarked
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

        // auto_center_marks — automatically place center marks (+ extended centerlines) on every hole/slot
        // in every model view of the active drawing (the SolidWorks "Auto Insert > Center Marks"). The
        // difficulty-2 forcing-function capability: a bracket/flange with holes needs center marks the
        // model-item auto-dimension pass doesn't add. ROBUST/automatic — `IView.AutoInsertCenterMarks2`
        // operates per view with NO coordinate pick (the drawing-side analogue of index-based selection,
        // ADR-033/034). All swconst values are inlined int constants (ADR-018). Bumps state_version; echoes
        // the total center-mark count in result_geometry (in-band verify, ADR-023 spirit).
        public ExecutionResponse AutoCenterMarks(ToolRequest request)
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
                bool includeSlots = p?.Value<bool?>("include_slots") ?? true;
                bool extendedLines = p?.Value<bool?>("extended_lines") ?? true;

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var drawingDoc = modelDoc as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Call create_drawing + add_drawing_view first.");

                // swAutoInsertCenterMarkTypes_e bitmask (inlined ints, ADR-018): Hole=1, Slots=4.
                int insertType = 1;
                if (includeSlots) insertType |= 4;
                int insertOption = 2; // swCenterMarkStyle_e.swCenterMark_Single

                int viewsProcessed = 0;
                int viewsMarked = 0;
                var modelViews = new List<IView>();
                object viewObj = drawingDoc.GetFirstView(); // first view = the sheet
                int guardCounter = 0;
                while (viewObj != null && guardCounter++ < 2000)
                {
                    var view = viewObj as IView;
                    object next = (view != null) ? view.GetNextView() : null;
                    if (view != null)
                    {
                        int vType = -1;
                        try { vType = view.Type; } catch { } // swDrawingViewTypes_e: 1 = sheet
                        if (vType != 1)
                        {
                            modelViews.Add(view);
                            bool ok = false;
                            // Many drawing annotation ops act on the ACTIVE view — activate it first.
                            try { drawingDoc.ActivateView(view.Name); } catch { }
                            try
                            {
                                // AutoInsertCenterMarks2(InsertType, InsertOption, LinearSlotCenter,
                                //   ArcSlotCenter, UseDocumentDefaults, Size, Gap, ExtendedLines,
                                //   CenterLineFont, Angle). UseDocumentDefaults=true → SW's own center-mark
                                //   size/style (robust); the explicit args are then ignored by SW.
                                ok = view.AutoInsertCenterMarks2(insertType, insertOption,
                                    includeSlots, includeSlots, true, 0.0025, 0.0,
                                    extendedLines, true, 0.0);
                                viewsProcessed++;
                            }
                            catch (Exception exv) { ExecLog.Write($"auto_center_marks: view '{view.Name}' threw {exv.Message}"); }
                            if (ok) viewsMarked++;
                            ExecLog.Write($"auto_center_marks: view '{view.Name}' type={vType} ok={ok}");
                        }
                    }
                    viewObj = next;
                }

                // Commit so the inserted center marks/centerlines are realized (and so the per-view
                // counters below are not stale — this interop's GetCenterMarkCount can read 0 pre-rebuild).
                try { modelDoc.EditRebuild3(); } catch { }
                modelDoc.GraphicsRedraw2();

                // views_marked (the API success count) is the RELIABLE in-band signal that center marks
                // were auto-inserted; the raw mark/line counts are best-effort (this interop under-reports
                // them — verified visually that marks ARE present even when these read 0).
                int totalMarks = 0, totalLines = 0;
                foreach (var v in modelViews)
                {
                    try { totalMarks += v.GetCenterMarkCount(); } catch { }
                    try { totalLines += v.GetCenterLineCount(); } catch { }
                }

                ExecLog.Write($"auto_center_marks: views_marked={viewsMarked}/{viewsProcessed} marks={totalMarks} lines={totalLines} " +
                    $"includeSlots={includeSlots} extendedLines={extendedLines}");

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
                    ResultGeometry = new JObject
                    {
                        ["views_marked"] = viewsMarked,
                        ["views_processed"] = viewsProcessed,
                        ["center_marks"] = totalMarks,   // best-effort (interop under-reports; marks ARE present)
                        ["center_lines"] = totalLines,   // best-effort
                        ["include_slots"] = includeSlots
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

        // add_hole_callout — insert a hole callout (e.g. "n× ØD THRU") on the hole edge nearest the sheet
        // point (px, py) in the active drawing. Coordinate-based, mirroring add_drawing_dimension's EDGE
        // pick (the accepted #6 idiom; a drawing edge is selected by point). Uses
        // IDrawingDoc.AddHoleCallout2(X, Y, Z) — verified against the interop. Bumps state_version.
        // NOTE: coordinate selection is fragile for crowded geometry (KNOWN-LIMITATIONS #6); prefer
        // auto_center_marks for centerlines and use this for explicit hole callouts where wanted.
        public ExecutionResponse AddHoleCallout(ToolRequest request)
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
                if (px == null || py == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "px and py are required (a point on the hole edge in sheet meters).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var drawingDoc = modelDoc as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Use create_drawing + add_drawing_view first.");

                // Select the hole's projected EDGE at the point (drawing views project the model as EDGE
                // entities; same pick order add_drawing_dimension uses). This also sets the owning view.
                modelDoc.ClearSelection2(true);
                bool selected = modelDoc.Extension.SelectByID2(
                    "", "EDGE", px.Value, py.Value, 0, false, 0, null, 0);
                if (!selected)
                    selected = modelDoc.Extension.SelectByID2(
                        "", "SILHOUETTE", px.Value, py.Value, 0, false, 0, null, 0);
                if (!selected)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "EDGE_NOT_FOUND", $"No drawing hole edge found at or near ({px.Value}, {py.Value}). Point at a projected circular edge in a view.");

                object callout = drawingDoc.AddHoleCallout2(px.Value, py.Value, 0.0);
                if (callout == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "CALLOUT_FAILED", "AddHoleCallout2 returned null — the selected edge may not be a hole/circular edge. Point at a hole's projected circle.");

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
                        Features = new List<string>(),
                        Dimensions = new List<string> { $"hole_callout@({px.Value},{py.Value})" }
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

        // add_section_view — create a SECTION VIEW by cutting along an EXISTING straight edge/line already in
        // the drawing (the robust method the user taught: "use the lines already in the technical drawing —
        // I chose a point on the cutting surface"). The difficulty-3 capability: a blind pocket's depth can't
        // be shown in a projected orthographic view (its floor is a hidden edge), but a section through it
        // exposes the depth as a real, dimensionable edge. Flow: select the existing edge at (edge_x,edge_y)
        // — a point on a projected straight edge that defines the cut location/direction — then
        // IDrawingDoc.CreateSectionViewAt5(X,Y,Z,Label,Options,Excluded,Depth) places the section at (px,py)
        // (with a CreateSectionViewAt2 fallback — At5 can return null on some states). NO new cutting line is
        // drawn (that earlier approach was fragile, KNOWN-LIMITATIONS #21). Signatures verified against the
        // interop; swconst (swCreateSectionView_ChangeDirection=4) inlined (ADR-018). Bumps state_version.
        // NOTE: edge selection is coordinate-based (#6) — point at a real projected edge; a clean drawing
        // state matters (repeated failed attempts can corrupt section creation — restart/rebuild if so).
        public ExecutionResponse AddSectionView(ToolRequest request)
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
                double? edgeX = p?.Value<double?>("edge_x");
                double? edgeY = p?.Value<double?>("edge_y");
                double? x1 = p?.Value<double?>("x1");
                double? y1 = p?.Value<double?>("y1");
                double? x2 = p?.Value<double?>("x2");
                double? y2 = p?.Value<double?>("y2");
                double? px = p?.Value<double?>("px");
                double? py = p?.Value<double?>("py");
                string label = p?.Value<string>("label");
                if (string.IsNullOrEmpty(label)) label = "A";
                bool flip = p?.Value<bool?>("flip") ?? false;
                double? scale = p?.Value<double?>("scale");

                // Two ways to define the cut: EDGE mode (edge_x,edge_y → cut ALONG an existing projected
                // edge — best when a real edge lies on the desired plane) or LINE mode (x1,y1,x2,y2 → draw a
                // cut line, best for cutting THROUGH a feature's interior, e.g. a pocket's middle, where no
                // edge exists). px,py = section placement (which side the section projects to).
                bool lineMode = (x1 != null && y1 != null && x2 != null && y2 != null);
                bool edgeMode = (edgeX != null && edgeY != null);
                if ((!lineMode && !edgeMode) || px == null || py == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "Provide either edge_x,edge_y (cut along an existing edge) OR x1,y1,x2,y2 (draw a cut line through a feature), plus px,py (placement). Sheet meters.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                var drawingDoc = modelDoc as IDrawingDoc;
                if (drawingDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NOT_A_DRAWING", "Active document is not a drawing. Use create_drawing + add_drawing_view first.");

                // DIAG (#21 auto-aim): log each model view's outline + projected straight lines (so an
                // interior cut line crossing the target feature can be chosen). GetLines3 → flat double[];
                // probable stride 6 = [x1,y1,z1,x2,y2,z2] per line, in sheet meters.
                try
                {
                    object dvo = drawingDoc.GetFirstView();
                    int dg = 0;
                    while (dvo != null && dg++ < 50)
                    {
                        var dv = dvo as IView;
                        object dn = (dv != null) ? dv.GetNextView() : null;
                        if (dv != null)
                        {
                            int dt = -1; try { dt = dv.Type; } catch { }
                            if (dt != 1)
                            {
                                string ob = ""; try { var o = dv.GetOutline() as double[]; if (o != null) ob = string.Join(",", o); } catch { }
                                double[] lines = null; try { lines = dv.GetLines3() as double[]; } catch { }
                                int nL = lines != null ? lines.Length : 0;
                                ExecLog.Write($"add_section_view DIAG view '{dv.Name}' type={dt} outline=[{ob}] linesLen={nL}");
                                if (lines != null)
                                    for (int i = 0; i + 5 < lines.Length && i / 6 < 60; i += 6)
                                        ExecLog.Write($"  L{i / 6}: ({R6(lines[i])},{R6(lines[i + 1])})-({R6(lines[i + 3])},{R6(lines[i + 4])})");
                            }
                        }
                        dvo = dn;
                    }
                }
                catch (Exception exd) { ExecLog.Write($"add_section_view DIAG err: {exd.Message}"); }

                // ROOT-CAUSE FIX (#21): ACTIVATE the model view the cut belongs to BEFORE drawing/selecting
                // the cut line, so the line is added to THAT VIEW's sketch — not the sheet. Without an active
                // view the section line has no owning view and CreateSectionViewAt5 returns null (and the At2
                // fallback then faulted SW with RPC_E_SERVERFAULT). The GUI activates the view implicitly when
                // the user clicks inside it (user-taught flow, 2026-07-10). view_name overrides; otherwise the
                // view is found by outline-containment of the cut point. ActivateView verified via reflection.
                double cutMidX = lineMode ? (x1.Value + x2.Value) / 2.0 : edgeX.Value;
                double cutMidY = lineMode ? (y1.Value + y2.Value) / 2.0 : edgeY.Value;
                string targetView = p?.Value<string>("view_name");
                if (string.IsNullOrEmpty(targetView))
                    targetView = FindDrawingViewAtPoint(drawingDoc, cutMidX, cutMidY);
                if (!string.IsNullOrEmpty(targetView))
                {
                    bool activated = false;
                    try { activated = drawingDoc.ActivateView(targetView); } catch (Exception exa) { ExecLog.Write($"add_section_view: ActivateView threw {exa.Message}"); }
                    ExecLog.Write($"add_section_view: ActivateView('{targetView}') -> {activated}");
                }
                else ExecLog.Write($"add_section_view: no model view contains cut point ({cutMidX},{cutMidY}) — proceeding without explicit activation");

                // Define the cut line and SELECT it (drawing views project the model as EDGE entities;
                // selecting the line/edge also sets the owning view). The selected line IS the section cut.
                modelDoc.ClearSelection2(true);
                bool sel = false;
                double selX = 0, selY = 0;     // a point on the selected cut line (for the At2 re-select)
                string selType = "EDGE";
                if (lineMode)
                {
                    // The cut line must live in the TARGET VIEW's sketch. When a view is active,
                    // SketchManager.CreateLine works in VIEW-SKETCH space (model scale, view-centred) — NOT
                    // sheet space. So transform the intended SHEET endpoints into view-sketch space via
                    // view.GetSketch().ModelToSketchTransform (the published CodeStack recipe) before drawing;
                    // otherwise the line lands far OUTSIDE the part and CreateSectionViewAt5 returns null — the
                    // real ROOT CAUSE of #21 (a raw sheet-coord line drawn in an active view misses the geometry,
                    // yet still object-selects, so the failure looked like a flaky API).
                    IView tView = FindViewObjectByName(drawingDoc, targetView);
                    if (tView == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SECTION_LINE_FAILED", "Could not resolve the model view to draw the cut line in. Point the cut inside a view, or pass view_name.");
                    double[] vp1 = TransformSheetToViewSketch(tView, x1.Value, y1.Value);
                    double[] vp2 = TransformSheetToViewSketch(tView, x2.Value, y2.Value);
                    if (vp1 == null || vp2 == null || vp1.Length < 3 || vp2.Length < 3)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SECTION_LINE_FAILED", "Could not transform the sheet coordinates into the view sketch space.");
                    drawingDoc.ActivateView(tView.Name);
                    var seg = modelDoc.SketchManager.CreateLine(vp1[0], vp1[1], vp1[2], vp2[0], vp2[1], vp2[2]) as ISketchSegment;
                    if (modelDoc.SketchManager.ActiveSketch != null)
                        modelDoc.SketchManager.InsertSketch(true);
                    if (seg == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SECTION_LINE_FAILED", "Could not sketch the cut line in the view.");
                    selX = (x1.Value + x2.Value) / 2.0; selY = (y1.Value + y2.Value) / 2.0; selType = "SKETCHSEGMENT";
                    modelDoc.ClearSelection2(true);
                    // The transformed line DISPLAYS at the intended sheet location, so a sheet-coord pick finds
                    // it (also a self-check that the transform was right); object-select is the robust fallback.
                    sel = modelDoc.Extension.SelectByID2("", "SKETCHSEGMENT", selX, selY, 0, false, 0, null, 0);
                    if (!sel) try { sel = seg.Select4(false, null); } catch { }
                    ExecLog.Write($"add_section_view: LINE sheet ({x1.Value},{y1.Value})-({x2.Value},{y2.Value}) -> viewSketch ({R6(vp1[0])},{R6(vp1[1])})-({R6(vp2[0])},{R6(vp2[1])}) selected={sel} coordPick={(sel ? "y" : "n")}");
                    if (!sel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SECTION_LINE_FAILED", "Drew the cut line but could not select it.");
                }
                else
                {
                    selX = edgeX.Value; selY = edgeY.Value;
                    sel = modelDoc.Extension.SelectByID2("", "EDGE", selX, selY, 0, false, 0, null, 0); selType = "EDGE";
                    if (!sel) { sel = modelDoc.Extension.SelectByID2("", "SILHOUETTE", selX, selY, 0, false, 0, null, 0); selType = "SILHOUETTE"; }
                    if (!sel) { sel = modelDoc.Extension.SelectByID2("", "SKETCHSEGMENT", selX, selY, 0, false, 0, null, 0); selType = "SKETCHSEGMENT"; }
                    ExecLog.Write($"add_section_view: EDGE at ({selX},{selY}) selected={sel}");
                    if (!sel)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDGE_NOT_FOUND", $"No projected straight edge/line found at or near ({selX}, {selY}). Point at an edge already shown in a view, or use x1,y1,x2,y2 to draw a cut line through the feature.");
                }

                // Read-only diagnostic (#21): confirm exactly what SW believes is selected at call time
                // (a valid section line should be a sketch segment = swSelSKETCHSEGS 9, or an edge = 1).
                try
                {
                    var selMgr = modelDoc.ISelectionManager as ISelectionMgr;
                    if (selMgr != null)
                    {
                        int selCount = selMgr.GetSelectedObjectCount2(-1);
                        int selT = selCount > 0 ? selMgr.GetSelectedObjectType3(1, -1) : -1;
                        ExecLog.Write($"add_section_view: pre-create selection count={selCount} type={selT} activeView='{targetView}'");
                    }
                }
                catch (Exception exs) { ExecLog.Write($"add_section_view: sel diag err {exs.Message}"); }

                int options = 0;
                if (flip) options |= 4; // swCreateSectionViewAtOptions_e.swCreateSectionView_ChangeDirection

                // SectionDepth = 0 → a full section. Options 0 → standard aligned. Try the modern overload,
                // fall back to the older At2 (At5 can return null on some states even when the cut is valid).
                IView view = null;
                try { view = drawingDoc.CreateSectionViewAt5(px.Value, py.Value, 0, label, options, null, 0.0) as IView; }
                catch (Exception ex5) { ExecLog.Write($"add_section_view: At5 threw {ex5.Message}"); }
                ExecLog.Write($"add_section_view: CreateSectionViewAt5 -> {(view == null ? "null" : view.Name)}");
                if (view == null)
                {
                    // SAFETY: the CreateSectionViewAt2 fallback is DISABLED. On this SW 2026 (34.x) build it
                    // does not merely fail — it throws RPC_E_SERVERFAULT (0x80010105) and can CRASH SolidWorks
                    // (observed twice 2026-07-10, then a full slddrw crash). At5 returning null is a clean,
                    // recoverable failure; escalating to At2 is net-negative. See KNOWN-LIMITATIONS #21 / ADR-037.
                    ExecLog.Write("add_section_view: At5 returned null; At2 fallback DISABLED (it crashes SW). Failing cleanly.");
                }
                if (view == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SECTION_VIEW_FAILED",
                        "CreateSectionView returned null — the selected edge could not define a valid section, or the drawing state is corrupted (restart/rebuild the server and retry on a clean state).");

                if (scale != null && scale.Value > 0)
                    try { view.ScaleDecimal = scale.Value; } catch { }

                modelDoc.GraphicsRedraw2();

                string viewName = "";
                try { viewName = view.Name; } catch { }
                ExecLog.Write($"add_section_view: created '{viewName}' label={label} mode={(lineMode ? "line" : "edge")} cut=({selX},{selY}) at ({px.Value},{py.Value}) flip={flip}");

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
                        Features = new List<string> { viewName },
                        Dimensions = new List<string>()
                    },
                    ResultGeometry = new JObject
                    {
                        ["section_view"] = viewName,
                        ["label"] = label
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

        // Find the model view whose sheet OUTLINE contains a point (sheet meters). Skips the sheet view
        // (Type==1). Used to activate the correct owning view for a section cut. Returns null if none contains it.
        private string FindDrawingViewAtPoint(IDrawingDoc drawingDoc, double sx, double sy)
        {
            try
            {
                object dvo = drawingDoc.GetFirstView();
                int guard = 0;
                while (dvo != null && guard++ < 200)
                {
                    var dv = dvo as IView;
                    object dn = (dv != null) ? dv.GetNextView() : null;
                    if (dv != null)
                    {
                        int dt = -1; try { dt = dv.Type; } catch { }
                        if (dt != 1)
                        {
                            double[] o = null; try { o = dv.GetOutline() as double[]; } catch { }
                            if (o != null && o.Length >= 4 && sx >= o[0] && sx <= o[2] && sy >= o[1] && sy <= o[3])
                                return dv.Name;
                        }
                    }
                    dvo = dn;
                }
            }
            catch (Exception ex) { ExecLog.Write($"FindDrawingViewAtPoint err: {ex.Message}"); }
            return null;
        }

        // Resolve an IView by its Name in the active drawing (first name match; null if none).
        private IView FindViewObjectByName(IDrawingDoc drawingDoc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                object dvo = drawingDoc.GetFirstView();
                int guard = 0;
                while (dvo != null && guard++ < 200)
                {
                    var dv = dvo as IView;
                    object dn = (dv != null) ? dv.GetNextView() : null;
                    if (dv != null && dv.Name == name) return dv;
                    dvo = dn;
                }
            }
            catch (Exception ex) { ExecLog.Write($"FindViewObjectByName err: {ex.Message}"); }
            return null;
        }

        // Transform a DRAWING SHEET point (meters) into a view's SKETCH coordinate space, so a line drawn via
        // SketchManager while that view is active appears at the intended sheet location. Published recipe:
        // view.GetSketch().ModelToSketchTransform + IMathUtility (handles the view's scale, offset and any
        // rotation). Returns {x,y,z} in view-sketch space, or null on failure.
        private double[] TransformSheetToViewSketch(IView view, double sheetX, double sheetY)
        {
            try
            {
                var sk = view.GetSketch() as ISketch;
                if (sk == null) return null;
                var xform = sk.ModelToSketchTransform;
                if (xform == null) return null;
                var mu = _solidWorks.GetMathUtility() as IMathUtility;
                if (mu == null) return null;
                var pt = mu.CreatePoint(new double[] { sheetX, sheetY, 0.0 }) as IMathPoint;
                if (pt == null) return null;
                pt = pt.MultiplyTransform(xform) as IMathPoint;
                if (pt == null) return null;
                return pt.ArrayData as double[];
            }
            catch (Exception ex) { ExecLog.Write($"TransformSheetToViewSketch err: {ex.Message}"); return null; }
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
                    // Two chamfer modes (grown for level-2-2: Chamfer1 dist-angle @45°, Chamfer2 dist-dist 3×5).
                    // InsertFeatureChamfer(Options, ChamferType, Width, Angle, OtherDist, VC1, VC2, VC3):
                    //   Width = D1 (first-face setback), Angle = radians (AngleDistance), OtherDist = D2
                    //   (second-face setback, DistanceDistance). Enum values are compile-time constants,
                    //   inlined by the C# compiler -> no runtime swconst load (ADR-018):
                    //   swChamferAngleDistance=1, swChamferDistanceDistance=2, swFeatureChamferFlipDirection=1.
                    // A DistanceDistance chamfer is directional (which face gets D1 vs D2) — a wrong side is
                    // corrected by chamfer_flip (the FlipDirection option), same idea as rib/extrude reverse.
                    string chamferType = (p?.Value<string>("chamfer_type") ?? "distance_angle").ToLowerInvariant();
                    bool chamferFlip = p?.Value<bool?>("chamfer_flip") ?? false;
                    int chamferOpts = chamferFlip ? (int)swFeatureChamferOption_e.swFeatureChamferFlipDirection : 0;
                    if (chamferType == "distance_distance")
                    {
                        double? dist2 = p?.Value<double?>("distance2");
                        if (dist2 == null || dist2.Value <= 0)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "chamfer_type 'distance_distance' requires distance2 > 0 (the second-face setback, meters).");
                        feature = modelDoc.FeatureManager.InsertFeatureChamfer(
                            chamferOpts,
                            (int)swChamferType_e.swChamferDistanceDistance,
                            radiusOrDist.Value,                          // D1 (Width — first-face setback)
                            0.0,                                         // Angle unused
                            dist2.Value,                                 // D2 (OtherDist — second-face setback)
                            0.0, 0.0, 0.0) as IFeature;
                        ExecLog.Write($"add_edge_feature chamfer(dist-dist): d1={radiusOrDist.Value} d2={dist2.Value} flip={chamferFlip} -> {(feature == null ? "NULL" : feature.Name)}");
                    }
                    else // distance_angle (default; angle in DEGREES at the tool boundary, default 45)
                    {
                        double angleDeg = p?.Value<double?>("angle") ?? 45.0;
                        double chamferAngle = angleDeg * Math.PI / 180.0;
                        feature = modelDoc.FeatureManager.InsertFeatureChamfer(
                            chamferOpts,
                            (int)swChamferType_e.swChamferAngleDistance,
                            radiusOrDist.Value,                          // Width (setback distance, D1)
                            chamferAngle,                                // angle in radians
                            0.0, 0.0, 0.0, 0.0) as IFeature;
                        ExecLog.Write($"add_edge_feature chamfer(dist-angle): dist={radiusOrDist.Value} angle={angleDeg} flip={chamferFlip} -> {(feature == null ? "NULL" : feature.Name)}");
                    }
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
                else if (analysisType == "bodies")
                {
                    // Per-BODY fingerprint (multibody parts — e.g. a FLATTENED assembly STEP):
                    // volume/area/centroid + the body's FACE INDEX RANGE in the same enumeration
                    // ReadFaces walks, so a caller can segment analyze_model(faces) per body and
                    // match bodies to known parts (assembly reconstruction, Phase B 1-3 class).
                    var partDoc = modelDoc as IPartDoc;
                    if (partDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE", "analysis_type='bodies' requires a part document.");
                    var root = new JObject();
                    var arr = new JArray();
                    int faceCursor = 0, bidx = 0;
                    object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                    if (bodies != null)
                    {
                        foreach (var b in bodies)
                        {
                            var body = b as IBody2;
                            if (body == null) continue;
                            var bj = new JObject();
                            bj["i"] = bidx++;
                            try
                            {
                                // Imported breps often carry the source product name (Fusion/NX
                                // exports) — free naming for body->part extraction.
                                var bn = body.Name;
                                if (!string.IsNullOrEmpty(bn)) bj["name"] = bn;
                            }
                            catch { }
                            int fc = body.GetFaceCount();
                            bj["face_start"] = faceCursor;
                            bj["face_count"] = fc;
                            faceCursor += fc;
                            bj["edges"] = body.GetEdgeCount();
                            bj["vertices"] = body.GetVertexCount();
                            try
                            {
                                // IBody2.GetMassProperties(density=1): [0..2]=centroid, [3]=volume,
                                // [4]=area (layout sanity-checked live against document totals).
                                var mp = body.GetMassProperties(1.0) as double[];
                                if (mp == null && body.GetMassProperties(1.0) is object[] oa2)
                                    mp = Array.ConvertAll(oa2, o => (double)o);
                                if (mp != null && mp.Length >= 5)
                                {
                                    bj["centroid"] = new JArray { Math.Round(mp[0], 9), Math.Round(mp[1], 9), Math.Round(mp[2], 9) };
                                    bj["volume"] = mp[3];
                                    bj["area"] = mp[4];
                                }
                            }
                            catch { }
                            arr.Add(bj);
                        }
                    }
                    root["body_count"] = arr.Count;
                    root["bodies"] = arr;
                    results.Add(root.ToString(Newtonsoft.Json.Formatting.None));
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
                else if (analysisType == "feature_map")
                {
                    // Per-feature geometry ATTRIBUTION via the rollback bar: walk the tree base→end and
                    // for each solid-affecting feature diff the topology against the previous stop — all
                    // INSIDE this call (one compact JSON out, no per-stop raw dumps to the host).
                    // consumed_edges = the deterministic fillet/chamfer edge anchors (they reference the
                    // PRE-feature geometry, exactly IR-ADR-008's provenance rule). Non-destructive: the
                    // bar is restored to the END in finally; nothing is saved; no state_version bump.
                    var partDoc = modelDoc as IPartDoc;
                    if (partDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE",
                            "analysis_type='feature_map' requires a part document.");

                    string fromFeature = p?.Value<string>("from_feature");
                    string toFeature = p?.Value<string>("to_feature");
                    string errCode, errMsg;
                    JObject mapResult = BuildFeatureMap(modelDoc, partDoc, fromFeature, toFeature,
                        out errCode, out errMsg);
                    if (mapResult == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            errCode ?? "FEATURE_MAP_FAILED", errMsg ?? "feature_map failed.");
                    results.Add(mapResult.ToString(Newtonsoft.Json.Formatting.None));
                }
                else
                {
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_ANALYSIS_TYPE",
                        $"analysis_type '{analysisType}' is not supported. Supported: mass_properties, geometry, bodies, edges, faces, features, sketch, feature_map.");
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

                    // InsertRefPlane's Distance is UNSIGNED — a negative value is silently NOT
                    // applied (found live on 2-1: a -0.007 'offset plane' landed ON its parent at
                    // y=0; the earlier 1-2 'proof' was masked by through-all symmetry). The minus
                    // side needs the OptionFlip constraint bit + the absolute distance.
                    int planeConstraint = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
                    if (offset < 0)
                        planeConstraint |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
                    feature = featureMgr.InsertRefPlane(
                        planeConstraint, Math.Abs(offset), 0, 0, 0, 0) as IFeature;
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

                // Hide the created plane/axis immediately (user rule 2026-07-05): reference geometry
                // is scaffolding for downstream features (offset sketches, pattern axes) and must not
                // clutter the modeling view. Selection by NAME still works on hidden ref geometry
                // (SelectByID2 / SelectPlaneFlexible), so downstream consumers are unaffected.
                // Best-effort: a hide failure never fails the create.
                if (geoType == "plane" || geoType == "axis")
                {
                    try
                    {
                        modelDoc.ClearSelection2(true);
                        if (feature.Select2(false, 0))
                            modelDoc.BlankRefGeom();
                        modelDoc.ClearSelection2(true);
                    }
                    catch { }
                }

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

                if (patternType != "linear" && patternType != "circular" && patternType != "mirror")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "pattern_type is required: 'linear', 'circular', or 'mirror'.");
                if (string.IsNullOrEmpty(featureName) && patternType != "mirror")
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
                else if (patternType == "mirror")
                {
                    // Mirror pattern (grown for 4-1's Mirror1): mirrors FEATURES about a plane.
                    // InsertMirrorFeature2 selection marks read from the local CHM (ADR-035):
                    // features to mirror = mark 1, mirror plane / planar face = mark 2.
                    string planeName = p?.Value<string>("plane");
                    if (string.IsNullOrEmpty(planeName)) planeName = "Right Plane";
                    bool geometryPattern = p?.Value<bool?>("geometry_pattern") ?? false;

                    var mirrorNames = new List<string>();
                    string featuresJson = p?.Value<string>("features_json");
                    if (!string.IsNullOrEmpty(featuresJson) && featuresJson != "[]")
                    {
                        try
                        {
                            var arr = JArray.Parse(featuresJson);
                            foreach (var t in arr) mirrorNames.Add(t.ToString());
                        }
                        catch (Exception ex)
                        {
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "INVALID_PARAMETER",
                                $"features_json must be a JSON array of feature tree names: {ex.Message}");
                        }
                    }
                    else if (!string.IsNullOrEmpty(featureName))
                        mirrorNames.Add(featureName);
                    if (mirrorNames.Count == 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER",
                            "mirror requires features_json (JSON array of feature tree names, e.g. '[\"Edge-Flange1\",\"Sketched Bend2\"]') or feature_name.");

                    // Sheet-metal parts keep a (suppressed) Flat-Pattern as the LAST feature with the
                    // rollback bar AFTER it; the generic InsertMirrorFeature2 would insert after
                    // Flat-Pattern (illegal → null; live-caught on the scratch test — the sheet-metal-
                    // aware bend API handles this itself). Same fix as the sheet-metal cut (ADR-026):
                    // roll the bar before Flat-Pattern, insert, restore.
                    IFeature mirrorFlatPat = FindFlatPattern(modelDoc);
                    bool mirrorRolled = false;
                    if (mirrorFlatPat != null)
                    {
                        try
                        {
                            mirrorRolled = modelDoc.FeatureManager.EditRollback(
                                (int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, mirrorFlatPat.Name);
                        }
                        catch { mirrorRolled = false; }
                    }
                    try
                    {
                        modelDoc.ClearSelection2(true);
                        foreach (var nm in mirrorNames)
                        {
                            bool sel = modelDoc.Extension.SelectByID2(nm, "BODYFEATURE", 0, 0, 0, true, 1, null, 0);
                            if (!sel)
                            {
                                // Fallback for feature types BODYFEATURE won't match: the same tree walk
                                // analyze uses + IFeature.Select2 with the mark.
                                var mf = FindFeatureByName(modelDoc, nm, null);
                                sel = mf != null && mf.Select2(true, 1);
                            }
                            if (!sel)
                                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                    "FEATURE_NOT_FOUND",
                                    $"Feature '{nm}' not found for mirror. Names must match the feature tree exactly.");
                        }

                        if (!SelectPlaneFlexible(modelDoc, planeName, true, 2))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "PLANE_NOT_FOUND",
                                $"Mirror plane '{planeName}' not found. Use a default plane name or a created reference plane's name.");

                        feature = featureMgr.InsertMirrorFeature2(false, geometryPattern, false, false, 0) as IFeature;
                        ExecLog.Write($"mirror: features=[{string.Join(",", mirrorNames)}] plane={planeName} rolled={mirrorRolled} -> {(feature == null ? "NULL" : feature.Name)}");
                    }
                    finally
                    {
                        if (mirrorRolled)
                        {
                            try { modelDoc.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); }
                            catch { }
                        }
                    }
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

                if (featureType != "base_flange" && featureType != "edge_flange" && featureType != "flat_pattern"
                    && featureType != "sketched_bend" && featureType != "edge_flange_sketch"
                    && featureType != "edge_flange_finish")
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "feature_type is required: 'base_flange', 'edge_flange', 'edge_flange_sketch', 'edge_flange_finish', 'flat_pattern', or 'sketched_bend'.");

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
                    // Thickness DIRECTION matters for reproduction: a wrong side puts every downstream
                    // bend-sketch plane off the sheet — and the flags also define the intrinsic sheet
                    // orientation bend directions fold against, so REPRODUCE THE ORIGINAL'S OWN FLAGS
                    // (the base_flange reader reports them) rather than deriving from face positions
                    // alone (4-1's symmetric blank was B-rep-identical to a reversed t/2 one, but bends
                    // folded mirrored). Empirically (probe 2026-07-07): ReverseThickness alone had NO
                    // effect on a CLOSED profile — the blank is an extrusion there and ReverseDirection
                    // flips its side; set both so open profiles follow too.
                    bool revThick = p?.Value<bool?>("reverse_thickness") ?? false;
                    bfData.ReverseThickness = revThick;
                    bfData.ReverseDirection = revThick;
                    // Symmetric = material grows BOTH ways off the sketch plane (±t/2), like a mid-plane
                    // extrude. 4-1's original blank is symmetric (sheet z ∈ [−t/2, +t/2]) with NO reverse
                    // flags — and the intrinsic sheet orientation those flags define is what downstream
                    // bend directions are measured against, so reproduce the ORIGINAL's exact config.
                    if (p?.Value<bool?>("symmetric_thickness") == true)
                        bfData.SymmetricThickness = true;
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
                else if (featureType == "edge_flange_sketch")
                {
                    // STEP 1 of the CUSTOM-PROFILE edge flange (SW's documented flow: generate the
                    // edge-linked profile sketch, EDIT it, create the flange from it — an arbitrary
                    // user sketch is NOT accepted by the flange API). Selects the attach edge, calls
                    // InsertSketchForEdgeFlange, optionally CLEARS the generated default profile, and
                    // leaves the sketch ACTIVE, echoing its MEASURED frame exactly like create_sketch:
                    // the generated sketch's frame is unpredictable, so the caller (IR compiler)
                    // MUST transform the original profile's 2D coordinates into it (IR-ADR-010).
                    double? esx = p?.Value<double?>("ex");
                    double? esy = p?.Value<double?>("ey");
                    double? esz = p?.Value<double?>("ez");
                    double esAngleDeg = p?.Value<double?>("angle") ?? 90.0;
                    bool esFlip = p?.Value<bool?>("flip") ?? false;
                    bool esClear = p?.Value<bool?>("clear_profile") ?? true;
                    var esEdge = SelectFlangeEdge(modelDoc, p, esx, esy, esz, out string esEdgeErr);
                    if (esEdge == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDGE_NOT_FOUND", esEdgeErr);

                    var genSketch = modelDoc.InsertSketchForEdgeFlange(esEdge, esAngleDeg * Math.PI / 180.0, esFlip) as IFeature;
                    var esActive = modelDoc.SketchManager.ActiveSketch;
                    ExecLog.Write($"edge_flange_sketch: InsertSketchForEdgeFlange -> {(genSketch == null ? "NULL" : genSketch.Name)} flip={esFlip} active={(esActive != null)}");
                    if (genSketch == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SHEET_METAL_FAILED",
                            "InsertSketchForEdgeFlange returned null. Verify the edge is a straight sheet-metal edge (a LONG boundary edge of a sheet face, not a thickness edge).");
                    if (esActive == null)
                    {
                        // InsertSketchForEdgeFlange creates the sketch but (live-proven 2026-07-07)
                        // does NOT leave it in edit mode — open it explicitly so the caller can draw.
                        modelDoc.ClearSelection2(true);
                        modelDoc.Extension.SelectByID2(genSketch.Name, "SKETCH", 0, 0, 0, false, 0, null, 0);
                        modelDoc.EditSketch();
                        esActive = modelDoc.SketchManager.ActiveSketch;
                        ExecLog.Write($"edge_flange_sketch: EditSketch({genSketch.Name}) -> active={(esActive != null)}");
                    }
                    if (esActive == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SHEET_METAL_FAILED",
                            $"Generated profile sketch '{genSketch.Name}' could not be opened for edit.");

                    if (esClear)
                    {
                        // Wipe the generated default profile (a rectangle spanning the edge) so the
                        // caller can draw the ORIGINAL's custom profile via add_sketch_entity.
                        var esSegs = esActive.GetSketchSegments() as object[];
                        modelDoc.ClearSelection2(true);
                        int marked = 0;
                        if (esSegs != null)
                            foreach (var o in esSegs)
                            {
                                var ss = o as ISketchSegment;
                                if (ss != null && ss.Select4(true, null)) marked++;
                            }
                        if (marked > 0) modelDoc.EditDelete();
                        ExecLog.Write($"edge_flange_sketch: cleared {marked} generated segments");
                    }

                    object esFrameEcho = null;
                    try { esFrameEcho = ReadSketchPlane(esActive); } catch { }
                    var esResp = new ExecutionResponse
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
                            ActiveSketch = genSketch.Name,
                            Features = new List<string>(),
                            Dimensions = new List<string>()
                        },
                        ResultGeometry = esFrameEcho,
                        Error = null
                    };
                    _guard.RegisterCompleted(request.OperationId, esResp);
                    return esResp;
                }
                else if (featureType == "edge_flange_finish")
                {
                    // STEP 2: the ACTIVE (edited) profile sketch + the SAME edge -> InsertSheetMetalEdgeFlange2.
                    // The custom profile itself defines the flange outline and length, so unlike the
                    // default-profile path there is NO OffsetDistance override afterwards. Radius and
                    // position replay the ORIGINAL's values (the analyze_model(features) edge_flange block).
                    double? efx = p?.Value<double?>("ex");
                    double? efy = p?.Value<double?>("ey");
                    double? efz = p?.Value<double?>("ez");
                    double efAngleDeg = p?.Value<double?>("angle") ?? 90.0;
                    bool efUseDefaultRadius = p?.Value<bool?>("use_default_radius") ?? false;
                    double efRadius = p?.Value<double?>("bend_radius") ?? 0.001;
                    string efPosStr = (p?.Value<string>("bend_position") ?? "material_inside").ToLowerInvariant();
                    // swFlangePositionTypes_e (same encoding the readers report): 1=material_inside,
                    // 2=material_outside, 3=bend_outside, 4=centerline, 5=bend_sharp.
                    int efPos;
                    switch (efPosStr)
                    {
                        case "material_outside": efPos = 2; break;
                        case "bend_outside": efPos = 3; break;
                        case "centerline": efPos = 4; break;
                        case "bend_sharp": efPos = 5; break;
                        default: efPos = 1; break; // material_inside
                    }
                    var profFeat = modelDoc.SketchManager.ActiveSketch as IFeature;
                    if (profFeat == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_NOT_ACTIVE",
                            "edge_flange_finish requires the generated profile sketch to be ACTIVE (run edge_flange_sketch, then draw the profile).");
                    modelDoc.SketchManager.InsertSketch(true);

                    var efEdge2 = SelectFlangeEdge(modelDoc, p, efx, efy, efz, out string efEdgeErr);
                    if (efEdge2 == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDGE_NOT_FOUND", efEdgeErr);

                    int efOpts = (int)(swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseReliefRatio
                                     | swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief);
                    if (efUseDefaultRadius)
                        efOpts |= (int)swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRadius;

                    feature = featureMgr.InsertSheetMetalEdgeFlange2(
                        new Edge[] { efEdge2 }, new Feature[] { (Feature)profFeat },
                        efOpts, efAngleDeg * Math.PI / 180.0, efRadius, efPos, 0.0,
                        (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular,
                        0.5, 0.001, 0.001, 0, null) as IFeature;
                    ExecLog.Write($"edge_flange_finish: InsertSheetMetalEdgeFlange2 sketch={profFeat.Name} pos={efPos} defRad={efUseDefaultRadius} -> {(feature == null ? "NULL" : feature.Name)}");
                    if (feature != null)
                        modelDoc.EditRebuild3();
                }
                else if (featureType == "sketched_bend")
                {
                    // Sketched bend (SM3dBend): bends the sheet about the bend LINE(S) in the ACTIVE
                    // sketch; the FIXED side is the pre-selected face (grown for 4-1). Signature +
                    // BendPos encoding (0=centerline 1=material-inside 2=material-outside 3=bend-outside)
                    // read from the local sldworksapi.chm (ADR-035 discipline); NOTE this encoding
                    // DIFFERS from ISketchedBendFeatureData.PositionType (swFlangePositionTypes_e,
                    // where 4=centerline) — the reader maps both to the same canonical string.
                    double sbAngleDeg = p?.Value<double?>("angle") ?? 90.0;
                    double sbAngleRad = sbAngleDeg * Math.PI / 180.0;
                    bool useDefaultRadius = p?.Value<bool?>("use_default_radius") ?? false;
                    double sbRadius = p?.Value<double?>("bend_radius") ?? 0.001;
                    bool sbFlip = p?.Value<bool?>("flip") ?? false;
                    string posStr = (p?.Value<string>("bend_position") ?? "centerline").ToLowerInvariant();
                    short bendPos;
                    switch (posStr)
                    {
                        case "material_inside": bendPos = 1; break;
                        case "material_outside": bendPos = 2; break;
                        case "bend_outside": bendPos = 3; break;
                        default: bendPos = 0; break; // centerline
                    }

                    var bendSketchFeat = modelDoc.SketchManager.ActiveSketch as IFeature;
                    if (bendSketchFeat == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "SKETCH_NOT_ACTIVE",
                            "sketched_bend requires the bend-line sketch to be ACTIVE (create_sketch on the sheet face + add_sketch_entity line first).");

                    int? fixedFaceIndex = p?.Value<int?>("fixed_face_index");
                    double? fx = p?.Value<double?>("fixed_x");
                    double? fy = p?.Value<double?>("fixed_y");
                    double? fz = p?.Value<double?>("fixed_z");

                    // Attempt 0: fixed face selected while the bend sketch is still ACTIVE (the UI flow —
                    // the command is invoked from inside the sketch). Attempt 1: exit the sketch,
                    // re-select the bend sketch by name + the face (append) — the recorded-macro flow.
                    for (int attempt = 0; attempt < 2 && feature == null; attempt++)
                    {
                        bool append = attempt == 1;
                        if (attempt == 1)
                        {
                            if (modelDoc.SketchManager.ActiveSketch != null)
                                modelDoc.SketchManager.InsertSketch(true);
                            modelDoc.ClearSelection2(true);
                            modelDoc.Extension.SelectByID2(bendSketchFeat.Name, "SKETCH", 0, 0, 0, false, 0, null, 0);
                        }
                        bool faceSel = false;
                        if (fixedFaceIndex != null && fixedFaceIndex.Value >= 0)
                        {
                            var allFaces = FlattenFaces(modelDoc as IPartDoc);
                            if (fixedFaceIndex.Value < allFaces.Count)
                                faceSel = (allFaces[fixedFaceIndex.Value] as IEntity).Select4(append, null);
                        }
                        else
                        {
                            faceSel = modelDoc.Extension.SelectByID2("", "FACE",
                                fx ?? 0.0, fy ?? 0.0, fz ?? 0.0, append, 0, null, 0);
                        }
                        if (!faceSel)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "FACE_NOT_FOUND",
                                "sketched_bend could not select the FIXED face — pass fixed_face_index (from analyze_model(faces)) or fixed_x/y/z, a point ON the face that must stay put.");
                        feature = featureMgr.InsertSheetMetal3dBend(sbAngleRad, useDefaultRadius, sbRadius, sbFlip, bendPos, null) as IFeature;
                        ExecLog.Write($"sketched_bend: attempt={attempt} fixedFace={(fixedFaceIndex != null ? fixedFaceIndex.ToString() : "coord")} pos={bendPos} -> {(feature == null ? "NULL" : feature.Name)}");
                    }
                    if (feature != null)
                    {
                        // InsertSheetMetal3dBend IGNORES its Flip argument (proven live on 4-1 Bend1:
                        // flip true/false produced IDENTICAL folds, z-mirrored from the original, and
                        // the created feature read back ReverseDirection=false either way). Enforce the
                        // requested direction post-insert through the feature data + ModifyDefinition.
                        var bdef = feature.GetDefinition() as ISketchedBendFeatureData;
                        if (bdef != null && bdef.ReverseDirection != sbFlip)
                        {
                            bool bacc = false;
                            try { bacc = bdef.AccessSelections(modelDoc, null); } catch { }
                            bool modified = false;
                            try
                            {
                                bdef.ReverseDirection = sbFlip;
                                modified = feature.ModifyDefinition(bdef, modelDoc, null);
                                ExecLog.Write($"sketched_bend: post-insert flip enforce -> {sbFlip} (modify={modified})");
                            }
                            finally
                            {
                                // ModifyDefinition consumes the selection access; release only if it didn't run.
                                if (bacc && !modified) { try { bdef.ReleaseSelectionAccess(); } catch { } }
                            }
                            if (!modified)
                                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                    "BEND_FLIP_FAILED",
                                    "sketched_bend created the bend but could not enforce the requested flip (ModifyDefinition failed).");
                        }
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
            "EnvFolder", "InkMarkupFolder", "EqnFolder",
            // Sheet-metal cut-list folder ("Sheet<1>" under Solid Bodies) — a UI container, no design
            // intent (it also broke feature_map's baseline: rolling before it no-ops, 4-1 live).
            "CutListFolder"
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
            else if (type == "SM3dBend")
                TryReadSketchedBend(feat, modelDoc, fj);
            else if (type == "SMBaseFlange")
                TryReadBaseFlange(feat, fj);
            else if (type == "EdgeFlange")
                TryReadEdgeFlange(feat, modelDoc, fj);
            else if (type == "MirrorPattern")
                TryReadMirror(feat, modelDoc, fj);

            // Extrude/cut end-condition + depth (blind) + reverse flag, so a rebuild knows HOW it was made.
            var ex = ReadExtrude(feat);
            if (ex != null) fj["extrude"] = ex;

            return fj;
        }

        // Attach-edge selection for the edge-flange ops. Priority: edge_index (robust — matches
        // analyze_model(edges)'s stable `i`, the same FlattenEdges enumeration; bypasses the
        // coordinate-pick fragility, KNOWN-LIMITATIONS #6 — live on 4-1: EF2's slanted attach edge
        // existed EXACTLY at the pick point yet SelectByID2 missed it) → ex/ey/ez coordinate pick.
        private Edge SelectFlangeEdge(IModelDoc2 modelDoc, JObject p, double? ex, double? ey, double? ez, out string error)
        {
            error = null;
            int? edgeIndex = p?.Value<int?>("edge_index");
            modelDoc.ClearSelection2(true);
            if (edgeIndex != null && edgeIndex.Value >= 0)
            {
                var all = FlattenEdges(modelDoc as IPartDoc);
                if (edgeIndex.Value >= all.Count)
                {
                    error = $"edge_index {edgeIndex.Value} out of range (part has {all.Count} edges).";
                    return null;
                }
                var ent = all[edgeIndex.Value] as IEntity;
                if (ent == null || !ent.Select4(false, null))
                {
                    error = $"edge_index {edgeIndex.Value}: selection failed.";
                    return null;
                }
                return all[edgeIndex.Value] as Edge;
            }
            if (ex == null || ey == null || ez == null)
            {
                error = "provide edge_index (from analyze_model(edges), PREFERRED) or ex/ey/ez (a point on the attach edge).";
                return null;
            }
            if (!modelDoc.Extension.SelectByID2("", "EDGE", ex.Value, ey.Value, ez.Value, false, 0, null, 0))
            {
                error = $"No edge found at ({ex.Value}, {ey.Value}, {ez.Value}). Prefer edge_index from analyze_model(edges).";
                return null;
            }
            var selMgr = modelDoc.SelectionManager as ISelectionMgr;
            var edge = selMgr?.GetSelectedObject6(1, -1) as Edge;
            if (edge == null)
                error = $"Entity at ({ex.Value}, {ey.Value}, {ez.Value}) is not an edge.";
            return edge;
        }

        // EdgeFlange — angle/radius/position/gap are plain properties; the ATTACH edge(s) sit
        // behind AccessSelections (released in finally — TryReadMirror discipline) and are emitted
        // with the shared BuildEdgeJson, so each edge's `mid` replays directly as the tool's
        // ex/ey/ez. `profile_sketch` names the flange's ProfileFeature subfeature — the (possibly
        // custom-edited) profile, read fully via analyze_model(sketch, name=...). Ground-truth
        // readback (IR-ADR-014): a rebuild replays these values, it never derives them.
        private void TryReadEdgeFlange(IFeature feat, IModelDoc2 modelDoc, JObject fj)
        {
            try
            {
                var def = feat.GetDefinition() as IEdgeFlangeFeatureData;
                if (def == null) return;
                var ej = new JObject();
                ej["angle"] = R6(def.BendAngle); // radians
                if (!def.UseDefaultBendRadius) ej["radius"] = R6(def.BendRadius);
                string pos;
                switch (def.PositionType)
                {
                    case 1: pos = "material_inside"; break;
                    case 2: pos = "material_outside"; break;
                    case 3: pos = "bend_outside"; break;
                    case 4: pos = "centerline"; break;
                    default: pos = "bend_sharp"; break; // 5
                }
                ej["position"] = pos;
                try { if (def.GapDistance > 0) ej["gap"] = R6(def.GapDistance); } catch { }
                try
                {
                    var sub = feat.GetFirstSubFeature() as IFeature;
                    while (sub != null)
                    {
                        if (sub.GetTypeName2() == "ProfileFeature") { ej["profile_sketch"] = sub.Name; break; }
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                catch { }
                bool acc = false;
                try { acc = def.AccessSelections(modelDoc, null); } catch { }
                if (acc)
                {
                    try
                    {
                        var edges = def.Edges as object[];
                        if (edges != null)
                        {
                            var arr = new JArray();
                            foreach (var o in edges)
                            {
                                var e = o as IEdge;
                                if (e != null) arr.Add(BuildEdgeJson(e, -1));
                            }
                            if (arr.Count > 0) ej["edges"] = arr;
                        }
                    }
                    finally
                    {
                        try { def.ReleaseSelectionAccess(); } catch { }
                    }
                }
                fj["edge_flange"] = ej;
            }
            catch { }
        }

        // SMBaseFlange — thickness/radius/k-factor + the two direction flags, all plain properties
        // (no AccessSelections). The flags matter beyond documentation: identical blank B-reps can
        // carry OPPOSITE intrinsic sheet orientations (ReverseThickness vs ReverseDirection routes),
        // and downstream bend directions are defined relative to that orientation (the 4-1 Bend1
        // mirror-fold lesson).
        private void TryReadBaseFlange(IFeature feat, JObject fj)
        {
            try
            {
                var def = feat.GetDefinition() as IBaseFlangeFeatureData;
                if (def == null) return;
                var bj = new JObject();
                bj["thickness"] = R6(def.Thickness);
                bj["bend_radius"] = R6(def.BendRadius);
                if (!def.UseGaugeTable) bj["k_factor"] = R6(def.KFactor);
                if (def.ReverseThickness) bj["reverse_thickness"] = true;
                if (def.ReverseDirection) bj["reverse_direction"] = true;
                if (def.SymmetricThickness) bj["symmetric_thickness"] = true;
                fj["base_flange"] = bj;
            }
            catch { }
        }

        // SM3dBend (sketched bend) parameters — angle/radius/position/direction read straight off
        // ISketchedBendFeatureData. `position` maps swFlangePositionTypes_e (4=centerline!) to the
        // canonical string sheet_metal_feature(sketched_bend, bend_position) accepts back, so the
        // recipe value is replayable as-is (the INSERT API's BendPos uses a different 0-based
        // encoding — the tool owns that mapping). `radius` omitted when the bend uses the sheet
        // default. The FIXED-face pick sits behind AccessSelections (temporarily rolls the model
        // back; released in finally — same discipline as TryReadMirror): `fixed_pick` is the stored
        // pick point (reflection-verified: object GetFixedFace(out x, out y, out z)); the returned
        // face's normal + bounding box are emitted too, so a rebuild can pick a model-space point on
        // the SAME side without guessing (the 4-1 Bend1 lesson: a derived pick chose the wrong region).
        private void TryReadSketchedBend(IFeature feat, IModelDoc2 modelDoc, JObject fj)
        {
            try
            {
                var def = feat.GetDefinition() as ISketchedBendFeatureData;
                if (def == null) return;
                var bj = new JObject();
                bj["angle"] = R6(def.BendAngle); // radians
                if (!def.UseDefaultBendRadius) bj["radius"] = R6(def.BendRadius);
                string pos;
                switch (def.PositionType)
                {
                    case 1: pos = "material_inside"; break;
                    case 2: pos = "material_outside"; break;
                    case 3: pos = "bend_outside"; break;
                    case 5: pos = "bend_sharp"; break;
                    default: pos = "centerline"; break; // 4 = swFlangePositionTypeBendCenterLine
                }
                bj["position"] = pos;
                if (def.ReverseDirection) bj["flip"] = true;
                bool acc = false;
                try { acc = def.AccessSelections(modelDoc, null); } catch { }
                if (acc)
                {
                    try
                    {
                        double px, py, pz;
                        object faceObj = def.GetFixedFace(out px, out py, out pz);
                        bj["fixed_pick"] = new JArray { R6(px), R6(py), R6(pz) };
                        var face = faceObj as IFace2;
                        if (face != null)
                        {
                            try
                            {
                                var n = face.Normal as double[];
                                if (n != null) bj["fixed_face_normal"] = new JArray { R6(n[0]), R6(n[1]), R6(n[2]) };
                            }
                            catch { }
                            try
                            {
                                var box = face.GetBox() as double[];
                                if (box != null && box.Length >= 6)
                                    bj["fixed_face_box"] = new JArray { R6(box[0]), R6(box[1]), R6(box[2]), R6(box[3]), R6(box[4]), R6(box[5]) };
                            }
                            catch { }
                        }
                    }
                    finally
                    {
                        try { def.ReleaseSelectionAccess(); } catch { }
                    }
                }
                fj["sketched_bend"] = bj;
            }
            catch { }
        }

        // MirrorPattern — the mirror PLANE name + the mirrored (seed) feature names, so a mirror is
        // reproducible without guessing what it mirrors. Plane/PatternFeatureArray sit behind
        // AccessSelections (temporarily rolls the model back); always released in finally — same
        // non-destructive discipline as feature_map. Best-effort: a failure just omits the block.
        private void TryReadMirror(IFeature feat, IModelDoc2 modelDoc, JObject fj)
        {
            try
            {
                var def = feat.GetDefinition() as IMirrorPatternFeatureData;
                if (def == null) return;
                var mj = new JObject();
                bool acc = false;
                try { acc = def.AccessSelections(modelDoc, null); } catch { }
                try
                {
                    var planeObj = def.Plane;
                    string planeName = (planeObj as IFeature)?.Name;
                    if (planeName != null) mj["plane"] = planeName;
                    else if (planeObj is IFace2) mj["plane_is_face"] = true;
                    var feats = def.PatternFeatureArray as object[];
                    if (feats != null)
                    {
                        var arr = new JArray();
                        foreach (var o in feats)
                        {
                            var f = o as IFeature;
                            if (f != null) arr.Add(f.Name);
                        }
                        if (arr.Count > 0) mj["features"] = arr;
                    }
                }
                finally
                {
                    if (acc) { try { def.ReleaseSelectionAccess(); } catch { } }
                }
                if (mj.Count > 0) fj["mirror"] = mj;
            }
            catch { }
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
                // `reversed` is normalized against the CANONICAL plane axis, not the raw flag: the
                // real extrude direction = profile-sketch normal ⊕ ReverseDirection, and a sketch's
                // OWN normal depends on how it was authored (a Front-Plane sketch drawn "from behind"
                // carries a -Z normal). Found live on 1-1: Boss-Extrude2 read "not reversed" (raw
                // flag false) yet the wall is built toward -Z — a recipe replay via create_sketch
                // (which always sketches with the canonical +axis normal) then extruded the mirror.
                // After normalization, reversed=true ⇔ the feature's material lies toward the
                // canonical -axis ⇔ replay with extrude_feature(reverse=true). Non-axis-aligned
                // sketch planes (sign unknown) keep the raw flag unchanged.
                bool rev = ed.ReverseDirection;
                if (ProfileSketchNormalSign(feat) < 0) rev = !rev;
                if (rev) ej["reversed"] = true;
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
        // Signed principal-axis direction of an extrude's PROFILE-SKETCH normal: +1 along the
        // canonical +axis (+Z Front / +Y Top / +X Right), -1 opposite, 0 unknown (no sketch parent
        // or a non-axis-aligned plane — in which case the caller must not flip anything). The
        // profile sketch is the extrude feature's ProfileFeature parent.
        private static int ProfileSketchNormalSign(IFeature feat)
        {
            try
            {
                object[] parents = feat.GetParents() as object[];
                if (parents == null) return 0;
                foreach (var p in parents)
                {
                    var pf = p as IFeature;
                    if (pf == null || pf.GetTypeName2() != "ProfileFeature") continue;
                    var sk = pf.GetSpecificFeature2() as ISketch;
                    var s2m = sk?.ModelToSketchTransform?.IInverse();
                    double[] d = s2m?.ArrayData as double[];
                    if (d == null || d.Length < 12) return 0;
                    double nx = d[2], ny = d[5], nz = d[8];
                    double ax = Math.Abs(nx), ay = Math.Abs(ny), az = Math.Abs(nz);
                    double dom = (az >= ax && az >= ay) ? nz : (ay >= ax ? ny : nx);
                    if (Math.Abs(dom) < 0.999) return 0;  // angled plane — don't guess
                    return dom >= 0 ? 1 : -1;
                }
            }
            catch { }
            return 0;
        }

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
                // The FULL sketch frame (in-plane axes + origin in model space, columns of the
                // sketch→model rotation). {ref, offset} alone is LOSSY: it drops the support's
                // normal SIGN and the in-plane axis orientation — a sketch on a -Y-normal face is
                // the MIRROR of one on a +Y-normal plane at the same height, so replaying its 2D
                // coordinates without the frame builds the pockets in the wrong place (found live
                // on 2-1's Sketch5). The compiler compares this recorded frame against the frame
                // create_sketch MEASURES on the rebuild and transforms coordinates — no assumptions.
                // ArrayData layout is COLUMN-major w.r.t. sketch axes (proven empirically on the
                // 2-1 rebuild: its Right-plane sketch builds toward -Z, and only the d[0..2]
                // reading yields xdir=(0,0,-1) to match): sketch x-axis = d[0..2], y = d[3..5],
                // normal = d[6..8]. (The legacy nx/ny/nz=(d[2],d[5],d[8]) classification above is
                // abs-based and live-proven — deliberately left untouched.)
                pj["frame"] = new JObject
                {
                    ["origin"] = new JArray { R6(ox), R6(oy), R6(oz) },
                    ["xdir"] = new JArray { R6(d[0]), R6(d[1]), R6(d[2]) },
                    ["ydir"] = new JArray { R6(d[3]), R6(d[4]), R6(d[5]) },
                };
                string axisName = PrincipalAxis(nx, ny, nz);
                string refName = DefaultPlaneForAxis(axisName);
                if (refName != null)
                {
                    // Canonical English default-plane name, computed from the NORMAL — never read from the
                    // localized plane name, so it's correct on a Turkish/any-language SW (create_sketch maps
                    // the English name language-independently via SelectPlaneFlexible, ADR-007). offset =
                    // signed perpendicular distance from the global origin: 0 ⇒ the default plane itself;
                    // ≠0 ⇒ a parallel surface at that height (sketch on the face there, or an offset plane).
                    pj["ref"] = refName;
                    // Offset along the CANONICAL +axis (X/Y/Z), NOT the legacy nx/ny/nz pseudo-normal:
                    // that vector is (xdir.z, ydir.z, normal.z) — fine for the ABS-based axis classification
                    // above, but its SIGN flips with the sketch's in-plane orientation. Live-caught on
                    // level-2-2's Sketch2 (+y face, ydir (0,0,-1)): legacy read offset -0.052444, and the
                    // rebuild resolved the face on the OPPOSITE side — a silently mirrored hole that the
                    // topology/volume verified-criteria cannot catch. The consumers (resolve_face_on_plane,
                    // InsertRefPlane lowering) interpret offset as the coordinate along the datum's +normal.
                    double off = R6(axisName == "X" ? ox : axisName == "Y" ? oy : oz);
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
                            // Sweep sense (+1 CCW / -1 CW w.r.t. the sketch-plane normal): without it,
                            // center+start+end describe TWO arcs (minor/major), so a deterministic
                            // rebuild via add_sketch_entity(arc_center, direction=dir) needs the sign.
                            try { ej["dir"] = ((ISketchArc)seg).GetRotationDir(); } catch { }
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
        // APPEND-select the i-th solid-body face with a selection Mark — the up-to-surface end-condition
        // reference for FeatureExtrusion3/FeatureCut4. Same GetBodies2(swSolidBody,true) → GetFaces()
        // enumeration analyze_model(faces) reports and create_sketch(on_face, face_index) walks, so the
        // caller's face index means the same thing everywhere. Mark: per the FeatureExtrusion3 remarks
        // (local sldworksapi.chm) "End condition reference entity = Mark 1" (profile 0, direction edge 16,
        // start reference 32, bodies 8) — matches CodeStack's working up-to-surface example.
        private bool SelectFaceByIndexAppend(IModelDoc2 modelDoc, int faceIndex, int mark)
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
            if (faceIndex < 0 || faceIndex >= allFaces.Count) return false;
            var ent = allFaces[faceIndex] as IEntity;
            if (ent == null) return false;
            var selData = (modelDoc.SelectionManager as ISelectionMgr)?.CreateSelectData();
            if (selData == null) return false;
            selData.Mark = mark;
            return ent.Select4(true, selData);
        }

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
                else if (surf != null)
                {
                    // Non-planar faces: report the surface KIND + matching geometry (Phase B —
                    // a concentric mate's anchor is a CYLINDRICAL face; the resolver matches it
                    // by axis + radius). CylinderParams layout: [0..2]=origin, [3..5]=axis
                    // (unit), [6]=radius. All faces also get a representative on-surface point.
                    try
                    {
                        if (surf.IsCylinder())
                        {
                            fj["kind"] = "cylinder";
                            var cp2 = surf.CylinderParams as double[];
                            if (cp2 == null && surf.CylinderParams is object[] cpo)
                                cp2 = Array.ConvertAll(cpo, o => (double)o);
                            if (cp2 != null && cp2.Length >= 7)
                            {
                                fj["origin"] = new JArray { R6(cp2[0]), R6(cp2[1]), R6(cp2[2]) };
                                fj["axis"] = new JArray { R6(cp2[3]), R6(cp2[4]), R6(cp2[5]) };
                                fj["radius"] = R6(cp2[6]);
                            }
                        }
                        else if (surf.IsCone()) fj["kind"] = "cone";
                        else if (surf.IsSphere()) fj["kind"] = "sphere";
                        var uv2 = face.GetUVBounds() as double[];
                        if (uv2 != null && uv2.Length >= 4)
                        {
                            var pt2 = surf.Evaluate((uv2[0] + uv2[1]) / 2.0, (uv2[2] + uv2[3]) / 2.0, 0, 0) as double[];
                            if (pt2 != null && pt2.Length >= 3)
                                fj["point"] = new JArray { R6(pt2[0]), R6(pt2[1]), R6(pt2[2]) };
                        }
                    }
                    catch { }
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

        // ------------------------------------------------------------------
        // feature_map — rollback-bar geometry attribution (analysis_type='feature_map')
        // ------------------------------------------------------------------

        // Feature types that can never alter SOLID geometry: they appear in the map for tree coverage
        // but get no rollback stop / snapshot (a full topology snapshot per sketch or ref plane would
        // pay real COM cost for a guaranteed-empty delta).
        private static readonly HashSet<string> NonSolidFeatureTypes = new HashSet<string>
        {
            "ProfileFeature", "3DProfileFeature", "RefPlane", "RefAxis", "RefPoint", "CoordSys",
            "OriginProfileFeature",
            // The SheetMetal DEFINITION feature (thickness/radius container): it builds no geometry
            // itself, and live on 4-1 EditRollback(ToAfterFeature, "Sheet-Metal1") JUMPED THE BAR TO
            // THE END (the stop showed the full final body and Base-Flange1 then diffed NEGATIVE).
            // Skipping it attributes the blank correctly to SMBaseFlange.
            "SheetMetal"
        };

        // 1µm — a rollback stop REPLAYS the same history, so surviving geometry matches to double
        // precision; the tolerance only needs to sit far below feature scale (same grid as R6).
        private const double MapTol = 1e-6;

        private sealed class EdgeSnap
        {
            public double[] Start, End, Mid;
            public double Len;      // chord (open) / circumference (closed) — identity + size hint
            public bool Matched;
        }

        private sealed class FaceSnap
        {
            public bool Planar;
            public double[] Normal; // planar faces only
            public double[] Point;  // representative on-surface point (UV mid)
            public double Area;
            public bool Matched;
        }

        private static bool PtEq(double[] a, double[] b)
        {
            return a != null && b != null
                && Math.Abs(a[0] - b[0]) <= MapTol
                && Math.Abs(a[1] - b[1]) <= MapTol
                && Math.Abs(a[2] - b[2]) <= MapTol;
        }

        private static JArray Pt6(double[] a) { return new JArray { R6(a[0]), R6(a[1]), R6(a[2]) }; }

        // Snapshot every solid body's edges/faces at the CURRENT rollback stop. Same enumeration the
        // shared readers walk (GetBodies2(swSolidBody,true) → GetEdges()/GetFaces()); open-edge midpoints
        // stay off the IGetCurve path (ADR-030). counts = {faces, edges, vertices} (authoritative, from
        // the body counters, so the delta is right even if an exotic face yields no representative point).
        private void SnapshotTopology(IPartDoc partDoc, List<EdgeSnap> edges, List<FaceSnap> faces, int[] counts)
        {
            object[] bodies = partDoc?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies == null) return;
            foreach (var b in bodies)
            {
                var body = b as IBody2;
                if (body == null) continue;
                counts[0] += body.GetFaceCount();
                counts[1] += body.GetEdgeCount();
                counts[2] += body.GetVertexCount();

                object[] es = body.GetEdges() as object[];
                if (es != null)
                    foreach (var e in es)
                    {
                        var edge = e as IEdge;
                        if (edge == null) continue;
                        var cp = edge.GetCurveParams2() as double[];
                        if (cp == null || cp.Length < 8) continue;
                        var snap = new EdgeSnap
                        {
                            Start = new[] { cp[0], cp[1], cp[2] },
                            End = new[] { cp[3], cp[4], cp[5] }
                        };
                        double dx = cp[0] - cp[3], dy = cp[1] - cp[4], dz = cp[2] - cp[5];
                        double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (chord > 1e-9)
                        {
                            snap.Mid = new[] { (cp[0] + cp[3]) / 2.0, (cp[1] + cp[4]) / 2.0, (cp[2] + cp[5]) / 2.0 };
                            snap.Len = chord;
                        }
                        else
                        {
                            // Closed edge (full circle): on-edge midpoint via the curve (rare — same
                            // trade BuildEdgeJson makes).
                            var curve = edge.IGetCurve();
                            if (curve != null)
                            {
                                var mid = curve.Evaluate2((cp[6] + cp[7]) / 2.0, 0) as double[];
                                if (mid != null && mid.Length >= 3) snap.Mid = new[] { mid[0], mid[1], mid[2] };
                                try { snap.Len = curve.GetLength3(cp[6], cp[7]); } catch { }
                            }
                            if (snap.Mid == null) snap.Mid = snap.Start;
                        }
                        edges.Add(snap);
                    }

                object[] fs = body.GetFaces() as object[];
                if (fs != null)
                    foreach (var f in fs)
                    {
                        var face = f as IFace2;
                        if (face == null) continue;
                        var fsnap = new FaceSnap();
                        try
                        {
                            var surf = face.IGetSurface();
                            fsnap.Planar = surf != null && surf.IsPlane();
                            try { fsnap.Area = face.GetArea(); } catch { }
                            if (fsnap.Planar)
                            {
                                var n = face.Normal as double[];
                                if (n != null && n.Length >= 3) fsnap.Normal = new[] { n[0], n[1], n[2] };
                            }
                            var uv = face.GetUVBounds() as double[];
                            if (surf != null && uv != null && uv.Length >= 4)
                            {
                                var pt = surf.Evaluate((uv[0] + uv[1]) / 2.0, (uv[2] + uv[3]) / 2.0, 0, 0) as double[];
                                if (pt != null && pt.Length >= 3) fsnap.Point = new[] { pt[0], pt[1], pt[2] };
                            }
                        }
                        catch { }
                        if (fsnap.Point != null) faces.Add(fsnap);
                    }
            }
        }

        // Geometric diff between two rollback stops — matching is by GEOMETRY (midpoints / planes),
        // never by index: SolidWorks renumbers faces/edges at every stop. Adds consumed_edges /
        // created_faces to the entry (only when non-empty — keeps the map compact).
        private void DiffTopology(List<EdgeSnap> prevE, List<FaceSnap> prevF,
            List<EdgeSnap> curE, List<FaceSnap> curF, JObject entry)
        {
            // The prev snapshot was the CUR snapshot of the previous diff, so its Matched flags are
            // stale — an edge that survived step N would silently skip the consumed check at step N+1
            // (live-caught: both of level-2-2's chamfers reported no consumed edge). Reset them all.
            foreach (var pe in prevE) pe.Matched = false;
            foreach (var pf in prevF) pf.Matched = false;

            // Edge pass 1 — identical edges (same midpoint AND length) survive unchanged.
            foreach (var pe in prevE)
                foreach (var ce in curE)
                {
                    if (ce.Matched) continue;
                    if (PtEq(pe.Mid, ce.Mid) && Math.Abs(pe.Len - ce.Len) <= MapTol)
                    { pe.Matched = ce.Matched = true; break; }
                }

            // An unmatched previous edge whose BOTH endpoints are gone was CONSUMED (the fillet/chamfer
            // target). If an endpoint survives, the feature merely TRIMMED/extended the edge (e.g. the
            // neighbours of a filleted corner — their midpoints move by r/2, far beyond tolerance) and
            // it is NOT reported: that separation is what keeps consumed_edges a clean anchor list.
            var consumed = new JArray();
            foreach (var pe in prevE)
            {
                if (pe.Matched) continue;
                bool startSurvives = false, endSurvives = false;
                foreach (var ce in curE)
                {
                    if (!startSurvives && (PtEq(pe.Start, ce.Start) || PtEq(pe.Start, ce.End))) startSurvives = true;
                    if (!endSurvives && (PtEq(pe.End, ce.Start) || PtEq(pe.End, ce.End))) endSurvives = true;
                    if (startSurvives && endSurvives) break;
                }
                if (!startSurvives && !endSurvives)
                    consumed.Add(new JObject { ["mid"] = Pt6(pe.Mid), ["len"] = R6(pe.Len) });
            }
            if (consumed.Count > 0) entry["consumed_edges"] = consumed;

            // Face pass 1 — identical faces (same representative point AND area).
            foreach (var pf in prevF)
                foreach (var cf in curF)
                {
                    if (cf.Matched) continue;
                    if (PtEq(pf.Point, cf.Point)
                        && Math.Abs(pf.Area - cf.Area) <= 1e-9 + 1e-6 * Math.Max(pf.Area, cf.Area))
                    { pf.Matched = cf.Matched = true; break; }
                }

            // Face pass 2 — surviving PLANES: an unmatched current planar face lying on a plane that
            // already existed is a TRIMMED/extended face (its UV-mid representative point moves with the
            // trim), not a new surface. Only genuinely NEW surfaces are reported as created. (A trimmed
            // NON-planar face can still reappear here — known noise, the anchors come from consumed_edges.)
            var created = new JArray();
            foreach (var cf in curF)
            {
                if (cf.Matched) continue;
                bool onExistingPlane = false;
                if (cf.Planar && cf.Normal != null)
                {
                    foreach (var pf in prevF)
                    {
                        if (!pf.Planar || pf.Normal == null) continue;
                        double dot = pf.Normal[0] * cf.Normal[0] + pf.Normal[1] * cf.Normal[1] + pf.Normal[2] * cf.Normal[2];
                        if (dot < 1.0 - 1e-6) continue; // must be parallel, SAME sign
                        double d = (pf.Point[0] - cf.Point[0]) * cf.Normal[0]
                                 + (pf.Point[1] - cf.Point[1]) * cf.Normal[1]
                                 + (pf.Point[2] - cf.Point[2]) * cf.Normal[2];
                        if (Math.Abs(d) <= MapTol) { onExistingPlane = true; break; }
                    }
                }
                if (onExistingPlane) continue;
                var cj = new JObject { ["point"] = Pt6(cf.Point), ["planar"] = cf.Planar };
                if (cf.Normal != null) cj["normal"] = Pt6(cf.Normal);
                cj["area"] = R6(cf.Area);
                created.Add(cj);
            }
            if (created.Count > 0) entry["created_faces"] = created;
        }

        private JObject BuildFeatureMap(IModelDoc2 modelDoc, IPartDoc partDoc, string fromFeature, string toFeature,
            out string errCode, out string errMsg)
        {
            errCode = null; errMsg = null;

            // Cache the walk UP FRONT, at the fully-rolled-forward state: features BEHIND a rolled-back
            // bar report IsSuppressed()==true, so name/type/suppression must be read before the bar moves.
            var names = new List<string>(); var types = new List<string>(); var supp = new List<bool>();
            var feat = modelDoc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 5000)
            {
                string t = feat.GetTypeName2() ?? "";
                if (!NoiseFeatureTypes.Contains(t))
                {
                    names.Add(feat.Name); types.Add(t);
                    bool s = false; try { s = feat.IsSuppressed(); } catch { }
                    supp.Add(s);
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            if (names.Count == 0)
            {
                errCode = "FEATURE_MAP_FAILED"; errMsg = "The feature tree is empty.";
                return null;
            }

            int startIdx = 0, endIdx = names.Count - 1;
            if (!string.IsNullOrEmpty(fromFeature))
            {
                startIdx = names.IndexOf(fromFeature);
                if (startIdx < 0)
                {
                    errCode = "FEATURE_NOT_FOUND";
                    errMsg = $"from_feature '{fromFeature}' not found. Available: {string.Join(", ", names)}";
                    return null;
                }
            }
            if (!string.IsNullOrEmpty(toFeature))
            {
                endIdx = names.IndexOf(toFeature);
                if (endIdx < 0)
                {
                    errCode = "FEATURE_NOT_FOUND";
                    errMsg = $"to_feature '{toFeature}' not found. Available: {string.Join(", ", names)}";
                    return null;
                }
            }
            if (endIdx < startIdx)
            {
                errCode = "INVALID_RANGE";
                errMsg = $"to_feature '{toFeature}' precedes from_feature '{fromFeature}' in the tree.";
                return null;
            }

            // First rollback stop = the first unsuppressed solid-affecting feature in range; the baseline
            // snapshot is taken just BEFORE it.
            int firstStop = -1;
            for (int i = startIdx; i <= endIdx; i++)
                if (!supp[i] && !NonSolidFeatureTypes.Contains(types[i])) { firstStop = i; break; }

            var mapArr = new JArray();
            bool rolled = false;
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var prevEdges = new List<EdgeSnap>(); var prevFaces = new List<FaceSnap>(); var prevCounts = new int[3];
                if (firstStop >= 0)
                {
                    rolled = true;
                    bool okBase = modelDoc.FeatureManager.EditRollback(
                        (int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, names[firstStop]);
                    if (!okBase)
                    {
                        errCode = "ROLLBACK_FAILED";
                        errMsg = $"Could not move the rollback bar before '{names[firstStop]}' (is a sketch being edited?).";
                        return null;
                    }
                    SnapshotTopology(partDoc, prevEdges, prevFaces, prevCounts);
                }

                for (int i = startIdx; i <= endIdx; i++)
                {
                    var entry = new JObject { ["feature"] = names[i], ["type"] = types[i] };
                    if (supp[i]) { entry["suppressed"] = true; mapArr.Add(entry); continue; }
                    if (NonSolidFeatureTypes.Contains(types[i])) { mapArr.Add(entry); continue; }

                    bool ok = modelDoc.FeatureManager.EditRollback(
                        (int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, names[i]);
                    if (!ok)
                    {
                        // Bar did not move — this feature's changes FOLD INTO the next stop's delta.
                        entry["error"] = "ROLLBACK_FAILED (delta folds into the next feature)";
                        mapArr.Add(entry);
                        continue;
                    }

                    var curEdges = new List<EdgeSnap>(); var curFaces = new List<FaceSnap>(); var curCounts = new int[3];
                    SnapshotTopology(partDoc, curEdges, curFaces, curCounts);
                    entry["delta"] = new JObject
                    {
                        ["faces"] = curCounts[0] - prevCounts[0],
                        ["edges"] = curCounts[1] - prevCounts[1],
                        ["vertices"] = curCounts[2] - prevCounts[2]
                    };
                    DiffTopology(prevEdges, prevFaces, curEdges, curFaces, entry);
                    prevEdges = curEdges; prevFaces = curFaces; prevCounts = curCounts;
                    mapArr.Add(entry);
                }
            }
            finally
            {
                // Non-destructive guarantee: whatever happened, put the bar back at the END so the
                // original document is left exactly as found (nothing is saved either way).
                if (rolled)
                    try { modelDoc.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, ""); } catch { }
            }
            swTotal.Stop();
            ExecLog.Write($"FeatureMap: entries={mapArr.Count} range=[{names[startIdx]}..{names[endIdx]}] total={swTotal.ElapsedMilliseconds}ms");

            var root = new JObject();
            root["feature_count"] = mapArr.Count;
            root["map"] = mapArr;
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

        // ==================================================================
        // ASSEMBLY SURFACE (Phase B, ADR-047) — readers first (B1), builders (B3).
        // Signatures reflection-verified 2026-07-09 (scratch/b1_reflection_notes.md).
        // Mate types/alignments come from the ENUMS — live SW is Turkish, display
        // names are never trusted. Enum ints inlined per ADR-018 (values reflected
        // from swconst 2026-07-09).
        // ==================================================================

        // swMateType_e — only the vocabulary slice + safe neighbors get canonical names;
        // anything else reports unknown_<n> and becomes a VOCABULARY_GAP at the IR step (C5).
        private static string MateTypeName(int t)
        {
            switch (t)
            {
                case 0: return "coincident";
                case 1: return "concentric";
                case 2: return "perpendicular";
                case 3: return "parallel";
                case 4: return "tangent";
                case 5: return "distance";
                case 6: return "angle";
                case 8: return "symmetric";
                case 11: return "width";
                case 16: return "lock";
                case 20: return "coordinate";
                case 21: return "slot";
                case 22: return "hinge";
                case 24: return "profile_center";
                default: return "unknown_" + t;
            }
        }

        private static int MateTypeFromName(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "coincident": return 0;
                case "concentric": return 1;
                case "perpendicular": return 2;
                case "parallel": return 3;
                case "tangent": return 4;
                case "distance": return 5;
                case "angle": return 6;
                case "lock": return 16;
                default: return -1;
            }
        }

        // swMateAlign_e: 0=aligned, 1=anti_aligned, 2=closest
        private static string MateAlignName(int a)
        {
            switch (a)
            {
                case 0: return "aligned";
                case 1: return "anti_aligned";
                case 2: return "closest";
                default: return "unknown_" + a;
            }
        }

        private static int MateAlignFromName(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "aligned": return 0;
                case "anti_aligned": return 1;
                default: return 2; // closest — let SolidWorks pick the nearer solution
            }
        }

        // swMateEntity2ReferenceType_e — the KIND of geometry a mate side references.
        private static string MateEntityKind(int rt)
        {
            switch (rt)
            {
                case 0: return "point";
                case 1: return "line";
                case 2: return "circle";
                case 3: return "plane";
                case 4: return "cylinder";
                case 5: return "sphere";
                case 6: return "set";
                case 7: return "cone";
                case 11: return "ellipse";
                case 12: return "general_curve";
                default: return "unknown_" + rt;
            }
        }

        // Component transform readback — Transform2.ArrayData: [0..8] = 3x3 rotation (row major),
        // [9..11] = translation (meters), [12] = scale. Rounded to 9dp: comfortably beyond the
        // ADR-047 verification tolerances (pos <= 1µm, rot <= 1e-6) without rounding AT them.
        private static JArray TransformJson(IComponent2 comp)
        {
            try
            {
                var xf = comp.Transform2;
                if (xf == null) return null;
                var d = xf.ArrayData as double[];
                if (d == null && xf.ArrayData is object[] oa)
                    d = Array.ConvertAll(oa, o => (double)o);
                if (d == null || d.Length < 13) return null;
                var arr = new JArray();
                for (int i = 0; i < 13; i++) arr.Add(Math.Round(d[i], 9));
                return arr;
            }
            catch { return null; }
        }

        private IComponent2 FindComponentByName(IAssemblyDoc asm, string name)
        {
            object[] comps = asm.GetComponents(true) as object[];
            if (comps == null) return null;
            foreach (var c in comps)
            {
                var comp = c as IComponent2;
                if (comp == null) continue;
                if (string.Equals(comp.Name2, name, StringComparison.OrdinalIgnoreCase)) return comp;
            }
            return null;
        }

        // Component occurrences in FEATURE-TREE order — GetComponents(true) returns an
        // ARBITRARY order (observed live: second insert listed first), but the IR grammar says
        // components first, IN TREE ORDER. Component occurrences are features of type
        // "Reference" whose specific feature is the Component2.
        private List<IComponent2> ComponentsInTreeOrder(IModelDoc2 modelDoc, IAssemblyDoc asm)
        {
            var list = new List<IComponent2>();
            var seen = new HashSet<string>();
            var feat = modelDoc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 10000)
            {
                if ((feat.GetTypeName2() ?? "") == "Reference")
                {
                    var comp = feat.GetSpecificFeature2() as IComponent2;
                    if (comp != null && seen.Add(comp.Name2)) list.Add(comp);
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            if (list.Count == 0)
            {
                // Fallback — never return nothing just because the tree walk found no Reference
                // features (unexpected): fall back to GetComponents' arbitrary order.
                object[] comps = asm.GetComponents(true) as object[];
                if (comps != null)
                    foreach (var c in comps)
                    {
                        var comp = c as IComponent2;
                        if (comp != null) list.Add(comp);
                    }
            }
            return list;
        }

        // One component occurrence -> its JSON entry (shared by the flat and top-level reads).
        private JObject ComponentJson(IComponent2 comp, int idx)
        {
            var cj = new JObject();
            cj["i"] = idx;
            cj["name"] = comp.Name2;
            try { cj["path"] = comp.GetPathName(); } catch { }
            try
            {
                var cfg = comp.ReferencedConfiguration;
                if (!string.IsNullOrEmpty(cfg)) cj["config"] = cfg;
            }
            catch { }
            cj["fixed"] = comp.IsFixed();
            if (comp.IsSuppressed()) cj["suppressed"] = true;
            var t = TransformJson(comp);
            if (t != null) cj["transform"] = t;
            return cj;
        }

        // Recursive LEAF collection for components_flat: a WRAPPER/subassembly level is
        // descended into (the user-provided samples wrap everything one level down); leaves
        // report their Transform2, which SolidWorks gives in ROOT-assembly space. The IR stays
        // FLAT — this read is ground-truth acquisition, and the artifact notes the wrapper.
        private void CollectLeafComponents(IComponent2 comp, JArray arr, ref int idx, string parent)
        {
            object[] children = null;
            try { children = comp.GetChildren() as object[]; } catch { }
            if (children != null && children.Length > 0)
            {
                foreach (var ch in children)
                {
                    var c = ch as IComponent2;
                    if (c != null) CollectLeafComponents(c, arr, ref idx, comp.Name2);
                }
                return;
            }
            var cj = ComponentJson(comp, idx++);
            if (parent != null) cj["parent"] = parent;
            arr.Add(cj);
        }

        // Component tree read (B1). Top-level components in TREE order; per instance: name,
        // source path, config, fixed/suppressed flags, FULL Transform2 (the verification ground
        // truth, ADR-047). A component with children is a SUBASSEMBLY — out of scope first round
        // (ADR-047d): still reported (with child count) so the IR step can record the gap loudly.
        private JObject ReadComponents(IModelDoc2 modelDoc, IAssemblyDoc asm)
        {
            var root = new JObject();
            var arr = new JArray();
            int idx = 0;
            var comps = ComponentsInTreeOrder(modelDoc, asm);
            {
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var cj = new JObject();
                    cj["i"] = idx++;
                    cj["name"] = comp.Name2;
                    try { cj["path"] = comp.GetPathName(); } catch { }
                    try
                    {
                        var cfg = comp.ReferencedConfiguration;
                        if (!string.IsNullOrEmpty(cfg)) cj["config"] = cfg;
                    }
                    catch { }
                    cj["fixed"] = comp.IsFixed();
                    if (comp.IsSuppressed()) cj["suppressed"] = true;
                    try
                    {
                        int children = comp.IGetChildrenCount();
                        if (children > 0) cj["children"] = children;
                    }
                    catch { }
                    var t = TransformJson(comp);
                    if (t != null) cj["transform"] = t;
                    arr.Add(cj);
                }
            }
            root["component_count"] = arr.Count;
            root["components"] = arr;
            return root;
        }

        // Mate read (B1) — walks the MateGroup's subfeatures, which IS creation order (the IR's
        // mate grammar: creation order is law). Type/alignment from the ENUMS (never the Turkish
        // display name); distance/angle value in SI from the mate's own dimension (C7 verbatim);
        // per entity: owning component (null = the assembly's own datum), geometry KIND, and
        // EntityParams (location + direction + radii, assembly space) as the geometric anchor.
        // Read-only: plain FeatureData reads, no AccessSelections needed.
        private JObject ReadMates(IModelDoc2 modelDoc)
        {
            var root = new JObject();
            var arr = new JArray();
            int idx = 0;
            var feat = modelDoc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 5000)
            {
                if ((feat.GetTypeName2() ?? "") == "MateGroup")
                {
                    var sub = feat.GetFirstSubFeature() as IFeature;
                    int subGuard = 0;
                    while (sub != null && subGuard++ < 2000)
                    {
                        var mate = sub.GetSpecificFeature2() as IMate2;
                        if (mate != null)
                        {
                            var mj = new JObject();
                            mj["i"] = idx++;
                            mj["name"] = sub.Name;
                            int mtype = -1;
                            try { mtype = mate.Type; } catch { }
                            mj["type"] = MateTypeName(mtype);
                            try { mj["alignment"] = MateAlignName(mate.Alignment); } catch { }
                            if (mtype == 5 || mtype == 6) // distance / angle carry a driving dimension
                            {
                                try
                                {
                                    var dd = mate.get_DisplayDimension2(0);
                                    var dim = dd?.GetDimension2(0);
                                    if (dim != null) mj["value"] = Math.Round(dim.SystemValue, 9); // SI: m / rad
                                }
                                catch { }
                            }
                            var ents = new JArray();
                            int ec = 0;
                            try { ec = mate.GetMateEntityCount(); } catch { }
                            for (int i = 0; i < ec; i++)
                            {
                                try
                                {
                                    var me = mate.MateEntity(i);
                                    if (me == null) continue;
                                    var ej = new JObject();
                                    var rc = me.ReferenceComponent;
                                    ej["component"] = rc != null ? rc.Name2 : null;
                                    ej["kind"] = MateEntityKind(me.ReferenceType2);
                                    var raw = me.EntityParams;
                                    double[] prms = raw as double[];
                                    if (prms == null && raw is object[] oa)
                                        prms = Array.ConvertAll(oa, o => (double)o);
                                    if (prms != null)
                                    {
                                        var pa = new JArray();
                                        foreach (var v in prms) pa.Add(Math.Round(v, 9));
                                        ej["params"] = pa;
                                    }
                                    ents.Add(ej);
                                }
                                catch { }
                            }
                            mj["entities"] = ents;
                            arr.Add(mj);
                        }
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            root["mate_count"] = arr.Count;
            root["mates"] = arr;
            return root;
        }

        // Component-scoped face/edge enumeration (B3's index-first selection, ADR-047 carry-over:
        // KNOWN-LIMITATIONS #6 — coordinate picks miss real geometry; select by INDEX instead).
        // The enumeration order here is EXACTLY the order GetComponentEntity walks, so an index
        // from this read selects the same entity in add_mate. The component's transform is
        // included so the caller can map coordinates between part and assembly space.
        private JObject ReadComponentEntities(IAssemblyDoc asm, string componentName, bool faces,
            out string errCode, out string errMsg)
        {
            errCode = null; errMsg = null;
            var comp = FindComponentByName(asm, componentName);
            if (comp == null)
            {
                errCode = "COMPONENT_NOT_FOUND";
                errMsg = $"No top-level component named '{componentName}'. Use analyze_assembly(components) for the exact Name2 values (e.g. 'Shaft-1').";
                return null;
            }
            var root = new JObject();
            root["component"] = comp.Name2;
            var t = TransformJson(comp);
            if (t != null) root["transform"] = t;
            var arr = new JArray();
            int idx = 0;
            object info;
            object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[];
            if (bodies != null)
            {
                foreach (var b in bodies)
                {
                    var body = b as IBody2;
                    if (body == null) continue;
                    if (faces)
                    {
                        object[] fs = body.GetFaces() as object[];
                        if (fs == null) continue;
                        foreach (var f in fs)
                        {
                            var face = f as IFace2;
                            if (face == null) continue;
                            arr.Add(BuildFaceJson(face, idx++));
                        }
                    }
                    else
                    {
                        object[] es = body.GetEdges() as object[];
                        if (es == null) continue;
                        foreach (var e in es)
                        {
                            var edge = e as IEdge;
                            if (edge == null) continue;
                            arr.Add(BuildEdgeJson(edge, idx++));
                        }
                    }
                }
            }
            root[faces ? "face_count" : "edge_count"] = arr.Count;
            root[faces ? "faces" : "edges"] = arr;
            return root;
        }

        // Resolve (component, kind, index) → the selectable entity, using the SAME enumeration
        // ReadComponentEntities reports. Returns null with a populated error when unresolvable.
        private object GetComponentEntity(IAssemblyDoc asm, string componentName, string kind, int index,
            out string errCode, out string errMsg)
        {
            errCode = null; errMsg = null;
            var comp = FindComponentByName(asm, componentName);
            if (comp == null)
            {
                errCode = "COMPONENT_NOT_FOUND";
                errMsg = $"No top-level component named '{componentName}'.";
                return null;
            }
            bool faces = kind == "face";
            if (!faces && kind != "edge")
            {
                errCode = "UNSUPPORTED_ENTITY_KIND";
                errMsg = $"Entity kind '{kind}' is not supported yet (face|edge). Datum-plane mates are a recorded gap.";
                return null;
            }
            int idx = 0;
            object info;
            object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[];
            if (bodies != null)
            {
                foreach (var b in bodies)
                {
                    var body = b as IBody2;
                    if (body == null) continue;
                    object[] items = faces ? body.GetFaces() as object[] : body.GetEdges() as object[];
                    if (items == null) continue;
                    foreach (var it in items)
                    {
                        if (it == null) continue;
                        if (idx == index) return it;
                        idx++;
                    }
                }
            }
            errCode = "ENTITY_NOT_FOUND";
            errMsg = $"{kind} index {index} not found on '{componentName}' (component has {idx} {kind}s). Re-read analyze_assembly({kind}s, component=...) — indices shift when geometry changes.";
            return null;
        }

        // analyze_assembly — the B1 READ tool. analysis_type: 'components' (tree + transforms),
        // 'mates' (creation order, enum types, values, entity anchors), 'faces'/'edges'
        // (component-scoped, index-first; requires 'component'). Read-only, no state bump.
        // mass_properties for the whole assembly: analyze_model(mass_properties) already works.
        public ExecutionResponse AnalyzeAssembly(ToolRequest request)
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
                string analysisType = (p?.Value<string>("analysis_type") ?? "components").ToLowerInvariant();

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "analyze_assembly requires an ASSEMBLY document to be active.");

                var results = new List<string>();
                if (analysisType == "components")
                {
                    results.Add(ReadComponents(modelDoc, asm).ToString(Newtonsoft.Json.Formatting.None));
                }
                else if (analysisType == "components_flat")
                {
                    // LEAF occurrences across all nesting levels (tree order, depth-first),
                    // transforms in ROOT space — flattens wrapper/subassembly levels for
                    // ground-truth acquisition (the IR itself stays flat; nesting is noted).
                    var root = new JObject();
                    var arr = new JArray();
                    int idx = 0;
                    foreach (var comp in ComponentsInTreeOrder(modelDoc, asm))
                        CollectLeafComponents(comp, arr, ref idx, null);
                    root["component_count"] = arr.Count;
                    root["components"] = arr;
                    results.Add(root.ToString(Newtonsoft.Json.Formatting.None));
                }
                else if (analysisType == "mates")
                {
                    results.Add(ReadMates(modelDoc).ToString(Newtonsoft.Json.Formatting.None));
                }
                else if (analysisType == "faces" || analysisType == "edges")
                {
                    string componentName = p?.Value<string>("component");
                    if (string.IsNullOrEmpty(componentName))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "MISSING_PARAMETER",
                            $"analysis_type='{analysisType}' requires 'component' (Name2 from analyze_assembly(components), e.g. 'Shaft-1').");
                    string errCode, errMsg;
                    var res = ReadComponentEntities(asm, componentName, analysisType == "faces", out errCode, out errMsg);
                    if (res == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), errCode, errMsg);
                    results.Add(res.ToString(Newtonsoft.Json.Formatting.None));
                }
                else
                {
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_ANALYSIS_TYPE",
                        $"analysis_type '{analysisType}' is not supported. Supported: components, components_flat, mates, faces, edges.");
                }

                // Read-only — no state bump (same pattern as AnalyzeModel).
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
                        DocumentType = "ASSEMBLY",
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

        // open_new_assembly — blank ASSEMBLY document (the twin of open_new_part).
        public ExecutionResponse OpenNewAssembly(ToolRequest request)
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
                    templatePath = _solidWorks.GetUserPreferenceStringValue(
                        (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
                }

                if (string.IsNullOrEmpty(templatePath) || !System.IO.File.Exists(templatePath))
                {
                    string templatesDir = System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                        "SolidWorks");
                    var found = System.IO.Directory.GetFiles(templatesDir, "*.asmdot",
                        System.IO.SearchOption.AllDirectories);
                    if (found.Length == 0)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "TEMPLATE_NOT_FOUND",
                            "No assembly template (.asmdot) found. Set a default template in SolidWorks → Tools → Options → Default Templates.");
                    templatePath = found[0];
                }

                object doc = _solidWorks.NewDocument(templatePath, 0, 0, 0);
                var modelDoc = doc as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_CREATION_FAILED",
                        $"SolidWorks NewDocument returned null. Template used: '{templatePath}'.");

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
                        DocumentType = "ASSEMBLY",
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

        // insert_component — AddComponent5 (B3). The component document must be LOADED first
        // (AddComponent5 contract), so we preload it silently, re-activate the assembly, insert,
        // then close the preloaded doc (the assembly holds its own reference). fixed=true fixes
        // the inserted instance; fixed=false explicitly unfixes (SolidWorks auto-fixes a first
        // component in some flows — we make the state deterministic either way, C7 spirit).
        public ExecutionResponse InsertComponent(ToolRequest request)
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
                if (string.IsNullOrEmpty(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "file_path is required (.sldprt to insert).");
                if (!System.IO.File.Exists(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "FILE_NOT_FOUND", $"No file at '{filePath}'.");
                double x = p?.Value<double?>("x") ?? 0.0;
                double y = p?.Value<double?>("y") ?? 0.0;
                double z = p?.Value<double?>("z") ?? 0.0;
                string config = p?.Value<string>("config") ?? "";
                bool? fixedFlag = p?.Value<bool?>("fixed");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "insert_component requires an ASSEMBLY document to be active (open_new_assembly first).");
                string asmTitle = modelDoc.GetTitle();

                // Preload the component document (required by AddComponent5), then restore the
                // assembly as active — OpenDoc6 activates what it opens.
                int e1 = 0, w1 = 0;
                var partDoc = _solidWorks.OpenDoc6(filePath, (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e1, ref w1) as IModelDoc2;
                if (partDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "OPEN_FAILED", $"Could not preload '{filePath}' (errors={e1}).");
                string partTitle = partDoc.GetTitle();
                int actErr = 0;
                _solidWorks.ActivateDoc3(asmTitle, false, 1 /* swDontRebuildActiveDoc */, ref actErr);

                var comp = asm.AddComponent5(filePath,
                    0 /* swAddComponentConfigOptions_CurrentSelectedConfig */, "",
                    false, config, x, y, z);
                if (comp == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INSERT_FAILED",
                        $"AddComponent5 returned null for '{filePath}' (config='{config}'). The file must be a loadable part/assembly document.");

                // Optional FULL transform placement (13 doubles: 3x3 rotation row-major, translation
                // xyz, scale — the exact layout analyze_assembly reports). Needed for mate-less
                // imported assemblies (1-3 class) whose fixed components sit at arbitrary rotations
                // x/y/z alone cannot express. JSON-string param per the ADR-022 idiom.
                string transformJson = p?.Value<string>("transform_json");
                if (!string.IsNullOrEmpty(transformJson) && transformJson != "[]")
                {
                    var tvals = Newtonsoft.Json.Linq.JArray.Parse(transformJson);
                    if (tvals.Count < 13)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "transform_json needs 13 numbers: 9 rotation (row-major) + 3 translation + scale.");
                    var arr = new double[16];
                    for (int i = 0; i < 13; i++) arr[i] = (double)tvals[i];
                    arr[13] = arr[14] = arr[15] = 0.0;
                    var mu = _solidWorks.IGetMathUtility();
                    var xf = mu.CreateTransform(arr) as MathTransform;
                    if (xf == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "TRANSFORM_FAILED", "CreateTransform rejected the array.");
                    comp.Transform2 = xf;
                }

                // Deterministic fixed state when the caller asked for one.
                if (fixedFlag.HasValue && comp.IsFixed() != fixedFlag.Value)
                {
                    modelDoc.ClearSelection2(true);
                    if (comp.Select4(false, null, false))
                    {
                        if (fixedFlag.Value) asm.FixComponent(); else asm.UnfixComponent();
                    }
                    modelDoc.ClearSelection2(true);
                }

                // Close the preloaded standalone doc — the assembly keeps its own reference.
                try { if (partTitle != asmTitle) _solidWorks.CloseDoc(partTitle); } catch { }

                // CloseDoc leaves the ACTIVE doc unpredictable (observed live: the next call saw a
                // non-assembly active doc). Re-activate the assembly deterministically.
                int actErr2 = 0;
                _solidWorks.ActivateDoc3(asmTitle, false, 1 /* swDontRebuildActiveDoc */, ref actErr2);

                modelDoc.EditRebuild3();

                var features = new List<string>();
                // features[0] = the PLAIN runtime component name (instance-numbered Name2) — the
                // pycompiler records features[0] as the node's runtime name, exactly like created
                // feature names elsewhere; mates reference components through it.
                features.Add(comp.Name2);
                features.Add($"fixed={comp.IsFixed()}");
                var tj = TransformJson(comp);
                if (tj != null) features.Add("transform=" + tj.ToString(Newtonsoft.Json.Formatting.None));

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
                        DocumentType = "ASSEMBLY",
                        ActiveSketch = null,
                        Features = features,
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

        // The most recent mate feature's name (the MateGroup's LAST subfeature) — AddMate5
        // returns a Mate2 with no name accessor, so the readback goes through the tree.
        private string LastMateName(IModelDoc2 modelDoc)
        {
            string last = null;
            var feat = modelDoc.FirstFeature() as IFeature;
            int guard = 0;
            while (feat != null && guard++ < 5000)
            {
                if ((feat.GetTypeName2() ?? "") == "MateGroup")
                {
                    var sub = feat.GetFirstSubFeature() as IFeature;
                    int subGuard = 0;
                    while (sub != null && subGuard++ < 2000)
                    {
                        if (sub.GetSpecificFeature2() is IMate2) last = sub.Name;
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            return last;
        }

        // add_mate — AddMate5 (B3). Entity selection is INDEX-FIRST: each side is
        // (component Name2, kind face|edge, index from analyze_assembly(faces|edges, component)).
        // value: METERS for distance, DEGREES for angle (C4 — degrees at tool boundaries).
        // After the mate: rebuild + report BOTH sides' component transforms (the per-mate stop
        // check — compare against the original's readback before the next mate).
        public ExecutionResponse AddMateTool(ToolRequest request)
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
                string mateType = p?.Value<string>("mate_type");
                int mateTypeInt = MateTypeFromName(mateType);
                if (mateTypeInt < 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "UNSUPPORTED_MATE_TYPE",
                        $"mate_type '{mateType}' is not supported. Supported: coincident, concentric, perpendicular, parallel, tangent, distance, angle, lock.");
                int alignInt = MateAlignFromName(p?.Value<string>("alignment"));
                bool flip = p?.Value<bool?>("flip") ?? false;
                double value = p?.Value<double?>("value") ?? 0.0;

                string aComp = p?.Value<string>("a_component");
                string aKind = (p?.Value<string>("a_kind") ?? "face").ToLowerInvariant();
                int aIndex = p?.Value<int?>("a_index") ?? -1;
                string bComp = p?.Value<string>("b_component");
                string bKind = (p?.Value<string>("b_kind") ?? "face").ToLowerInvariant();
                int bIndex = p?.Value<int?>("b_index") ?? -1;
                if (string.IsNullOrEmpty(aComp) || string.IsNullOrEmpty(bComp) || aIndex < 0 || bIndex < 0)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER",
                        "a_component/a_index and b_component/b_index are required (indices from analyze_assembly(faces|edges, component=...)).");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "add_mate requires an ASSEMBLY document to be active.");

                string errCode, errMsg;
                object entA = GetComponentEntity(asm, aComp, aKind, aIndex, out errCode, out errMsg);
                if (entA == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), errCode, "side A: " + errMsg);
                object entB = GetComponentEntity(asm, bComp, bKind, bIndex, out errCode, out errMsg);
                if (entB == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), errCode, "side B: " + errMsg);

                modelDoc.ClearSelection2(true);
                var selMgr = modelDoc.SelectionManager as ISelectionMgr;
                var sd = selMgr?.CreateSelectData();
                if (sd != null) sd.Mark = 1; // mate entities carry mark 1
                bool selA = ((IEntity)entA).Select4(true, sd);
                bool selB = ((IEntity)entB).Select4(true, sd);
                if (!selA || !selB)
                {
                    modelDoc.ClearSelection2(true);
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SELECTION_FAILED", $"Entity selection failed (A={selA}, B={selB}).");
                }

                double dist = 0, distU = 0, distL = 0, ang = 0, angU = 0, angL = 0;
                if (mateTypeInt == 5) { dist = value; distU = value; distL = value; }
                if (mateTypeInt == 6) { ang = value * Math.PI / 180.0; angU = ang; angL = ang; }

                int errStat = 0;
                var mate = asm.AddMate5(mateTypeInt, alignInt, flip,
                    dist, distU, distL, 0, 0, ang, angU, angL,
                    false /* ForPositioningOnly */, false /* LockRotation */, 0 /* WidthMateOption */,
                    out errStat);
                modelDoc.ClearSelection2(true);
                if (mate == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "ADD_MATE_FAILED",
                        $"AddMate5 returned null (ErrorStatus={errStat}, swAddMateError_e). Check the entity kinds fit the mate type (e.g. concentric wants cylindrical faces) and the alignment.");

                modelDoc.EditRebuild3();

                var features = new List<string>();
                string mateName = LastMateName(modelDoc);
                if (mateName != null) features.Add($"mate={mateName}");
                features.Add($"type={MateTypeName(mateTypeInt)}");
                features.Add($"error_status={errStat}");
                var compA = FindComponentByName(asm, aComp);
                var compB = FindComponentByName(asm, bComp);
                var ta = compA != null ? TransformJson(compA) : null;
                var tb = compB != null ? TransformJson(compB) : null;
                if (ta != null) features.Add("a_transform=" + ta.ToString(Newtonsoft.Json.Formatting.None));
                if (tb != null) features.Add("b_transform=" + tb.ToString(Newtonsoft.Json.Formatting.None));

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
                        DocumentType = "ASSEMBLY",
                        ActiveSketch = null,
                        Features = features,
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

        // save_body_as_part — extract ONE body of a multibody part into its own SLDPRT
        // (CreateFeatureFromBody3 on a fresh part; geometry stays in the SOURCE coordinates).
        // The missing-parts half of flattened-assembly reconstruction (ADR-048): a structured
        // assembly STEP with no per-part files flattens to a multibody import; each distinct
        // body becomes a referenced part file, instances get transforms via fingerprint match.
        public ExecutionResponse SaveBodyAsPart(ToolRequest request)
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
                int bodyIndex = p?.Value<int?>("body_index") ?? -1;
                string filePath = p?.Value<string>("file_path");
                if (bodyIndex < 0 || string.IsNullOrEmpty(filePath))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "body_index (>= 0) and file_path (.sldprt) are required.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var partDoc = modelDoc as IPartDoc;
                if (partDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "save_body_as_part requires a (multibody) PART document to be active.");
                string srcTitle = modelDoc.GetTitle();

                object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (bodies == null || bodyIndex >= bodies.Length)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "BODY_NOT_FOUND", $"body_index {bodyIndex} out of range (document has {bodies?.Length ?? 0} solid bodies).");
                var body = bodies[bodyIndex] as IBody2;
                object copy = body.Copy2(false);
                if (copy == null) copy = body.Copy();
                if (copy == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "BODY_COPY_FAILED", "IBody2.Copy2/Copy returned null.");

                string templatePath = _solidWorks.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var newDoc = _solidWorks.NewDocument(templatePath, 0, 0, 0) as IModelDoc2;
                var newPart = newDoc as IPartDoc;
                if (newPart == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "DOCUMENT_CREATION_FAILED", "Could not create the target part document.");
                string newTitle = newDoc.GetTitle();

                var feat = newPart.CreateFeatureFromBody3(copy, false, 0) as IFeature;
                if (feat == null)
                {
                    try { _solidWorks.CloseDoc(newTitle); } catch { }
                    int actErr0 = 0;
                    _solidWorks.ActivateDoc3(srcTitle, false, 1, ref actErr0);
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "BODY_FEATURE_FAILED", "CreateFeatureFromBody3 returned null for the copied body.");
                }
                newDoc.EditRebuild3();

                string dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                int se = 0, sw = 0;
                bool ok = newDoc.Extension.SaveAs3(filePath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, null, ref se, ref sw);
                try { _solidWorks.CloseDoc(newDoc.GetTitle()); } catch { }
                int actErr = 0;
                _solidWorks.ActivateDoc3(srcTitle, false, 1 /* swDontRebuildActiveDoc */, ref actErr);
                if (!ok)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SAVE_FAILED", $"SaveAs3('{filePath}') failed (errors={se}, warnings={sw}).");

                var features = new List<string>();
                features.Add(System.IO.Path.GetFileName(filePath));
                try { if (!string.IsNullOrEmpty(body.Name)) features.Add("body_name=" + body.Name); } catch { }

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = srcTitle,
                        DocumentType = "PART",
                        ActiveSketch = null,
                        Features = features,
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
