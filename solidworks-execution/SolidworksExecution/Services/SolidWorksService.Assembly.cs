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
    // SolidWorksService partial: assembly tools (open/insert/mate/save-body) and the assembly readers (components, mates, entities).
    public partial class SolidWorksService
    {

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
    }
}
