namespace Rxdk.SkinBld;

/// <summary>
/// Resource identifiers for sections and objects the built-in schema does not
/// define. skinbld hashes the upper-cased name - qualified as "Section$Object"
/// for objects - into 16 bits, then tags it according to the section kind.
/// </summary>
internal static class SkinHash
{
    /// <summary>Every screen section must open with this object, whose id is fixed.</summary>
    public const uint ScreenObjectId = 0x40001001;

    public const uint ScreenFlag = 0x40000000;
    public const uint TextFlag = 0x80000000;

    public static ushort Hash(string text)
    {
        uint h = 0;
        foreach (var c in text)
            h = ((h * 0x112) + char.ToUpperInvariant(c)) ^ 0xA563;
        return (ushort)h;
    }

    public static uint SectionId(string sectionName) => Hash(sectionName);

    public static uint ObjectId(string sectionName, string objectName, SectionKind kind)
    {
        if (kind == SectionKind.Screen && string.Equals(objectName, "Screen", StringComparison.OrdinalIgnoreCase))
            return ScreenObjectId;

        uint id = Hash(sectionName + "$" + objectName);
        return kind switch
        {
            SectionKind.Screen => id | ScreenFlag,
            SectionKind.String or SectionKind.Audio => id | TextFlag,
            _ => id,
        };
    }
}
