using Microsoft.Build.CPPTasks;
using Microsoft.Build.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;

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

        protected string ReadSwitchMap(string propertyName, IDictionary<string, string> switchMap, string value)
        {
            return ReadSwitchMap(propertyName, switchMap.Select(kv => new[] { kv.Key, kv.Value }).ToArray(), value);
        }

        protected string JoinSwitches(string[] switches)
        {
            return string.Join(" ", switches);
        }

        /// <summary>
        /// Get a property's value, or null if it's not set
        /// </summary>
        private object PropertyOrNull(string name)
        {
            // return nothing if the property is unset
            if (!IsPropertySet(name))
            {
                return null;
            }

            // get the switch
            var toolSwitch = ActiveToolSwitches[name];
            switch (toolSwitch.Type)
            {
                case ToolSwitchType.Boolean:
                    return toolSwitch.BooleanValue;
                case ToolSwitchType.String:
                case ToolSwitchType.File:
                case ToolSwitchType.Directory:
                    return toolSwitch.Value;
                case ToolSwitchType.StringArray:
                case ToolSwitchType.StringPathArray:
                    return toolSwitch.StringList;
                case ToolSwitchType.ITaskItem:
                    return toolSwitch.TaskItem;
                case ToolSwitchType.ITaskItemArray:
                    return toolSwitch.TaskItemArray;
                case ToolSwitchType.Integer:
                    return int.Parse(toolSwitch.Value);
            }

            return null;
        }

        /// <summary>
        /// Get a property as a certain type
        /// </summary>
        protected T PropertyOrNull<T>([CallerMemberName] string name = null)
        {
            return (T)PropertyOrNull(name);
        }

        protected void UpdateSwitch(ToolSwitch toolSwitch, object value, [CallerMemberName] string name = null)
        {
            // set name and value
            toolSwitch.Name = name;
            // set the right field based on type
            switch (toolSwitch.Type)
            {
                case ToolSwitchType.Boolean:
                    toolSwitch.BooleanValue = (bool)value;
                    break;
                case ToolSwitchType.String:
                case ToolSwitchType.File:
                    toolSwitch.Value = (string)value;
                    break;
                case ToolSwitchType.Directory:
                    toolSwitch.Value = EnsureTrailingSlash((string)value);
                    break;
                case ToolSwitchType.StringArray:
                case ToolSwitchType.StringPathArray:
                    toolSwitch.StringList = (string[])value;
                    break;
                case ToolSwitchType.ITaskItem:
                    toolSwitch.TaskItem = (ITaskItem)value;
                    break;
                case ToolSwitchType.ITaskItemArray:
                    toolSwitch.TaskItemArray = (ITaskItem[])value;
                    break;
                case ToolSwitchType.Integer:
                default:
                    toolSwitch.Value = value.ToString();
                    break;
            }

            // replace the switch and add it to the active values
            ActiveToolSwitches[name] = toolSwitch;
            AddActiveSwitchToolValue(toolSwitch);
        }

        protected void UpdateSwitch(ToolSwitch toolSwitch, Dictionary<string, string> switchMap, string value, [CallerMemberName] string name = null)
        {
            // set switch value and indicate that it's a multivalue
            toolSwitch.SwitchValue = ReadSwitchMap(name, switchMap, value);
            toolSwitch.MultipleValues = true;

            UpdateSwitch(toolSwitch, value, name);
        }
    }
}
