namespace Rxdk.Engine.Platform;

/// <summary>
/// Platform paths for host tools, the staged SDK, and the managed Zig install. Ported from the
/// path logic in RXDK-VSCode (bridgePath.ts, hostTools.ts, sdkStaging.ts, zigRuntime.ts). Now that
/// this engine is shared by both IDEs -- VS20XX (Windows) and VS Code (Windows/Linux/macOS) -- it is
/// cross-platform: the executable suffix and RID follow the OS, and the …/RXDK roots honor the
/// RXDK_STAGED_* env overrides the caller sets for its per-platform layout.
/// </summary>
public static class RxdkPaths
{
    /// <summary>Host-tools RID for the current OS/arch.</summary>
    public static string ToolRid =>
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier is var rid && rid.StartsWith("win")
            ? "win-x64"
            : OperatingSystem.IsMacOS()
                ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64");

    /// <summary>Executable name for a host tool: adds the .exe suffix on Windows only.</summary>
    public static string HostToolExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? $"{baseName}.exe" : baseName;

    // The machine-wide data root: %ProgramData% on Windows, the platform CommonApplicationData
    // elsewhere. In practice the caller (VS Code / VS20XX) overrides each …/RXDK root via the
    // RXDK_STAGED_* env vars below, so this is only the last-resort default.
    private static string ProgramData()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData");
        if (!string.IsNullOrEmpty(programData))
        {
            return programData;
        }
        return OperatingSystem.IsWindows()
            ? @"C:\ProgramData"
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    }

    private static string LocalAppData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // ---- Staged host tools (…/RXDK/tools) ----

    public static string GetDefaultStagedToolsRoot() =>
        Path.Combine(ProgramData(), "RXDK", "tools");

    /// <summary>Effective staged tools root, honoring the RXDK_STAGED_TOOLS override.</summary>
    public static string GetStagedToolsRoot() =>
        EnvOverride("RXDK_STAGED_TOOLS") ?? GetDefaultStagedToolsRoot();

    /// <summary>Absolute path to a host tool in the staged tools root (may not exist yet).</summary>
    public static string ResolveHostTool(string baseName) =>
        Path.Combine(GetStagedToolsRoot(), HostToolExecutableName(baseName));

    // ---- Staged SDK (headers + libs, …/RXDK/sdk) ----

    public static string GetDefaultStagedSdkRoot() =>
        Path.Combine(ProgramData(), "RXDK", "sdk");

    /// <summary>Effective staged SDK root, honoring the RXDK_STAGED_SDK override.</summary>
    public static string GetStagedSdkRoot() =>
        EnvOverride("RXDK_STAGED_SDK") ?? GetDefaultStagedSdkRoot();

    // ---- Staged docs (RXDK-Docs, …/RXDK/docs) ----

    public static string GetDefaultStagedDocsRoot() =>
        Path.Combine(ProgramData(), "RXDK", "docs");

    /// <summary>Effective staged docs root, honoring the RXDK_STAGED_DOCS override.</summary>
    public static string GetStagedDocsRoot() =>
        EnvOverride("RXDK_STAGED_DOCS") ?? GetDefaultStagedDocsRoot();

    // ---- Staged samples (RXDK-Samples, …/RXDK/samples) ----

    public static string GetDefaultStagedSamplesRoot() =>
        Path.Combine(ProgramData(), "RXDK", "samples");

    /// <summary>Effective staged samples root, honoring the RXDK_STAGED_SAMPLES override.</summary>
    public static string GetStagedSamplesRoot() =>
        EnvOverride("RXDK_STAGED_SAMPLES") ?? GetDefaultStagedSamplesRoot();

    // ---- Managed Zig install (…/RXDK/zig under LocalAppData) ----

    /// <summary>Persistent Zig install root.</summary>
    public static string GetZigInstallRoot() =>
        Path.Combine(LocalAppData(), "RXDK", "zig");

    private static string? EnvOverride(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
    }
}
