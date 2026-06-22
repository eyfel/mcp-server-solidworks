namespace SolidworksExecution.Models
{
    public class ToolRequest
    {
        public string OperationId { get; set; }
        public string Tool { get; set; }
        public int StateVersion { get; set; }
        public object Params { get; set; }
    }
}
