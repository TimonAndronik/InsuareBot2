using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InsuranceBot
{
    internal class Document
    {
        public long UserId { get; set; }
        public string DocumentType { get; set; }
        public byte[] Image { get; set; }
    }
}
