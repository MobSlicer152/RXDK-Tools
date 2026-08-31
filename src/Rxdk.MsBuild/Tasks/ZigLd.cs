using Microsoft.Build.CPPTasks;
using System;
using System.Text.RegularExpressions;

namespace Rxdk.MsBuild.Tasks
{
    public class ZigLd : ZigToolTask
    {
        public ZigLd()
        {
            switchOrderList.AddRange(new string[] {
                "OutputFile",
                "ShowProgress",
                "Version",
                "VerboseOutput",
                "Trace",
                "TraceSymbols",
                "PrintMap",
                "LinkerScript",
                "UnresolvedSymbolReferences",
                "OptimizeforMemory",
                "SharedLibrarySearchPath",
                "AdditionalLibraryDirectories",
                "IgnoreSpecificDefaultLibraries",
                "IgnoreDefaultLibraries",
                "ForceUndefineSymbolReferences",
                "DebuggerSymbolInformation",
                "GenerateMapFile",
                "Relocation",
                "FunctionBinding",
                "NoExecStackRequired",
                "WholeArchiveBegin",
                "AdditionalOptions",
                "Sources",
                "AdditionalDependencies",
                "WholeArchiveEnd",
                "LibraryDependencies",
                "EnableASAN"
            });
        }

        protected override string SubTool => "ld";

        public virtual string OutputFile
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new(ToolSwitchType.File)
                    {
                        DisplayName = "Output File",
                        Description = "The option overrides the default name and location of the program that the linker creates. (-o)",
                        SwitchValue = "-o ",
                    },
                    value
                );
            }
        }

        public virtual bool ShowProgress
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new(ToolSwitchType.Boolean)
                    {
                        DisplayName = "Show Progress",
                        Description = "Prints Linker Progress Messages.",
                        SwitchValue = "-Wl,--stats",
                    },
                    value
                );
            }
        }

        //public virtual bool Version
        //{
        //
        //}

        protected static Regex ldMessageRegex = new Regex("^\\s*(?<FILENAME>[^:]*):(((?<LINE>\\d*):)?)(\\s*(?<CATEGORY>(fatal error|error|warning|note)):)?\\s*(?<TEXT>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100.0));
    }
}
