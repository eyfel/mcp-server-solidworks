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
    // SolidWorksService partial: read-only part analysis (analyze_model modes, selection readback, feature/sketch readers, topology snapshot/diff for feature_map).
    public partial class SolidWorksService
    {

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
                    // Batch-1 Task-3: OPTIONAL near/k/axis narrow this to the k nearest edges to a point
                    // (with an optional direction filter) — a filtered READ, not the full dump. A
                    // parameterless call is byte-for-byte the historical full dump (backward compatible).
                    var partDoc = modelDoc as IPartDoc;
                    if (partDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE",
                            "analysis_type='edges' requires a part document.");
                    double[] near = ParseVec3(p?.Value<string>("near"));
                    double[] axis = ParseVec3(p?.Value<string>("axis"));
                    int k = p?.Value<int?>("k") ?? 0;
                    JObject edgeAnalysis = ReadEdges(partDoc, near, k, axis);
                    results.Add(edgeAnalysis.ToString(Newtonsoft.Json.Formatting.None));
                }
                else if (analysisType == "faces")
                {
                    // Planar-face list with centroid/normal/area + a stable index — the twin of 'edges'
                    // (ADR-027). Lets a caller pick a face to sketch on (create_sketch on_face + face_index)
                    // WITHOUT a coordinate pick, which is fragile on a revolve end-cap (the face centroid can
                    // sit on the revolve axis/origin → ambiguous SelectByID2). Baby reference-resolver → P1.3.
                    // Batch-1 Task-3: OPTIONAL near/k/axis narrow this to the k nearest faces to a point (with
                    // an optional normal filter). Parameterless call = the historical full dump (compatible).
                    var partDoc = modelDoc as IPartDoc;
                    if (partDoc == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE",
                            "analysis_type='faces' requires a part document.");
                    double[] near = ParseVec3(p?.Value<string>("near"));
                    double[] axis = ParseVec3(p?.Value<string>("axis"));
                    int k = p?.Value<int?>("k") ?? 0;
                    JObject faceAnalysis = ReadFaces(partDoc, near, k, axis);
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
                    // localized plane name, so it's correct on any SW localization (create_sketch maps
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

        private JObject ReadEdges(IPartDoc partDoc, double[] near = null, int k = 0, double[] axis = null)
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
                        // BuildEdgeJson assigns the STABLE full-enumeration index `i` — kept even after a
                        // near/k filter, so a filtered result still selects by the same index.
                        edgesArr.Add(BuildEdgeJson(edge, idx++));
                    }
                }
            }
            swTotal.Stop();
            ExecLog.Write($"ReadEdges: edges={edgesArr.Count} total={swTotal.ElapsedMilliseconds}ms near={(near != null)} k={k}");
            int totalCount = edgesArr.Count;

            if (near != null)
            {
                // The edge's representative point is its `mid`; the direction filter (axis) compares the
                // edge's CHORD direction (start→end) — for a curved edge that's the chord, documented.
                var filtered = NarrowByNear(edgesArr, near, k, axis, "mid",
                    (ej) => EdgeChordDir(ej));
                root["edge_count"] = filtered.Count;
                root["total_edge_count"] = totalCount;
                root["near"] = new JArray { R6(near[0]), R6(near[1]), R6(near[2]) };
                if (k > 0) root["k"] = k;
                if (axis != null) root["axis"] = new JArray { R6(axis[0]), R6(axis[1]), R6(axis[2]) };
                root["edges"] = filtered;
                return root;
            }
            root["edge_count"] = totalCount;
            root["edges"] = edgesArr;
            return root;
        }

        // Face list with a stable index — the twin of ReadEdges (ADR-027). The `i` matches the same
        // GetBodies2(swSolidBody,true) → GetFaces() enumeration create_sketch(on_face, face_index) walks,
        // so a caller picks a face to sketch on by index instead of a fragile coordinate pick. Per planar
        // face we report normal + area + a representative on-plane point; non-planar faces still get an
        // index (so the numbering stays aligned) but no normal.
        private JObject ReadFaces(IPartDoc partDoc, double[] near = null, int k = 0, double[] axis = null)
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
            ExecLog.Write($"ReadFaces: faces={facesArr.Count} near={(near != null)} k={k}");
            int totalCount = facesArr.Count;

            if (near != null)
            {
                // The face's representative point is `point`; the axis filter compares the (planar-face)
                // NORMAL — a non-planar face has no single normal, so it's dropped when an axis is given.
                var filtered = NarrowByNear(facesArr, near, k, axis, "point",
                    (fj) => (fj["normal"] as JArray) != null
                        ? new[] { fj["normal"][0].Value<double>(), fj["normal"][1].Value<double>(), fj["normal"][2].Value<double>() }
                        : null);
                root["face_count"] = filtered.Count;
                root["total_face_count"] = totalCount;
                root["near"] = new JArray { R6(near[0]), R6(near[1]), R6(near[2]) };
                if (k > 0) root["k"] = k;
                if (axis != null) root["axis"] = new JArray { R6(axis[0]), R6(axis[1]), R6(axis[2]) };
                root["faces"] = filtered;
                return root;
            }
            root["face_count"] = totalCount;
            root["faces"] = facesArr;
            return root;
        }

        // ------------------------------------------------------------------
        // Batch-1 Task-3 — targeted near/k narrowing (shared by edges + faces).
        // ------------------------------------------------------------------

        // Parse a "[x,y,z]" JSON-string param (the ADR-022 array-param idiom) into a double[3]; null/blank →
        // null (= "not requested"), which the readers treat as the full-dump path.
        private static double[] ParseVec3(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try
            {
                var tok = JToken.Parse(s) as JArray;
                if (tok == null || tok.Count < 3) return null;
                return new[] { tok[0].Value<double>(), tok[1].Value<double>(), tok[2].Value<double>() };
            }
            catch { return null; }
        }

        // Chord direction (unit) of an edge JSON built by BuildEdgeJson (start→end); null if degenerate.
        private static double[] EdgeChordDir(JObject ej)
        {
            var s = ej["start"] as JArray; var e = ej["end"] as JArray;
            if (s == null || e == null) return null;
            double dx = e[0].Value<double>() - s[0].Value<double>();
            double dy = e[1].Value<double>() - s[1].Value<double>();
            double dz = e[2].Value<double>() - s[2].Value<double>();
            double L = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (L < 1e-9) return null;
            return new[] { dx / L, dy / L, dz / L };
        }

        // Keep the k entries of `items` nearest (Euclidean) to `near` — measured from each item's
        // representative point (`ptKey` = "mid" for edges, "point" for faces). Optional `axis`: keep only
        // items whose direction (dirOf) is ~parallel to axis (|dot| ≥ 0.999), so e.g. "the nearest face on
        // a +Z-normal plane" is one call. k ≤ 0 ⇒ no count cap (all that pass the axis filter, sorted by
        // distance). Items with no representative point are dropped (can't be ranked). The returned JObjects
        // are the SAME objects (stable `i` preserved), each annotated with `dist` (meters, R6).
        private static JArray NarrowByNear(JArray items, double[] near, int k, double[] axis,
            string ptKey, Func<JObject, double[]> dirOf)
        {
            double[] axisN = null;
            if (axis != null)
            {
                double al = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
                if (al > 1e-9) axisN = new[] { axis[0] / al, axis[1] / al, axis[2] / al };
            }
            var scored = new List<KeyValuePair<double, JObject>>();
            foreach (var it in items)
            {
                var jo = it as JObject;
                if (jo == null) continue;
                var pt = jo[ptKey] as JArray;
                if (pt == null || pt.Count < 3) continue;
                if (axisN != null)
                {
                    var dir = dirOf(jo);
                    if (dir == null) continue;
                    double dot = Math.Abs(dir[0] * axisN[0] + dir[1] * axisN[1] + dir[2] * axisN[2]);
                    if (dot < 0.999) continue;
                }
                double dx = pt[0].Value<double>() - near[0];
                double dy = pt[1].Value<double>() - near[1];
                double dz = pt[2].Value<double>() - near[2];
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                jo["dist"] = R6(dist);
                scored.Add(new KeyValuePair<double, JObject>(dist, jo));
            }
            scored.Sort((a, b) => a.Key.CompareTo(b.Key));
            var outArr = new JArray();
            int limit = k > 0 ? Math.Min(k, scored.Count) : scored.Count;
            for (int i = 0; i < limit; i++) outArr.Add(scored[i].Value);
            return outArr;
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
    }
}
