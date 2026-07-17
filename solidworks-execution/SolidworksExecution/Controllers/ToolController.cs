using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;
using SolidworksExecution.Services;

namespace SolidworksExecution.Controllers
{
    [RoutePrefix("api/tool")]
    public class ToolController : ApiController
    {
        private readonly SolidWorksService _service;
        private readonly IOperationGuard _guard;

        public ToolController() : this(OperationGuard.Instance) { }

        public ToolController(IOperationGuard guard)
        {
            _guard = guard;
            _service = new SolidWorksService(guard);
        }

        // Read-only resync endpoint: returns the current authoritative state_version
        // WITHOUT a state_version check or increment. The adapter calls this to recover
        // from a desync (e.g. the execution server was restarted after a rebuild).
        [HttpGet]
        [Route("state")]
        public HttpResponseMessage GetState()
        {
            return Request.CreateResponse(HttpStatusCode.OK, new
            {
                stateVersion = _guard.GetCurrentStateVersion()
            });
        }

        // Health endpoint (P0.6). `~/` escapes the api/tool prefix → GET /health. Reports the server is
        // up, the current state_version, and whether SolidWorks COM is currently attachable (probed on the
        // STA thread — COM_ATTACH is lazy/per-request, so this is a fresh attach attempt) plus the active
        // document. Never 500s: a COM probe failure is reported as comAttached=false, not an error status.
        [HttpGet]
        [Route("~/health")]
        public HttpResponseMessage Health()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                var com = StaExecutor.Instance.Run(() => _service.GetHealthInfo());
                foreach (var kv in com) info[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                info["comAttached"] = false;
                info["comError"] = ex.Message;
            }
            object attached;
            info.TryGetValue("comAttached", out attached);
            ExecLog.Write($"<- health UP comAttached={attached}");
            return Request.CreateResponse(HttpStatusCode.OK, info);
        }

        // Lifecycle bootstrap (ensure_ready). `~/` escapes the api/tool prefix → POST /ensure_ready.
        // POST because it is side-effecting: it launches SolidWorks via COM if it isn't running.
        // Distinct from /health (a read-only probe) — this one makes the environment ready. Does NOT
        // open/create a document. Runs on the STA thread (touches COM) and never 500s (a launch
        // failure is reported as comAttached=false). Not part of the state_version/idempotency
        // envelope — it's a lifecycle call, not a CAD operation.
        [HttpPost]
        [Route("~/ensure_ready")]
        public HttpResponseMessage EnsureReady()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                var com = StaExecutor.Instance.Run(() => _service.EnsureReady());
                foreach (var kv in com) info[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                info["comAttached"] = false;
                info["ensureError"] = ex.Message;
            }
            object attached, launched;
            info.TryGetValue("comAttached", out attached);
            info.TryGetValue("swLaunched", out launched);
            ExecLog.Write($"<- ensure_ready UP comAttached={attached} swLaunched={launched}");
            return Request.CreateResponse(HttpStatusCode.OK, info);
        }

        [HttpPost]
        [Route("execute")]
        public HttpResponseMessage Execute([FromBody] ToolRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.OperationId) || string.IsNullOrEmpty(request.Tool))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "operation_id and tool are required." });

            ExecLog.Write($"-> {request.Tool} op={request.OperationId} sv={request.StateVersion}");

            ExecutionResponse response;

            try
            {
                // All COM-touching work runs on the single dedicated STA thread (P0.1) — OWIN request
                // threads are MTA, which made sheet-metal calls return null and add_edge_feature deadlock.
                response = StaExecutor.Instance.Run(() => Dispatch(request));
            }
            catch (System.Exception ex)
            {
                ExecLog.Write($"<- {request.Tool} UNHANDLED {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }

            if (response == null)
            {
                ExecLog.Write($"<- {request.Tool} UNKNOWN_TOOL");
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = $"Unknown tool: {request.Tool}" });
            }

            if (response.Status == "FAILED")
                ExecLog.Write($"<- {request.Tool} FAILED {response.Error?.Code}: {response.Error?.Message}");
            else
                ExecLog.Write($"<- {request.Tool} {response.Status} sv={response.StateVersion}");

            return Request.CreateResponse(HttpStatusCode.OK, response);
        }

        // Routes a tool request to its service method. Runs on the STA thread (see Execute).
        // Returns null for an unknown tool so the caller can emit the BadRequest response.
        private ExecutionResponse Dispatch(ToolRequest request)
        {
            switch (request.Tool)
            {
                case "open_new_part":          return _service.OpenNewPart(request);
                case "open_document":          return _service.OpenDocument(request);
                case "create_sketch":          return _service.CreateSketch(request);
                case "add_sketch_entity":      return _service.AddSketchEntity(request);
                case "add_sketch_entities":    return _service.AddSketchEntities(request);
                case "add_dimension":          return _service.AddDimension(request);
                case "add_sketch_constraint":  return _service.AddSketchConstraint(request);
                case "add_edge_feature":       return _service.AddEdgeFeature(request);
                case "create_rib":             return _service.CreateRib(request);
                case "create_drawing":         return _service.CreateDrawing(request);
                case "add_drawing_view":       return _service.AddDrawingView(request);
                case "add_flat_pattern_view":  return _service.AddFlatPatternView(request);
                case "add_drawing_dimension":  return _service.AddDrawingDimension(request);
                case "auto_dimension_drawing": return _service.AutoDimensionDrawing(request);
                case "auto_center_marks":      return _service.AutoCenterMarks(request);
                case "add_hole_callout":       return _service.AddHoleCallout(request);
                case "add_section_view":       return _service.AddSectionView(request);
                case "save_document":          return _service.SaveDocument(request);
                case "export_document":        return _service.ExportDocument(request);
                case "batch_export":           return _service.BatchExport(request);
                case "extrude_feature":        return _service.ExtrudeFeature(request);
                case "verify_state":           return _service.VerifyState(request);
                case "close_document":         return _service.CloseDocument(request);
                case "analyze_model":          return _service.AnalyzeModel(request);
                case "analyze_drawing":        return _service.AnalyzeDrawing(request);
                case "get_selection":          return _service.GetSelection(request);
                case "edit_sketch":            return _service.EditSketch(request);
                case "add_reference_geometry": return _service.AddReferenceGeometry(request);
                case "create_pattern":         return _service.CreatePattern(request);
                case "set_part_material":      return _service.SetPartMaterial(request);
                case "sheet_metal_feature":    return _service.SheetMetalFeature(request);
                case "modify_dimension":       return _service.ModifyDimension(request);
                case "edit_feature":           return _service.EditFeature(request);
                case "activate_document":      return _service.ActivateDocument(request);
                case "analyze_assembly":       return _service.AnalyzeAssembly(request);
                case "open_new_assembly":      return _service.OpenNewAssembly(request);
                case "insert_component":       return _service.InsertComponent(request);
                case "add_mate":               return _service.AddMateTool(request);
                case "save_body_as_part":      return _service.SaveBodyAsPart(request);
                default:                       return null;
            }
        }
    }
}
