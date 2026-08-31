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
                "Target"
            };
        }
        protected override ArrayList SwitchOrderList => switchOrderList;
        protected ArrayList switchOrderList;

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

        /// <summary>
        /// Dump an XML fragment to expedite writing .targets files
        /// </summary>
        public void DumpXmlFragment(string parent = null)
        {
            var name = GetType().Name;
            var start = $"<{name} ";
            Console.Write(start);
            var pad = new string(' ', start.Length);
            bool first = true;
            foreach (string prop in switchOrderList)
            {
                var currentPad = first ? "" : $"\n{pad}";
                Console.Write($"{currentPad}{prop}=\"{(!string.IsNullOrEmpty(parent) ? $"%({parent}.{prop})" : "")}\"");
                first = false;
            }

            Console.WriteLine($" />");
        }
    }
}
