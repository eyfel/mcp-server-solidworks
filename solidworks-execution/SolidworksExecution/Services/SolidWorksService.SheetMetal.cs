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
    // SolidWorksService partial: sheet-metal features (base/edge flange, sketched bend, flat pattern) and their edge selection.
    public partial class SolidWorksService
    {

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
                    double flangeLength = p?.Value<double?>("flange_length") ?? 0.02;
                    double angleDeg = p?.Value<double?>("angle") ?? 90.0;
                    double angleRad = angleDeg * Math.PI / 180.0;

                    // Edge selection shares SelectFlangeEdge with the sketch/finish phases
                    // (v0.7.0): edge_index (from analyze_model(edges)) PREFERRED — the raw
                    // coordinate pick can miss a real edge (KNOWN-LIMITATIONS #6); ex/ey/ez
                    // stay the fallback. The IR's length-mode flange lowers to edge_index.
                    var flangeEdge = SelectFlangeEdge(modelDoc, p,
                        p?.Value<double?>("ex"), p?.Value<double?>("ey"), p?.Value<double?>("ez"),
                        out string efSimpleEdgeErr);
                    if (flangeEdge == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "EDGE_NOT_FOUND", efSimpleEdgeErr);

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
                        // Re-select the edge (sketch creation/exit can clear the selection list) —
                        // via the entity OBJECT so it works for edge_index and coordinate callers alike.
                        modelDoc.ClearSelection2(true);
                        (flangeEdge as IEntity)?.Select4(false, null);

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
    }
}
