using System.Reflection;

namespace Rxdk.SkinBld;

internal enum SectionKind
{
    Screen = 0,
    String = 1,
    Image = 2,
    Audio = 3,
}

internal sealed class SchemaObject
{
    public required string Name { get; init; }
    public required uint Id { get; init; }
}

internal sealed class SchemaSection
{
    public required string Name { get; init; }
    public required uint Id { get; init; }
    public required SectionKind Kind { get; init; }
    public List<SchemaObject> Objects { get; } = [];

    public SchemaObject? Find(string name) =>
        Objects.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The built-in section and object tables that skinbld carries for the "UIX"
/// application. The count of built-in sections is written into the skin header,
/// and a section's object table always lists every built-in object - including
/// the ones the input never mentions.
/// </summary>
internal sealed class SkinSchema
{
    public List<SchemaSection> Sections { get; } = [];

    public SchemaSection? Find(string name) =>
        Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static SkinSchema Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith("UixSchema.txt", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    private static SkinSchema Parse(TextReader reader)
    {
        var schema = new SkinSchema();
        SchemaSection? current = null;

        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#')
                continue;

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "section")
            {
                current = new SchemaSection
                {
                    Name = parts[1],
                    Id = ParseId(parts[2]),
                    Kind = (SectionKind)int.Parse(parts[3]),
                };
                schema.Sections.Add(current);
            }
            else
            {
                current!.Objects.Add(new SchemaObject { Name = parts[0], Id = ParseId(parts[1]) });
            }
        }

        return schema;
    }

    private static uint ParseId(string text) =>
        Convert.ToUInt32(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, 16);
}
