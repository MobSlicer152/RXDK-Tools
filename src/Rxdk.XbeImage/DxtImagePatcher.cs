namespace Rxdk.XbeImage;

/// <summary>
/// Turns a linked DXT (Xbox debug-monitor extension) PE into the form xbdm loads
/// from <c>E:\dxt</c>. xbdm's loader (StLoadImage in the leaked ntos/dm source)
/// reads the <b>entire raw file</b> into MmDbgAllocateMemory, relocates it, and
/// jumps to <c>DllBase + AddressOfEntryPoint</c> -- it never section-maps. So the
/// image must be <b>flat</b> (file offset == RVA) or the entry point and the
/// import/reloc directories all resolve to the wrong bytes.
///
/// A normal linker emits SectionAlignment=0x1000 / FileAlignment=0x200 (RVA !=
/// file offset), and zig cc won't let us override FileAlignment, so this patcher
/// rewrites the image: it lays every section's raw data down at its RVA, sets
/// FileAlignment == SectionAlignment, and coerces the subsystem to
/// IMAGE_SUBSYSTEM_XBOX (14). No XBE wrapping or signing.
/// </summary>
public static class DxtImagePatcher
{
    public static void Patch(ImageBldOptions options)
    {
        if (string.IsNullOrEmpty(options.InputFilePath))
        {
            throw new XbeImageException("No input DXT specified.");
        }

        var bytes = File.ReadAllBytes(options.InputFilePath);

        // Validate the PE32/i386 headers and coerce the subsystem to Xbox.
        Pe32Helpers.ValidatePe32Image(bytes, options.InputFilePath);

        var flat = Flatten(bytes);

        var outputPath = string.IsNullOrEmpty(options.OutputFilePath)
            ? options.InputFilePath
            : options.OutputFilePath;

        File.WriteAllBytes(outputPath, flat);
    }

    private static byte[] Flatten(byte[] image)
    {
        int sizeOfImage;
        int sizeOfHeaders;
        int sectionAlignment;
        {
            ref var optional = ref Pe32Reader.GetOptionalHeader(image);
            sizeOfImage = (int)optional.SizeOfImage;
            sizeOfHeaders = (int)optional.SizeOfHeaders;
            sectionAlignment = (int)optional.SectionAlignment;
        }

        var sectionCount = Pe32Helpers.SectionCount(image);
        var flat = new byte[sizeOfImage];

        // Headers verbatim at file offset 0 (subsystem already patched above).
        image.AsSpan(0, Math.Min(sizeOfHeaders, image.Length)).CopyTo(flat);

        // Copy each section's raw data to its RVA so file offset == RVA. Any
        // VirtualSize beyond SizeOfRawData stays zero (the buffer is zeroed).
        var sections = new ImageSectionHeader[sectionCount];
        for (var i = 0; i < sectionCount; i++)
        {
            var section = Pe32Helpers.ReadSectionHeader(image, i);
            sections[i] = section;

            var rawPtr = (int)section.PointerToRawData;
            var rawSize = (int)section.SizeOfRawData;
            var virtualAddress = (int)section.VirtualAddress;
            if (rawSize <= 0 || rawPtr <= 0)
            {
                continue;
            }

            var copy = Math.Min(rawSize, sizeOfImage - virtualAddress);
            copy = Math.Min(copy, image.Length - rawPtr);
            if (copy > 0)
            {
                image.AsSpan(rawPtr, copy).CopyTo(flat.AsSpan(virtualAddress));
            }
        }

        // FileAlignment == SectionAlignment, and every section's file pointer is
        // now its RVA -- the on-disk image is identical to the in-memory image.
        {
            ref var optional = ref Pe32Reader.GetOptionalHeader(flat);
            optional.FileAlignment = (uint)sectionAlignment;
        }
        for (var i = 0; i < sectionCount; i++)
        {
            var section = sections[i];
            var span = Math.Max((int)section.VirtualSize, (int)section.SizeOfRawData);
            section.PointerToRawData = section.VirtualAddress;
            section.SizeOfRawData = (uint)AlignUp(span, sectionAlignment);
            Pe32Helpers.WriteSectionHeader(flat, i, section);
        }

        return flat;
    }

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}
