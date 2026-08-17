using System.Text;

namespace Rxdk.SkinBld;

/// <summary>
/// Writes the merged input (sk_pre.inx): every section in input order with its
/// entries, annotated with the file each run of entries came from. Useful for
/// checking what a set of inputs and includes actually amounts to.
/// </summary>
internal static class PreWriter
{
    public static string Build(List<InxSection> sections)
    {
        var text = new StringBuilder();
        foreach (var section in sections)
        {
            text.Append("\r\n[").Append(section.Name).Append("]\r\n");

            var source = string.Empty;
            foreach (var entry in section.Entries)
            {
                if (entry.File != source)
                {
                    source = entry.File;
                    text.Append(";\r\n; ").Append(source).Append("\r\n;\r\n");
                }
                text.Append(entry.Field).Append("=\"").Append(entry.Value).Append("\"\r\n");
            }
        }
        return text.ToString();
    }
}
