namespace Rxdk.SkinBld;

/// <summary>Mirrors UIX_SKIN_LAYOUT_INFO, with skinbld's defaults for unset fields.</summary>
internal sealed class SkinLayout
{
    public const int SizeOf = 44;

    public ushort X;
    public ushort Y;
    public ushort Width = 8;
    public ushort Height = 8;
    public uint ImageOffset = 0xFFFFFFFF;
    public uint BackColor;
    public uint TextColor = 0xFFFFFFFF;
    public uint DisabledTextColor = 0xFFB0B0B0;
    public uint SelectionBackColor;
    public uint HighlightedTextColor = 0xFFFFFF00;
    public ushort FontHeight = 16;
    public ushort Flags;
    public ushort XOffset = 4;
    public ushort YOffset;

    /// <summary>A byte followed by three reserved ones, so large values wrap.</summary>
    public byte CustomParam;

    public void Write(BinaryWriter writer)
    {
        writer.Write(X);
        writer.Write(Y);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(ImageOffset);
        writer.Write(BackColor);
        writer.Write(TextColor);
        writer.Write(DisabledTextColor);
        writer.Write(SelectionBackColor);
        writer.Write(HighlightedTextColor);
        writer.Write(FontHeight);
        writer.Write(Flags);
        writer.Write(XOffset);
        writer.Write(YOffset);
        writer.Write(CustomParam);
        writer.Write((byte)0);
        writer.Write((ushort)0);
    }
}

/// <summary>
/// An icon embedded in a string by a <c>{IMG_NAME}</c> marker. Count is how many
/// characters the icon stands in for, which is always one.
/// </summary>
internal sealed record SkinIcon(uint IconResId, uint InsertPosition, uint Count = 1);

internal sealed class SkinImage
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    public string? AlphaSource { get; init; }
    public string Format { get; set; } = "D3DFMT_DXT3";
}

internal sealed class SkinObject
{
    public required string Name { get; init; }
    public required uint Id { get; set; }

    /// <summary>Set once the input mentions the object; absent objects still occupy a table slot.</summary>
    public bool Present { get; set; }

    /// <summary>Order of first mention, which is the order payloads are written in.</summary>
    public int InputOrder { get; set; }

    public SkinLayout? Layout { get; set; }
    public string? Text { get; set; }
    public List<SkinIcon> Icons { get; } = [];
    public SkinImage? Image { get; set; }
    public uint BlobOffset { get; set; } = 0xFFFFFFFF;
}

internal sealed class SkinSectionData
{
    public required string Name { get; init; }
    public required uint SectionId { get; init; }
    public required SectionKind Kind { get; init; }
    public uint Language { get; set; }

    /// <summary>The section's name as the input spelled it, used for the header.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Position of the section's first mention in the input.</summary>
    public int HeaderOrder { get; set; } = int.MaxValue;

    /// <summary>Table order: built-in objects first, then any the input adds.</summary>
    public List<SkinObject> Objects { get; } = [];

    public byte[]? Xpr { get; set; }
    public uint XprOffset { get; set; } = 0xFFFFFFFF;

    public uint RecordId => (Language << 16) | SectionId;

    public IEnumerable<SkinObject> PresentObjects =>
        Objects.Where(o => o.Present).OrderBy(o => o.InputOrder);

    public SkinObject? Find(string name) =>
        Objects.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class Skin
{
    public string Application { get; set; } = "UIX";
    public List<SkinSectionData> Sections { get; } = [];
    public int BuiltInSectionCount { get; set; }
}
