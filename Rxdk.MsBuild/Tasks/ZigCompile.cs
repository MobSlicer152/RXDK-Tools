using Microsoft.Build.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rxdk.MsBuild.Tasks
{
    public class ZigCompile : RxdkToolTask
    {
        protected override string TrackerIntermediateDirectory => throw new NotImplementedException();
        protected override ITaskItem[] TrackedInputFiles => throw new NotImplementedException();
        protected override ArrayList SwitchOrderList => switchOrderList;
        protected ArrayList switchOrderList;

        public ZigCompile()
        {
            switchOrderList = new ArrayList()
            {
                "AlwaysAppend"
            };
        }

        protected override string ToolName => "zig.exe";
        protected override string AlwaysAppend => "cc";
    }
}
