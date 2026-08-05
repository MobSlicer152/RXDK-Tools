namespace Rxdk.Xsasm;

/// <summary>
/// One error or warning. Formatted the way the original assembler wrote them, so
/// editors that already parse xsasm output keep working:
///     file(line) : error: message
/// </summary>
internal sealed record Diagnostic(string File, int Line, bool IsError, string Message)
{
    public static Diagnostic Err(string file, int line, string message) =>
        new(file, line, true, message);

    public static Diagnostic Warn(string file, int line, string message) =>
        new(file, line, false, message);

    public override string ToString() =>
        $"{File}({Line}) : {(IsError ? "error" : "warning")}: {Message}";
}

internal sealed class AssemblyException(string message) : Exception(message);
