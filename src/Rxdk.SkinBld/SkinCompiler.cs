using System.Globalization;

namespace Rxdk.SkinBld;

/// <summary>
/// Turns parsed input sections into the skin model: resolves resource ids,
/// applies layout properties over skinbld's defaults, and collects the strings,
/// audio cue names and images each section contributes.
/// </summary>
internal sealed class SkinCompiler(SkinSchema schema)
{
    private const int MaxCoordinate = 1920;

    private readonly Dictionary<uint, uint> _idInstances = [];
    private int _order;

    public Skin Compile(List<InxSection> input)
    {
        var skin = new Skin();

        var skinSection = input.FirstOrDefault(s => string.Equals(s.Name, "Skin", StringComparison.OrdinalIgnoreCase))
            ?? throw new SkinBldException("Missing [Skin] section");
        var application = skinSection.Entries
            .FirstOrDefault(e => string.Equals(e.Field, "Application", StringComparison.OrdinalIgnoreCase))?.Value
            ?? throw new SkinBldException("Missing 'Application' in [Skin] section");
        skin.Application = application;
        skin.BuiltInSectionCount = schema.Sections.Count;

        // The three resource sections exist up front, so they lead the output
        // regardless of where the input mentions them.
        foreach (var name in new[] { "STRING", "AUDIO", "IMAGE" })
            skin.Sections.Add(CreateSection(schema.Find(name)!, 0));

        var sectionOrder = 0;
        foreach (var section in input)
        {
            if (string.Equals(section.Name, "Skin", StringComparison.OrdinalIgnoreCase))
                continue;

            var language = ParseLanguage(section);
            var target = Resolve(skin, section, language);
            if (target.DisplayName.Length == 0)
                target.DisplayName = section.Name;
            target.HeaderOrder = Math.Min(target.HeaderOrder, sectionOrder++);

            foreach (var entry in section.Entries)
                Apply(target, entry);
        }

        foreach (var section in skin.Sections)
        {
            if (section.Kind == SectionKind.Screen)
                OffsetByScreenOrigin(section);
        }

        return skin;
    }

    private SkinSectionData CreateSection(SchemaSection schemaSection, uint language)
    {
        var section = new SkinSectionData
        {
            Name = schemaSection.Name,
            SectionId = schemaSection.Id,
            Kind = schemaSection.Kind,
            Language = language,
        };

        foreach (var o in schemaSection.Objects)
            section.Objects.Add(new SkinObject { Name = o.Name, Id = o.Id });

        return section;
    }

    private SkinSectionData Resolve(Skin skin, InxSection input, uint language)
    {
        var schemaSection = schema.Find(input.Name);
        var sectionId = schemaSection?.Id ?? SkinHash.SectionId(input.Name);

        var existing = skin.Sections.FirstOrDefault(s => s.SectionId == sectionId && s.Language == language);
        if (existing is not null)
            return existing;

        var section = schemaSection is not null
            ? CreateSection(schemaSection, language)
            : new SkinSectionData
            {
                Name = input.Name,
                SectionId = sectionId,
                Kind = SectionKind.Screen,
                Language = language,
            };

        skin.Sections.Add(section);
        return section;
    }

    private static uint ParseLanguage(InxSection section)
    {
        var entry = section.Entries
            .FirstOrDefault(e => string.Equals(e.Field, "Language", StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Value.Length == 0)
            return 0;

        return entry.Value.ToLowerInvariant() switch
        {
            "en" => 1,
            "ja" => 2,
            "de" => 3,
            "fr" => 4,
            "es" => 5,
            "it" => 6,
            "ko" => 7,
            "tw" => 8,
            "br" => 9,
            _ => throw new SkinBldException($"Invalid language '{entry.Value}' at line {entry.Line}"),
        };
    }

    private void Apply(SkinSectionData section, InxEntry entry)
    {
        if (string.Equals(entry.Field, "Language", StringComparison.OrdinalIgnoreCase))
            return;

        var dot = entry.Field.IndexOf('.');
        var objectName = dot < 0 ? entry.Field : entry.Field[..dot];
        var property = dot < 0 ? null : entry.Field[(dot + 1)..];

        var target = section.Find(objectName) ?? AddObject(section, objectName);
        if (!target.Present)
        {
            target.Present = true;
            target.InputOrder = _order++;
            if (section.Kind == SectionKind.Screen)
                target.Layout = new SkinLayout();
        }

        switch (section.Kind)
        {
            case SectionKind.String:
                if (property is not null)
                    throw new SkinBldException($"Unknown field '{entry.Field}' at line {entry.Line}");
                SetText(target, entry);
                break;

            case SectionKind.Audio:
                if (property is not null)
                    throw new SkinBldException($"Unknown field '{entry.Field}' at line {entry.Line}");
                target.Text = entry.Value;
                break;

            case SectionKind.Image:
                ApplyImage(target, property, entry);
                break;

            default:
                ApplyLayout(target, property, entry);
                break;
        }
    }

    private SkinObject AddObject(SkinSectionData section, string name)
    {
        var id = SkinHash.ObjectId(section.Name, name, section.Kind);

        // Distinct names can hash alike; each later collision takes the next
        // instance number in the id's high word.
        var instance = _idInstances.TryGetValue(id, out var next) ? next : 0;
        _idInstances[id] = instance + 1;

        var o = new SkinObject { Name = name, Id = id | (instance << 16) };
        section.Objects.Add(o);
        return o;
    }

    private static void ApplyImage(SkinObject target, string? property, InxEntry entry)
    {
        if (property is null)
        {
            target.Image = new SkinImage { Name = target.Name, Source = entry.Value };
            return;
        }

        if (target.Image is null)
            throw new SkinBldException($"Image source for '{target.Name}' must come first, at line {entry.Line}");

        switch (property.ToLowerInvariant())
        {
            case "alphasource":
                target.Image = new SkinImage
                {
                    Name = target.Image.Name,
                    Source = target.Image.Source,
                    AlphaSource = entry.Value,
                    Format = target.Image.Format,
                };
                break;
            case "format":
                target.Image.Format = entry.Value;
                break;
            default:
                throw new SkinBldException($"Unknown field '{entry.Field}' at line {entry.Line}");
        }
    }

    private static void ApplyLayout(SkinObject target, string? property, InxEntry entry)
    {
        if (property is null)
            throw new SkinBldException($"Field '{entry.Field}' needs a property at line {entry.Line}");

        var layout = target.Layout!;
        switch (property.ToLowerInvariant())
        {
            case "x": layout.X = Coordinate(entry); break;
            case "y": layout.Y = Coordinate(entry); break;
            case "width": layout.Width = Coordinate(entry); break;
            case "height": layout.Height = Coordinate(entry); break;
            case "fontheight": layout.FontHeight = Coordinate(entry); break;
            case "xoffset": layout.XOffset = Coordinate(entry); break;
            case "yoffset": layout.YOffset = Coordinate(entry); break;
            case "backcolor": layout.BackColor = Color(entry); break;
            case "textcolor": layout.TextColor = Color(entry); break;
            case "disabledtextcolor": layout.DisabledTextColor = Color(entry); break;
            case "selectionbackcolor": layout.SelectionBackColor = Color(entry); break;
            case "highlightedtextcolor": layout.HighlightedTextColor = Color(entry); break;
            case "aligntext": layout.Flags = Alignment(entry); break;
            case "visibleitems":
            case "customparam": layout.CustomParam = (byte)Number(entry); break;

            case "source":
                target.Image = new SkinImage { Name = target.Name, Source = entry.Value };
                break;
            case "alphasource":
            case "format":
                ApplyImage(target, property, entry);
                break;

            default:
                throw new SkinBldException($"Unknown field '{entry.Field}' at line {entry.Line}");
        }
    }

    private static ushort Coordinate(InxEntry entry)
    {
        var value = Number(entry);
        if (value > MaxCoordinate)
            throw new SkinBldException($"Value must be between 0 and {MaxCoordinate} at line {entry.Line}");
        return (ushort)value;
    }

    private static uint Number(InxEntry entry)
    {
        if (!uint.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new SkinBldException($"Number expected at line {entry.Line}");
        return value;
    }

    private static uint Color(InxEntry entry)
    {
        var text = entry.Value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return value;

        throw new SkinBldException($"Color expected at line {entry.Line}");
    }

    private static ushort Alignment(InxEntry entry) => entry.Value.ToLowerInvariant() switch
    {
        "left" => 0,
        "center" => 1,
        "right" => 2,
        _ => throw new SkinBldException($"Left, Center or Right expected at line {entry.Line}"),
    };

    /// <summary>
    /// Stores a string's text, turning "\n" into a line break and pulling
    /// {IMG_NAME} markers out into icon records.
    /// </summary>
    private void SetText(SkinObject target, InxEntry entry)
    {
        var text = Unescape(entry.Value);
        var builder = new System.Text.StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                builder.Append(text[i]);
                continue;
            }

            var end = text.IndexOf('}', i);
            if (end < 0)
                throw new SkinBldException($"Unterminated icon name at line {entry.Line}");

            var token = text[(i + 1)..end];
            if (TryParseCharacterCode(token, out var character))
                builder.Append(character);
            else
                target.Icons.Add(new SkinIcon(ResolveIcon(token, entry), (uint)builder.Length));
            i = end;
        }

        if (target.Name.StartsWith("STRP_", StringComparison.OrdinalIgnoreCase) && !builder.ToString().Contains("%s"))
            throw new SkinBldException($"Field '{target.Name}' should contain a '%s' at line {entry.Line}");

        target.Text = builder.ToString();
    }

    /// <summary>
    /// A brace token holds either the name of an image to insert as an icon or a
    /// character code written as a C integer literal: hex behind "0x", octal
    /// behind a leading zero, decimal otherwise.
    /// </summary>
    private static bool TryParseCharacterCode(string token, out char character)
    {
        character = '\0';
        if (token.Length == 0 || !char.IsAsciiDigit(token[0]))
            return false;

        var (digits, radix) =
            token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? (token[2..], 16u) :
            token.Length > 1 && token[0] == '0' ? (token[1..], 8u) :
            (token, 10u);

        if (digits.Length == 0)
            return false;

        uint value = 0;
        foreach (var digit in digits)
        {
            var d = (uint)(char.IsAsciiDigit(digit) ? digit - '0'
                : char.IsAsciiHexDigit(digit) ? char.ToLowerInvariant(digit) - 'a' + 10
                : 99);
            if (d >= radix)
                return false;

            value = value * radix + d;
            if (value > char.MaxValue)
                return false;
        }

        character = (char)value;
        return true;
    }

    /// <summary>
    /// Expands the escapes the input files document: "\n" plus "\xHH" and
    /// "\uHHHH" for arbitrary characters by code point.
    /// </summary>
    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
            return text;

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 == text.Length)
            {
                builder.Append(text[i]);
                continue;
            }

            var digits = char.ToLowerInvariant(text[i + 1]) switch { 'x' => 2, 'u' => 4, _ => 0 };
            if (digits != 0 && i + 1 + digits < text.Length &&
                ushort.TryParse(text.AsSpan(i + 2, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                builder.Append((char)code);
                i += 1 + digits;
                continue;
            }

            if (text[i + 1] == 'n')
            {
                builder.Append('\n');
                i++;
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    private uint ResolveIcon(string name, InxEntry entry)
    {
        var images = schema.Find("IMAGE")!;
        var image = images.Find(name);
        if (image is not null)
            return image.Id;

        // Icons may also name an image the input itself added.
        return SkinHash.ObjectId(images.Name, name, SectionKind.Image);
    }

    /// <summary>
    /// A screen's position is the origin for everything on it, so skinbld folds
    /// the Screen object's X/Y into each sibling's coordinates.
    /// </summary>
    private static void OffsetByScreenOrigin(SkinSectionData section)
    {
        var screen = section.Find("Screen");
        if (screen?.Layout is null || !screen.Present)
            return;

        var (dx, dy) = (screen.Layout.X, screen.Layout.Y);
        if (dx == 0 && dy == 0)
            return;

        foreach (var o in section.Objects)
        {
            if (o == screen || o.Layout is null || !o.Present)
                continue;
            o.Layout.X = (ushort)(o.Layout.X + dx);
            o.Layout.Y = (ushort)(o.Layout.Y + dy);
        }
    }
}
