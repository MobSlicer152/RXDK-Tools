// Port of the XDK bundler's CVolumeTexture (volumetexture.cpp): Depth slices,
// each loaded + resized to Width x Height, laid out as one 3D-swizzled volume
// behind a volume-flagged D3DTexture header.
//
// The .rdf spells the slices as a repeated Source (with an optional AlphaSource
// after each), so the two arrive as parallel lists in file order and slice N is
// the Nth Source. The XDK reads them the same way, which is why Depth is
// declared separately rather than inferred: a mismatch between the two is the
// author's mistake and is worth reporting rather than silently padding.

namespace Rxdk.Bundler;

internal sealed class VolumeTexture : BaseTexture, IResource
{
    public readonly List<string> Sources = new();
    public readonly List<string> AlphaSources = new();
    public uint Width, Height, Depth;

    private readonly List<CImage> _slices = new();

    public VolumeTexture(Bundler b) : base(b) { }

    public void SaveToBundle(out uint cbHeader, out uint cbData)
    {
        LoadVolume();

        B.PadToAlignment(XboxFormats.D3DTEXTURE_ALIGNMENT);
        cbHeader = SaveHeaderInfo(B.CbData);
        cbData = SaveVolume();
    }

    private void LoadVolume()
    {
        // Format defaults to A8R8G8B8 when the .rdf omits it, the same as a 2D
        // texture -- VolumeSprites relies on that default.
        if (!string.IsNullOrEmpty(FormatName))
        {
            FormatIndex = XboxFormats.FormatFromString(FormatName);
            if (FormatIndex < 0)
                throw new BundlerException($"Invalid texture format: {FormatName}");
        }

        if (FormatIndex < 0)
        {
            FormatName = "D3DFMT_A8R8G8B8";
            FormatIndex = XboxFormats.FormatFromString(FormatName);
        }

        FormatName = Spec.Name;

        if (Spec.Type == FmtType.Compressed)
            throw new BundlerException("Volume textures cannot have compressed formats");

        if (Sources.Count == 0)
            throw new BundlerException("Volume texture has no Source slices");

        // Depth defaults to however many slices were listed.
        if (Depth == 0)
            Depth = (uint)Sources.Count;

        if (Sources.Count != Depth)
            throw new BundlerException(
                $"Volume texture declares Depth {Depth} but lists {Sources.Count} Source slice(s)");

        for (int i = 0; i < Sources.Count; i++)
        {
            string alpha = i < AlphaSources.Count ? AlphaSources[i] : "";
            _slices.Add(LoadImage(Sources[i], alpha, B.PathPrefix));
        }

        // Width/Height default to the first slice rounded down to a power of two,
        // the same way a 2D texture defaults from its single source.
        if (Width == 0)
            for (Width = 1; Width < _slices[0].Width; Width <<= 1) { }
        if (Height == 0)
            for (Height = 1; Height < _slices[0].Height; Height <<= 1) { }
        if (Width > 4096) Width = 4096;
        if (Height > 4096) Height = 4096;

        uint maxLevels = 1;
        while ((1u << (int)(maxLevels - 1)) < Math.Max(Math.Max(Width, Height), Depth)) maxLevels++;
        if (Levels < 1 || Levels > maxLevels) Levels = maxLevels;

        for (int i = 0; i < _slices.Count; i++)
            _slices[i] = ResizeImage(Width, Height, _slices[i]);
    }

    //
    // Mip chain for a volume. Unlike SaveImage's 2D chain this has to halve the
    // depth as well, and a volume mip is built from TWO source slices averaged
    // together -- dropping every other slice instead would alias along w in a
    // way a 2D mip never does, because the depth axis is filtered at sample time
    // just like u and v.
    //
    private uint SaveVolume()
    {
        uint width = Width, height = Height, depth = Depth;
        uint written = 0;

        // Level 0 is the loaded slices; deeper levels are produced from the
        // previous level, so keep the working set as raw A8R8G8B8.
        var level = new List<byte[]>();
        for (int i = 0; i < _slices.Count; i++)
        {
            var mip = new CImage(Width, Height, _slices[i].Format);
            CImage.Blt(mip, _slices[i], Filter, 0);
            level.Add(mip.Data);
        }

        var surface = new byte[Width * Height * Depth * 4];

        for (uint l = 0; l < Levels; l++)
        {
            // Pack the level's slices into one tightly-packed volume.
            uint sliceTexels = width * height;
            var volume = new byte[sliceTexels * depth * 4];
            for (uint z = 0; z < depth; z++)
                Array.Copy(level[(int)z], 0, volume, z * sliceTexels * 4, sliceTexels * 4);

            ConvertTextureFormat(volume, width, height, depth, surface, Spec.XboxFormat);
            if (Spec.Type == FmtType.Swizzled)
                written += WriteSwizzledTextureData(surface, width, height, depth);
            else
                written += WriteLinearTextureData(surface, width, height, depth);

            if (l + 1 >= Levels)
                break;

            level = BuildNextLevel(level, ref width, ref height, ref depth);
        }

        return written;
    }

    //
    // Box-filter one level down in all three axes.
    //
    private static List<byte[]> BuildNextLevel(List<byte[]> level, ref uint width, ref uint height, ref uint depth)
    {
        uint newWidth  = width  >= 2 ? width  >> 1 : width;
        uint newHeight = height >= 2 ? height >> 1 : height;
        uint newDepth  = depth  >= 2 ? depth  >> 1 : depth;

        var next = new List<byte[]>((int)newDepth);

        for (uint z = 0; z < newDepth; z++)
        {
            // The two slices this one averages. When depth has bottomed out at 1
            // both indices land on the same slice, which degenerates to a plain
            // 2D box filter -- correct, and what a 1-deep volume should do.
            byte[] near = level[(int)(depth >= 2 ? z * 2 : z)];
            byte[] far  = level[(int)(depth >= 2 ? z * 2 + 1 : z)];

            var dst = new byte[newWidth * newHeight * 4];

            for (uint y = 0; y < newHeight; y++)
            {
                uint y0 = height >= 2 ? y * 2 : y;
                uint y1 = height >= 2 ? y * 2 + 1 : y;

                for (uint x = 0; x < newWidth; x++)
                {
                    uint x0 = width >= 2 ? x * 2 : x;
                    uint x1 = width >= 2 ? x * 2 + 1 : x;

                    for (uint c = 0; c < 4; c++)
                    {
                        uint sum =
                            (uint)near[(y0 * width + x0) * 4 + c] +
                            (uint)near[(y0 * width + x1) * 4 + c] +
                            (uint)near[(y1 * width + x0) * 4 + c] +
                            (uint)near[(y1 * width + x1) * 4 + c] +
                            (uint)far [(y0 * width + x0) * 4 + c] +
                            (uint)far [(y0 * width + x1) * 4 + c] +
                            (uint)far [(y1 * width + x0) * 4 + c] +
                            (uint)far [(y1 * width + x1) * 4 + c];

                        dst[(y * newWidth + x) * 4 + c] = (byte)(sum / 8);
                    }
                }
            }

            next.Add(dst);
        }

        width  = newWidth;
        height = newHeight;
        depth  = newDepth;
        return next;
    }

    // XGSetVolumeTextureHeader -> D3DTexture {Common,Data,Lock,Format,Size} with
    // the volume flag. Size is left zero: EncodeFormat only fills the packed size
    // word for a non-mipped 2D surface, and a volume never qualifies.
    private uint SaveHeaderInfo(uint dwStart)
    {
        XboxFormats.EncodeFormat(Width, Height, Depth, Levels, Spec.XboxFormat, 0, false, true,
                                 out uint format, out uint size);

        uint common = 1u | XboxFormats.D3DCOMMON_TYPE_TEXTURE | XboxFormats.D3DCOMMON_VIDEOMEMORY;

        Span<byte> hdr = stackalloc byte[20];
        BitConverter.TryWriteBytes(hdr.Slice(0, 4), common);
        BitConverter.TryWriteBytes(hdr.Slice(4, 4), dwStart);
        BitConverter.TryWriteBytes(hdr.Slice(8, 4), 0u);
        BitConverter.TryWriteBytes(hdr.Slice(12, 4), format);
        BitConverter.TryWriteBytes(hdr.Slice(16, 4), size);
        B.WriteHeader(hdr);
        return 20;
    }
}
