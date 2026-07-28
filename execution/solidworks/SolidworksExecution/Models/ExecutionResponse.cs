using Newtonsoft.Json;

namespace SolidworksExecution.Models
{
    public class ExecutionResponse
    {
        public string OperationId { get; set; }
        public string Status { get; set; }
        public bool Verified { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? StateVersion { get; set; }

        [JsonProperty("last_known_state_version", NullValueHandling = NullValueHandling.Ignore)]
        public int? LastKnownStateVersion { get; set; }

        public CadState CadState { get; set; }

        // Read-only echo of the REAL geometry a create tool just produced (e.g. an arc's actual
        // radius/center/endpoints after AddToDB), so the caller can self-verify in-band without a
        // separate analyze round-trip. Purely informational: does NOT participate in state_version
        // or idempotency. Null for tools that don't populate it.
        [JsonProperty("result_geometry", NullValueHandling = NullValueHandling.Ignore)]
        public object ResultGeometry { get; set; }

        public ExecutionError Error { get; set; }
    }

    public class ExecutionError
    {
        public string Code { get; set; }
        public string Message { get; set; }
    }
}
