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
    // SolidWorksService partial: part-modeling features (extrude/revolve/sweep/loft, rib, edge fillet/chamfer, reference geometry, patterns, material, dimension/feature edits).
    public partial class SolidWorksService
    {

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
    }
}
