using Microsoft.Build.CPPTasks;
using Microsoft.Build.Framework;
using System;
using System.Collections;
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
        protected override ArrayList SwitchOrderList => switchOrderList;
        protected ArrayList switchOrderList;

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
                    return toolSwitch.Number;
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
                    toolSwitch.Number = (int)value;
                    break;
            }

            // replace the switch and add it to the active values
            ActiveToolSwitches[name] = toolSwitch;
            AddActiveSwitchToolValue(toolSwitch);

#if DEBUG
            // dont do a repeat dump
            if (beingDumped && !toolSwitch.MultipleValues)
            {
                DumpLangProperty(toolSwitch, []);
                return;
            }
#endif
        }

        protected void UpdateSwitch(ToolSwitch toolSwitch, Dictionary<string, string> switchMap, string value, [CallerMemberName] string name = null)
        {
            // set switch value and indicate that it's a multivalue
            toolSwitch.SwitchValue = ReadSwitchMap(name, switchMap, value);
            toolSwitch.MultipleValues = true;

            UpdateSwitch(toolSwitch, value, name);

#if DEBUG
            // dump specially with the switch map
            if (beingDumped)
            {
                DumpLangProperty(toolSwitch, switchMap);
            }
#endif
        }

#if DEBUG
        /// <summary>
        /// Custom XML printer to match MSBuild stuff more closely
        /// </summary>
        /// <param name="name">Element name</param>
        /// <param name="attributes">Element attributes</param>
        /// <param name="printBody">An optional function that prints a body</param>
        /// <param name="initialPad">How far to indent the element</param>
        protected static void PrintXmlElement(string name, Dictionary<string, string> attributes, Action<int> printBody = null, int initialPad = 0)
        {
            var start = $"<{name} ";
            Console.Write(start);

            var indent = new string(' ', initialPad);
            var pad = indent + new string(' ', start.Length);
            bool first = true;
            foreach (var (attrib, value) in attributes)
            {
                var currentPad = first ? "" : $"\n{pad}";
                Console.Write($"{currentPad}{attrib}=\"{value}\"");
                first = false;
            }

            if (printBody != null)
            {
                Console.WriteLine(" >");
                printBody.Invoke(initialPad + 4);
                Console.WriteLine($"{indent}</{name}>");
            }
            else
            {
                Console.WriteLine(" />");
            }
        }

        /// <summary>
        /// Dump an XML fragment to expedite writing .targets files
        /// </summary>
        public static void DumpTargetsFragment<T>(string parent = null)
            where T : RxdkToolTask, new()
        {
            var temp = new T();
            temp.beingDumped = true;

            var attribs = new Dictionary<string, string>();
            foreach (string prop in temp.switchOrderList)
            {
                attribs[prop] = !string.IsNullOrEmpty(parent) ? $"%({parent}.{prop})" : "";
            }
            PrintXmlElement(typeof(T).Name, attribs);
        }

        public struct LangFragmentSettings
        {
            public string RuleName { get; set; }
            public string RuleDisplayName { get; set; }
            public string SwitchPrefix { get; set; } = "-";

            public LangFragmentSettings()
            {
            }
        }

        private bool beingDumped = false;
        private int indent = 0;

        protected static void DumpLangProperty(ToolSwitch toolSwitch, Dictionary<string, string> switchMap)
        {
            Console.WriteLine(toolSwitch.Name);
        }

        /// <summary>
        /// Dump an XML scaffold for <LangID>/<task>.xml files
        /// </summary>
        public static void DumpLangScaffold<T>(LangFragmentSettings settings)
            where T : RxdkToolTask, new()
        {
            var temp = new T();
            temp.beingDumped = true;

            Console.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            PrintXmlElement("Rule", new Dictionary<string, string>()
                {
                    {"Name", settings.RuleName},
                    {"DisplayName", settings.RuleDisplayName},
                    {"SwitchValue", settings.SwitchPrefix},
                    {"PageTemplate", "tool"},
                    {"xmlns", "http://schemas.microsoft.com/build/2009/properties"},
                    {"xmlns:x", "http://schemas.microsoft.com/winfx/2006/xaml"},
                    {"xmlns:sys","clr-namespace:System;assembly=mscorlib" },
                },
                (int pad) =>
                {
                    temp.indent = pad;
                    foreach (string propertyName in temp.switchOrderList)
                    {
                        var property = temp.GetType().GetProperty(propertyName);
                        if (property != null)
                        {
                            // trigger a call to UpdateSwitch, which calls DumpLangFragment because beingDumped is true
                            //
                            // i admit this a jank way to do it, ideally in the future it will be the other way around
                            // and the classes can be generated from the lang file. i just wanted to get it working.
                            // this is also only to accelerate something i could hand-type anyway.
                            property.SetValue(temp, null);
                        }
                    }
                }
            );
        }
#endif
    }
}
