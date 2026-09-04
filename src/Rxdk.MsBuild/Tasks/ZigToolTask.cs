using Microsoft.Build.CPPTasks;
using Microsoft.Build.Framework;
using Rxdk.Engine.Bootstrap;
using Rxdk.Engine.Platform;
using System;
using System.Collections;
using System.IO;
using System.Text;

namespace Rxdk.MsBuild.Tasks
{
    public abstract class ZigToolTask : RxdkToolTask
    {
        public ZigToolTask()
        {
            switchOrderList = new ArrayList()
            {
                "SubTool",
                "Target",
                "Machine"
            };
        }

        protected override string TrackerIntermediateDirectory => TrackerLogDirectory ?? "";

        public virtual string TrackerLogDirectory
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new(ToolSwitchType.Directory)
                    {
                        DisplayName = "Tracker Log Directory",
                        Description = "Tracker Log Directory.",
                    },
                    value
                );
            }
        }

        protected override string ToolName =>
            ZigRuntime.ResolveZigExecutableAsync().GetAwaiter().GetResult() ??
                throw new FileNotFoundException("Zig not found.");
        protected abstract string SubTool { get; }
        protected string Target => "-target x86-windows-gnu";
        protected string Machine => "-march=pentium3";

        [Required]
        protected virtual ITaskItem[] Sources
        {
            get => PropertyOrNull<ITaskItem[]>();
            set
            {
                UpdateSwitch(
                    new(ToolSwitchType.ITaskItemArray)
                    {
                        Separator = " ",
                        Required = true,
                    },
                    value
                );
            }
        }

        protected override ITaskItem[] TrackedInputFiles => Sources;
        protected override Encoding ResponseFileEncoding => Encoding.UTF8;
        protected override Encoding StandardOutputEncoding => Encoding.UTF8;
        protected override Encoding StandardErrorEncoding => Encoding.UTF8;
    }
}
