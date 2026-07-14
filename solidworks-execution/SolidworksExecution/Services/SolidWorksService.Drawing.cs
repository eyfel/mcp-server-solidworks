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
    // SolidWorksService partial: technical-drawing tools (views, sections, auto-dimension, center marks, callouts) and the drawing readers.
    public partial class SolidWorksService
    {

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
    }
}
