using System.Text;

namespace Rxdk.SkinBld;

internal sealed class SkinBldException(string message) : Exception(message);

/// <summary>One <c>Field="Value"</c> line from an input file.</summary>
internal sealed record InxEntry(string Field, string Value, int Line, string File);

internal sealed class InxSection(string name, int line, string file)
{
    public string Name { get; } = name;
    public int Line { get; } = line;

    /// <summary>The input file that opened this section, as /pre reports it.</summary>
    public string File { get; } = file;

    public List<InxEntry> Entries { get; } = [];
}

/// <summary>
/// Reads a skin description (.inx) and the localization files it includes. Both
/// are Unicode text; values are quoted, and ';' or '#' starts a comment.
/// </summary>
internal static class InxParser
{
    public static List<InxSection> Parse(string path)
    {
        var sections = new List<InxSection>();
        ParseInto(sections, path, null);
        return sections;
    }

    private static void ParseInto(List<InxSection> sections, string path, InxSection? target)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new SkinBldException($"Couldn't open file '{path}'");

        var directory = Path.GetDirectoryName(full)!;
        var current = target;
        var lineNumber = 0;

        foreach (var raw in ReadLines(full))
        {
            lineNumber++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                // An included file's own headers are ignored: everything it
                // holds belongs to the section that pulled it in.
                if (target is not null)
                    continue;

                var end = line.IndexOf(']');
                if (end < 0)
                    throw new SkinBldException($"Unterminated section name at line {lineNumber}");
                current = new InxSection(line[1..end].Trim(), lineNumber, full);
                sections.Add(current);
                continue;
            }

            var (field, value) = SplitAssignment(line, lineNumber);
            if (current is null)
                throw new SkinBldException($"Field '{field}' outside of a section at line {lineNumber}");

            if (string.Equals(field, "Include", StringComparison.OrdinalIgnoreCase))
            {
                ParseInto(sections, Path.Combine(directory, ToNativePath(value)), current);
                continue;
            }

            current.Entries.Add(new InxEntry(field, value, lineNumber, full));
        }
    }

    private static (string Field, string Value) SplitAssignment(string line, int lineNumber)
    {
        var equals = line.IndexOf('=');
        if (equals < 0)
            throw new SkinBldException($"Expected '=' at line {lineNumber}");

        var field = line[..equals].Trim();
        var rest = line[(equals + 1)..].Trim();
        if (rest.Length < 2 || rest[0] != '"' || rest[^1] != '"')
            throw new SkinBldException($"String expected at line {lineNumber}");

        return (field, rest[1..^1]);
    }

    /// <summary>Strips a trailing comment, ignoring markers inside a quoted value.</summary>
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (!quoted && (line[i] == ';' || line[i] == '#'))
                return line[..i];
        }
        return line;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        // The files are UTF-16 with a byte-order mark; tolerate UTF-8 as well.
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, new UnicodeEncoding(false, true), detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>Input files spell paths with backslashes; accept them anywhere.</summary>
    public static string ToNativePath(string path) =>
        Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);
}
