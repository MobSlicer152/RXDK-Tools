// Port of the XDK bundler's CCubemap (cubemap.cpp): six faces (XP,XN,YP,YN,ZP,ZN),
// each loaded + resized to Size x Size, laid out swizzled/compressed like a texture,
// with a cube-flagged D3DTexture header from XGSetCubeTextureHeader.

namespace Rxdk.Bundler;

internal sealed class Cubemap : BaseTexture, IResource
{
    public string SourceXP = "", SourceXN = "", SourceYP = "", SourceYN = "", SourceZP = "", SourceZN = "";
    public string AlphaXP = "", AlphaXN = "", AlphaYP = "", AlphaYN = "", AlphaZP = "", AlphaZN = "";
    public uint Size;

    private CImage _xp = null!, _xn = null!, _yp = null!, _yn = null!, _zp = null!, _zn = null!;

    public Cubemap(Bundler b) : base(b) { }

    public void SaveToBundle(out uint cbHeader, out uint cbData)
    {
        LoadCubemap();

        B.PadToAlignment(XboxFormats.D3DTEXTURE_ALIGNMENT);
        cbHeader = SaveHeaderInfo(B.CbData);

        cbData = 0;
        cbData += SaveImage(Levels, _xp);
        cbData += SaveImage(Levels, _xn);
        cbData += SaveImage(Levels, _yp);
        cbData += SaveImage(Levels, _yn);
        cbData += SaveImage(Levels, _zp);
        cbData += SaveImage(Levels, _zn);
    }

    private void LoadCubemap()
    {
        FormatIndex = XboxFormats.FormatFromString(FormatName);
        if (FormatIndex < 0)
            throw new BundlerException($"Invalid texture format: {FormatName}");
        FormatName = Spec.Name;

        if (Spec.Type == FmtType.Linear)
            throw new BundlerException("Cubemaps cannot have linear formats");

        _xp = LoadImage(SourceXP, AlphaXP, B.PathPrefix);
        _xn = LoadImage(SourceXN, AlphaXN, B.PathPrefix);
        _yp = LoadImage(SourceYP, AlphaYP, B.PathPrefix);
        _yn = LoadImage(SourceYN, AlphaYN, B.PathPrefix);
        _zp = LoadImage(SourceZP, AlphaZP, B.PathPrefix);
        _zn = LoadImage(SourceZN, AlphaZN, B.PathPrefix);

        if (Size == 0)
            for (Size = 1; Size < _xp.Width; Size <<= 1) { }
        if (Size > 4096) Size = 4096;

        // Default level count (cubemap.cpp): defaults to 1 unless Levels overrides.
        uint maxLevels = 1;
        while ((1u << (int)(maxLevels - 1)) < Size) maxLevels++;
        if (Levels < 1 || Levels > maxLevels) Levels = maxLevels;

        _xp = ResizeImage(Size, Size, _xp);
        _xn = ResizeImage(Size, Size, _xn);
        _yp = ResizeImage(Size, Size, _yp);
        _yn = ResizeImage(Size, Size, _yn);
        _zp = ResizeImage(Size, Size, _zp);
        _zn = ResizeImage(Size, Size, _zn);
    }

    // XGSetCubeTextureHeader -> D3DTexture {Common,Data,Lock,Format,Size} with the cube flag.
    private uint SaveHeaderInfo(uint dwStart)
    {
        XboxFormats.EncodeFormat(Size, Size, 1, Levels, Spec.XboxFormat, 0, true, false,
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
