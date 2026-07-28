using System.Collections.Generic;

namespace SolidworksExecution.Models
{
    public class CadState
    {
        public int StateVersion { get; set; }
        public string ActiveDocument { get; set; }
        public string DocumentType { get; set; }
        public string ActiveSketch { get; set; }
        public List<string> Features { get; set; }
        public List<string> Dimensions { get; set; }
    }
}
