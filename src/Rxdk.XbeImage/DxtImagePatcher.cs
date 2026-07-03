namespace Rxdk.XbeImage;

/// <summary>
/// Patches a linked DXT (Xbox debug-monitor extension) PE so it matches a retail
/// <c>.dxt</c>: it coerces the subsystem to <c>IMAGE_SUBSYSTEM_XBOX</c> (14).
/// Unlike the XBE builder, it does NOT wrap, sign, or relocate the image -- xbdm
/// loads the raw PE from <c>E:\dxt</c> at debug-monitor init, relocating it itself
/// (so the base and relocations from the linker are kept as-is).
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

        // ValidatePe32Image verifies the PE32/i386 headers and coerces the
        // subsystem byte to IMAGE_SUBSYSTEM_XBOX in place (see Pe32Helpers).
        Pe32Helpers.ValidatePe32Image(bytes, options.InputFilePath);

        var outputPath = string.IsNullOrEmpty(options.OutputFilePath)
            ? options.InputFilePath
            : options.OutputFilePath;

        File.WriteAllBytes(outputPath, bytes);
    }
}
