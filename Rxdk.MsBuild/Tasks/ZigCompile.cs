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
    public class ZigCompile : ZigToolTask
    {
        protected override ITaskItem[] TrackedInputFiles => Sources;

        public ZigCompile()
        {
            switchOrderList.AddRange(new string[] {
                "SubTool",
                "Target",
                "Optimization",
                "AlwaysAppend",
                "DebugInfo",
                "AdditionalIncludeDirectories",
                "ObjectFileName",
                "WarningLevel",
                "TreatWarningAsError",
                "Verbose",
                "TrackerLogDirectory",
                "StrictAliasing",
                "OmitFramePointers",
                "BufferSecurityCheck",
                "RuntimeTypeInfo",
                "CLanguageStandard",
                "CppLanguageStandard",
                "PreprocessorDefinitions",
                "UndefinePreprocessorDefinitions",
                "UndefineAllPreprocessorDefinitions",
                "AdditionalOptions",
                "Sources",
            });
        }

        protected override string SubTool => "cc";

        protected string Optimization
        {
            get => PropertyOrNull<string>("Optimization");
            set =>
                UpdateSwitch(
                    "Optimization",
                    new ToolSwitch(ToolSwitchType.String)
                    {
                        DisplayName = "Optimization",
                        Description = "Specifies the optimization level for the title.",
                        ArgumentRelationList = new ArrayList(),
                        Value = value,
                        MultipleValues = true,
                    },
                    new Dictionary<string, string> {
                        {"None", "-O0"},
                        {"FavorSpeed", "-O2"},
                        {"FavorSize", "-Os"},
                        {"Full", "-O3"},
                    },
                    value
                );
        }

        protected override string AlwaysAppend => JoinSwitches(new string[] {
            "-ffreestanding", "-fno-stack-protector", "-fms-extensions", "-fms-compatibility",
            "-nostdinc", "-include", "picolibc.h", "-march=pentium3",
            // Every Xbox title is built with _XBOX/XBOX defined (the XDK did this); a lot of
            // Xbox headers/code select their platform path on it.
            "-D_XBOX", "-DXBOX",
            // Keep Clang from inline-expanding memmove/memcpy-shaped calls past picolibc's
            // -fno-builtin implementations, and pin the retail (_DEBUG-off) SDK link path.
            "-fno-builtin", // "-U_DEBUG", // hopefully _DEBUG can stay working
            // picolibc's default assert() calls __assert_no_args(), which prints a bare
            // "assertion failed" -- useless for locating a fault in a title. Ask for the
            // variant that reports the expression, file and line.
            "-D__ASSERT_VERBOSE",
            // Thread-local storage: emulated TLS (a per-thread table reached via
            // __emutls_get_address, backed by libc tss/emutls.c) instead of the native
            // Windows __tls_index/TEB %fs model, which the RXDK runtime never sets up.
            // Without this, any title `__thread`/`thread_local` (e.g. stb_image's
            // stbi__g_failure_reason / vertically_flip_on_load) reads a wild fixed
            // address and bugchecks. Matches how libcpp is built (xbox_target.zig
            // cppFlags).
            "-femulated-tls",

            // -I (not -isystem) everywhere: the SDK's clean-room windef.h/etc. must win over zig's
            // bundled MinGW headers, which -isystem would let shadow them.
            // The sample + framework code is compiled warning-clean; only these unavoidable suppressions
            // remain, and none of them is a fixable source defect:
            //   * c++11-narrowing / address-of-temporary — clang treats these as hard ERRORS on legacy
            //     XDK idioms (braced-init narrowing e.g. STRING={(USHORT)strlen(s),...}; and taking the
            //     address of a temporary passed to a D3DX helper, D3DXVec3Cross(&out,&D3DXVECTOR3(...),...)
            //     — the temporary lives to end-of-expression so the callee is safe). Rewriting Microsoft's
            //     reference idioms is out of scope.
            //   * ignored-pragma-intrinsic — clang cannot honor MSVC's `#pragma intrinsic`; harmless.
            //   * multichar — the XDK FOURCC idiom ('YV12' etc.) is intentional, not a bug.
            //   * unused-command-line-argument — build-driver noise (a flag that doesn't apply to a TU).
            //   * deprecated-enum-enum-conversion — the D3D8 pixel-shader register-combiner API is
            //     *defined* by OR-ing the named PS_REGISTER / PS_CHANNEL / PS_INPUTMAPPING enums together
            //     (see d3d8types.h's own combiner examples). It is the documented, retail-faithful idiom;
            //     C++20 deprecates cross-enum bitwise ops in general but this usage is correct by design,
            //     and casting at every combiner call site across the shader samples would only obscure it.
            "-Wno-c++11-narrowing",
            "-Wno-address-of-temporary",
            "-Wno-ignored-pragma-intrinsic",
            "-Wno-multichar",
            "-Wno-unused-command-line-argument",
            "-Wno-deprecated-enum-enum-conversion",
        });

        protected string DebugInfo
        {
            get => PropertyOrNull<string>("DebugInfo");
            set => UpdateSwitch(
                "DebugInfo",
                new ToolSwitch(ToolSwitchType.String)
                {
                    DisplayName = "Debug Information",
                    Description = "Specifies the type of debugging information generated by the compiler.",
                    MultipleValues = true
                },
                new Dictionary<string, string>
                {
                    {"None", ""},
                    {"LineTables", "-gline-tables-only"},
                    {"Full", "-g"},
                },
                value
            );
        }

        protected string[] AdditionalIncludeDirectories
        {
            get => PropertyOrNull<string[]>("AdditionalIncludeDirectories");
            set => UpdateSwitch(
                "AdditionalIncludeDirectories",
                new ToolSwitch(ToolSwitchType.StringPathArray)
                {
                    DisplayName = "Additional Include Directories",
                    Description = "Specifies one or more directories to add to the include path; separate with semi-colons if more than one. (-I[path]).",
                    SwitchValue = "-I ",

                },
                value
            );
        }

        protected string ObjectFileName { get => PropertyOrNull<string>("ObjectFileName"); }
        protected string WarningLevel { get => PropertyOrNull<string>("WarningLevel"); }
        protected bool TreatWarningAsError { get => PropertyOrNull<bool>("TreatWarningAsError"); }
        protected string[] DisableSpecificWarnings { get => PropertyOrNull<string[]>("DisableSpecificWarnings"); }
        protected bool Verbose { get => PropertyOrNull<bool>("Verbose"); }
        protected string TrackerLogDirectory { get => PropertyOrNull<string>("TrackerLogDirectory"); }
        protected bool StrictAliasing { get => PropertyOrNull<bool>("StrictAliasing"); }
        protected bool OmitFramePointers { get => PropertyOrNull<bool>("OmitFramePointers"); }
        protected bool BufferSecurityCheck { get => PropertyOrNull<bool>("BufferSecurityCheck"); }
        protected bool RuntimeTypeInfo { get => PropertyOrNull<bool>("RuntimeTypeInfo"); }
        protected string CLanguageStandard { get => PropertyOrNull<string>("CLanguageStandard"); }
        protected string CppLanguageStandard { get => PropertyOrNull<string>("CppLanguageStandard"); }
        protected string[] PreprocessorDefinitions { get => PropertyOrNull<string[]>("PreprocessorDefinitions"); }
        protected string[] UndefinePreprocessorDefinitions { get => PropertyOrNull<string[]>("UndefinePreprocessorDefinitions"); }
        protected bool UndefineAllPreprocessorDefinitions { get => PropertyOrNull<bool>("UndefineAllPreprocessorDefinitions"); }
    }
}
