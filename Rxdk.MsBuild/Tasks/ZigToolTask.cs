using Microsoft.Build.CPPTasks;
using Microsoft.Build.Framework;
using System.Collections;
using System.Text;

namespace Rxdk.MsBuild.Tasks
{
    public abstract class ZigToolTask : RxdkToolTask
    {
        protected override string TrackerIntermediateDirectory => "";
        protected override ArrayList SwitchOrderList => switchOrderList;
        protected ArrayList switchOrderList;

        public ZigToolTask()
        {
            switchOrderList = new ArrayList()
            {
                "ToolName",
                "SubTool",
                "Target"
            };
        }

        protected override string ToolName => "zig.exe";
        protected abstract string SubTool { get; }
        protected string Target => "-target x86-windows-gnu";
        [Required]
        protected virtual ITaskItem[] Sources
        {
            get => PropertyOrNull<ITaskItem[]>("Sources");
            set
            {
                base.ActiveToolSwitches.Remove("Sources");
                ToolSwitch toolSwitch = new ToolSwitch(ToolSwitchType.ITaskItemArray)
                {
                    Separator = " ",
                    Required = true,
                    ArgumentRelationList = new ArrayList(),
                    TaskItemArray = value,
                };
                base.ActiveToolSwitches.Add("Sources", toolSwitch);
                base.AddActiveSwitchToolValue(toolSwitch);
            }
        }
        protected override ITaskItem[] TrackedInputFiles => Sources;
        protected override Encoding ResponseFileEncoding => Encoding.UTF8;
        protected override Encoding StandardOutputEncoding => Encoding.UTF8;
        protected override Encoding StandardErrorEncoding => Encoding.UTF8;
    }
}
