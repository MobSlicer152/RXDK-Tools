// Faithful, byte-exact C# port of the XDK Bundler tool's CD3DXCodec.h / CD3DXCodec.cpp.
//
// This is a line-for-line translation of the per-format pixel encode/decode used by
// the D3DX-free blitter (CXD3DXBlt). All arithmetic is single-precision float, all
// integer truncation is toward zero (matching the original's F2I with x87 RC=truncate),
// and every bit shift/mask is preserved exactly so that pixel output is bit-identical.
//
// C++ raw pointers are represented as a byte[] plus an int offset. Surface dwords are
// read/written little-endian (matching x86 *(UINT32*)ptr), which also matches how
// CImage stores A8R8G8B8 as B,G,R,A bytes.

namespace Rxdk.Bundler;

// D3DFMT type classes (CD3DXCodec.h)
internal static class CodecType
{
    public const uint CODEC_RGB = 0x01;
    public const uint CODEC_P = 0x02;
    public const uint CODEC_UV = 0x03;
    public const uint CODEC_ZS = 0x04;
}

/// <summary>The canonical intermediate pixel: four single-precision floats, order r,g,b,a.</summary>
internal struct D3DXCOLOR
{
    public float r;
    public float g;
    public float b;
    public float a;

    public D3DXCOLOR(float r, float g, float b, float a)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    // D3DXCOLOR(DWORD) constructor from d3dx8math.inl (used to build the color key).
    public D3DXCOLOR(uint dw)
    {
        const float f = 1.0f / 255.0f;
        r = f * (byte)(dw >> 16);
        g = f * (byte)(dw >> 8);
        b = f * (byte)(dw >> 0);
        a = f * (byte)(dw >> 24);
    }

    public static D3DXCOLOR operator +(D3DXCOLOR x, D3DXCOLOR y)
        => new D3DXCOLOR(x.r + y.r, x.g + y.g, x.b + y.b, x.a + y.a);

    public static D3DXCOLOR operator -(D3DXCOLOR x, D3DXCOLOR y)
        => new D3DXCOLOR(x.r - y.r, x.g - y.g, x.b - y.b, x.a - y.a);

    public static D3DXCOLOR operator *(D3DXCOLOR x, float f)
        => new D3DXCOLOR(x.r * f, x.g * f, x.b * f, x.a * f);

    public static bool operator ==(D3DXCOLOR x, D3DXCOLOR y)
        => x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a;

    public static bool operator !=(D3DXCOLOR x, D3DXCOLOR y)
        => !(x == y);

    public override bool Equals(object? obj) => obj is D3DXCOLOR c && this == c;

    public override int GetHashCode() => HashCode.Combine(r, g, b, a);
}

/// <summary>D3DBOX (d3d8types.h).</summary>
internal struct D3DBOX
{
    public uint Left, Top, Right, Bottom, Front, Back;
}

/// <summary>D3DX_BLT (CD3DXCodec.h). Surface pointer is a byte[] plus an int offset.</summary>
internal sealed class D3DX_BLT
{
    public byte[] pData = Array.Empty<byte>();
    public int dataOffset;
    public uint Format;

    public uint RowPitch;
    public uint SlicePitch;

    public D3DBOX Region;
    public D3DBOX SubRegion;

    public bool bDither;

    public uint ColorKey;
    public uint[]? pPalette; // 256 entries, each 0xAARRGGBB (PALETTEENTRY = peRed<<16|peGreen<<8|peBlue|peFlags<<24)
}

///////////////////////////////////////////////////////////////////////////
// CXD3DXCodec
///////////////////////////////////////////////////////////////////////////

internal class CXD3DXCodec
{
    // Dither tables (CD3DXCodec.cpp)
    private static readonly float[] g_fDitherOff =
    {
        0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f,
        0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f,
        0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f,
        0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f,
    };

    private static readonly float[] g_fDitherOn =
    {
        0.96875f, 0.46875f, 0.84375f, 0.34375f, 0.96875f, 0.46875f, 0.84375f, 0.34375f,
        0.21875f, 0.71875f, 0.09375f, 0.59375f, 0.21875f, 0.71875f, 0.09375f, 0.59375f,
        0.78125f, 0.28125f, 0.90625f, 0.40625f, 0.78125f, 0.28125f, 0.90625f, 0.40625f,
        0.03125f, 0.53125f, 0.15625f, 0.65625f, 0.03125f, 0.53125f, 0.15625f, 0.65625f,
    };

    public uint m_Format;
    public uint m_dwType;
    public bool m_bLinear;
    public bool m_bColorKey;
    public bool m_bPalettized;

    public byte[] m_pbData = Array.Empty<byte>();
    public int m_pbOffset;
    public D3DXCOLOR m_ColorKey;
    public float[] m_pfDither = g_fDitherOff;
    public D3DXCOLOR[] m_pPalette = new D3DXCOLOR[256];
    public D3DBOX m_Box;

    public uint m_uPitch;
    public uint m_uSlice;
    public uint m_uWidth;
    public uint m_uHeight;
    public uint m_uDepth;
    public uint m_uWidthBytes;
    public uint m_uBytesPerPixel;

    // ---- Fast FLOAT->INT (truncate toward zero, matching F2IBegin RC=CLAMP + fistp) ----
    //
    // The argument is double even though every caller feeds it a float expression.
    // The original ran with the x87 precision control pinned to 24 bits, so its
    // stored floats are what a C# float holds - but the scale-and-dither
    // expression handed to F2I never reached memory, so it kept register width.
    // Rounding it to float first would round ties like 151.5 the wrong way.
    public static int F2I(double f) => (int)f;

    // ---- Little-endian surface access ----
    protected static ushort R16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    protected static void W16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    protected static uint R32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    protected static void W32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

    public static CXD3DXCodec? Create(D3DX_BLT pBlt)
    {
        switch (pBlt.Format)
        {
            // NOTE: the #if 0 formats (R8G8B8, R3G3B2, A8R3G3B2, X4R4G4B4, A8P8, A4L4,
            // X8L8V8U8, W11V11U10, UYVY, YUY2, DXT*) are intentionally NOT created,
            // exactly matching the original CXD3DXCodec::Create switch.
            case D3DFmt.A8R8G8B8: return new CXD3DXCodec_A8R8G8B8(pBlt);
            case D3DFmt.X8R8G8B8: return new CXD3DXCodec_X8R8G8B8(pBlt);
            case D3DFmt.R5G6B5: return new CXD3DXCodec_R5G6B5(pBlt);
            case D3DFmt.X1R5G5B5: return new CXD3DXCodec_X1R5G5B5(pBlt);
            case D3DFmt.A1R5G5B5: return new CXD3DXCodec_A1R5G5B5(pBlt);
            case D3DFmt.A4R4G4B4: return new CXD3DXCodec_A4R4G4B4(pBlt);
            case D3DFmt.A8: return new CXD3DXCodec_A8(pBlt);
            case D3DFmt.P8: return new CXD3DXCodec_P8(pBlt);
            case D3DFmt.L8: return new CXD3DXCodec_L8(pBlt);
            case D3DFmt.A8L8: return new CXD3DXCodec_A8L8(pBlt);

            case D3DFmt.V8U8: return new CXD3DXCodec_V8U8(pBlt);
            case D3DFmt.L6V5U5: return new CXD3DXCodec_L6V5U5(pBlt);
            case D3DFmt.Q8W8V8U8: return new CXD3DXCodec_Q8W8V8U8(pBlt);
            case D3DFmt.V16U16: return new CXD3DXCodec_V16U16(pBlt);
        }

        return null;
    }

    protected CXD3DXCodec(D3DX_BLT pBlt, uint uBPP, uint dwType)
    {
        m_pbData = pBlt.pData;
        m_pbOffset = pBlt.dataOffset;
        m_Format = pBlt.Format;
        m_uPitch = pBlt.RowPitch;
        m_uSlice = pBlt.SlicePitch;
        m_Box = pBlt.SubRegion;
        m_ColorKey = new D3DXCOLOR(pBlt.ColorKey);
        m_bColorKey = pBlt.ColorKey != 0;
        m_pfDither = pBlt.bDither ? g_fDitherOn : g_fDitherOff;
        m_uBytesPerPixel = uBPP >> 3;
        m_bLinear = uBPP != 0;
        m_dwType = dwType;

        if (CodecType.CODEC_P == m_dwType)
        {
            m_dwType = CodecType.CODEC_RGB;
            m_bPalettized = true;

            if (pBlt.pPalette != null)
            {
                for (uint i = 0; i < 256; i++)
                {
                    uint e = pBlt.pPalette[i];
                    uint peRed = (e >> 16) & 0xff;
                    uint peGreen = (e >> 8) & 0xff;
                    uint peBlue = (e >> 0) & 0xff;
                    uint peFlags = (e >> 24) & 0xff;

                    m_pPalette[i].r = (float)peRed * (1.0f / 255.0f);
                    m_pPalette[i].g = (float)peGreen * (1.0f / 255.0f);
                    m_pPalette[i].b = (float)peBlue * (1.0f / 255.0f);
                    m_pPalette[i].a = (float)peFlags * (1.0f / 255.0f);
                }
            }
            else
            {
                for (uint i = 0; i < 256; i++)
                {
                    m_pPalette[i].r = m_pPalette[i].g = m_pPalette[i].b = m_pPalette[i].a = 1.0f;
                }
            }
        }
        else
        {
            m_bPalettized = false;
        }

        m_uWidth = (uint)(m_Box.Right - m_Box.Left);
        m_uHeight = (uint)(m_Box.Bottom - m_Box.Top);
        m_uDepth = (uint)(m_Box.Back - m_Box.Front);
        m_uWidthBytes = m_uWidth * m_uBytesPerPixel;

        if (m_bLinear)
        {
            m_pbOffset += (int)(m_Box.Front * m_uSlice + m_Box.Top * m_uPitch + m_Box.Left * m_uBytesPerPixel);

            m_Box.Left = 0;
            m_Box.Top = 0;
            m_Box.Front = 0;
            m_Box.Right = m_uWidth;
            m_Box.Bottom = m_uHeight;
            m_Box.Back = m_uDepth;
        }
    }

    public virtual void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        // Do nothing
    }

    public virtual void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        // Do nothing
    }

    /// <summary>Flush any pending cached writes (mirrors the C++ codec destructors' Commit()).</summary>
    public virtual void Finish()
    {
    }

    protected void ColorKey(D3DXCOLOR[] pColors, int off)
    {
        int lim = off + (int)m_uWidth;
        for (int i = off; i < lim; i++)
        {
            if (pColors[i] == m_ColorKey)
            {
                pColors[i].r = pColors[i].g = pColors[i].b = pColors[i].a = 0.0f;
            }
        }
    }

    // Helpers for computing the base byte offset of a row/slice.
    protected int RowBase(uint uRow, uint uSlice) => m_pbOffset + (int)(uRow * m_uPitch + uSlice * m_uSlice);

    protected int DitherBase(uint uRow, uint uSlice) => (int)((uSlice & 3) + ((uRow & 3) * 8));
}

///////////////////////////////////////////////////////////////////////////
// Specific RGB codecs
///////////////////////////////////////////////////////////////////////////

internal sealed class CXD3DXCodec_R8G8B8 : CXD3DXCodec
{
    public CXD3DXCodec_R8G8B8(D3DX_BLT pBlt) : base(pBlt, 24, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            m_pbData[pub + 0] = (byte)F2I(pColors[off + (int)i].b * 255.0 + fDither);
            m_pbData[pub + 1] = (byte)F2I(pColors[off + (int)i].g * 255.0 + fDither);
            m_pbData[pub + 2] = (byte)F2I(pColors[off + (int)i].r * 255.0 + fDither);
            pub += 3;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            pColors[off + (int)i].r = (float)m_pbData[pub + 2] * (1.0f / 255.0f);
            pColors[off + (int)i].g = (float)m_pbData[pub + 1] * (1.0f / 255.0f);
            pColors[off + (int)i].b = (float)m_pbData[pub + 0] * (1.0f / 255.0f);
            pColors[off + (int)i].a = 1.0f;
            pub += 3;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_A8R8G8B8 : CXD3DXCodec
{
    public CXD3DXCodec_A8R8G8B8(D3DX_BLT pBlt) : base(pBlt, 32, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            uint v = (uint)((F2I(c.r * 255.0 + fDither) << 16) |
                            (F2I(c.g * 255.0 + fDither) << 8) |
                            (F2I(c.b * 255.0 + fDither) << 0) |
                            (F2I(c.a * 255.0 + fDither) << 24));
            W32(m_pbData, p, v);
            p += 4;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            uint v = R32(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 16) & 255) * (1.0f / 255.0f);
            pColors[off + (int)i].g = (float)((v >> 8) & 255) * (1.0f / 255.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 255) * (1.0f / 255.0f);
            pColors[off + (int)i].a = (float)((v >> 24) & 255) * (1.0f / 255.0f);
            p += 4;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_X8R8G8B8 : CXD3DXCodec
{
    public CXD3DXCodec_X8R8G8B8(D3DX_BLT pBlt) : base(pBlt, 32, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            uint v = (uint)((F2I(c.r * 255.0 + fDither) << 16) |
                            (F2I(c.g * 255.0 + fDither) << 8) |
                            (F2I(c.b * 255.0 + fDither) << 0));
            W32(m_pbData, p, v);
            p += 4;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            uint v = R32(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 16) & 255) * (1.0f / 255.0f);
            pColors[off + (int)i].g = (float)((v >> 8) & 255) * (1.0f / 255.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 255) * (1.0f / 255.0f);
            pColors[off + (int)i].a = 1.0f;
            p += 4;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_R5G6B5 : CXD3DXCodec
{
    public CXD3DXCodec_R5G6B5(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)((F2I(c.r * 31.0 + fDither) << 11) |
                                (F2I(c.g * 63.0 + fDither) << 5) |
                                (F2I(c.b * 31.0 + fDither) << 0));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 11) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].g = (float)((v >> 5) & 63) * (1.0f / 63.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].a = 1.0f;
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_X1R5G5B5 : CXD3DXCodec
{
    public CXD3DXCodec_X1R5G5B5(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)((F2I(c.r * 31.0 + fDither) << 10) |
                                (F2I(c.g * 31.0 + fDither) << 5) |
                                (F2I(c.b * 31.0 + fDither) << 0));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 10) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].g = (float)((v >> 5) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].a = 1.0f;
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_A1R5G5B5 : CXD3DXCodec
{
    public CXD3DXCodec_A1R5G5B5(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)((F2I(c.r * 31.0 + fDither) << 10) |
                                (F2I(c.g * 31.0 + fDither) << 5) |
                                (F2I(c.b * 31.0 + fDither) << 0) |
                                (F2I(c.a * 1.0 + fDither) << 15));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 10) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].g = (float)((v >> 5) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 31) * (1.0f / 31.0f);
            pColors[off + (int)i].a = (float)((v >> 15) & 1) * (1.0f / 1.0f);
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_A4R4G4B4 : CXD3DXCodec
{
    public CXD3DXCodec_A4R4G4B4(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)((F2I(c.r * 15.0 + fDither) << 8) |
                                (F2I(c.g * 15.0 + fDither) << 4) |
                                (F2I(c.b * 15.0 + fDither) << 0) |
                                (F2I(c.a * 15.0 + fDither) << 12));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 8) & 15) * (1.0f / 15.0f);
            pColors[off + (int)i].g = (float)((v >> 4) & 15) * (1.0f / 15.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 15) * (1.0f / 15.0f);
            pColors[off + (int)i].a = (float)((v >> 12) & 15) * (1.0f / 15.0f);
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_R3G3B2 : CXD3DXCodec
{
    public CXD3DXCodec_R3G3B2(D3DX_BLT pBlt) : base(pBlt, 8, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            m_pbData[pub] = (byte)((F2I(c.r * 7.0 + fDither) << 5) |
                                   (F2I(c.g * 7.0 + fDither) << 2) |
                                   (F2I(c.b * 3.0 + fDither) << 0));
            pub++;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            byte v = m_pbData[pub];
            pColors[off + (int)i].r = (float)((v >> 5) & 7) * (1.0f / 7.0f);
            pColors[off + (int)i].g = (float)((v >> 2) & 7) * (1.0f / 7.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 3) * (1.0f / 3.0f);
            pColors[off + (int)i].a = 1.0f;
            pub++;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_A8 : CXD3DXCodec
{
    public CXD3DXCodec_A8(D3DX_BLT pBlt) : base(pBlt, 8, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            m_pbData[pub] = (byte)F2I(pColors[off + (int)i].a * 255.0 + fDither);
            pub++;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            pColors[off + (int)i].r = 1.0f;
            pColors[off + (int)i].g = 1.0f;
            pColors[off + (int)i].b = 1.0f;
            pColors[off + (int)i].a = (float)m_pbData[pub] * (1.0f / 255.0f);
            pub++;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_A8R3G3B2 : CXD3DXCodec
{
    public CXD3DXCodec_A8R3G3B2(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)((F2I(c.r * 7.0 + fDither) << 5) |
                                (F2I(c.g * 7.0 + fDither) << 2) |
                                (F2I(c.b * 3.0 + fDither) << 0) |
                                (F2I(c.a * 255.0 + fDither) << 8));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 5) & 7) * (1.0f / 7.0f);
            pColors[off + (int)i].g = (float)((v >> 2) & 7) * (1.0f / 7.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 3) * (1.0f / 3.0f);
            pColors[off + (int)i].a = (float)((v >> 8) & 255) * (1.0f / 255.0f);
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_X4R4G4B4 : CXD3DXCodec
{
    public CXD3DXCodec_X4R4G4B4(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)((F2I(c.r * 15.0 + fDither) << 8) |
                                (F2I(c.g * 15.0 + fDither) << 4) |
                                (F2I(c.b * 15.0 + fDither) << 0));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i].r = (float)((v >> 8) & 15) * (1.0f / 15.0f);
            pColors[off + (int)i].g = (float)((v >> 4) & 15) * (1.0f / 15.0f);
            pColors[off + (int)i].b = (float)((v >> 0) & 15) * (1.0f / 15.0f);
            pColors[off + (int)i].a = 1.0f;
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

// A8P8 is a palettized (CODEC_P) codec.
internal sealed class CXD3DXCodec_A8P8 : CXD3DXCodec
{
    public CXD3DXCodec_A8P8(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_P) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];

            uint uMin = 0;
            float fDistMin = float.MaxValue;

            for (uint u = 0; u < 256; u++)
            {
                // Faithful: original reads *pColors (the row base) here, not pColors[i].
                D3DXCOLOR color = pColors[off] - m_pPalette[u];
                float fDist = color.r * color.r + color.g * color.g + color.b * color.b;

                if (fDist < fDistMin)
                    uMin = u;
            }

            ushort v = (ushort)(uMin | (uint)(F2I(pColors[off + (int)i].a * 255.0 + fDither) << 8));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i] = m_pPalette[v & 255];
            pColors[off + (int)i].a = (float)((v >> 8) & 255) * (1.0f / 255.0f);
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_P8 : CXD3DXCodec
{
    public CXD3DXCodec_P8(D3DX_BLT pBlt) : base(pBlt, 8, CodecType.CODEC_P) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            uint uMin = 0;
            float fDistMin = float.MaxValue;

            for (uint u = 0; u < 256; u++)
            {
                D3DXCOLOR color = pColors[off + (int)i] - m_pPalette[u];
                float fDist = color.r * color.r + color.g * color.g + color.b * color.b + color.a * color.a;

                if (fDist < fDistMin)
                {
                    uMin = u;
                    fDistMin = fDist;
                }
            }

            m_pbData[pub] = (byte)uMin;
            pub++;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            pColors[off + (int)i] = m_pPalette[m_pbData[pub]];
            pub++;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_L8 : CXD3DXCodec
{
    public CXD3DXCodec_L8(D3DX_BLT pBlt) : base(pBlt, 8, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            m_pbData[pub] = (byte)F2I((c.r * 0.2125 + c.g * 0.7154 + c.b * 0.0721) * 255.0 + fDither);
            pub++;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float l = (float)m_pbData[pub] * (1.0f / 255.0f);
            pColors[off + (int)i].r = pColors[off + (int)i].g = pColors[off + (int)i].b = l;
            pColors[off + (int)i].a = 1.0f;
            pub++;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_A8L8 : CXD3DXCodec
{
    public CXD3DXCodec_A8L8(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)((F2I((c.r * 0.2125 + c.g * 0.7154 + c.b * 0.0721) * 255.0 + fDither) << 0) |
                                (F2I(c.a * 255.0 + fDither) << 8));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            float l = (float)((v >> 0) & 255) * (1.0f / 255.0f);
            pColors[off + (int)i].r = pColors[off + (int)i].g = pColors[off + (int)i].b = l;
            pColors[off + (int)i].a = (float)((v >> 8) & 255) * (1.0f / 255.0f);
            p += 2;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_A4L4 : CXD3DXCodec
{
    public CXD3DXCodec_A4L4(D3DX_BLT pBlt) : base(pBlt, 8, CodecType.CODEC_RGB) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            m_pbData[pub] = (byte)((F2I((c.r * 0.2125 + c.g * 0.7154 + c.b * 0.0721) * 15.0 + fDither) << 0) |
                                   (F2I(c.a * 15.0 + fDither) << 4));
            pub++;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int pub = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            byte v = m_pbData[pub];
            float l = (float)((v >> 0) & 15) * (1.0f / 15.0f);
            pColors[off + (int)i].r = pColors[off + (int)i].g = pColors[off + (int)i].b = l;
            pColors[off + (int)i].a = (float)((v >> 4) & 15) * (1.0f / 15.0f);
            pub++;
        }
        if (m_bColorKey) ColorKey(pColors, off);
    }
}

///////////////////////////////////////////////////////////////////////////
// Specific UV codecs
///////////////////////////////////////////////////////////////////////////

internal sealed class CXD3DXCodec_V8U8 : CXD3DXCodec
{
    public CXD3DXCodec_V8U8(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_UV) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            ushort v = (ushort)(((F2I(c.r * 128.0 + fDither) & 255) << 0) |
                                ((F2I(c.g * 128.0 + fDither) & 255) << 8));
            W16(m_pbData, p, v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            pColors[off + (int)i].r = (float)(sbyte)((v >> 0) & 255) * (1.0f / 128.0f);
            pColors[off + (int)i].g = (float)(sbyte)((v >> 8) & 255) * (1.0f / 128.0f);
            pColors[off + (int)i].b = 0.0f;
            pColors[off + (int)i].a = 1.0f;
            p += 2;
        }
    }
}

internal sealed class CXD3DXCodec_L6V5U5 : CXD3DXCodec
{
    public CXD3DXCodec_L6V5U5(D3DX_BLT pBlt) : base(pBlt, 16, CodecType.CODEC_UV) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            uint v = (uint)(((F2I(c.r * 16.0 + fDither) & 31) << 0) |
                            ((F2I(c.g * 16.0 + fDither) & 31) << 5) |
                            ((F2I(c.a * 63.0 + fDither) & 63) << 10));
            W16(m_pbData, p, (ushort)v);
            p += 2;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            ushort v = R16(m_pbData, p);
            sbyte iU = (sbyte)((v >> 0) & 31);
            sbyte iV = (sbyte)((v >> 5) & 31);

            // Sign extension
            iU = (sbyte)(iU << 3); iU = (sbyte)(iU >> 3);
            iV = (sbyte)(iV << 3); iV = (sbyte)(iV >> 3);

            pColors[off + (int)i].r = (float)iU * (1.0f / 16.0f);
            pColors[off + (int)i].g = (float)iV * (1.0f / 16.0f);
            pColors[off + (int)i].b = 0.0f;
            pColors[off + (int)i].a = (float)((v >> 10) & 63) * (1.0f / 63.0f);
            p += 2;
        }
    }
}

internal sealed class CXD3DXCodec_X8L8V8U8 : CXD3DXCodec
{
    public CXD3DXCodec_X8L8V8U8(D3DX_BLT pBlt) : base(pBlt, 32, CodecType.CODEC_UV) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            uint v = (uint)(((F2I(c.r * 128.0 + fDither) & 255) << 0) |
                            ((F2I(c.g * 128.0 + fDither) & 255) << 8) |
                            ((F2I(c.a * 255.0 + fDither) & 255) << 16));
            W32(m_pbData, p, v);
            p += 4;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            uint v = R32(m_pbData, p);
            pColors[off + (int)i].r = (float)(sbyte)((v >> 0) & 255) * (1.0f / 128.0f);
            pColors[off + (int)i].g = (float)(sbyte)((v >> 8) & 255) * (1.0f / 128.0f);
            pColors[off + (int)i].b = 0.0f;
            pColors[off + (int)i].a = (float)((v >> 16) & 255) * (1.0f / 255.0f);
            p += 4;
        }
    }
}

internal sealed class CXD3DXCodec_Q8W8V8U8 : CXD3DXCodec
{
    public CXD3DXCodec_Q8W8V8U8(D3DX_BLT pBlt) : base(pBlt, 32, CodecType.CODEC_UV) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            uint v = (uint)(((F2I(c.r * 128.0 + fDither) & 255) << 0) |
                            ((F2I(c.g * 128.0 + fDither) & 255) << 8) |
                            ((F2I(c.b * 128.0 + fDither) & 255) << 16) |
                            ((F2I(c.a * 128.0 + fDither) & 255) << 24));
            W32(m_pbData, p, v);
            p += 4;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            uint v = R32(m_pbData, p);
            pColors[off + (int)i].r = (float)(sbyte)((v >> 0) & 255) * (1.0f / 128.0f);
            pColors[off + (int)i].g = (float)(sbyte)((v >> 8) & 255) * (1.0f / 128.0f);
            pColors[off + (int)i].b = (float)(sbyte)((v >> 16) & 255) * (1.0f / 128.0f);
            pColors[off + (int)i].a = (float)(sbyte)((v >> 24) & 255) * (1.0f / 128.0f);
            p += 4;
        }
    }
}

internal sealed class CXD3DXCodec_V16U16 : CXD3DXCodec
{
    public CXD3DXCodec_V16U16(D3DX_BLT pBlt) : base(pBlt, 32, CodecType.CODEC_UV) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            uint v = (uint)(((F2I(c.r * 32768.0 + fDither) & 65535) << 0) |
                            ((F2I(c.g * 32768.0 + fDither) & 65535) << 16));
            W32(m_pbData, p, v);
            p += 4;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            uint v = R32(m_pbData, p);
            pColors[off + (int)i].r = (float)(short)((v >> 0) & 65535) * (1.0f / 32768.0f);
            pColors[off + (int)i].g = (float)(short)((v >> 16) & 65535) * (1.0f / 32768.0f);
            pColors[off + (int)i].b = 0.0f;
            pColors[off + (int)i].a = 1.0f;
            p += 4;
        }
    }
}

internal sealed class CXD3DXCodec_W11V11U10 : CXD3DXCodec
{
    public CXD3DXCodec_W11V11U10(D3DX_BLT pBlt) : base(pBlt, 32, CodecType.CODEC_UV) { }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        int db = DitherBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            float fDither = m_pfDither[db + (int)(i & 3)];
            ref D3DXCOLOR c = ref pColors[off + (int)i];
            uint v = (uint)(((F2I(c.r * 512.0 + fDither) & 1023) << 0) |
                            ((F2I(c.g * 1024.0 + fDither) & 2047) << 10) |
                            ((F2I(c.b * 1024.0 + fDither) & 2046) << 21));
            W32(m_pbData, p, v);
            p += 4;
        }
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        int p = RowBase(uRow, uSlice);
        for (uint i = 0; i < m_uWidth; i++)
        {
            uint v = R32(m_pbData, p);
            short iU = (short)((v >> 0) & 1023);
            short iV = (short)((v >> 10) & 2047);
            short iW = (short)((v >> 21) & 2047);

            // Sign extension
            iU = (short)(iU << 6); iU = (short)(iU >> 6);
            iV = (short)(iV << 5); iV = (short)(iV >> 5);
            iW = (short)(iW << 5); iW = (short)(iW >> 5);

            pColors[off + (int)i].r = (float)iU * (1.0f / 512.0f);
            pColors[off + (int)i].g = (float)iV * (1.0f / 1024.0f);
            pColors[off + (int)i].b = (float)iW * (1.0f / 1024.0f);
            pColors[off + (int)i].a = 1.0f;
            p += 4;
        }
    }
}

///////////////////////////////////////////////////////////////////////////
// CXD3DXCodecYUV
///////////////////////////////////////////////////////////////////////////

internal class CXD3DXCodecYUV : CXD3DXCodec
{
    protected D3DXCOLOR[] m_pCache;
    protected D3DBOX m_CacheBox;
    protected uint m_uCacheWidth;
    protected bool m_bCacheDirty;
    protected bool m_bCacheAllocated;
    protected uint m_uYShift;
    protected uint m_uUVShift;

    public CXD3DXCodecYUV(D3DX_BLT pBlt) : base(pBlt, 0, CodecType.CODEC_RGB)
    {
        m_CacheBox.Left = m_Box.Left & ~1u;
        m_CacheBox.Top = 0;
        m_CacheBox.Front = 0;
        m_CacheBox.Right = (m_Box.Right + 1) & ~1u;
        m_CacheBox.Bottom = 0;
        m_CacheBox.Back = 0;

        m_uCacheWidth = m_CacheBox.Right - m_CacheBox.Left;

        m_bCacheDirty = false;
        m_bCacheAllocated = true;

        m_pCache = new D3DXCOLOR[m_uCacheWidth];

        if (D3DFmt.UYVY == pBlt.Format)
        {
            m_uYShift = 8;
            m_uUVShift = 0;
        }
        else
        {
            m_uYShift = 0;
            m_uUVShift = 8;
        }
    }

    public override void Finish() => Commit();

    public int Commit()
    {
        if (!m_bCacheDirty || !m_bCacheAllocated)
            return CXD3DXBlt.S_OK;

        int pus = m_pbOffset + (int)(m_CacheBox.Left * 2 + m_CacheBox.Top * m_uPitch + m_CacheBox.Front * m_uSlice);
        int pc = 0;

        for (uint uLeft = m_CacheBox.Left; uLeft < m_CacheBox.Right; uLeft += 2)
        {
            float fY0 = 65.481f * m_pCache[pc + 0].r + 128.553f * m_pCache[pc + 0].g + 24.966f * m_pCache[pc + 0].b;
            float fY1 = 65.481f * m_pCache[pc + 1].r + 128.553f * m_pCache[pc + 1].g + 24.966f * m_pCache[pc + 1].b;

            float fU = -37.797f * m_pCache[pc + 0].r + -74.203f * m_pCache[pc + 0].g + 112.000f * m_pCache[pc + 0].b;
            float fV = 112.000f * m_pCache[pc + 0].r + -93.786f * m_pCache[pc + 0].g + -18.214f * m_pCache[pc + 0].b;

            int nY0 = F2I(fY0 + 0.5) + 16;
            int nY1 = F2I(fY1 + 0.5) + 16;
            int nU = F2I(fU + 0.5) + 128;
            int nV = F2I(fV + 0.5) + 128;

            nY0 = (nY0 < 0) ? 0 : ((nY0 > 0xff) ? 0xff : nY0);
            nY1 = (nY1 < 0) ? 0 : ((nY1 > 0xff) ? 0xff : nY1);
            nU = (nU < 0) ? 0 : ((nU > 0xff) ? 0xff : nU);
            nV = (nV < 0) ? 0 : ((nV > 0xff) ? 0xff : nV);

            W16(m_pbData, pus + 0, (ushort)((nY0 << (int)m_uYShift) | (nU << (int)m_uUVShift)));
            W16(m_pbData, pus + 2, (ushort)((nY1 << (int)m_uYShift) | (nV << (int)m_uUVShift)));

            pc += 2;
            pus += 4;
        }

        m_bCacheDirty = false;
        return CXD3DXBlt.S_OK;
    }

    public int Fetch(uint uRow, uint uSlice, bool bRead)
    {
        if (!m_bCacheAllocated)
            return CXD3DXBlt.E_OUTOFMEMORY;

        if (uRow >= m_CacheBox.Top && uRow < m_CacheBox.Bottom &&
            uSlice >= m_CacheBox.Front && uSlice < m_CacheBox.Back)
        {
            return CXD3DXBlt.S_OK;
        }

        int hr = Commit();
        if (CXD3DXBlt.FAILED(hr))
            return hr;

        m_CacheBox.Top = uRow;
        m_CacheBox.Bottom = uRow + 1;
        m_CacheBox.Front = uSlice;
        m_CacheBox.Back = uSlice + 1;

        if (!bRead)
            return CXD3DXBlt.S_OK;

        int pus = m_pbOffset + (int)(m_CacheBox.Left * 2 + m_CacheBox.Top * m_uPitch + m_CacheBox.Front * m_uSlice);
        int pc = 0;

        for (uint uLeft = m_CacheBox.Left; uLeft < m_CacheBox.Right; uLeft += 2)
        {
            ushort s0 = R16(m_pbData, pus + 0);
            ushort s1 = R16(m_pbData, pus + 2);

            float fY0 = (float)((s0 >> (int)m_uYShift) & 0xff) - 16.0f;
            float fU = (float)((s0 >> (int)m_uUVShift) & 0xff) - 128.0f;

            float fY1 = (float)((s1 >> (int)m_uYShift) & 0xff) - 16.0f;
            float fV = (float)((s1 >> (int)m_uUVShift) & 0xff) - 128.0f;

            m_pCache[pc + 0].r = 0.00456621f * fY0 + 0.00625893f * fV;
            m_pCache[pc + 0].g = 0.00456621f * fY0 - 0.00153632f * fU - 0.00318811f * fV;
            m_pCache[pc + 0].b = 0.00456621f * fY0 + 0.00791071f * fU;
            m_pCache[pc + 0].a = 1.0f;

            m_pCache[pc + 0].r = (m_pCache[pc + 0].r < 0.0f) ? 0.0f : ((m_pCache[pc + 0].r > 1.0f) ? 1.0f : m_pCache[pc + 0].r);
            m_pCache[pc + 0].g = (m_pCache[pc + 0].g < 0.0f) ? 0.0f : ((m_pCache[pc + 0].g > 1.0f) ? 1.0f : m_pCache[pc + 0].g);
            m_pCache[pc + 0].b = (m_pCache[pc + 0].b < 0.0f) ? 0.0f : ((m_pCache[pc + 0].b > 1.0f) ? 1.0f : m_pCache[pc + 0].b);

            m_pCache[pc + 1].r = 0.00456621f * fY1 + 0.00625893f * fV;
            m_pCache[pc + 1].g = 0.00456621f * fY1 - 0.00153632f * fU - 0.00318811f * fV;
            m_pCache[pc + 1].b = 0.00456621f * fY1 + 0.00791071f * fU;
            m_pCache[pc + 1].a = 1.0f;

            m_pCache[pc + 1].r = (m_pCache[pc + 1].r < 0.0f) ? 0.0f : ((m_pCache[pc + 1].r > 1.0f) ? 1.0f : m_pCache[pc + 1].r);
            m_pCache[pc + 1].g = (m_pCache[pc + 1].g < 0.0f) ? 0.0f : ((m_pCache[pc + 1].g > 1.0f) ? 1.0f : m_pCache[pc + 1].g);
            m_pCache[pc + 1].b = (m_pCache[pc + 1].b < 0.0f) ? 0.0f : ((m_pCache[pc + 1].b > 1.0f) ? 1.0f : m_pCache[pc + 1].b);

            pc += 2;
            pus += 4;
        }

        return CXD3DXBlt.S_OK;
    }

    public override void Encode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        uRow += m_Box.Top;
        uSlice += m_Box.Front;

        if (CXD3DXBlt.FAILED(Fetch(uRow, uSlice, m_uCacheWidth != m_uWidth)))
            return;

        Array.Copy(pColors, off, m_pCache, (int)(m_Box.Left - m_CacheBox.Left), (int)m_uWidth);
        m_bCacheDirty = true;
    }

    public override void Decode(uint uRow, uint uSlice, D3DXCOLOR[] pColors, int off)
    {
        uRow += m_Box.Top;
        uSlice += m_Box.Front;

        if (CXD3DXBlt.FAILED(Fetch(uRow, uSlice, true)))
            return;

        Array.Copy(m_pCache, (int)(m_Box.Left - m_CacheBox.Left), pColors, off, (int)m_uWidth);

        if (m_bColorKey)
            ColorKey(pColors, off);
    }
}

internal sealed class CXD3DXCodec_UYVY : CXD3DXCodecYUV
{
    public CXD3DXCodec_UYVY(D3DX_BLT pBlt) : base(pBlt) { }
}

internal sealed class CXD3DXCodec_YUY2 : CXD3DXCodecYUV
{
    public CXD3DXCodec_YUY2(D3DX_BLT pBlt) : base(pBlt) { }
}

///////////////////////////////////////////////////////////////////////////
// CXD3DXCodecDXT
//
// The original CXD3DXCodecDXT implementation is guarded by #ifdef SUPPORT_DXT
// (undefined for the bundler) and depends on the S3TC block encoders declared in
// basetexture.h (XXEncodeBlockRGB / EncodeBlockAlpha3 / EncodeBlockAlpha4, etc.),
// which are not part of this port. CXD3DXCodec::Create never instantiates a DXT
// codec, so this path is unreachable; the class is provided for completeness and
// throws if it is ever constructed.
///////////////////////////////////////////////////////////////////////////

internal class CXD3DXCodecDXT : CXD3DXCodec
{
    public CXD3DXCodecDXT(D3DX_BLT pBlt) : base(pBlt, 0, CodecType.CODEC_RGB)
    {
        throw new BundlerException("DXT (S3TC) block codec is not ported (SUPPORT_DXT was not defined in the source bundler).");
    }
}

internal sealed class CXD3DXCodec_DXT1 : CXD3DXCodecDXT { public CXD3DXCodec_DXT1(D3DX_BLT pBlt) : base(pBlt) { } }
internal sealed class CXD3DXCodec_DXT2 : CXD3DXCodecDXT { public CXD3DXCodec_DXT2(D3DX_BLT pBlt) : base(pBlt) { } }
internal sealed class CXD3DXCodec_DXT3 : CXD3DXCodecDXT { public CXD3DXCodec_DXT3(D3DX_BLT pBlt) : base(pBlt) { } }
internal sealed class CXD3DXCodec_DXT4 : CXD3DXCodecDXT { public CXD3DXCodec_DXT4(D3DX_BLT pBlt) : base(pBlt) { } }
internal sealed class CXD3DXCodec_DXT5 : CXD3DXCodecDXT { public CXD3DXCodec_DXT5(D3DX_BLT pBlt) : base(pBlt) { } }
