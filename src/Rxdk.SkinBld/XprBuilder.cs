using System.Text;

namespace Rxdk.SkinBld;

/// <summary>
/// Packs a section's images by handing the bundler the same resource
/// description file skinbld would have written, then reading back the .xpr.
/// </summary>
internal sealed class XprBuilder(bool keepRdf, bool quiet)
{
    /// <summary>Each texture occupies a fixed-size resource header in the .xpr.</summary>
    private const int ResourceHeaderSize = 20;

    public byte[]? Build(SkinSectionData section, string workDirectory)
    {
        var images = section.PresentObjects
            .Where(o => o.Image is not null)
            .Select(o => o.Image!)
            .ToList();
        if (images.Count == 0)
            return null;

        // The layout's ImageOffset selects a texture by its slot in the .xpr.
        var slot = 0;
        foreach (var o in section.PresentObjects.Where(o => o.Image is not null))
        {
            if (o.Layout is not null)
                o.Layout.ImageOffset = (uint)(slot * ResourceHeaderSize);
            o.BlobOffset = (uint)(slot * ResourceHeaderSize);
            slot++;
        }

        var rdfPath = Path.Combine(workDirectory, $"skin_{section.Name}_{section.Language}.rdf");
        var xprPath = Path.ChangeExtension(rdfPath, ".xpr");
        File.WriteAllText(rdfPath, BuildRdf(images), Encoding.ASCII);

        try
        {
            var args = new List<string> { "-o", xprPath, "-h", Path.ChangeExtension(rdfPath, ".h") };
            if (quiet)
                args.Add("-q");
            args.Add(rdfPath);

            // 5849's skinbld tags its textures with DMA channel A.
            Bundler.XboxFormats.DmaChannel = Bundler.XboxFormats.D3DFORMAT_DMACHANNEL_A;

            var bundler = new Bundler.Bundler();
            // skinbld.exe merges an AlphaSource from the blue channel (byte 0),
            // unlike bundler.exe which uses the alpha/X channel (byte 3). Keep the
            // skin's shipped byte-parity by opting into the blue-channel merge.
            bundler.AlphaFromBlueChannel = true;
            // skinbld.exe runs the codec at the default 53-bit x87 precision, unlike
            // bundler.exe which pins _PC_24 (float). Keep the skin's shipped bytes by
            // evaluating the scale-and-dither expression in full double precision.
            bundler.FullPrecisionF2I = true;
            bundler.Initialize([.. args]);
            bundler.Process();

            return File.ReadAllBytes(xprPath);
        }
        finally
        {
            if (!keepRdf)
            {
                Delete(rdfPath);
                Delete(xprPath);
                Delete(Path.ChangeExtension(rdfPath, ".h"));
            }
        }
    }

    private static string BuildRdf(List<SkinImage> images)
    {
        var rdf = new StringBuilder();
        foreach (var image in images)
        {
            rdf.Append("Texture ").Append(image.Name).Append(" \n{\n");
            rdf.Append("   Source ").Append(InxParser.ToNativePath(image.Source)).Append('\n');
            if (image.AlphaSource is not null)
                rdf.Append("   AlphaSource ").Append(InxParser.ToNativePath(image.AlphaSource)).Append('\n');
            rdf.Append("   Levels 1\n");
            rdf.Append("   Format ").Append(image.Format).Append('\n');
            rdf.Append("}\n");
        }
        return rdf.ToString();
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary is not worth failing the build over.
        }
    }
}
