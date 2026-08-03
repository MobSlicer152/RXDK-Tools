// .xap (XACT project) parser + object model.
//
// The .xap is a text format of nested brace blocks and "Key = Value;" assignments,
// where both keys and block names can contain spaces (e.g. "Wave Entry Index",
// "Play with Pitch and Volume Variation"). We tokenize on the structural characters
// '{', '}', '=' and ';' and treat everything between them as a (trimmed) label/value.
//
// This drives the .xsb/.xwb/.h generation. The XDK tool that owns this format is
// "xactbld"; the leak's internal test tool (filegen) consumed a flattened INI and only
// emitted the soundbank - the wave-bank build, the header emit and the Marker event are
// reconstructed here from the runtime on-disk layout (libxact wavbndlr.h / xactp.h).

using System.Globalization;

namespace Rxdk.XactBld;

/// <summary>A node in the parsed .xap tree: ordered properties + ordered child blocks.</summary>
internal sealed class XapNode
{
    public string Name = "";
    public readonly List<(string Key, string Value)> Props = new();
    public readonly List<XapNode> Children = new();

    public string? Prop(string key)
    {
        foreach (var (k, v) in Props)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    public int IntProp(string key, int fallback = 0)
    {
        var s = Prop(key);
        if (s == null) return fallback;
        return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public long LongProp(string key, long fallback = 0)
    {
        var s = Prop(key);
        if (s == null) return fallback;
        return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public IEnumerable<XapNode> ChildrenNamed(string name) =>
        Children.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public XapNode? Child(string name) => ChildrenNamed(name).FirstOrDefault();
}

/// <summary>Recursive-descent parser for the brace/assignment .xap grammar.</summary>
internal static class XapParser
{
    public static XapNode Parse(string text)
    {
        int pos = 0;
        var root = new XapNode { Name = "" };
        ParseBody(text, ref pos, root, topLevel: true);
        return root;
    }

    // Parse statements into 'node' until a closing '}' (or EOF at top level).
    private static void ParseBody(string text, ref int pos, XapNode node, bool topLevel)
    {
        while (pos < text.Length)
        {
            SkipTrivia(text, ref pos);
            if (pos >= text.Length) break;

            if (text[pos] == '}')
            {
                pos++; // consume close brace
                if (topLevel)
                    continue; // stray brace at top level - ignore
                return;
            }

            // Read a label up to the next structural character.
            int start = pos;
            while (pos < text.Length && text[pos] != '{' && text[pos] != '}' &&
                   text[pos] != '=' && text[pos] != ';')
            {
                // Skip line comments inside a label region.
                if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
                {
                    // rewind label end to here; a comment ends the label scan
                    break;
                }
                pos++;
            }
            string label = text.Substring(start, pos - start).Trim();

            if (pos >= text.Length)
                break;

            char c = text[pos];
            if (c == '{')
            {
                pos++; // consume '{'
                var child = new XapNode { Name = label };
                ParseBody(text, ref pos, child, topLevel: false);
                node.Children.Add(child);
            }
            else if (c == '=')
            {
                pos++; // consume '='
                int vstart = pos;
                while (pos < text.Length && text[pos] != ';' && text[pos] != '}')
                    pos++;
                string value = text.Substring(vstart, pos - vstart).Trim();
                if (pos < text.Length && text[pos] == ';') pos++;
                if (label.Length > 0)
                    node.Props.Add((label, value));
            }
            else if (c == ';')
            {
                pos++; // bare statement terminator
            }
            else if (c == '}')
            {
                // handled at loop top on next iteration
            }
        }
    }

    private static void SkipTrivia(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            char c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            break;
        }
    }
}
