using Microsoft.Build.CPPTasks;
using Microsoft.Build.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Rxdk.MsBuild.Tasks
{
    public class ImageBld : RxdkToolTask
    {
        public ImageBld()
        {
            switchOrderList = new ArrayList()
            {
                "StackSize",
                "Debug",
                "NoLogo",
                "NoLibWarn",
                "LimitMemory",
                "DontModifyHardDisk",
                "DontMountUtilityDrive",
                "FormatUtilityDrive",
                "UtilityDriveClusterSize",
                "NoPreload",
                "TestId",
                "TestAltId",
                "TestRegion",
                "TestRatings",
                "TestMediaTypes",
                "TestLanKey",
                "TestSignKey",
                "TestName",
                "TestVersion",
                "TitleInfo",
                "TitleImage",
                "DefaultSaveImage",
            };
        }

        protected override string ToolName => "imagebld.exe";

        public int StackSize
        {
            get => PropertyOrNull<int>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Integer)
                    {
                        DisplayName = "Stack Size",
                        Description = "Title thread stack size in bytes (/stack)."
                    },
                    value
                );
            }
        }
        public bool Debug
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Boolean)
                    {
                        DisplayName = "Include Debug Info",
                        Description = "Include the debug directory in the XBE so the debugger can resolve symbols (/debug)."
                    },
                    value
                );
            }
        }
        public bool NoLogo
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Boolean)
                    {
                        DisplayName = "Suppress Startup Banner",
                        Description = "Do not print the imagebld banner (/nologo)."
                    },
                    value
                );
            }
        }
        public bool NoLibWarn
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {

                    },
                    value
                );
            }
        }
        public bool LimitMemory
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Boolean)
                    {
                    },
                    value
                );
            }
        }
        public bool DontModifyHardDisk
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Boolean)
                    {
                    },
                    value
                );
            }
        }
        public bool DontMountUtilityDrive
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Boolean)
                    {
                    },
                    value
                );
            }
        }
        public bool FormatUtilityDrive
        {
            get => PropertyOrNull<bool>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Boolean)
                    {
                    },
                    value
                );
            }
        }
        public int UtilityDriveClusterSize
        {
            get => PropertyOrNull<int>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.Integer)
                    {
                    },
                    value
                );
            }
        }
        public string[] NoPreload
        {
            get => PropertyOrNull<string[]>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.StringArray)
                    {
                    },
                    value
                );
            }
        }
        public string TestId
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch(ToolSwitchType.String)
                    {
                    },
                    value
                );
            }
        }
        public string TestAltId
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }
        public string TestRegion
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }
        public string TestRatings
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }
        public string TestMediaTypes
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }
        public string TestLanKey
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }
        public string TestSignKey
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }
        public string TestName
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
new ToolSwitch()
{
},
value
);
            }
        }
        public string TestVersion
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
new ToolSwitch()
{
},
value
);
            }
        }
        public string TitleInfo
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
new ToolSwitch()
{
},
value
);
            }
        }
        public string TitleImage
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }
        public string DefaultSaveImage
        {
            get => PropertyOrNull<string>();
            set
            {
                UpdateSwitch(
                    new ToolSwitch()
                    {
                    },
                    value
                );
            }
        }

        protected override string TrackerIntermediateDirectory => throw new NotImplementedException();
        protected override ITaskItem[] TrackedInputFiles => throw new NotImplementedException();
    }


}
