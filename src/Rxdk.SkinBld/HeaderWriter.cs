using System.Text;

namespace Rxdk.SkinBld;

/// <summary>
/// Writes the C/C++ header of resource identifiers (sk_res.h). Sections appear
/// in the order the input introduced them - language variants of a section
/// collapse into one entry - and each lists only the objects the input names.
/// </summary>
internal static class HeaderWriter
{
    private const int NameColumnWidth = 44;

    public static string Build(Skin skin, IEnumerable<string> inputs)
    {
        var text = new StringBuilder();
        text.Append('\n');
        text.Append("//\n");
        foreach (var input in inputs)
            text.Append("// (skinbld) ").Append(input).Append('\n');
        text.Append("//\n\n");

        var seen = new HashSet<uint>();
        foreach (var section in skin.Sections.OrderBy(s => s.HeaderOrder))
        {
            if (!section.Objects.Any(o => o.Present) || !seen.Add(section.SectionId))
                continue;

            text.Append("\n//\n// [").Append(section.DisplayName).Append("] Section\n//\n\n");
            Define(text, "SECTION_" + section.DisplayName.ToUpperInvariant(), section.SectionId);
            text.Append('\n');

            var prefix = section.Kind == SectionKind.Screen
                ? section.DisplayName.ToUpperInvariant() + "_"
                : string.Empty;
            foreach (var o in section.PresentObjects)
                Define(text, prefix + o.Name.ToUpperInvariant(), o.Id);
        }

        return text.ToString().Replace("\n", "\r\n");
    }

    private static void Define(StringBuilder text, string name, uint value)
    {
        text.Append("#define ").Append(name);
        text.Append(' ', Math.Max(1, NameColumnWidth - name.Length));
        text.Append("0x").Append(value.ToString("x8")).Append('\n');
    }
}
