// D3DX filter flags and the PC-side D3DFORMAT ids used by the image loader and
// blitter (d3d8types.h / d3dx8.h). These are the Win32 D3D8 values the original
// bundler compiled against; CImage stores a source surface in one of these
// formats and the blitter converts to A8R8G8B8.

namespace Rxdk.Bundler;

internal static class D3DX
{
    public const uint DEFAULT = 0xFFFFFFFF;

    public const uint FILTER_NONE = 1u << 0;
    public const uint FILTER_POINT = 2u << 0;
    public const uint FILTER_LINEAR = 3u << 0;
    public const uint FILTER_TRIANGLE = 4u << 0;
    public const uint FILTER_BOX = 5u << 0;

    public const uint FILTER_MIRROR_U = 1u << 16;
    public const uint FILTER_MIRROR_V = 2u << 16;
    public const uint FILTER_MIRROR_W = 4u << 16;
    public const uint FILTER_MIRROR = 7u << 16;
    public const uint FILTER_DITHER = 1u << 19;
}

/// <summary>PC (Win32) D3DFORMAT ids — the CImage source-surface formats.</summary>
internal static class D3DFmt
{
    public const uint UNKNOWN = 0;
    public const uint R8G8B8 = 20;
    public const uint A8R8G8B8 = 21;
    public const uint X8R8G8B8 = 22;
    public const uint R5G6B5 = 23;
    public const uint X1R5G5B5 = 24;
    public const uint A1R5G5B5 = 25;
    public const uint A4R4G4B4 = 26;
    public const uint R3G3B2 = 27;
    public const uint A8 = 28;
    public const uint A8R3G3B2 = 29;
    public const uint X4R4G4B4 = 30;
    public const uint A8P8 = 40;
    public const uint P8 = 41;
    public const uint L8 = 50;
    public const uint A8L8 = 51;
    public const uint A4L4 = 52;
    public const uint V8U8 = 60;
    public const uint L6V5U5 = 61;
    public const uint X8L8V8U8 = 62;
    public const uint Q8W8V8U8 = 63;
    public const uint V16U16 = 64;
    public const uint W11V11U10 = 65;

    // MAKEFOURCC values (d3d8types.h)
    public const uint UYVY = 0x59565955; // 'UYVY'
    public const uint YUY2 = 0x32595559; // 'YUY2'
    public const uint DXT1 = 0x31545844; // 'DXT1'
    public const uint DXT2 = 0x32545844; // 'DXT2'
    public const uint DXT3 = 0x33545844; // 'DXT3'
    public const uint DXT4 = 0x34545844; // 'DXT4'
    public const uint DXT5 = 0x35545844; // 'DXT5'
}
