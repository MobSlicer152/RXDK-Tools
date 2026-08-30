using System;
using System.Text.RegularExpressions;

namespace Rxdk.MsBuild.Tasks
{
    public class ZigLd : ZigToolTask
    {
        public ZigLd()
        {

        }

        protected override string SubTool => "ld";

        protected static Regex ldMessageRegex = new Regex("^\\s*(?<FILENAME>[^:]*):(((?<LINE>\\d*):)?)(\\s*(?<CATEGORY>(fatal error|error|warning|note)):)?\\s*(?<TEXT>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100.0));
    }
}
