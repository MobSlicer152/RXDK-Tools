using System.Text;

namespace Rxdk.SkinBld;

/// <summary>
/// Serializes a skin: a 20-byte header, one 20-byte directory record per
/// section, then each section's payload. A payload is an object table of
/// (resource id, blob offset) pairs followed by the blob those offsets index -
/// layout records, strings, cue names, and any packed textures.
/// </summary>
internal static class UixWriter
{
    private const int HeaderSize = 20;
    private const int RecordSize = 20;
    private const int ObjectTableEntrySize = 8;
    private const uint NoOffset = 0xFFFFFFFF;

    public static byte[] Write(Skin skin)
    {
        var sections = skin.Sections.Where(s => s.Objects.Any(o => o.Present)).ToList();
        var blobs = sections.Select(BuildBlob).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("XSK0"));
        writer.Write((ushort)RecordSize);
        writer.Write((ushort)sections.Count);
        writer.Write(ApplicationName(skin.Application));
        writer.Write((uint)skin.BuiltInSectionCount);

        var offset = (uint)(HeaderSize + sections.Count * RecordSize);
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var payloadSize = (uint)(section.Objects.Count * ObjectTableEntrySize + blobs[i].Length);

            writer.Write(section.RecordId);
            writer.Write((ushort)section.Kind);
            writer.Write((ushort)section.Objects.Count);
            writer.Write(offset);
            writer.Write(section.XprOffset);
            writer.Write(payloadSize);

            offset += payloadSize;
        }

        for (var i = 0; i < sections.Count; i++)
        {
            foreach (var o in sections[i].Objects)
            {
                writer.Write(o.Id);
                writer.Write(o.Present ? o.BlobOffset : NoOffset);
            }
            writer.Write(blobs[i]);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>The application name occupies a fixed 8-byte field.</summary>
    private static byte[] ApplicationName(string application)
    {
        var field = new byte[8];
        var text = Encoding.ASCII.GetBytes(application);
        Array.Copy(text, field, Math.Min(text.Length, field.Length - 1));
        return field;
    }

    private static byte[] BuildBlob(SkinSectionData section)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        switch (section.Kind)
        {
            case SectionKind.Image:
                // Nothing but the packed textures; offsets were assigned when
                // the .xpr was built.
                section.XprOffset = 0;
                writer.Write(section.Xpr ?? []);
                break;

            case SectionKind.String:
                foreach (var o in section.PresentObjects)
                {
                    o.BlobOffset = (uint)stream.Position;
                    WriteString(writer, o);
                }
                break;

            case SectionKind.Audio:
                foreach (var o in section.PresentObjects)
                {
                    o.BlobOffset = (uint)stream.Position;
                    writer.Write(Encoding.Unicode.GetBytes(o.Text ?? string.Empty));
                    writer.Write((ushort)0);
                }
                break;

            default:
                foreach (var o in section.PresentObjects)
                {
                    o.BlobOffset = (uint)stream.Position;
                    o.Layout!.Write(writer);
                }
                if (section.Xpr is not null)
                {
                    section.XprOffset = (uint)stream.Position;
                    writer.Write(section.Xpr);
                }
                break;
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// A string is UTF-16 text with a terminator. When it carries icons, a
    /// marker word, the icon count and one UIX_SKIN_ICON_INFO per icon come
    /// first.
    /// </summary>
    private static void WriteString(BinaryWriter writer, SkinObject o)
    {
        const ushort iconMarker = 0xE801;

        if (o.Icons.Count > 0)
        {
            writer.Write(iconMarker);
            writer.Write((ushort)o.Icons.Count);
            foreach (var icon in o.Icons)
            {
                writer.Write(icon.IconResId);
                writer.Write(icon.InsertPosition);
                writer.Write(icon.Count);
            }
        }

        writer.Write(Encoding.Unicode.GetBytes(o.Text ?? string.Empty));
        writer.Write((ushort)0);
    }
}
