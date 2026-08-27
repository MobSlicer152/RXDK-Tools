using Microsoft.Build.CPPTasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Threading.Tasks;

namespace Rxdk.MsBuild.Tasks
{
    public abstract class RxdkToolTask : TrackedVCToolTask
    {
        protected RxdkToolTask()
            : base(new ResourceManager("Microsoft.Build.CPPTasks.Strings", Assembly.GetAssembly(typeof(TrackedVCToolTask))))
        {
        }

        protected override string GenerateFullPathToTool()
        {
            return ToolName;
        }
    }
}
