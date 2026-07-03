namespace Rxdk.XboxLaunch;

public sealed class XboxLaunchOptions
{
    public string? Directory { get; set; }
    public string? Title { get; set; }
    public string CommandLine { get; set; } = string.Empty;
    public string? ConsoleName { get; set; }
    public bool Reboot { get; set; } = true;
    public int TimeoutMs { get; set; } = 120_000;

    // Launch-and-run: skip the initial breakpoint and stop-on-thread-create,
    // then just stream debug output for TimeoutMs and leave the title running
    // on the kit (like a manual launch). Without this, the title is halted at
    // the first thread creation waiting for a debugger and never runs.
    public bool Go { get; set; } = false;

    // Reboot-only: warm-reboot the console and exit, without launching a title.
    // Used to reload E:\dxt debug-monitor extensions (xbdm re-scans E:\dxt at
    // debug-monitor init on every boot). /dir and /title are not required.
    public bool RebootOnly { get; set; } = false;
}

public enum XboxLaunchExitCode
{
    Success = 0,
    Error = 1,
    NoConsole = 2,
}
