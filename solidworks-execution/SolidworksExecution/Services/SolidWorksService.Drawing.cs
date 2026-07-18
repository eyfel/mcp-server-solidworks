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
                // Batch-4: deterministic relations + cross-view station join keys. Relations are computed
                // FROM the geometry read, so the flag forces geometry on. All Batch-4 blocks are ADDITIVE —
                // the raw geometry arrays are never replaced or altered by them.
                bool includeRelations = pAd?.Value<bool?>("include_relations") ?? false;
                if (includeRelations) includeGeometry = true;

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
                var stationViews = new List<StationViewData>();   // Batch-4: per-view data for the station join
                var relSkippedVids = new JArray();                // views excluded from relations (loud, C5)

                object viewObj = drawingDoc.GetFirstView(); // first view = the sheet container
                int guard = 0;
                while (viewObj != null && guard++ < 2000)
                {
                    var view = viewObj as IView;
                    if (view != null)
                    {
                        var vj = new JObject();
                        string vid = "v" + viewsArr.Count;   // views[] index — the contract's view alias
                        vj["name"] = view.Name;
                        int vType = -1;
                        try { vType = view.Type; vj["type"] = vType; } catch { }   // swDrawingViewTypes_e (raw int)
                        try { vj["scale"] = R6(view.ScaleDecimal); } catch { }
                        try
                        {
                            var pos = view.Position as double[];
                            if (pos != null && pos.Length >= 2)
                                vj["pos"] = new JArray { R6(pos[0]), R6(pos[1]) };
                        }
                        catch { }
                        if (includeRelations) vj["vid"] = vid;

                        // Geometry first (Batch-4 reordering): the relation/measure blocks and the dimension
                        // attachment both consume the SAME extraction — one read, no second GetPolylines7 pass.
                        ViewGeomData geomData = null;
                        double[] frO = null, frX = null, frY = null;
                        if (includeGeometry)
                        {
                            geomData = ExtractViewGeometry(view);
                            vj["geometry"] = BuildGeometryJson(geomData, view);
                        }
                        bool relationsHere = includeRelations && vType != 1
                            && geomData != null && geomData.Err == null;
                        if (relationsHere)
                        {
                            if (ViewModelFrameRaw(view, out frO, out frX, out frY))
                            {
                                var rel = ComputeViewRelations(geomData);
                                if (rel != null) vj["relations"] = rel;
                                var cm = ReadCenterMarks(view, frO, frX, frY, geomData);
                                if (cm != null && cm.Count > 0) vj["center_marks"] = cm;
                                var cl = ReadCenterlines(view, geomData);
                                if (cl != null && cl.Count > 0) vj["centerlines"] = cl;
                                stationViews.Add(new StationViewData { Vid = vid, O = frO, X = frX, Y = frY, Geom = geomData });
                            }
                            else
                            {
                                // No usable view→model transform ⇒ this view cannot join relations/stations.
                                // Recorded loudly instead of silently thinning the output (C5).
                                relSkippedVids.Add(vid);
                                relationsHere = false;
                            }
                        }
                        var dimsArr = ReadViewDimensions(view, relationsHere ? geomData : null, frO, frX, frY);
                        totalDims += dimsArr.Count;
                        vj["dimensions"] = dimsArr;
                        // Batch-1 Task-2: section views also carry cutting-plane metadata (parent view +
                        // model-space normal/frame) so section-coordinate → 3D mapping is arithmetic.
                        var sect = ReadSectionMetadata(view);
                        if (sect != null) vj["section"] = sect;
                        viewsArr.Add(vj);
                    }
                    viewObj = (view != null) ? view.GetNextView() : null;
                }

                root["view_count"] = viewsArr.Count;
                root["dimension_count"] = totalDims;
                root["views"] = viewsArr;
                if (includeRelations)
                {
                    var st = ComputeStations(stationViews);
                    if (st != null) root["stations"] = st;
                    if (relSkippedVids.Count > 0) root["relations_skipped_views"] = relSkippedVids;
                }

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
        //
        // Batch-1 Task-1 (DIMENSION→GEOMETRY MAPPING): each dim also reports WHAT it measures, as DATA
        // (never inference — the "what is this 17mm the dimension of?" question). Added fields:
        //   diametric  — true when the display shows it as a Ø (IDisplayDimension.Diametric) — kills "is 17 the
        //                diameter?" (this IS reliable; IDimension.GetType() is NOT — it comes back Unknown(0)
        //                for most model-inserted drawing dims and mis-labels a chamfer angle, so it is not used).
        //   attached   — the KIND(s) of geometry the dimension is attached to (swSelectType_e: edge/vertex/
        //                sketch_seg/...), read from the underlying IAnnotation's attached-entity types.
        //   anchors    — the dimension's REFERENCE POINTS in MODEL space (meters) for a model dimension — the
        //                concrete 3D geometry it snaps to (live-proven: the Ø17 bore's anchor is [0,0,0.0651],
        //                on the axis at the top plane), so a reader maps the value to a 3D location arithmetically.
        // All reflection-verified against SolidWorks.Interop.sldworks (IAnnotation.GetAttachedEntityTypes,
        // IDimension.GetReferencePointsCount/IGetReferencePoints→IMathPoint.ArrayData). swconst ints inlined
        // (ADR-018 — never load swconst as a type at runtime).
        //
        // Batch-4 (DIMENSION → PRIMITIVE ATTACHMENT): when the view's extracted geometry + model frame are
        // handed in (include_relations mode), each dimension additionally names the primitive id(s) it
        // measures — `measures` — resolved ARITHMETICALLY from its anchors: anchor (model 3D) → view 2D via
        // the frame (u=(p−o)·xdir, v=(p−o)·ydir), then point-vs-primitive distance ≤ RelTol. Sources:
        // anchor_at_center (anchor sits on a circle/arc CENTER — the live-proven Ø-dim shape) or
        // anchor_on_primitive (anchor lies on the primitive's geometry). A dimension whose anchors resolve
        // to NO primitive is flagged `unattached:true` — loud, never dropped, never guessed (C5).
        private JArray ReadViewDimensions(IView view, ViewGeomData geom = null,
            double[] frO = null, double[] frX = null, double[] frY = null)
        {
            var arr = new JArray();
            bool attach = geom != null && frO != null && frX != null && frY != null;
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
                        // value_si must be SI (meters/radians) for EVERY dim. GetSystemValue3(swThisConfiguration)
                        // is correct for MODEL dims shown in the drawing, but for reference dims CREATED in the
                        // drawing (RD*@Drawing View…) it resolves no configuration and the value falls through in
                        // DOCUMENT units (live on level-2-2: RD4 read 125.864691 "meters" = the 125.86 mm block
                        // width). SystemValue is the configuration-independent SI accessor (the same one
                        // modify_dimension writes through) — prefer it here; drawings have no configurations, so
                        // nothing is lost. GetSystemValue3 stays as the fallback, raw Value as the last resort.
                        double val = dim.Value;
                        bool gotSi = false;
                        try { val = dim.SystemValue; gotSi = true; } catch { }
                        if (!gotSi)
                        {
                            try
                            {
                                var sv = dim.GetSystemValue3(1, null) as double[]; // 1 = swThisConfiguration (inlined)
                                if (sv != null && sv.Length > 0) val = sv[0];
                            }
                            catch { }
                        }
                        dj["value_si"] = R6(val);

                        // --- Ø flag (the reliable dimension-kind signal) ---
                        try { if (disp.Diametric) dj["diametric"] = true; } catch { }

                        // --- attached-entity KIND(s): what geometry this dim hangs off ---
                        try
                        {
                            var ann = disp.GetAnnotation() as IAnnotation;
                            if (ann != null)
                            {
                                object rawTypes = null;
                                try { rawTypes = ann.GetAttachedEntityTypes(); } catch { }
                                var kinds = new JArray();
                                var seenKinds = new HashSet<string>();
                                foreach (int et in ToIntArray(rawTypes))
                                {
                                    string kn = AttachEntityKindName(et);
                                    if (seenKinds.Add(kn)) kinds.Add(kn);
                                }
                                if (kinds.Count > 0) dj["attached"] = kinds;
                            }
                        }
                        catch { }

                        // --- ANCHOR points (sheet space, meters): the concrete geometry the dim references ---
                        JArray anchorsArr = null;
                        try
                        {
                            anchorsArr = ReadDimensionAnchors(dim);
                            if (anchorsArr != null && anchorsArr.Count > 0) dj["anchors"] = anchorsArr;
                        }
                        catch { }

                        // --- Batch-4: name the primitive(s) this dimension measures (or flag it loudly) ---
                        if (attach)
                        {
                            try { AttachDimensionToPrimitives(dj, anchorsArr, geom, frO, frX, frY); }
                            catch { }
                        }

                        arr.Add(dj);
                    }
                }
                object next = null;
                try { next = disp != null ? disp.GetNext5() : null; } catch { next = null; }
                dispObj = next;
            }
            return arr;
        }

        // The dimension's reference points (the points it snaps to) in MODEL space (meters) for a model
        // dimension. IDimension exposes GetReferencePointsCount() + IGetReferencePoints(i)→IMathPoint
        // (ArrayData = [x,y,z]); the aggregate get_ReferencePoints() returns an array of the same MathPoints.
        // We prefer the indexed accessor and fall back to the aggregate. Rounded to 6 decimals (1µm). Identical
        // points are de-duped: model-inserted drawing dims frequently report both reference points at the same
        // spot (the attach location), so a lone point carries the same information without the duplicate noise.
        private JArray ReadDimensionAnchors(IDimension dim)
        {
            var raw = new JArray();
            int n = 0;
            try { n = dim.GetReferencePointsCount(); } catch { n = 0; }
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    IMathPoint mp = null;
                    try { mp = dim.IGetReferencePoints(i) as IMathPoint; } catch { mp = null; }
                    var pj = MathPointJson(mp);
                    if (pj != null) raw.Add(pj);
                }
            }
            if (raw.Count == 0)
            {
                // Fallback: the aggregate accessor (array of MathPoint COM objects).
                try
                {
                    var agg = dim.ReferencePoints as object[];
                    if (agg != null)
                        foreach (var o in agg)
                        {
                            var pj = MathPointJson(o as IMathPoint);
                            if (pj != null) raw.Add(pj);
                        }
                }
                catch { }
            }
            // De-dup identical consecutive points.
            var pts = new JArray();
            foreach (var t in raw)
            {
                bool dup = false;
                foreach (var u in pts)
                    if (JToken.DeepEquals(t, u)) { dup = true; break; }
                if (!dup) pts.Add(t);
            }
            return pts;
        }

        private static JArray MathPointJson(IMathPoint mp)
        {
            if (mp == null) return null;
            try
            {
                var d = mp.ArrayData as double[];
                if (d != null && d.Length >= 3)
                    return new JArray { R6(d[0]), R6(d[1]), R6(d[2]) };
            }
            catch { }
            return null;
        }

        // Coerce a COM VARIANT array (int[] or object[] of boxed ints) into an int[]. GetAttachedEntityTypes
        // and section-line getters come back as one of these shapes depending on marshaling.
        private static int[] ToIntArray(object raw)
        {
            if (raw is int[] ia) return ia;
            if (raw is object[] oa)
            {
                var outp = new int[oa.Length];
                for (int i = 0; i < oa.Length; i++)
                {
                    try { outp[i] = Convert.ToInt32(oa[i]); } catch { outp[i] = 0; }
                }
                return outp;
            }
            if (raw is double[] da)
            {
                var outp = new int[da.Length];
                for (int i = 0; i < da.Length; i++) outp[i] = (int)Math.Round(da[i]);
                return outp;
            }
            return System.Array.Empty<int>();
        }

        // Coerce a COM VARIANT array (double[] or object[] of boxed numbers) into a double[].
        private static double[] ToDoubleArray(object raw)
        {
            if (raw is double[] da) return da;
            if (raw is object[] oa)
            {
                var outp = new double[oa.Length];
                for (int i = 0; i < oa.Length; i++)
                {
                    try { outp[i] = Convert.ToDouble(oa[i]); } catch { return null; }
                }
                return outp;
            }
            return null;
        }

        // swSelectType_e (subset that a drawing dimension attaches to) → label. Inlined ints (ADR-018).
        private static string AttachEntityKindName(int t)
        {
            switch (t)
            {
                case 1: return "edge";
                case 2: return "face";
                case 3: return "vertex";
                case 9: return "sketch";
                case 10: return "sketch_seg";
                case 11: return "sketch_point";
                default: return "sel_" + t;
            }
        }

        // Batch-1 Task-2 — SECTION VIEW cutting-plane metadata. For a section view (Type==2 =
        // swDrawingSectionView, or one that owns an IDrSection) report, as DATA:
        //   parent_view  — the view the section was cut from (IView.GetBaseView, fallback IDrSection.GetView)
        //   cut_normal   — the cutting-plane NORMAL in MODEL space (= the section view's viewing direction):
        //                  the section view's view→model transform maps view-Z to this. Read exactly as
        //                  ReadSketchPlane reads a sketch normal (inverse transform, columns d[2],d[5],d[8]),
        //                  so the sign convention matches the part-side readers. Normalized.
        //   axis         — cut_normal snapped to a principal axis ("X"/"Y"/"Z") when axis-aligned — the direct
        //                  answer to "which axis is A-A perpendicular to?" (else null → read cut_normal).
        //   frame        — the section view's model-space frame {origin, xdir, ydir} (view→model), so a caller
        //                  maps a 2D section-view coordinate to 3D: p_model = origin + u*xdir + v*ydir.
        //   label        — the section label (A, B, ...) when available from the IDrSection.
        // All reflection-verified: IView.Type / GetBaseView / ModelToViewTransform / GetSection→IDrSection.
        // Best-effort — any failure just omits the affected field; a non-section view returns null.
        private JObject ReadSectionMetadata(IView view)
        {
            IDrSection section = null;
            try { section = view.GetSection() as IDrSection; } catch { section = null; }
            int vtype = -1;
            try { vtype = view.Type; } catch { }
            // Gate: swDrawingSectionView (2) OR an owning IDrSection is present.
            if (vtype != 2 && section == null) return null;

            var sj = new JObject();

            // Parent view — the view the cut was taken in.
            try
            {
                var baseView = view.GetBaseView() as IView;
                if (baseView != null) sj["parent_view"] = baseView.Name;
            }
            catch { }
            if (sj["parent_view"] == null && section != null)
            {
                try { var pv = section.GetView() as IView; if (pv != null) sj["parent_view"] = pv.Name; }
                catch { }
            }

            // Section label (A / B / ...).
            if (section != null)
            {
                try { var lbl = section.GetLabel(); if (!string.IsNullOrEmpty(lbl)) sj["label"] = lbl; }
                catch { }
                try { if (section.IsAligned()) sj["aligned"] = true; } catch { }
            }

            // Cutting-plane normal + model-space frame, from the section view's view→model transform.
            try
            {
                var m2v = view.ModelToViewTransform;   // model → view
                var v2m = m2v != null ? m2v.IInverse() : null;   // view → model
                double[] d = v2m != null ? v2m.ArrayData as double[] : null;
                if (d != null && d.Length >= 12)
                {
                    // Same column reading as ReadSketchPlane: normal = mapped view-Z (d[2],d[5],d[8]);
                    // in-plane axes xdir = d[0..2], ydir = d[3..5].
                    double nx = d[2], ny = d[5], nz = d[8];
                    double nlen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nlen > 1e-9) { nx /= nlen; ny /= nlen; nz /= nlen; }
                    sj["cut_normal"] = new JArray { R6(nx), R6(ny), R6(nz) };
                    string ax = PrincipalAxis(nx, ny, nz);
                    if (ax != null) sj["axis"] = ax;
                }
                // Frame origin: the raw v2m translation (view 0,0,0 → model) is NOT the reference the
                // view's projected geometry uses (live on level-2-2 Section C-C it landed half a meter
                // off the part), so the frame is computed by ViewModelFrame — origin = the model-space
                // point of the view geometry's (0,0) — and shared with the geometry read.
                var fr = ViewModelFrame(view);
                if (fr != null) sj["frame"] = fr;
            }
            catch { }

            return sj.Count > 0 ? sj : null;
        }

        // The model-space frame of a drawing view's PROJECTED 2D geometry: p_model = origin + u*xdir +
        // v*ydir for a (u,v) from ReadViewGeometry (model-scale meters, centered on the view anchor).
        // xdir/ydir = the view→model rotation columns (same column convention as ReadSketchPlane, proven
        // on the section axis classification). origin = the view's sheet-space anchor (IView.Position)
        // mapped through the view→model transform via IMathUtility (MultiplyTransform applies the
        // transform's scale component, so the 1:viewscale sheet→model scaling is handled) — the raw v2m
        // translation is the model point of SHEET (0,0), not of the view anchor, which is why it read
        // half a meter off the part. Verified live on level-2-2 (block corners / bore heights map exactly).
        private JObject ViewModelFrame(IView view)
        {
            double[] o, x, y;
            if (!ViewModelFrameRaw(view, out o, out x, out y)) return null;
            var fj = new JObject
            {
                ["origin"] = new JArray { R6(o[0]), R6(o[1]), R6(o[2]) },
                ["xdir"] = new JArray { R6(x[0]), R6(x[1]), R6(x[2]) },
                ["ydir"] = new JArray { R6(y[0]), R6(y[1]), R6(y[2]) },
            };
            // Batch-4 follow-up (ADR-054): the view's NORMAL snapped to a signed principal axis
            // ("Y" / "-Z" ...) — the deterministic restatement of "which way does this view look".
            // Drafting semantics the recipe builds on it: a CIRCLE in this view is the cross-section
            // of a feature whose axis runs along this normal, so circles in different-normal views
            // are DIFFERENT features unless a section proves otherwise (the 2026-07-17 benchmark
            // model conflated a top-view Y-axis stepped hole with the front-view Z-axis loft chain).
            // Computed on the UNROUNDED frame vectors; omitted (loudly absent) when not axis-aligned.
            double nx = x[1] * y[2] - x[2] * y[1];
            double ny = x[2] * y[0] - x[0] * y[2];
            double nz = x[0] * y[1] - x[1] * y[0];
            string ax = PrincipalAxis(nx, ny, nz);
            if (ax != null)
            {
                double comp = ax == "X" ? nx : (ax == "Y" ? ny : nz);
                fj["normal_axis"] = (comp < 0 ? "-" : "") + ax;
            }
            return fj;
        }

        // Raw (unrounded) frame vectors — the single transform-reading path ViewModelFrame wraps. The
        // Batch-4 relation/station/attachment arithmetic runs on these raw doubles and rounds only at
        // JSON-emit time, so residuals report the true measured deviation.
        private bool ViewModelFrameRaw(IView view, out double[] origin, out double[] xdir, out double[] ydir)
        {
            origin = null; xdir = null; ydir = null;
            try
            {
                var m2v = view.ModelToViewTransform;
                var v2m = m2v != null ? m2v.IInverse() : null;
                double[] d = v2m != null ? v2m.ArrayData as double[] : null;
                if (d == null || d.Length < 12) return false;
                var pos = view.Position as double[];
                if (pos == null || pos.Length < 2) return false;
                var mu = _solidWorks.GetMathUtility() as IMathUtility;
                var pt = mu != null ? mu.CreatePoint(new double[] { pos[0], pos[1], 0.0 }) as IMathPoint : null;
                var mapped = pt != null ? pt.MultiplyTransform(v2m) as IMathPoint : null;
                double[] o = mapped != null ? mapped.ArrayData as double[] : null;
                if (o == null || o.Length < 3) return false;
                origin = new double[] { o[0], o[1], o[2] };
                xdir = new double[] { d[0], d[1], d[2] };
                ydir = new double[] { d[3], d[4], d[5] };
                return true;
            }
            catch { return false; }
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
        // Raw primitive holders for one view's extracted geometry (Batch-4). Values are UNROUNDED —
        // relations/stations/attachment compute on them and round only at emit time. Ids are positional
        // and documented in the contract: l<i> = lines[i], a<i> = curves[i], c<i> = circles[i].
        private class LineRec { public double X1, Y1, X2, Y2; }
        private class CurveRec
        {
            public int N;
            public double SX, SY, MX, MY, EX, EY;   // start / mid / end tessellation points
            public bool HasCenter;
            public double CX, CY, R;
        }
        private class CircleRec { public double CX, CY, R; }
        private class ViewGeomData
        {
            public readonly List<LineRec> Lines = new List<LineRec>();
            public readonly List<CurveRec> Curves = new List<CurveRec>();
            public readonly List<CircleRec> Circles = new List<CircleRec>();
            public string Err;
        }

        private ViewGeomData ExtractViewGeometry(IView view)
        {
            var g = new ViewGeomData();
            try { view.UpdateViewDisplayGeometry(); } catch { }
            double[] arr;
            try { object polys; view.GetPolylines7((short)0, out polys); arr = polys as double[]; }
            catch (Exception ex) { g.Err = ex.Message; return g; }
            int nlen = (arr == null) ? 0 : arr.Length;

            // Line-record signature: arr[i..i+3]==0, arr[i+7]==0, arr[i+8]==2.0, then two points. Each point
            // is (x, y, depth); the 3rd coord is the view DEPTH — CONSTANT per record (0 for some views,
            // non-zero for others), not necessarily 0. Require the two points to share the same depth.
            for (int i = 0; i + 15 <= nlen;)
            {
                if (Z(arr[i]) && Z(arr[i + 1]) && Z(arr[i + 2]) && Z(arr[i + 3]) && Z(arr[i + 7])
                    && arr[i + 8] == 2.0 && Math.Abs(arr[i + 11] - arr[i + 14]) < 1e-6 && InR(arr[i + 11])
                    && InR(arr[i + 9]) && InR(arr[i + 10]) && InR(arr[i + 12]) && InR(arr[i + 13]))
                {
                    g.Lines.Add(new LineRec { X1 = arr[i + 9], Y1 = arr[i + 10], X2 = arr[i + 12], Y2 = arr[i + 13] });
                    i += 15;
                }
                else i++;
            }

            // Curves: tolerant walk for N>=3 point-runs (tessellated arcs/circles). Every run gets a
            // fitted CENTER + RADIUS (circumcenter of three spread tessellation points — exact for any
            // circular arc, independent of tessellation uniformity). A CLOSED run (start == end) is a
            // FULL CIRCLE and goes to Circles as {cx, cy, r} — explicit, so a reader never has
            // to decode the "x1=+r, xm=-r" start/mid/end pattern (the Sonnet benchmark read that pattern
            // as "the tool doesn't report centers" and mis-placed every bore). Open runs stay in Curves
            // as {n, start, mid, end} + fitted center.
            // Points are (x, y, depth); require all points in a run to share one depth (planar) — this is
            // the validity filter (a header's stray values are not consistently planar).
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
                double sx = arr[basep], sy = arr[basep + 1];
                double ex = arr[basep + 3 * (N - 1)], ey = arr[basep + 3 * (N - 1) + 1];
                // Center fit from three spread points (0, N/3, 2N/3) — for a closed run the endpoints
                // coincide, so start/mid/end would be only two distinct points; the spread triple is
                // always three distinct points on the arc.
                int i1 = N / 3, i2 = (2 * N) / 3;
                double[] cc = Circumcenter(
                    sx, sy,
                    arr[basep + 3 * i1], arr[basep + 3 * i1 + 1],
                    arr[basep + 3 * i2], arr[basep + 3 * i2 + 1]);
                bool closed = Math.Abs(sx - ex) < 1e-6 && Math.Abs(sy - ey) < 1e-6;
                if (closed && cc != null)
                {
                    g.Circles.Add(new CircleRec { CX = cc[0], CY = cc[1], R = cc[2] });
                }
                else
                {
                    var cr = new CurveRec
                    {
                        N = N,
                        SX = sx, SY = sy,
                        MX = arr[basep + 3 * mid], MY = arr[basep + 3 * mid + 1],
                        EX = ex, EY = ey
                    };
                    if (cc != null) { cr.HasCenter = true; cr.CX = cc[0]; cr.CY = cc[1]; cr.R = cc[2]; }
                    g.Curves.Add(cr);
                }
                j = basep + 3 * N;
            }
            return g;
        }

        // Emit the extracted geometry in the contract shape (unchanged since Batch-1.5): lines / curves /
        // circles / frame, all R6-rounded.
        private JObject BuildGeometryJson(ViewGeomData g, IView view)
        {
            var gj = new JObject();
            if (g.Err != null) { gj["err"] = g.Err; return gj; }

            var lines = new JArray();
            foreach (var l in g.Lines)
                lines.Add(new JObject { ["x1"] = R6(l.X1), ["y1"] = R6(l.Y1), ["x2"] = R6(l.X2), ["y2"] = R6(l.Y2) });

            var curves = new JArray();
            foreach (var c in g.Curves)
            {
                var cj = new JObject();
                cj["n"] = c.N;
                cj["x1"] = R6(c.SX); cj["y1"] = R6(c.SY);
                cj["xm"] = R6(c.MX); cj["ym"] = R6(c.MY);
                cj["x2"] = R6(c.EX); cj["y2"] = R6(c.EY);
                if (c.HasCenter) { cj["cx"] = R6(c.CX); cj["cy"] = R6(c.CY); cj["r"] = R6(c.R); }
                curves.Add(cj);
            }

            var circles = new JArray();
            foreach (var c in g.Circles)
                circles.Add(new JObject { ["cx"] = R6(c.CX), ["cy"] = R6(c.CY), ["r"] = R6(c.R) });

            gj["lines"] = lines;
            gj["curves"] = curves;
            gj["circles"] = circles;
            // Model-space frame: p_model = origin + x*xdir + y*ydir for every coordinate above (and for
            // the circle centers) — the deterministic view-2D → model-3D mapping. Falls back to the
            // legacy prose note when the view exposes no transform (e.g. the sheet container).
            var vfr = ViewModelFrame(view);
            if (vfr != null)
            {
                gj["frame"] = vfr;
                var ext = ComputeViewExtent(g, view);
                if (ext != null) gj["extent"] = ext;
            }
            else gj["frame"] = "model-scale meters (x,y in the view plane), centered on view centroid";
            return gj;
        }

        // ADR-055: the view's MODEL-SPACE span along its two resolved axes, computed SERVER-SIDE over
        // ALL extracted primitives with the frame's component SIGNS applied — so "is there material
        // beyond z=0.045 in this view?" is a field read, not client-side frame arithmetic (benchmark
        // run #2 dropped ydir's sign and read the z∈[0.045,0.065] loft silhouette as absent). Exact:
        // circles contribute center±r, arcs contribute endpoints + any axis-extreme point that lies
        // within their angular span. Omitted (loudly absent) when the frame is not axis-aligned or the
        // view has no primitives.
        private JObject ComputeViewExtent(ViewGeomData g, IView view)
        {
            double[] o, x, y;
            if (!ViewModelFrameRaw(view, out o, out x, out y)) return null;
            string axX = PrincipalAxis(x[0], x[1], x[2]);
            string axY = PrincipalAxis(y[0], y[1], y[2]);
            if (axX == null || axY == null) return null;
            int kx = AxisIndex(axX), ky = AxisIndex(axY);

            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] mx = { double.MinValue, double.MinValue, double.MinValue };
            bool any = false;
            Action<double, double> add = (u, v) =>
            {
                foreach (int k in new int[] { kx, ky })
                {
                    double val = o[k] + u * x[k] + v * y[k];
                    if (val < mn[k]) mn[k] = val;
                    if (val > mx[k]) mx[k] = val;
                }
                any = true;
            };

            foreach (var l in g.Lines) { add(l.X1, l.Y1); add(l.X2, l.Y2); }
            foreach (var c in g.Circles)
            {
                add(c.CX - c.R, c.CY); add(c.CX + c.R, c.CY);
                add(c.CX, c.CY - c.R); add(c.CX, c.CY + c.R);
            }
            foreach (var a in g.Curves)
            {
                add(a.SX, a.SY); add(a.MX, a.MY); add(a.EX, a.EY);
                if (a.HasCenter)
                {
                    // Axis-extreme points of the arc's circle, included only when ON the arc.
                    double[][] cand = {
                        new double[] { a.CX - a.R, a.CY }, new double[] { a.CX + a.R, a.CY },
                        new double[] { a.CX, a.CY - a.R }, new double[] { a.CX, a.CY + a.R } };
                    foreach (var p in cand)
                        if (AngOnArc(a, p[0], p[1])) add(p[0], p[1]);
                }
            }
            if (!any) return null;

            var ext = new JObject();
            // Canonical X→Y→Z key order.
            foreach (int k in kx < ky ? new int[] { kx, ky } : new int[] { ky, kx })
            {
                string ax = k == 0 ? "X" : (k == 1 ? "Y" : "Z");
                ext[ax] = new JArray { R6(mn[k]), R6(mx[k]) };
            }
            return ext;
        }

        // Circumcenter + radius of the circle through three 2D points; null when degenerate (collinear).
        private static double[] Circumcenter(double ax, double ay, double bx, double by, double cx, double cy)
        {
            double dd = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(dd) < 1e-12) return null;
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / dd;
            double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / dd;
            double r = Math.Sqrt((ux - ax) * (ux - ax) + (uy - ay) * (uy - ay));
            return new double[] { ux, uy, r };
        }

        private static bool Z(double v) { return Math.Abs(v) < 1e-6; }
        private static bool InR(double v) { return Math.Abs(v) < 2.0; }

        // ====================================================================================
        // Batch-4 — deterministic RELATIONS with PROVENANCE + cross-view STATION join keys.
        //
        // Design rules (Observation-Graph review 2026-07-16, approved subset):
        //  - GROUP form only, never pairwise dumps (ADR-030 payload lesson).
        //  - Every relation/group carries `source` (a slug from the CLOSED enum documented in
        //    tool-schemas.json) + `residual` (max measured deviation, meters, 6dp). A relation that
        //    cannot name a deterministic source is structurally impossible to emit — the detector
        //    itself assigns the slug. No confidence scores, no inference fields.
        //  - Cross-view output = join keys + candidate sets. NEVER "same physical entity".
        //  - All math runs on RAW (unrounded) extracted values; rounding happens at emit time only,
        //    so `residual` reports the true deviation.
        //  - touches = junction contacts only (shared_endpoint + endpoint_on_curve). X-crossings of
        //    projected edges are DELIBERATELY not emitted: a 2D projection crossing carries no
        //    same-depth guarantee, so emitting it would invite a false "contact" reading.
        // ====================================================================================

        // Cluster / match tolerance: 10 µm at model scale. Payload resolution is 1 µm (6dp); the
        // circumcenter fit is exact for true circular arcs (sub-µm noise); genuinely distinct design
        // geometry sits ≥ 100 µm apart — two orders of margin on both sides.
        private const double RelTol = 1e-5;

        private class StationViewData
        {
            public string Vid;
            public double[] O, X, Y;
            public ViewGeomData Geom;
        }

        // Minimal union-find for the clustering passes.
        private class UF
        {
            private readonly int[] _p;
            public UF(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
            public int Find(int i) { while (_p[i] != i) { _p[i] = _p[_p[i]]; i = _p[i]; } return i; }
            public void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) _p[ra] = rb; }
        }

        private static double Dist2D(double ax, double ay, double bx, double by)
        {
            double dx = ax - bx, dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Sort primitive ids deterministically: kind rank c < a < l, then numeric index.
        private static void SortPrimitiveIds(List<string> ids)
        {
            ids.Sort((x, y) =>
            {
                int rx = x[0] == 'c' ? 0 : (x[0] == 'a' ? 1 : 2);
                int ry = y[0] == 'c' ? 0 : (y[0] == 'a' ? 1 : 2);
                if (rx != ry) return rx.CompareTo(ry);
                int ix, iy;
                int.TryParse(x.Substring(1), out ix);
                int.TryParse(y.Substring(1), out iy);
                return ix.CompareTo(iy);
            });
        }

        // Is the point (px,py) — assumed to lie ON the arc's circle — within the arc's swept span?
        // The arc runs start → mid → end; mid disambiguates the direction. Angular tolerance is the
        // linear RelTol mapped through the radius.
        private static bool AngOnArc(CurveRec a, double px, double py)
        {
            if (!a.HasCenter || a.R < 1e-9) return false;
            double TwoPi = 2.0 * Math.PI;
            double aS = Math.Atan2(a.SY - a.CY, a.SX - a.CX);
            double aM = Math.Atan2(a.MY - a.CY, a.MX - a.CX);
            double aE = Math.Atan2(a.EY - a.CY, a.EX - a.CX);
            double aP = Math.Atan2(py - a.CY, px - a.CX);
            double ccw = ((aE - aS) % TwoPi + TwoPi) % TwoPi;
            double posM = ((aM - aS) % TwoPi + TwoPi) % TwoPi;
            double posP = ((aP - aS) % TwoPi + TwoPi) % TwoPi;
            double tolA = Math.Min(0.5, RelTol / a.R);
            if (posM <= ccw)   // CCW arc from S to E
                return posP <= ccw + tolA || posP >= TwoPi - tolA;
            // CW arc: swept region is [ccw, 2π] (in CCW-offset terms)
            return posP >= ccw - tolA || posP <= tolA;
        }

        // Perpendicular foot of (px,py) on the INFINITE line p1→p2. Returns false when degenerate.
        private static bool LinePerpFoot(double px, double py, double x1, double y1, double x2, double y2,
            out double t, out double fx, out double fy, out double perpDist)
        {
            t = 0; fx = 0; fy = 0; perpDist = 0;
            double dx = x2 - x1, dy = y2 - y1;
            double l2 = dx * dx + dy * dy;
            if (l2 < 1e-18) return false;
            t = ((px - x1) * dx + (py - y1) * dy) / l2;
            fx = x1 + t * dx; fy = y1 + t * dy;
            perpDist = Dist2D(px, py, fx, fy);
            return true;
        }

        // Distance from (px,py) to the SEGMENT p1→p2 (clamped), with the unclamped param out.
        private static double SegDist(double px, double py, double x1, double y1, double x2, double y2, out double tRaw)
        {
            double t, fx, fy, pd;
            if (!LinePerpFoot(px, py, x1, y1, x2, y2, out t, out fx, out fy, out pd))
            { tRaw = 0; return Dist2D(px, py, x1, y1); }
            tRaw = t;
            double tc = t < 0 ? 0 : (t > 1 ? 1 : t);
            return Dist2D(px, py, x1 + tc * (x2 - x1), y1 + tc * (y2 - y1));
        }

        // ---- Per-view relation groups (all four mandated families) ----
        private JObject ComputeViewRelations(ViewGeomData g)
        {
            // Centered items: circles first, then arcs with a fitted center — ids c<i> / a<i>.
            int nc = g.Circles.Count;
            var items = new List<double[]>();       // {cx, cy, r}
            var itemIds = new List<string>();
            var itemIsArc = new List<bool>();
            var itemArc = new List<CurveRec>();
            for (int i = 0; i < nc; i++)
            {
                items.Add(new double[] { g.Circles[i].CX, g.Circles[i].CY, g.Circles[i].R });
                itemIds.Add("c" + i); itemIsArc.Add(false); itemArc.Add(null);
            }
            for (int i = 0; i < g.Curves.Count; i++)
            {
                var c = g.Curves[i];
                if (!c.HasCenter) continue;
                items.Add(new double[] { c.CX, c.CY, c.R });
                itemIds.Add("a" + i); itemIsArc.Add(true); itemArc.Add(c);
            }
            int n = items.Count;

            // concentric: shared center within RelTol.
            var concentric = new JArray();
            if (n >= 2)
            {
                var uf = new UF(n);
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                        if (Dist2D(items[i][0], items[i][1], items[j][0], items[j][1]) <= RelTol)
                            uf.Union(i, j);
                var groups = new Dictionary<int, List<int>>();
                for (int i = 0; i < n; i++)
                {
                    int r = uf.Find(i);
                    if (!groups.ContainsKey(r)) groups[r] = new List<int>();
                    groups[r].Add(i);
                }
                var ordered = new List<List<int>>();
                foreach (var kv in groups) if (kv.Value.Count >= 2) ordered.Add(kv.Value);
                ordered.Sort((a, b) => a[0].CompareTo(b[0]));
                foreach (var grp in ordered)
                {
                    double mx = 0, my = 0;
                    foreach (int i in grp) { mx += items[i][0]; my += items[i][1]; }
                    mx /= grp.Count; my /= grp.Count;
                    double resid = 0;
                    var radii = new List<double>();
                    var mids = new List<string>();
                    foreach (int i in grp)
                    {
                        resid = Math.Max(resid, Dist2D(items[i][0], items[i][1], mx, my));
                        radii.Add(items[i][2]);
                        mids.Add(itemIds[i]);
                    }
                    radii.Sort();
                    SortPrimitiveIds(mids);
                    var radiiArr = new JArray(); foreach (double r in radii) radiiArr.Add(R6(r));
                    concentric.Add(new JObject
                    {
                        ["members"] = new JArray(mids.ToArray()),
                        ["center"] = new JArray { R6(mx), R6(my) },
                        ["radii"] = radiiArr,
                        ["source"] = "center_coincide",
                        ["residual"] = R6(resid)
                    });
                }
            }

            // equal_diameter: full CIRCLES of the same r at distinct centers.
            var equalDia = new JArray();
            if (nc >= 2)
            {
                var uf = new UF(nc);
                for (int i = 0; i < nc; i++)
                    for (int j = i + 1; j < nc; j++)
                        if (Math.Abs(g.Circles[i].R - g.Circles[j].R) <= RelTol)
                            uf.Union(i, j);
                var groups = new Dictionary<int, List<int>>();
                for (int i = 0; i < nc; i++)
                {
                    int r = uf.Find(i);
                    if (!groups.ContainsKey(r)) groups[r] = new List<int>();
                    groups[r].Add(i);
                }
                var ordered = new List<List<int>>();
                foreach (var kv in groups)
                {
                    if (kv.Value.Count < 2) continue;
                    bool distinct = false;   // require at least one pair of genuinely distinct centers
                    for (int a = 0; a < kv.Value.Count && !distinct; a++)
                        for (int b = a + 1; b < kv.Value.Count && !distinct; b++)
                            if (Dist2D(g.Circles[kv.Value[a]].CX, g.Circles[kv.Value[a]].CY,
                                       g.Circles[kv.Value[b]].CX, g.Circles[kv.Value[b]].CY) > RelTol)
                                distinct = true;
                    if (distinct) ordered.Add(kv.Value);
                }
                ordered.Sort((a, b) => a[0].CompareTo(b[0]));
                foreach (var grp in ordered)
                {
                    double mr = 0;
                    foreach (int i in grp) mr += g.Circles[i].R;
                    mr /= grp.Count;
                    double resid = 0;
                    var mids = new JArray();
                    var centers = new JArray();
                    foreach (int i in grp)
                    {
                        resid = Math.Max(resid, Math.Abs(g.Circles[i].R - mr));
                        mids.Add("c" + i);
                        centers.Add(new JArray { R6(g.Circles[i].CX), R6(g.Circles[i].CY) });
                    }
                    equalDia.Add(new JObject
                    {
                        ["members"] = mids,
                        ["r"] = R6(mr),
                        ["centers"] = centers,
                        ["source"] = "radius_equal",
                        ["residual"] = R6(resid)
                    });
                }
            }

            // tangent: circle/arc ↔ circle/arc (dist = r1+r2 external / |r1−r2| internal) and
            // line ↔ circle/arc (perpendicular distance = r, foot inside the segment span).
            var tangent = new JArray();
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double d = Dist2D(items[i][0], items[i][1], items[j][0], items[j][1]);
                    if (d <= RelTol) continue;   // concentric — its own family
                    double ri = items[i][2], rj = items[j][2];
                    double devExt = Math.Abs(d - (ri + rj));
                    double devInt = Math.Abs(d - Math.Abs(ri - rj));
                    bool ext = devExt <= RelTol && devExt <= devInt;
                    bool intr = !ext && devInt <= RelTol && Math.Abs(ri - rj) > RelTol;
                    if (!ext && !intr) continue;
                    double px = items[i][0] + (items[j][0] - items[i][0]) * (ri / d);
                    double py = items[i][1] + (items[j][1] - items[i][1]) * (ri / d);
                    if (itemIsArc[i] && !AngOnArc(itemArc[i], px, py)) continue;
                    if (itemIsArc[j] && !AngOnArc(itemArc[j], px, py)) continue;
                    var mids = new List<string> { itemIds[i], itemIds[j] };
                    SortPrimitiveIds(mids);
                    tangent.Add(new JObject
                    {
                        ["members"] = new JArray(mids.ToArray()),
                        ["at"] = new JArray { R6(px), R6(py) },
                        ["source"] = ext ? "dist_eq_radius_sum" : "dist_eq_radius_diff",
                        ["residual"] = R6(ext ? devExt : devInt)
                    });
                }
            }
            for (int li = 0; li < g.Lines.Count; li++)
            {
                var l = g.Lines[li];
                double segLen = Dist2D(l.X1, l.Y1, l.X2, l.Y2);
                if (segLen < 1e-12) continue;
                double tolT = RelTol / segLen;
                for (int k = 0; k < n; k++)
                {
                    double t, fx, fy, pd;
                    if (!LinePerpFoot(items[k][0], items[k][1], l.X1, l.Y1, l.X2, l.Y2, out t, out fx, out fy, out pd))
                        continue;
                    if (pd <= RelTol) continue;   // line through the center — not tangency
                    double dev = Math.Abs(pd - items[k][2]);
                    if (dev > RelTol) continue;
                    if (t < -tolT || t > 1 + tolT) continue;
                    double px = items[k][0] + (fx - items[k][0]) * (items[k][2] / pd);
                    double py = items[k][1] + (fy - items[k][1]) * (items[k][2] / pd);
                    if (itemIsArc[k] && !AngOnArc(itemArc[k], px, py)) continue;
                    var mids = new List<string> { itemIds[k], "l" + li };
                    SortPrimitiveIds(mids);
                    tangent.Add(new JObject
                    {
                        ["members"] = new JArray(mids.ToArray()),
                        ["at"] = new JArray { R6(px), R6(py) },
                        ["source"] = "line_dist_eq_radius",
                        ["residual"] = R6(dev)
                    });
                }
            }

            // touches: shared-endpoint junctions + endpoint-on-curve T-contacts (no X-crossings).
            var touches = new JArray();
            {
                var epx = new List<double>(); var epy = new List<double>(); var epOwner = new List<string>();
                for (int i = 0; i < g.Lines.Count; i++)
                {
                    epx.Add(g.Lines[i].X1); epy.Add(g.Lines[i].Y1); epOwner.Add("l" + i);
                    epx.Add(g.Lines[i].X2); epy.Add(g.Lines[i].Y2); epOwner.Add("l" + i);
                }
                for (int i = 0; i < g.Curves.Count; i++)
                {
                    epx.Add(g.Curves[i].SX); epy.Add(g.Curves[i].SY); epOwner.Add("a" + i);
                    epx.Add(g.Curves[i].EX); epy.Add(g.Curves[i].EY); epOwner.Add("a" + i);
                }
                int m = epx.Count;
                if (m >= 2)
                {
                    var uf = new UF(m);
                    for (int i = 0; i < m; i++)
                        for (int j = i + 1; j < m; j++)
                            if (Dist2D(epx[i], epy[i], epx[j], epy[j]) <= RelTol)
                                uf.Union(i, j);
                    var clusters = new Dictionary<int, List<int>>();
                    for (int i = 0; i < m; i++)
                    {
                        int r = uf.Find(i);
                        if (!clusters.ContainsKey(r)) clusters[r] = new List<int>();
                        clusters[r].Add(i);
                    }
                    var ordered = new List<List<int>>();
                    foreach (var kv in clusters) if (kv.Value.Count >= 2) ordered.Add(kv.Value);
                    ordered.Sort((a, b) => a[0].CompareTo(b[0]));
                    foreach (var cl in ordered)
                    {
                        var owners = new List<string>();
                        foreach (int i in cl) if (!owners.Contains(epOwner[i])) owners.Add(epOwner[i]);
                        if (owners.Count < 2) continue;   // both endpoints of one degenerate primitive
                        double mx = 0, my = 0;
                        foreach (int i in cl) { mx += epx[i]; my += epy[i]; }
                        mx /= cl.Count; my /= cl.Count;
                        double resid = 0;
                        foreach (int i in cl) resid = Math.Max(resid, Dist2D(epx[i], epy[i], mx, my));
                        SortPrimitiveIds(owners);
                        touches.Add(new JObject
                        {
                            ["at"] = new JArray { R6(mx), R6(my) },
                            ["members"] = new JArray(owners.ToArray()),
                            ["source"] = "shared_endpoint",
                            ["residual"] = R6(resid)
                        });
                    }
                }

                // endpoint_on_curve: an endpoint lying on ANOTHER primitive's INTERIOR (T-contact).
                var tRecords = new List<double[]>();     // {x, y, residual}
                var tMembers = new List<List<string>>();
                for (int e = 0; e < m; e++)
                {
                    string owner = epOwner[e];
                    double px = epx[e], py = epy[e];
                    for (int li = 0; li < g.Lines.Count; li++)
                    {
                        if (owner == "l" + li) continue;
                        var l = g.Lines[li];
                        double segLen = Dist2D(l.X1, l.Y1, l.X2, l.Y2);
                        if (segLen < 1e-12) continue;
                        double tolT = RelTol / segLen;
                        double tRaw;
                        double dseg = SegDist(px, py, l.X1, l.Y1, l.X2, l.Y2, out tRaw);
                        if (dseg > RelTol) continue;
                        if (tRaw < tolT || tRaw > 1 - tolT) continue;   // endpoint region ⇒ shared_endpoint's turf
                        AddTContact(tRecords, tMembers, px, py, dseg, owner, "l" + li);
                    }
                    for (int ci = 0; ci < nc; ci++)
                    {
                        double dev = Math.Abs(Dist2D(px, py, g.Circles[ci].CX, g.Circles[ci].CY) - g.Circles[ci].R);
                        if (dev > RelTol) continue;
                        AddTContact(tRecords, tMembers, px, py, dev, owner, "c" + ci);
                    }
                    for (int ai = 0; ai < g.Curves.Count; ai++)
                    {
                        if (owner == "a" + ai) continue;
                        var a = g.Curves[ai];
                        if (!a.HasCenter) continue;
                        double dev = Math.Abs(Dist2D(px, py, a.CX, a.CY) - a.R);
                        if (dev > RelTol) continue;
                        if (!AngOnArc(a, px, py)) continue;
                        // near the arc's own endpoints it is a shared_endpoint junction, not a T
                        if (Dist2D(px, py, a.SX, a.SY) <= RelTol || Dist2D(px, py, a.EX, a.EY) <= RelTol) continue;
                        AddTContact(tRecords, tMembers, px, py, dev, owner, "a" + ai);
                    }
                }
                for (int i = 0; i < tRecords.Count; i++)
                {
                    SortPrimitiveIds(tMembers[i]);
                    touches.Add(new JObject
                    {
                        ["at"] = new JArray { R6(tRecords[i][0]), R6(tRecords[i][1]) },
                        ["members"] = new JArray(tMembers[i].ToArray()),
                        ["source"] = "endpoint_on_curve",
                        ["residual"] = R6(tRecords[i][2])
                    });
                }
            }

            var rel = new JObject();
            if (concentric.Count > 0) rel["concentric"] = concentric;
            if (equalDia.Count > 0) rel["equal_diameter"] = equalDia;
            if (tangent.Count > 0) rel["tangent"] = tangent;
            if (touches.Count > 0) rel["touches"] = touches;
            return rel.Count > 0 ? rel : null;   // empty buckets dropped
        }

        // Merge same-position T-contacts into one group record (position within RelTol).
        private static void AddTContact(List<double[]> recs, List<List<string>> members,
            double px, double py, double residual, string idA, string idB)
        {
            for (int i = 0; i < recs.Count; i++)
            {
                if (Dist2D(recs[i][0], recs[i][1], px, py) <= RelTol)
                {
                    if (!members[i].Contains(idA)) members[i].Add(idA);
                    if (!members[i].Contains(idB)) members[i].Add(idB);
                    recs[i][2] = Math.Max(recs[i][2], residual);
                    return;
                }
            }
            recs.Add(new double[] { px, py, residual });
            members.Add(new List<string> { idA, idB });
        }

        // ---- Cross-view STATION join keys ----
        // Each axis-aligned view resolves the two MODEL axes its frame spans; a primitive's key points
        // (circle center and center±r, line endpoints or its constant coordinate, arc center+endpoints)
        // yield model-axis coordinate values ("stations"). Values are clustered within RelTol per axis;
        // only clusters carrying members from ≥2 DIFFERENT views are emitted — those are the join keys.
        // Members are CANDIDATE sets; concluding "same physical entity" is the reader's job, and with
        // >1 candidate the ambiguity stays visible by construction.
        private JObject ComputeStations(List<StationViewData> svs)
        {
            var perAxis = new Dictionary<string, List<object[]>>();   // axis → {value, vid, id}
            var skipped = new JArray();

            foreach (var sv in svs)
            {
                string axX = PrincipalAxis(sv.X[0], sv.X[1], sv.X[2]);
                string axY = PrincipalAxis(sv.Y[0], sv.Y[1], sv.Y[2]);
                if (axX == null || axY == null)
                {
                    skipped.Add(sv.Vid);   // loud: this view cannot join the station table
                    continue;
                }
                int kx = AxisIndex(axX), ky = AxisIndex(axY);

                Action<string, double, double, double, string> addPoint = (id, u, v, r, mode) =>
                {
                    // p = O + u*X + v*Y (full 3D); take the two in-plane axis components.
                    double[] p = new double[3];
                    for (int k = 0; k < 3; k++) p[k] = sv.O[k] + u * sv.X[k] + v * sv.Y[k];
                    foreach (int k in new int[] { kx, ky })
                    {
                        string ax = k == 0 ? "X" : (k == 1 ? "Y" : "Z");
                        AddStation(perAxis, ax, p[k], sv.Vid, id);
                        if (mode == "circle" && r > 0)
                        {
                            AddStation(perAxis, ax, p[k] - r, sv.Vid, id);
                            AddStation(perAxis, ax, p[k] + r, sv.Vid, id);
                        }
                    }
                };

                var gd = sv.Geom;
                for (int i = 0; i < gd.Circles.Count; i++)
                    addPoint("c" + i, gd.Circles[i].CX, gd.Circles[i].CY, gd.Circles[i].R, "circle");
                for (int i = 0; i < gd.Lines.Count; i++)
                {
                    var l = gd.Lines[i];
                    // Constant-coordinate handling happens naturally: both endpoints land in one cluster.
                    addPoint("l" + i, l.X1, l.Y1, 0, "point");
                    addPoint("l" + i, l.X2, l.Y2, 0, "point");
                }
                for (int i = 0; i < gd.Curves.Count; i++)
                {
                    var a = gd.Curves[i];
                    if (a.HasCenter) addPoint("a" + i, a.CX, a.CY, 0, "point");
                    addPoint("a" + i, a.SX, a.SY, 0, "point");
                    addPoint("a" + i, a.EX, a.EY, 0, "point");
                }
            }

            var axes = new JArray();
            foreach (string ax in new string[] { "X", "Y", "Z" })
            {
                if (!perAxis.ContainsKey(ax)) continue;
                var entries = perAxis[ax];
                entries.Sort((a, b) => ((double)a[0]).CompareTo((double)b[0]));
                var values = new JArray();
                int i0 = 0;
                while (i0 < entries.Count)
                {
                    int i1 = i0;
                    while (i1 + 1 < entries.Count && (double)entries[i1 + 1][0] - (double)entries[i1][0] <= RelTol)
                        i1++;
                    // cluster = entries[i0..i1]
                    var vids = new HashSet<string>();
                    for (int i = i0; i <= i1; i++) vids.Add((string)entries[i][1]);
                    if (vids.Count >= 2)
                    {
                        double mean = 0;
                        for (int i = i0; i <= i1; i++) mean += (double)entries[i][0];
                        mean /= (i1 - i0 + 1);
                        double resid = 0;
                        for (int i = i0; i <= i1; i++) resid = Math.Max(resid, Math.Abs((double)entries[i][0] - mean));
                        var membersByVid = new SortedDictionary<string, List<string>>();
                        for (int i = i0; i <= i1; i++)
                        {
                            string vid = (string)entries[i][1], id = (string)entries[i][2];
                            if (!membersByVid.ContainsKey(vid)) membersByVid[vid] = new List<string>();
                            if (!membersByVid[vid].Contains(id)) membersByVid[vid].Add(id);
                        }
                        var membersJ = new JObject();
                        foreach (var kv in membersByVid)
                        {
                            SortPrimitiveIds(kv.Value);
                            membersJ[kv.Key] = new JArray(kv.Value.ToArray());
                        }
                        values.Add(new JObject
                        {
                            ["v"] = R6(mean),
                            ["members"] = membersJ,
                            ["residual"] = R6(resid)
                        });
                    }
                    i0 = i1 + 1;
                }
                if (values.Count > 0)
                    axes.Add(new JObject { ["axis"] = ax, ["values"] = values });
            }

            if (axes.Count == 0 && skipped.Count == 0) return null;
            var stations = new JObject { ["tol"] = RelTol, ["axes"] = axes };
            if (skipped.Count > 0) stations["skipped_views"] = skipped;
            return stations;
        }

        private static int AxisIndex(string ax) { return ax == "X" ? 0 : (ax == "Y" ? 1 : 2); }

        private static void AddStation(Dictionary<string, List<object[]>> perAxis, string ax, double v, string vid, string id)
        {
            if (!perAxis.ContainsKey(ax)) perAxis[ax] = new List<object[]>();
            perAxis[ax].Add(new object[] { v, vid, id });
        }

        // ---- Dimension → primitive attachment (Batch-4 deliverable 3) ----
        // Each MODEL-space anchor is mapped into the view's 2D frame (u=(p−o)·xdir, v=(p−o)·ydir) and
        // matched against the extracted primitives arithmetically. `measures` lists ALL primitives any
        // anchor resolves to (a corner anchor legitimately touches two lines — candidates stay visible);
        // `measure_src` = anchor_at_center when any anchor sits on a circle/arc center (the Ø/R shape),
        // else anchor_on_primitive; `measure_residual` = the worst contributing deviation. No resolvable
        // primitive at all ⇒ `unattached: true` — loud, never dropped, never guessed.
        private void AttachDimensionToPrimitives(JObject dj, JArray anchors, ViewGeomData g,
            double[] O, double[] X, double[] Y)
        {
            if (anchors == null || anchors.Count == 0) { dj["unattached"] = true; return; }
            var matched = new List<string>();
            bool anyCenter = false;
            double maxResid = 0;
            int unmatchedAnchors = 0;

            foreach (var aTok in anchors)
            {
                var pa = aTok as JArray;
                if (pa == null || pa.Count < 3) continue;
                double px = (double)pa[0], py = (double)pa[1], pz = (double)pa[2];
                double dx = px - O[0], dy = py - O[1], dz = pz - O[2];
                double u = dx * X[0] + dy * X[1] + dz * X[2];
                double v = dx * Y[0] + dy * Y[1] + dz * Y[2];
                bool any = false;

                for (int i = 0; i < g.Circles.Count; i++)
                {
                    double dc = Dist2D(u, v, g.Circles[i].CX, g.Circles[i].CY);
                    if (dc <= RelTol)
                    { AddMatch(matched, "c" + i, dc, ref maxResid); anyCenter = true; any = true; }
                    else if (Math.Abs(dc - g.Circles[i].R) <= RelTol)
                    { AddMatch(matched, "c" + i, Math.Abs(dc - g.Circles[i].R), ref maxResid); any = true; }
                }
                for (int i = 0; i < g.Curves.Count; i++)
                {
                    var a = g.Curves[i];
                    if (a.HasCenter)
                    {
                        double dc = Dist2D(u, v, a.CX, a.CY);
                        if (dc <= RelTol)
                        { AddMatch(matched, "a" + i, dc, ref maxResid); anyCenter = true; any = true; continue; }
                        double dev = Math.Abs(dc - a.R);
                        if (dev <= RelTol && AngOnArc(a, u, v))
                        { AddMatch(matched, "a" + i, dev, ref maxResid); any = true; continue; }
                    }
                    if (Dist2D(u, v, a.SX, a.SY) <= RelTol || Dist2D(u, v, a.EX, a.EY) <= RelTol)
                    { AddMatch(matched, "a" + i, 0, ref maxResid); any = true; }
                }
                for (int i = 0; i < g.Lines.Count; i++)
                {
                    var l = g.Lines[i];
                    double tRaw;
                    double d = SegDist(u, v, l.X1, l.Y1, l.X2, l.Y2, out tRaw);
                    if (d <= RelTol) { AddMatch(matched, "l" + i, d, ref maxResid); any = true; }
                }
                if (!any) unmatchedAnchors++;
            }

            if (matched.Count > 0)
            {
                SortPrimitiveIds(matched);
                dj["measures"] = new JArray(matched.ToArray());
                dj["measure_src"] = anyCenter ? "anchor_at_center" : "anchor_on_primitive";
                dj["measure_residual"] = R6(maxResid);
                if (unmatchedAnchors > 0) dj["unmatched_anchors"] = unmatchedAnchors;
            }
            else dj["unattached"] = true;
        }

        private static void AddMatch(List<string> matched, string id, double residual, ref double maxResid)
        {
            if (!matched.Contains(id)) matched.Add(id);
            maxResid = Math.Max(maxResid, residual);
        }

        // ---- Center marks (Batch-4 deliverable 5) ----
        // ICenterMark walk (GetFirstCenterMark/GetNext — reflection-verified). Position source PINNED by
        // the 2026-07-17 live A/B on level-2-2 (ADR-035 empiricism): `ICenterMark.GetPosition(index)`
        // returns the mark center directly in the VIEW-2D geometry space (its x,y matched the circle
        // centers exactly — "matched via view-space" for every mark), while `IAnnotation.GetPosition()`
        // is the sheet-space LABEL anchor (y ≈ 0.25 on the A3 sheet — never a mark center) and is
        // therefore NOT read. A mark whose position matches no circle center within RelTol is emitted
        // `unmatched: true` — loud, never guessed.
        private JArray ReadCenterMarks(IView view, double[] O, double[] X, double[] Y, ViewGeomData g)
        {
            var arrOut = new JArray();
            object cmObj = null;
            try { cmObj = view.GetFirstCenterMark(); } catch { return arrOut; }
            int guard = 0;
            while (cmObj != null && guard++ < 500)
            {
                var cm = cmObj as ICenterMark;
                if (cm == null) break;

                var rawPts = new List<double[]>();
                try
                {
                    bool grouped = false; int gc = 1;
                    try { grouped = cm.IsGrouped; } catch { }
                    try { if (grouped) gc = cm.GroupCount; } catch { gc = 1; }
                    for (int i = 0; i < Math.Max(1, gc) && i < 100; i++)
                    {
                        var p = ToDoubleArray(cm.GetPosition(i));
                        if (p != null && p.Length >= 2) rawPts.Add(new double[] { p[0], p[1] });
                    }
                }
                catch { }

                foreach (var raw in rawPts)
                {
                    double u = raw[0], v = raw[1];
                    var on = new List<string>();
                    double res = 0;
                    for (int i = 0; i < g.Circles.Count; i++)
                    {
                        double dc = Dist2D(u, v, g.Circles[i].CX, g.Circles[i].CY);
                        if (dc <= RelTol) { on.Add("c" + i); res = Math.Max(res, dc); }
                    }
                    if (on.Count > 0)
                    {
                        SortPrimitiveIds(on);
                        arrOut.Add(new JObject
                        {
                            ["at"] = new JArray { R6(u), R6(v) },
                            ["on"] = new JArray(on.ToArray()),
                            ["source"] = "center_coincide",
                            ["residual"] = R6(res)
                        });
                    }
                    else
                    {
                        ExecLog.Write($"center_mark UNMATCHED at=({R6(u)},{R6(v)}) view='{view.Name}'");
                        arrOut.Add(new JObject
                        {
                            ["at"] = new JArray { R6(u), R6(v) },
                            ["unmatched"] = true
                        });
                    }
                }

                object next = null;
                try { next = cm.GetNext(); } catch { next = null; }
                cmObj = next;
            }
            return arrOut;
        }

        // ---- Centerlines (Batch-4 deliverable 5, best-effort per the API's shape) ----
        // ICenterLine itself exposes NO endpoints (only GetAnnotation — a single point), so the
        // segment geometry is read from IView.GetCenterLineSketch()'s sketch segments, presumed to be
        // in the view's sketch space (= the geometry space; live A/B decides — raw values ExecLog'd).
        // When the view reports centerlines but the sketch route yields nothing readable, the caller
        // emits `centerlines_unreadable` — the gap stays loud instead of heuristically inferred.
        private JArray ReadCenterlines(IView view, ViewGeomData g)
        {
            var arrOut = new JArray();
            int reported = 0;
            try { reported = view.GetCenterLineCount(); } catch { }
            try
            {
                var sk = view.GetCenterLineSketch() as ISketch;
                if (sk != null)
                {
                    var segs = sk.GetSketchSegments() as object[];
                    if (segs != null)
                    {
                        foreach (var so in segs)
                        {
                            var seg = so as ISketchSegment;
                            if (seg == null) continue;
                            int st = -1;
                            try { st = seg.GetType(); } catch { }
                            if (st != 0) continue;   // swSketchLINE = 0 (inlined, ADR-018)
                            var ln = seg as ISketchLine;
                            if (ln == null) continue;
                            ISketchPoint sp = null, ep = null;
                            try { sp = ln.IGetStartPoint2(); ep = ln.IGetEndPoint2(); } catch { }
                            if (sp == null || ep == null) continue;
                            double x1 = sp.X, y1 = sp.Y, x2 = ep.X, y2 = ep.Y;
                            ExecLog.Write($"centerline raw=({x1:R},{y1:R})-({x2:R},{y2:R}) view='{view.Name}'");
                            var cj = new JObject
                            {
                                ["x1"] = R6(x1), ["y1"] = R6(y1),
                                ["x2"] = R6(x2), ["y2"] = R6(y2)
                            };
                            // Circles whose center lies ON this centerline (arithmetic, RelTol).
                            var through = new List<string>();
                            double worst = 0;
                            for (int i = 0; i < g.Circles.Count; i++)
                            {
                                double tRaw;
                                double d = SegDist(g.Circles[i].CX, g.Circles[i].CY, x1, y1, x2, y2, out tRaw);
                                if (d <= RelTol) { through.Add("c" + i); worst = Math.Max(worst, d); }
                            }
                            if (through.Count > 0)
                            {
                                SortPrimitiveIds(through);
                                cj["through"] = new JArray(through.ToArray());
                                cj["source"] = "center_coincide";
                                cj["residual"] = R6(worst);
                            }
                            arrOut.Add(cj);
                        }
                    }
                }
            }
            catch (Exception ex) { ExecLog.Write($"ReadCenterlines err: {ex.Message}"); }

            if (arrOut.Count == 0 && reported > 0)
            {
                // Loud gap: the view SAYS it has centerlines but the sketch route exposed none.
                var gap = new JObject { ["centerlines_reported"] = reported, ["unreadable"] = true };
                arrOut.Add(gap);
            }
            return arrOut;
        }

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
