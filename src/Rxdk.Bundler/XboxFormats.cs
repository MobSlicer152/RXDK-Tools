// Xbox D3D texture-format encoding for the bundler.
//
// This is a faithful C# port of the Xbox XGRAPHICS texture-header logic
// (libxgraphics/misc/header.cpp: EncodeFormat / EncodeTexture / XGSetTextureHeader)
// plus the format tables from the XDK bundler (basetexture.h/.cpp and
// libxgraphics/misc/header.h). It produces the packed D3DTexture header
// (Common/Data/Lock/Format/Size) that the runtime CXBPackedResource loader and
// the RXDK d3d8 runtime expect. Kept close to the original source so the output
// is bit-for-bit compatible with what the RXDK runtime consumes.

namespace Rxdk.Bundler;

/// <summary>Kind of a texture format, mirroring the bundler's FMT_TYPE enum.</summary>
internal enum FmtType
{
    Linear = 0,     // FMT_LINEAR
    Swizzled = 1,   // FMT_SWIZZLED
    Compressed = 2, // FMT_COMPRESSED
}

/// <summary>A named source format the .rdf can request (mirrors FORMATSPEC / g_TextureFormats).</summary>
internal readonly record struct FormatSpec(string Name, uint XboxFormat, FmtType Type, uint AlphaBits);

internal static class XboxFormats
{
    // --- Xbox packed-texture format ids (basetexture.h: X_D3DFMT_*) -----------
    public const uint X_D3DFMT_A8R8G8B8 = 0x06;
    public const uint X_D3DFMT_X8R8G8B8 = 0x07;
    public const uint X_D3DFMT_R5G6B5 = 0x05;
    public const uint X_D3DFMT_R6G5B5 = 0x27;
    public const uint X_D3DFMT_X1R5G5B5 = 0x03;
    public const uint X_D3DFMT_A1R5G5B5 = 0x02;
    public const uint X_D3DFMT_A4R4G4B4 = 0x04;
    public const uint X_D3DFMT_A8 = 0x19;
    public const uint X_D3DFMT_A8B8G8R8 = 0x3A;
    public const uint X_D3DFMT_B8G8R8A8 = 0x3B;
    public const uint X_D3DFMT_R4G4B4A4 = 0x39;
    public const uint X_D3DFMT_R5G5B5A1 = 0x38;
    public const uint X_D3DFMT_R8G8B8A8 = 0x3C;
    public const uint X_D3DFMT_R8B8 = 0x29;
    public const uint X_D3DFMT_G8B8 = 0x28;
    public const uint X_D3DFMT_P8 = 0x0B;
    public const uint X_D3DFMT_L8 = 0x00;
    public const uint X_D3DFMT_A8L8 = 0x1A;
    public const uint X_D3DFMT_AL8 = 0x01;
    public const uint X_D3DFMT_L16 = 0x32;
    public const uint X_D3DFMT_V8U8 = 0x28;
    public const uint X_D3DFMT_L6V5U5 = 0x27;
    public const uint X_D3DFMT_X8L8V8U8 = 0x07;
    public const uint X_D3DFMT_Q8W8V8U8 = 0x3A;
    public const uint X_D3DFMT_V16U16 = 0x33;
    public const uint X_D3DFMT_DXT1 = 0x0C;
    public const uint X_D3DFMT_DXT2 = 0x0E;
    public const uint X_D3DFMT_DXT3 = 0x0E;
    public const uint X_D3DFMT_DXT4 = 0x0F;
    public const uint X_D3DFMT_DXT5 = 0x0F;
    public const uint X_D3DFMT_LIN_A1R5G5B5 = 0x10;
    public const uint X_D3DFMT_LIN_A4R4G4B4 = 0x1D;
    public const uint X_D3DFMT_LIN_A8 = 0x1F;
    public const uint X_D3DFMT_LIN_A8B8G8R8 = 0x3F;
    public const uint X_D3DFMT_LIN_A8R8G8B8 = 0x12;
    public const uint X_D3DFMT_LIN_B8G8R8A8 = 0x40;
    public const uint X_D3DFMT_LIN_G8B8 = 0x17;
    public const uint X_D3DFMT_LIN_R4G4B4A4 = 0x3E;
    public const uint X_D3DFMT_LIN_R5G5B5A1 = 0x3D;
    public const uint X_D3DFMT_LIN_R5G6B5 = 0x11;
    public const uint X_D3DFMT_LIN_R6G5B5 = 0x37;
    public const uint X_D3DFMT_LIN_R8B8 = 0x16;
    public const uint X_D3DFMT_LIN_R8G8B8A8 = 0x41;
    public const uint X_D3DFMT_LIN_X1R5G5B5 = 0x1C;
    public const uint X_D3DFMT_LIN_X8R8G8B8 = 0x1E;
    public const uint X_D3DFMT_LIN_A8L8 = 0x20;
    public const uint X_D3DFMT_LIN_AL8 = 0x1B;
    public const uint X_D3DFMT_LIN_L16 = 0x35;
    public const uint X_D3DFMT_LIN_L8 = 0x13;
    public const uint X_D3DFMT_LIN_V16U16 = 0x36;
    public const uint X_D3DFMT_LIN_V8U8 = 0x17;
    public const uint X_D3DFMT_LIN_L6V5U5 = 0x37;
    public const uint X_D3DFMT_LIN_X8L8V8U8 = 0x1E;
    public const uint X_D3DFMT_LIN_Q8W8V8U8 = 0x12;

    // --- Format packing constants (basetexture.h / shared d3d8.h) -------------
    public const uint D3DFORMAT_DMACHANNEL_A = 0x00000001;
    // Retail/XDK xgraphics EncodeFormat tags pre-baked resources with DMA channel B
    // (verified byte-for-byte against the XDK's prebuilt .xpr golden files). This
    // differs from RXDK-Libs' own header.cpp, which uses channel A; the packed-resource
    // loader consumes the stored header verbatim, so we match the retail output.
    public const uint D3DFORMAT_DMACHANNEL_B = 0x00000002;

    // Microsoft shipped .xpr files tagged both ways: the sample media the port was
    // validated against uses channel B, while the 5849 bundler.exe binary emits
    // channel A. The bit selects a pusher DMA context and is ignored by the
    // packed-resource loader, so it only matters when reproducing a specific tool's
    // bytes -- skinbld sets channel A to match the skins 5849 produces.
    public static uint DmaChannel = D3DFORMAT_DMACHANNEL_B;

    public const uint D3DFORMAT_CUBEMAP = 0x00000004;
    public const uint D3DFORMAT_BORDERSOURCE_COLOR = 0x00000008;
    public const int D3DFORMAT_DIMENSION_SHIFT = 4;
    public const int D3DFORMAT_FORMAT_SHIFT = 8;
    public const int D3DFORMAT_MIPMAP_SHIFT = 16;
    public const int D3DFORMAT_USIZE_SHIFT = 20;
    public const int D3DFORMAT_VSIZE_SHIFT = 24;
    public const int D3DFORMAT_PSIZE_SHIFT = 28;

    public const uint D3DSIZE_WIDTH_MASK = 0x00000FFF;
    public const uint D3DSIZE_HEIGHT_MASK = 0x00FFF000;
    public const int D3DSIZE_HEIGHT_SHIFT = 12;
    public const uint D3DSIZE_PITCH_MASK = 0xFF000000;
    public const int D3DSIZE_PITCH_SHIFT = 24;

    // Common field (header.cpp uses D3DCOMMON_VIDEOMEMORY, which is 0 in the
    // shared d3d8.h — matches the golden .xpr Common == 0x00040001).
    public const uint D3DCOMMON_TYPE_TEXTURE = 0x00040000;
    public const uint D3DCOMMON_TYPE_VERTEXBUFFER = 0x00000000;
    public const uint D3DCOMMON_TYPE_INDEXBUFFER = 0x00010000;
    public const uint D3DCOMMON_VIDEOMEMORY = 0;

    public const uint D3DTEXTURE_ALIGNMENT = 128;
    public const uint D3DTEXTURE_CUBEFACE_ALIGNMENT = 128;
    public const uint D3DTEXTURE_PITCH_ALIGNMENT = 64;

    // header.h: bits-per-pixel / type flags, indexed by Xbox format id 0x00-0x41.
    private const byte FMT_DEPTHBUFFER = 0x40;
    private const byte FMT_BITSPERPIXEL = 0x3c;
    private const byte FMT_LINEAR = 0x02;
    private const byte FMT_SWIZZLED = 0x01;
    private const byte B32 = 0x20, B16 = 0x10, B8 = 0x08, B4 = 0x04;
    private const byte RT = 0x80, DB = 0x40, SW = FMT_SWIZZLED, LN = FMT_LINEAR;

    // g_TextureFormat[] from header.h — one byte per Xbox format id.
    private static readonly byte[] g_TextureFormat =
    {
        /*00 L8      */ (byte)(B8 | SW),
        /*01 AL8     */ (byte)(B8 | SW),
        /*02 A1R5G5B5*/ (byte)(B16 | SW),
        /*03 X1R5G5B5*/ (byte)(B16 | RT | SW),
        /*04 A4R4G4B4*/ (byte)(B16 | SW),
        /*05 R5G6B5  */ (byte)(B16 | RT | SW),
        /*06 A8R8G8B8*/ (byte)(B32 | RT | SW),
        /*07 X8R8G8B8*/ (byte)(B32 | RT | SW),
        /*08*/ 0, /*09*/ 0, /*0A*/ 0,
        /*0B P8      */ (byte)(B8 | SW),
        /*0C DXT1    */ B4,
        /*0D*/ 0,
        /*0E DXT2/3  */ B8,
        /*0F DXT4/5  */ B8,
        /*10 LIN_A1R5G5B5*/ (byte)(B16 | LN),
        /*11 LIN_R5G6B5  */ (byte)(B16 | RT | LN),
        /*12 LIN_A8R8G8B8*/ (byte)(B32 | RT | LN),
        /*13 LIN_L8      */ (byte)(B8 | LN),
        /*14*/ 0, /*15*/ 0,
        /*16 LIN_R8B8*/ (byte)(B16 | LN),
        /*17 LIN_G8B8*/ (byte)(B16 | LN),
        /*18*/ 0,
        /*19 A8      */ (byte)(B8 | SW),
        /*1A A8L8    */ (byte)(B16 | SW),
        /*1B LIN_AL8 */ (byte)(B8 | LN),
        /*1C LIN_X1R5G5B5*/ (byte)(B16 | RT | LN),
        /*1D LIN_A4R4G4B4*/ (byte)(B16 | LN),
        /*1E LIN_X8R8G8B8*/ (byte)(B32 | RT | LN),
        /*1F LIN_A8   */ (byte)(B8 | LN),
        /*20 LIN_A8L8 */ (byte)(B16 | LN),
        /*21*/ 0, /*22*/ 0, /*23*/ 0,
        /*24 UYVY */ B32,
        /*25 YUY2 */ B32,
        /*26*/ 0,
        /*27 R6G5B5/L6V5U5*/ (byte)(B16 | SW),
        /*28 G8B8/V8U8    */ (byte)(B16 | SW),
        /*29 R8B8         */ (byte)(B16 | SW),
        /*2A D24S8*/ (byte)(B32 | DB | SW),
        /*2B F24S8*/ (byte)(B32 | DB | SW),
        /*2C D16  */ (byte)(B16 | DB | SW),
        /*2D F16  */ (byte)(B16 | DB | SW),
        /*2E LIN_D24S8*/ (byte)(B32 | DB | LN),
        /*2F LIN_F24S8*/ (byte)(B32 | DB | LN),
        /*30 LIN_D16  */ (byte)(B16 | DB | LN),
        /*31 LIN_F16  */ (byte)(B16 | DB | LN),
        /*32 L16      */ (byte)(B16 | SW),
        /*33 V16U16   */ (byte)(B32 | SW),
        /*34*/ 0,
        /*35 LIN_L16  */ (byte)(B16 | LN),
        /*36*/ 0,
        /*37 LIN_R6G5B5*/ (byte)(B16 | LN),
        /*38 R5G5B5A1 */ (byte)(B16 | SW),
        /*39 R4G4B4A4 */ (byte)(B16 | SW),
        /*3A A8B8G8R8/Q8W8V8U8*/ (byte)(B32 | SW),
        /*3B B8G8R8A8 */ (byte)(B32 | SW),
        /*3C R8G8B8A8 */ (byte)(B32 | SW),
        /*3D LIN_R5G5B5A1*/ (byte)(B16 | LN),
        /*3E LIN_R4G4B4A4*/ (byte)(B16 | LN),
        /*3F LIN_A8B8G8R8*/ (byte)(B32 | LN),
        /*40 LIN_B8G8R8A8*/ (byte)(B32 | LN),
        /*41 LIN_R8G8B8A8*/ (byte)(B32 | LN),
    };

    // g_TextureFormats[] from basetexture.cpp — the format names a .rdf may use.
    public static readonly FormatSpec[] TextureFormats =
    {
        new("D3DFMT_A8R8G8B8", X_D3DFMT_A8R8G8B8, FmtType.Swizzled, 8),
        new("D3DFMT_X8R8G8B8", X_D3DFMT_X8R8G8B8, FmtType.Swizzled, 8),
        new("D3DFMT_A8B8G8R8", X_D3DFMT_A8B8G8R8, FmtType.Swizzled, 8),
        new("D3DFMT_B8G8R8A8", X_D3DFMT_B8G8R8A8, FmtType.Swizzled, 8),
        new("D3DFMT_R8G8B8A8", X_D3DFMT_R8G8B8A8, FmtType.Swizzled, 8),
        new("D3DFMT_X8L8V8U8", X_D3DFMT_X8L8V8U8, FmtType.Swizzled, 0),
        new("D3DFMT_Q8W8V8U8", X_D3DFMT_Q8W8V8U8, FmtType.Swizzled, 0),
        new("D3DFMT_V16U16", X_D3DFMT_V16U16, FmtType.Swizzled, 0),
        new("D3DFMT_A4R4G4B4", X_D3DFMT_A4R4G4B4, FmtType.Swizzled, 4),
        new("D3DFMT_R4G4B4A4", X_D3DFMT_R4G4B4A4, FmtType.Swizzled, 4),
        new("D3DFMT_X1R5G5B5", X_D3DFMT_X1R5G5B5, FmtType.Swizzled, 0),
        new("D3DFMT_A1R5G5B5", X_D3DFMT_A1R5G5B5, FmtType.Swizzled, 1),
        new("D3DFMT_R5G5B5A1", X_D3DFMT_R5G5B5A1, FmtType.Swizzled, 1),
        new("D3DFMT_R5G6B5", X_D3DFMT_R5G6B5, FmtType.Swizzled, 0),
        new("D3DFMT_R6G5B5", X_D3DFMT_R6G5B5, FmtType.Swizzled, 0),
        new("D3DFMT_L6V5U5", X_D3DFMT_L6V5U5, FmtType.Swizzled, 0),
        new("D3DFMT_R8B8", X_D3DFMT_R8B8, FmtType.Swizzled, 0),
        new("D3DFMT_G8B8", X_D3DFMT_G8B8, FmtType.Swizzled, 0),
        new("D3DFMT_V8U8", X_D3DFMT_V8U8, FmtType.Swizzled, 0),
        new("D3DFMT_A8L8", X_D3DFMT_A8L8, FmtType.Swizzled, 8),
        new("D3DFMT_AL8", X_D3DFMT_AL8, FmtType.Swizzled, 8),
        new("D3DFMT_A8", X_D3DFMT_A8, FmtType.Swizzled, 8),
        new("D3DFMT_L8", X_D3DFMT_L8, FmtType.Swizzled, 0),
        new("D3DFMT_L16", X_D3DFMT_L16, FmtType.Swizzled, 0),
        new("D3DFMT_DXT1", X_D3DFMT_DXT1, FmtType.Compressed, 1),
        new("D3DFMT_DXT2", X_D3DFMT_DXT2, FmtType.Compressed, 8),
        new("D3DFMT_DXT3", X_D3DFMT_DXT3, FmtType.Compressed, 8),
        new("D3DFMT_DXT4", X_D3DFMT_DXT4, FmtType.Compressed, 8),
        new("D3DFMT_DXT5", X_D3DFMT_DXT5, FmtType.Compressed, 8),
        new("D3DFMT_LIN_A8B8G8R8", X_D3DFMT_LIN_A8B8G8R8, FmtType.Linear, 8),
        new("D3DFMT_LIN_A8R8G8B8", X_D3DFMT_LIN_A8R8G8B8, FmtType.Linear, 8),
        new("D3DFMT_LIN_B8G8R8A8", X_D3DFMT_LIN_B8G8R8A8, FmtType.Linear, 8),
        new("D3DFMT_LIN_R8G8B8A8", X_D3DFMT_LIN_R8G8B8A8, FmtType.Linear, 8),
        new("D3DFMT_LIN_X8R8G8B8", X_D3DFMT_LIN_X8R8G8B8, FmtType.Linear, 0),
        new("D3DFMT_LIN_X8L8V8U8", X_D3DFMT_LIN_X8L8V8U8, FmtType.Linear, 0),
        new("D3DFMT_LIN_Q8W8V8U8", X_D3DFMT_LIN_Q8W8V8U8, FmtType.Linear, 0),
        new("D3DFMT_LIN_V16U16", X_D3DFMT_LIN_V16U16, FmtType.Linear, 0),
        new("D3DFMT_LIN_A4R4G4B4", X_D3DFMT_LIN_A4R4G4B4, FmtType.Linear, 4),
        new("D3DFMT_LIN_R4G4B4A4", X_D3DFMT_LIN_R4G4B4A4, FmtType.Linear, 4),
        new("D3DFMT_LIN_A1R5G5B5", X_D3DFMT_LIN_A1R5G5B5, FmtType.Linear, 1),
        new("D3DFMT_LIN_R5G5B5A1", X_D3DFMT_LIN_R5G5B5A1, FmtType.Linear, 1),
        new("D3DFMT_LIN_X1R5G5B5", X_D3DFMT_LIN_X1R5G5B5, FmtType.Linear, 0),
        new("D3DFMT_LIN_R5G6B5", X_D3DFMT_LIN_R5G6B5, FmtType.Linear, 0),
        new("D3DFMT_LIN_R6G5B5", X_D3DFMT_LIN_R6G5B5, FmtType.Linear, 0),
        new("D3DFMT_LIN_L6V5U5", X_D3DFMT_LIN_L6V5U5, FmtType.Linear, 0),
        new("D3DFMT_LIN_G8B8", X_D3DFMT_LIN_G8B8, FmtType.Linear, 0),
        new("D3DFMT_LIN_R8B8", X_D3DFMT_LIN_R8B8, FmtType.Linear, 0),
        new("D3DFMT_LIN_A8L8", X_D3DFMT_LIN_A8L8, FmtType.Linear, 8),
        new("D3DFMT_LIN_V8U8", X_D3DFMT_LIN_V8U8, FmtType.Linear, 0),
        new("D3DFMT_LIN_AL8", X_D3DFMT_LIN_AL8, FmtType.Linear, 8),
        new("D3DFMT_LIN_L16", X_D3DFMT_LIN_L16, FmtType.Linear, 0),
        new("D3DFMT_LIN_L8", X_D3DFMT_LIN_L8, FmtType.Linear, 0),
        new("D3DFMT_LIN_A8", X_D3DFMT_LIN_A8, FmtType.Linear, 8),
    };

    /// <summary>Look up a format name (case-insensitive) → index into <see cref="TextureFormats"/>, or -1.</summary>
    public static int FormatFromString(string name)
    {
        for (int i = 0; i < TextureFormats.Length; i++)
            if (string.Equals(TextureFormats[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // --- header.h helpers -----------------------------------------------------
    public static uint BitsPerPixelOfD3DFORMAT(uint format) => (uint)(g_TextureFormat[format] & FMT_BITSPERPIXEL);
    public static bool IsSwizzledD3DFORMAT(uint format) => (g_TextureFormat[format] & FMT_SWIZZLED) != 0;

    public static bool IsCompressedD3DFORMAT(uint format) =>
        format is X_D3DFMT_DXT1 or X_D3DFMT_DXT2 or X_D3DFMT_DXT4;

    public static bool IsValidDepthBufferD3DFORMAT(uint format) => (g_TextureFormat[format] & FMT_DEPTHBUFFER) != 0;

    // XGBytesPerPixelFromFormat: bits-per-pixel / 8 (valid for the 8/16/32bpp
    // formats that hit the linear/swizzled write paths).
    public static uint BytesPerPixelFromFormat(uint format) => BitsPerPixelOfD3DFORMAT(format) / 8;

    /// <summary>bsf: index of the lowest set bit. For power-of-2 inputs this is log2.</summary>
    public static uint Log2(uint value) => (uint)System.Numerics.BitOperations.TrailingZeroCount(value);

    public static uint MinimumTextureSizeOfD3DFORMAT(uint format) => IsCompressedD3DFORMAT(format) ? 2u : 0u;

    public static uint CalcPitch(uint width, uint texelSize) =>
        (width * texelSize / 8 + D3DTEXTURE_PITCH_ALIGNMENT - 1) & ~(D3DTEXTURE_PITCH_ALIGNMENT - 1);

    public static uint PitchFromSize(uint size) =>
        (((size & D3DSIZE_PITCH_MASK) >> D3DSIZE_PITCH_SHIFT) + 1) * D3DTEXTURE_PITCH_ALIGNMENT;

    /// <summary>
    /// Port of XGRAPHICS::EncodeFormat. Fills <paramref name="format"/>/<paramref name="size"/>
    /// and returns the number of data bytes the texture occupies.
    /// </summary>
    public static uint EncodeFormat(uint width, uint height, uint depth, uint levels,
                                    uint d3dFormat, uint pitch, bool isCubeMap, bool isVolume,
                                    out uint format, out uint size)
    {
        uint Size = 0;
        uint texelSize = BitsPerPixelOfD3DFORMAT(d3dFormat);

        uint logWidth, logHeight, logDepth, sizeWidth, sizeHeight;

        if (IsSwizzledD3DFORMAT(d3dFormat) || IsCompressedD3DFORMAT(d3dFormat))
        {
            logWidth = Log2(width);
            logHeight = Log2(height);
            logDepth = Log2(depth);
            sizeWidth = 0;
            sizeHeight = 0;

            uint logMin = MinimumTextureSizeOfD3DFORMAT(d3dFormat);

            if (levels == 0)
                levels = Math.Max(logWidth, Math.Max(logHeight, logDepth)) + 1;

            uint currentWidth = logWidth;
            uint currentHeight = logHeight;
            uint currentDepth = logDepth;

            for (uint currentLevel = levels; currentLevel != 0; currentLevel--)
            {
                uint logSize = Math.Max(currentWidth, logMin) + Math.Max(currentHeight, logMin) + currentDepth;
                Size += (1u << (int)logSize) * texelSize / 8;

                if (currentWidth > 0) currentWidth--;
                if (currentHeight > 0) currentHeight--;
                if (currentDepth > 0) currentDepth--;
            }

            if (isCubeMap)
            {
                Size = (Size + D3DTEXTURE_CUBEFACE_ALIGNMENT - 1) & ~(D3DTEXTURE_CUBEFACE_ALIGNMENT - 1);
                Size *= 6;
            }
        }
        else
        {
            logWidth = logHeight = logDepth = 0;

            if (levels == 0)
                levels = 1;

            if (pitch == 0)
                pitch = CalcPitch(width, texelSize);

            sizeWidth = width;
            sizeHeight = height;

            Size = pitch * height;
        }

        format = (isCubeMap ? 0x00000004u : 0u)
               | ((isVolume ? 3u : 2u) << D3DFORMAT_DIMENSION_SHIFT)
               | (d3dFormat << D3DFORMAT_FORMAT_SHIFT)
               | (levels << D3DFORMAT_MIPMAP_SHIFT)
               | (logWidth << D3DFORMAT_USIZE_SHIFT)
               | (logHeight << D3DFORMAT_VSIZE_SHIFT)
               | (logDepth << D3DFORMAT_PSIZE_SHIFT)
               | DmaChannel
               | D3DFORMAT_BORDERSOURCE_COLOR;

        if (sizeWidth != 0)
        {
            size = (sizeWidth - 1)
                 | ((sizeHeight - 1) << D3DSIZE_HEIGHT_SHIFT)
                 | (((pitch / D3DTEXTURE_PITCH_ALIGNMENT) - 1) << D3DSIZE_PITCH_SHIFT);
        }
        else
        {
            size = 0;
        }

        return Size;
    }
}
