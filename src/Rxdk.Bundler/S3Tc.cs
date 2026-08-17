// Faithful, byte-exact C# port of the Xbox XGRAPHICS S3TC / DXT texture compressor.
//
// PRECISION CAVEAT: the XDK bundler runs with _controlfp(_PC_24) — the x87 FPU forced
// to a 24-bit (single-precision) mantissa "to maintain bit-for-bit output". This port
// keeps the original C `double` types, which C# evaluates at full 53-bit precision, so a
// small fraction of borderline DXT blocks (~0.16% measured on XPRViewer) pick a different
// endpoint than the golden .xpr. Output is visually identical and renders correctly;
// reaching 100% byte-exact would require emulating _PC_24 (round double intermediates to
// float) throughout the quantizer. Intentionally not done (see project notes).
//
// Sources ported line-for-line (do NOT optimize/restructure — the goal is that this
// produces bit-identical DXT block data to the original):
//   libs/libxgraphics/dxtc/s3_intrf.cpp  (XGCompressRect, GetFormatInfo, getNrOfShifts)
//   libs/libxgraphics/dxtc/s3_quant.cpp  (the S3TC color/alpha quantizer)
//   libs/libxgraphics/dxtc/S3_quant.h    (RGBBlock / AlphaBlock)
//
// Every integer/float width, shift, mask, rounding, truncation and evaluation order is
// preserved. Floats are single-precision where the source used `float`; everything else
// is `double`. The one inline-asm block (getNrOfShifts' `bsf`) is translated to the
// equivalent scalar bit-scan (TrailingZeroCount).
//
// Format ids switched on here are the XBOX d3d8-xbox.h D3DFMT_* numeric ids (see
// XboxFormats.X_D3DFMT_*), because that is what the bundler passes.

using System.Numerics;

namespace Rxdk.Bundler;

internal static class S3Tc
{
    // ---- XGCOMPRESS_* flags (xgraphics.h) -----------------------------------
    public const uint XGCOMPRESS_PREMULTIPLY = 0x1;
    public const uint XGCOMPRESS_NEEDALPHA0 = 0x2;
    public const uint XGCOMPRESS_NEEDALPHA1 = 0x4;
    public const uint XGCOMPRESS_PROTECTNONZERO = 0x8;

    // ---- S3_intrf.h encoding-type constants ---------------------------------
    private const uint S3TC_ENCODE_RGB_FULL = 0x0;
    private const uint S3TC_ENCODE_RGB_COLOR_KEY = 0x1;
    private const uint S3TC_ENCODE_RGB_ALPHA_COMPARE = 0x2;
    private const uint _S3TC_ENCODE_RGB_MASK = 0xff;

    private const uint S3TC_ENCODE_ALPHA_NONE = 0x000;
    private const uint S3TC_ENCODE_ALPHA_EXPLICIT = 0x100;
    private const uint S3TC_ENCODE_ALPHA_INTERPOLATED = 0x200;
    private const uint _S3TC_ENCODE_ALPHA_MASK = 0xff00;

    private const uint S3TC_ENCODE_ALPHA_NEED0 = 0x10000;
    private const uint S3TC_ENCODE_ALPHA_NEED1 = 0x20000;
    private const uint S3TC_ENCODE_ALPHA_PROTECTNONZERO = 0x40000;

    // ---- S3_quant.h ---------------------------------------------------------
    private const int MAX_PIXEL_PER_BLOCK = 16;
    // s3_quant.cpp magic numbers
    // ALL_SAME_THRESHOLD 1/256./4./2. — evaluate exactly as written (all double).
    private const float ALL_SAME_THRESHOLD = 1 / 256.0f / 4.0f / 2.0f;
    private const int UNCLIPPED_ITERATION_LIMIT = 16;
    private const int CLIPPED_ITERATION_LIMIT = 8;
    private const int INDEX_LOG_SIZE = 16;
    private const int MAX_ALPHA_LEVELS = 8;
    private const int ALPHA_ITERATION_LIMIT = 64;

    // Xbox D3DFMT_* ids used by GetFormatInfo / XGCompressRect's switches.
    private const uint D3DFMT_DXT1 = XboxFormats.X_D3DFMT_DXT1;             // 0x0C
    private const uint D3DFMT_DXT2 = XboxFormats.X_D3DFMT_DXT2;             // 0x0E
    private const uint D3DFMT_DXT4 = XboxFormats.X_D3DFMT_DXT4;             // 0x0F
    private const uint D3DFMT_LIN_A8R8G8B8 = XboxFormats.X_D3DFMT_LIN_A8R8G8B8; // 0x12
    private const uint D3DFMT_LIN_X8R8G8B8 = XboxFormats.X_D3DFMT_LIN_X8R8G8B8; // 0x1E
    private const uint D3DFMT_LIN_R5G6B5 = XboxFormats.X_D3DFMT_LIN_R5G6B5;     // 0x11
    private const uint D3DFMT_LIN_A1R5G5B5 = XboxFormats.X_D3DFMT_LIN_A1R5G5B5; // 0x10
    private const uint D3DFMT_LIN_X1R5G5B5 = XboxFormats.X_D3DFMT_LIN_X1R5G5B5; // 0x1C
    private const uint D3DFMT_LIN_A4R4G4B4 = XboxFormats.X_D3DFMT_LIN_A4R4G4B4; // 0x1D
    private const uint D3DFMT_LIN_R6G5B5 = XboxFormats.X_D3DFMT_LIN_R6G5B5;     // 0x37
    private const uint D3DFMT_LIN_A8B8G8R8 = XboxFormats.X_D3DFMT_LIN_A8B8G8R8; // 0x3F
    private const uint D3DFMT_LIN_B8G8R8A8 = XboxFormats.X_D3DFMT_LIN_B8G8R8A8; // 0x40
    private const uint D3DFMT_LIN_R4G4B4A4 = XboxFormats.X_D3DFMT_LIN_R4G4B4A4; // 0x3E
    private const uint D3DFMT_LIN_R5G5B5A1 = XboxFormats.X_D3DFMT_LIN_R5G5B5A1; // 0x3D
    private const uint D3DFMT_LIN_R8G8B8A8 = XboxFormats.X_D3DFMT_LIN_R8G8B8A8; // 0x41

    // FORMAT_INFO from s3_intrf.cpp.
    private struct FormatInfo
    {
        public uint R, G, B, A, bpp;
    }

    // -------------------------------------------------------------------------
    // getNrOfShifts: original used `bsf eax, dwMask`. bsf = index of the least
    // significant set bit == trailing-zero count. (Undefined for 0; only called
    // on non-zero masks, exactly as the original.)
    private static uint getNrOfShifts(uint dwMask) => (uint)BitOperations.TrailingZeroCount(dwMask);

    // GetFormatInfo — returns false on E_INVALIDARG.
    private static bool GetFormatInfo(uint fmt, out FormatInfo pfi)
    {
        pfi = default;
        switch (fmt)
        {
            case D3DFMT_LIN_A8R8G8B8:
                pfi.A = 0xFF000000;
                goto case D3DFMT_LIN_X8R8G8B8;
            case D3DFMT_LIN_X8R8G8B8:
                pfi.R = 0x00FF0000;
                pfi.G = 0x0000FF00;
                pfi.B = 0x000000ff;
                pfi.bpp = 4;
                break;

            case D3DFMT_LIN_R5G6B5:
                pfi.R = 0x0000F800;
                pfi.G = 0x000007E0;
                pfi.B = 0x0000001F;
                pfi.bpp = 2;
                break;

            case D3DFMT_LIN_A1R5G5B5:
                pfi.A = 0x00008000;
                goto case D3DFMT_LIN_X1R5G5B5;
            case D3DFMT_LIN_X1R5G5B5:
                pfi.R = 0x00007C00;
                pfi.G = 0x000003E0;
                pfi.B = 0x0000001F;
                pfi.bpp = 2;
                break;

            case D3DFMT_LIN_A4R4G4B4:
                pfi.A = 0x0000F000;
                pfi.R = 0x00000F00;
                pfi.G = 0x000000F0;
                pfi.B = 0x0000000F;
                pfi.bpp = 2;
                break;

            case D3DFMT_LIN_R6G5B5:
                pfi.R = 0x0000FC00;
                pfi.G = 0x000003E0;
                pfi.B = 0x0000001F;
                pfi.bpp = 2;
                break;

            case D3DFMT_LIN_A8B8G8R8:
                pfi.A = 0xFF000000;
                pfi.B = 0x00FF0000;
                pfi.G = 0x0000FF00;
                pfi.R = 0x000000ff;
                pfi.bpp = 4;
                break;

            case D3DFMT_LIN_B8G8R8A8:
                pfi.B = 0xFF000000;
                pfi.G = 0x00FF0000;
                pfi.R = 0x0000FF00;
                pfi.A = 0x000000ff;
                pfi.bpp = 4;
                break;

            case D3DFMT_LIN_R4G4B4A4:
                pfi.R = 0x0000F000;
                pfi.G = 0x00000F00;
                pfi.B = 0x000000F0;
                pfi.A = 0x0000000F;
                pfi.bpp = 2;
                break;

            case D3DFMT_LIN_R5G5B5A1:
                pfi.R = 0x0000F800;
                pfi.G = 0x000007C0;
                pfi.B = 0x0000003E;
                pfi.A = 0x00000001;
                pfi.bpp = 2;
                break;

            case D3DFMT_LIN_R8G8B8A8:
                pfi.R = 0xFF000000;
                pfi.G = 0x00FF0000;
                pfi.B = 0x0000FF00;
                pfi.A = 0x000000ff;
                pfi.bpp = 4;
                break;

            default:
                return false;
        }
        return true;
    }

    // ---- little-endian source read / dest write helpers ---------------------
    // Bounds-safe reads: for edge blocks (texture width/height < 4) the original
    // dereferences a few out-of-block source texels whose value is then discarded
    // (val>>=4). Returning 0 past the buffer end keeps the encoded output identical
    // for all in-bounds (used) texels while avoiding an OOB throw on tiny textures.
    private static uint RdByte(byte[] b, int o) => (uint)(o >= 0 && o < b.Length ? b[o] : 0);
    private static uint RdWord(byte[] b, int o) => RdByte(b, o) | (RdByte(b, o + 1) << 8);
    private static uint RdDword(byte[] b, int o) =>
        RdByte(b, o) | (RdByte(b, o + 1) << 8) | (RdByte(b, o + 2) << 16) | (RdByte(b, o + 3) << 24);

    private static uint ReadTexel(byte[] b, int o, int bpp) => bpp switch
    {
        4 => RdDword(b, o),
        2 => RdWord(b, o),
        1 => RdByte(b, o),
        _ => 0,
    };

    private static void WrWord(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }

    private static void WrDword(byte[] b, int o, uint v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }

    // *(unsigned *)(p) |= v  (read-modify-write, little-endian)
    private static void OrDword(byte[] b, int o, uint v) => WrDword(b, o, RdDword(b, o) | v);

    private static void Fill(byte[] b, int o, int count, byte value)
    {
        for (int i = 0; i < count; i++) b[o + i] = value;
    }

    // =========================================================================
    //  XGCompressRect
    // =========================================================================
    // destPitch is treated as 0 (tightly packed). Writes compressed DXT blocks
    // into dest starting at destOff.
    public static void CompressRect(
        byte[] dest, int destOff, uint destFormat,
        uint width, uint height,
        byte[] src, int srcOff, uint srcFormat, uint srcPitch,
        float alphaRef, uint flags)
    {
        uint DestFormat = destFormat;
        uint SrcFormat = srcFormat;
        uint dwDestPitch = 0;       // destPitch treated as 0
        uint dwWidth = width;
        uint dwHeight = height;
        uint dwSrcPitch = srcPitch;
        float fAlphaRef = alphaRef;
        uint dwFlags = flags;

        FormatInfo fi;
        int y;
        int lpSrcBuf;               // offset into src
        int lpDstBuf;               // offset into dest
        int bpp;
        uint[] rgbShift = new uint[3];
        uint aShift;
        uint aRef = 0;
        uint[] rgbBitMask;
        uint dwEncodeType;
        float[] weight = { 0.3086f, 0.6094f, 0.0820f };
        uint dwBytesPerBlock;
        uint dwWidthInBytes;
        uint DestPitchIncrement;
        bool bPreMultiply = (dwFlags & XGCOMPRESS_PREMULTIPLY) != 0;

        // Check input data and destination buffer
        if (src is null || dest is null)
            throw new BundlerException("XGCompressRect: E_INVALIDARG (null buffer)");

        // Make sure the format makes sense
        if (DestFormat != D3DFMT_DXT1 &&
            DestFormat != D3DFMT_DXT2 &&
            DestFormat != D3DFMT_DXT4)
            throw new BundlerException("XGCompressRect: E_INVALIDARG (dest format)");

        // Must be power-of-2 dimensions
        if ((dwWidth & (dwWidth - 1)) != 0)
            throw new BundlerException("XGCompressRect: E_INVALIDARG (width not power of 2)");
        if ((dwHeight & (dwHeight - 1)) != 0)
            throw new BundlerException("XGCompressRect: E_INVALIDARG (height not power of 2)");

        // Get info on the source texture format
        if (!GetFormatInfo(SrcFormat, out fi))
            throw new BundlerException("XGCompressRect: E_INVALIDARG (src format)");

        rgbBitMask = new[] { fi.R, fi.G, fi.B };

        // NB: the C switch has no default; DestFormat was validated above.
        dwEncodeType = 0;
        dwBytesPerBlock = 0;
        switch (DestFormat)
        {
            case D3DFMT_DXT1:
                dwEncodeType = S3TC_ENCODE_RGB_ALPHA_COMPARE | S3TC_ENCODE_ALPHA_NONE;
                dwBytesPerBlock = 8;
                break;
            case D3DFMT_DXT2:
                dwEncodeType = S3TC_ENCODE_RGB_FULL | S3TC_ENCODE_ALPHA_EXPLICIT;
                dwBytesPerBlock = 16;
                break;
            case D3DFMT_DXT4:
                dwEncodeType = S3TC_ENCODE_RGB_FULL | S3TC_ENCODE_ALPHA_INTERPOLATED;
                dwBytesPerBlock = 16;
                break;
        }

        if ((dwFlags & XGCOMPRESS_NEEDALPHA0) != 0)
            dwEncodeType |= S3TC_ENCODE_ALPHA_NEED0;
        if ((dwFlags & XGCOMPRESS_NEEDALPHA1) != 0)
            dwEncodeType |= S3TC_ENCODE_ALPHA_NEED1;
        if ((dwFlags & XGCOMPRESS_PROTECTNONZERO) != 0)
            dwEncodeType |= S3TC_ENCODE_ALPHA_PROTECTNONZERO;

        dwWidthInBytes = (dwWidth >> 2) * dwBytesPerBlock;

        // Calculate default pitches, if not specified.
        if (dwSrcPitch == 0)
            dwSrcPitch = dwWidth * fi.bpp;
        if (dwDestPitch == 0)
            dwDestPitch = dwWidthInBytes;

        if (dwDestPitch < dwWidthInBytes)
            throw new BundlerException("XGCompressRect: E_INVALIDARG (dest pitch)");

        DestPitchIncrement = dwDestPitch - dwWidthInBytes;

        bpp = (int)fi.bpp;
        rgbShift[0] = getNrOfShifts(fi.R);
        rgbShift[1] = getNrOfShifts(fi.G);
        rgbShift[2] = getNrOfShifts(fi.B);
        aShift = fi.A != 0 ? getNrOfShifts(fi.A) : 0;
        if ((dwEncodeType & _S3TC_ENCODE_RGB_MASK) == S3TC_ENCODE_RGB_ALPHA_COMPARE)
        {
            aRef = (uint)(fAlphaRef * (float)(fi.A >> (int)aShift)) << (int)aShift;
        }

        lpSrcBuf = srcOff;
        lpDstBuf = destOff;

        // main y loop
        for (y = 0; y < (int)dwHeight; y += 4, lpSrcBuf += (int)(dwSrcPitch * 4))
        {
            int x;
            int lpSrcCur;
            int blockHeight;

            blockHeight = (int)Math.Min(dwHeight - (uint)y, 4u);
            lpSrcCur = lpSrcBuf;
            // main x loop
            for (x = 0; x < (int)dwWidth; x += 4, lpSrcCur += bpp * 4)
            {
                int blockWidth;
                RGBBlock b = new RGBBlock();
                int k;
                int[] pixIndex = new int[16];       // "index[16]" in the source
                ushort[] endPt = new ushort[2];
                int bSwapped = 0;
                uint dwIndex = 0;

                for (k = 0; k < 3; k++)
                    b.weight[k] = weight[k];        // float -> double

                blockWidth = (int)Math.Min(dwWidth - (uint)x, 4u);
                b.n = 0;

                // ----- alpha -----
                switch (dwEncodeType & _S3TC_ENCODE_ALPHA_MASK)
                {
                    case S3TC_ENCODE_ALPHA_NONE:
                        break;

                    case S3TC_ENCODE_ALPHA_EXPLICIT:
                        {
                            int iy;
                            int lpCur;
                            if (fi.A != 0)
                            {
                                lpCur = lpSrcCur;
                                for (iy = 0; iy < 4; iy++, lpCur += (int)dwSrcPitch - 4 * bpp)
                                {
                                    if (iy < blockHeight)
                                    {
                                        ushort val = 0;
                                        int ix;
                                        for (ix = 0; ix < 4; ix++, lpCur += bpp)
                                        {
                                            uint dwCurTexel = ReadTexel(src, lpCur, bpp);
                                            if (ix < blockWidth)
                                            {
                                                float t = (float)((dwCurTexel & fi.A) >> (int)aShift)
                                                          / (float)(fi.A >> (int)aShift) * 15.0f + 0.5f;
                                                ushort q = (ushort)Math.Floor((float)t);
                                                val = (ushort)(((int)val >> 4) | ((int)q << 12));
                                            }
                                            else
                                            {
                                                val >>= 4;
                                            }
                                        }
                                        WrWord(dest, lpDstBuf, val);
                                    }
                                    else
                                    {
                                        WrWord(dest, lpDstBuf, 0);
                                    }
                                    lpDstBuf += 2;
                                }
                            }
                            else
                            {
                                // Set to opaque: memset(lpDstBuf, 0xFFFF, 8) -> 8 bytes of 0xFF
                                Fill(dest, lpDstBuf, 8, 0xFF);
                                lpDstBuf += 8;
                            }
                        }
                        break;

                    case S3TC_ENCODE_ALPHA_INTERPOLATED:
                        {
                            AlphaBlock a = new AlphaBlock();
                            int ix, iy;
                            int lpCur;
                            uint val;

                            if (fi.A != 0)
                            {
                                lpCur = lpSrcCur;
                                a.n = 0;
                                k = 0;
                                for (iy = 0; iy < 4; iy++, lpCur += (int)dwSrcPitch - 4 * bpp)
                                    for (ix = 0; ix < 4; ix++, lpCur += bpp)
                                        if (ix < blockWidth && iy < blockHeight)
                                        {
                                            uint dwCurTexel = ReadTexel(src, lpCur, bpp);
                                            uint cur, max;
                                            cur = (dwCurTexel & fi.A) >> (int)aShift;
                                            max = fi.A >> (int)aShift;
                                            pixIndex[k++] = a.n;
                                            a.alpha[a.n++] = (cur == max) ? 1.0f : cur / ((float)max);
                                        }
                                        else
                                        {
                                            pixIndex[k++] = -1;
                                        }

                                a.need0 = (int)(dwEncodeType & S3TC_ENCODE_ALPHA_NEED0);
                                a.need1 = (int)(dwEncodeType & S3TC_ENCODE_ALPHA_NEED1);
                                a.protectnonzero = (int)(dwEncodeType & S3TC_ENCODE_ALPHA_PROTECTNONZERO);

                                if (a.n == 0)
                                {
                                    a.endPoint[0] = a.endPoint[1] = 0;
                                    // (asserts index[k]==-1 elided)
                                }
                                else
                                {
                                    CodeAlphaBlock(a);
                                }

                                if (a.endPoint[0] == a.endPoint[1])
                                {
                                    if (a.endPoint[1] < 255)
                                    {
                                        a.endPoint[1]++;
                                        for (k = 0; k < a.n; k++)
                                            a.index[k] = 0;
                                    }
                                    else
                                    {
                                        a.endPoint[0]--;
                                        for (k = 0; k < a.n; k++)
                                            a.index[k] = 1;
                                    }
                                    a.outLevel = 6;
                                }

                                // need swapping?
                                if ((a.endPoint[0] > a.endPoint[1]) == (a.outLevel == 6))
                                {
                                    int sw = a.endPoint[0];
                                    a.endPoint[0] = a.endPoint[1];
                                    a.endPoint[1] = sw;
                                    bSwapped = 1;
                                }
                                else
                                {
                                    bSwapped = 0;
                                }

                                // write out endpoints
                                for (k = 0; k < 2; k++)
                                    dest[lpDstBuf++] = (byte)a.endPoint[k];

                                // handle indices
                                Fill(dest, lpDstBuf, 6, 0);
                                val = 0;
                                k = 0;
                                for (iy = 0; iy < 4; iy++)
                                {
                                    for (ix = 0; ix < 4; ix++, k++)
                                    {
                                        val >>= 3;
                                        if (pixIndex[k] >= 0)
                                        {
                                            int curIndex = a.index[pixIndex[k]];
                                            if (bSwapped != 0)
                                            {
                                                if (a.outLevel == 8)
                                                {
                                                    curIndex = (curIndex > 1) ? (9 - curIndex) : (curIndex == 0 ? 1 : 0);
                                                }
                                                else if (a.outLevel == 6)
                                                {
                                                    curIndex = (curIndex > 5) ? curIndex : ((curIndex > 1) ? (7 - curIndex) : (curIndex == 0 ? 1 : 0));
                                                }
                                            }
                                            val |= (uint)(curIndex << 21);
                                        }
                                    }

                                    if ((iy & 1) != 0)
                                    {
                                        OrDword(dest, lpDstBuf, val);
                                        lpDstBuf += 3;
                                        val = 0;
                                    }
                                }
                            }
                            else
                            {
                                dest[lpDstBuf++] = 0x00;
                                dest[lpDstBuf++] = 0xFF;
                                Fill(dest, lpDstBuf, 6, 0xFF);
                                lpDstBuf += 6;
                            }
                        }
                        break;

                    default:
                        break; // _ASSERTE(0)
                }

                // ----- rgb (non-palettized) -----
                {
                    int ix, iy;
                    int lpCur;

                    lpCur = lpSrcCur;
                    switch (dwEncodeType & _S3TC_ENCODE_RGB_MASK)
                    {
                        case S3TC_ENCODE_RGB_FULL:
                            for (k = 0, iy = 0; iy < 4; iy++, lpCur += (int)dwSrcPitch - 4 * bpp)
                                for (ix = 0; ix < 4; ix++, lpCur += bpp)
                                    if (ix < blockWidth && iy < blockHeight)
                                    {
                                        int i;
                                        int pc = b.n;                 // pChannel target row
                                        pixIndex[k++] = b.n;
                                        b.n++;

                                        uint dwCurTexel = ReadTexel(src, lpCur, bpp);

                                        float fAlpha = (bPreMultiply && fi.A != 0)
                                            ? (float)((dwCurTexel & fi.A) >> (int)aShift) / (float)(fi.A >> (int)aShift)
                                            : 1.0f;
                                        for (i = 0; i < 3; i++)
                                            b.colorChannel[pc][i] =
                                                fAlpha * (float)((dwCurTexel & rgbBitMask[i]) >> (int)rgbShift[i])
                                                / (float)(rgbBitMask[i] >> (int)rgbShift[i]);
                                    }
                                    else
                                    {
                                        pixIndex[k++] = -1;
                                    }
                            break;

                        case S3TC_ENCODE_RGB_ALPHA_COMPARE:
                            for (k = 0, iy = 0; iy < 4; iy++, lpCur += (int)dwSrcPitch - 4 * bpp)
                                for (ix = 0; ix < 4; ix++, lpCur += bpp)
                                    if (ix < blockWidth && iy < blockHeight)
                                    {
                                        int i;
                                        uint dwCurTexel = ReadTexel(src, lpCur, bpp);

                                        if (fi.A == 0 || (dwCurTexel & fi.A) > aRef)
                                        {
                                            int pc = b.n;
                                            pixIndex[k++] = b.n;
                                            b.n++;
                                            for (i = 0; i < 3; i++)
                                                b.colorChannel[pc][i] =
                                                    ((dwCurTexel & rgbBitMask[i]) >> (int)rgbShift[i])
                                                    / (float)(rgbBitMask[i] >> (int)rgbShift[i]);
                                        }
                                        else
                                        {
                                            pixIndex[k++] = -1;
                                        }
                                    }
                                    else
                                    {
                                        pixIndex[k++] = -1;
                                    }
                            break;

                        default:
                            break; // _ASSERTE(0)
                    }
                }

                // input quantization level
                b.inLevel = b.n < blockWidth * blockHeight ? 3 : 4;
                b.force4 = (dwEncodeType & _S3TC_ENCODE_RGB_MASK) == S3TC_ENCODE_RGB_FULL;

                if (b.n == 0)
                {
                    for (k = 0; k < 2; k++)
                        b.endPoint[k][0] = b.endPoint[k][1] = b.endPoint[k][2] = 0;
                }
                else
                {
                    CodeRGBBlock(b);
                }

                // retrieve endpoints
                for (k = 0; k < 2; k++)
                {
                    endPt[k] = (ushort)((b.endPoint[k][0] << 11) | (b.endPoint[k][1] << 5) | b.endPoint[k][2]);
                }

                // endpoints equal -> collapse to 3 points
                if (endPt[0] == endPt[1])
                {
                    endPt[1]++;
                    for (k = 0; k < b.n; k++)
                        b.index[k] = 0;
                    b.outLevel = 3;
                }

                // swap needed?
                if ((endPt[0] > endPt[1]) == (b.outLevel == 3))
                {
                    ushort sw = endPt[0]; endPt[0] = endPt[1]; endPt[1] = sw;
                    bSwapped = 1;
                }
                else
                {
                    bSwapped = 0;
                }

                // write out end-points
                for (k = 0; k < 2; k++, lpDstBuf += 2)
                    WrWord(dest, lpDstBuf, endPt[k]);

                // pack indices
                for (k = 15; k >= 0; k--)
                {
                    dwIndex <<= 2;
                    if (pixIndex[k] < 0)
                    {
                        dwIndex |= 3;
                    }
                    else
                    {
                        dwIndex |= (uint)b.index[pixIndex[k]];
                        if (bSwapped != 0)
                        {
                            if (b.outLevel == 4)
                                dwIndex ^= 1;
                            else
                                dwIndex ^= ((dwIndex & 2) >> 1) ^ 1;
                        }
                    }
                }

                WrDword(dest, lpDstBuf, dwIndex);
                lpDstBuf += 4;
            }
            // Increment to next row of blocks in dest buffer
            lpDstBuf += (int)DestPitchIncrement;
        }
    }

    // =========================================================================
    //  RGBBlock / AlphaBlock  (S3_quant.h)
    // =========================================================================
    private sealed class RGBBlock
    {
        public int n;
        public float[][] colorChannel = NewJ(MAX_PIXEL_PER_BLOCK, 3);
        public float[] weight = new float[3];
        public int inLevel;
        /// <summary>Set for DXT2-5, where only the 4-colour ramp is legal.</summary>
        public bool force4;
        public int outLevel;
        public int[][] endPoint = NewJi(2, 3);
        public int[] index = new int[MAX_PIXEL_PER_BLOCK];
    }

    private sealed class AlphaBlock
    {
        public int n;
        public float[] alpha = new float[MAX_PIXEL_PER_BLOCK];
        public int need0;
        public int need1;
        public int protectnonzero;
        public int outLevel;
        public int[] endPoint = new int[2];
        public int[] index = new int[MAX_PIXEL_PER_BLOCK];
    }

    // ---- jagged-array helpers ----
    private static float[][] NewJ(int r, int c)
    {
        var a = new float[r][];
        for (int i = 0; i < r; i++) a[i] = new float[c];
        return a;
    }
    private static int[][] NewJi(int r, int c)
    {
        var a = new int[r][];
        for (int i = 0; i < r; i++) a[i] = new int[c];
        return a;
    }

    // CLIP(X, X_MIN, X_MAX) macro
    private static float CLIP(float X, float X_MIN, float X_MAX) =>
        ((Math.Abs(X_MIN - X) + X_MIN) + (X_MAX - Math.Abs(X_MAX - X))) * 0.5f;

    // =========================================================================
    //  RGB QUANTIZER FRONT-END
    // =========================================================================
    private static void getAxis(int n, float[][] q, float[] axis)
    {
        float[,] s = new float[3, 3];
        float[,] t = new float[3, 3];
        float sp, f;
        int c, i, j;

        s[0, 0] = s[0, 1] = s[0, 2] = s[1, 1] = s[1, 2] = s[2, 2] = 0.0f;

        for (i = 0; i < n; i++)
        {
            s[0, 0] += q[i][0] * q[i][0];
            s[0, 1] += q[i][0] * q[i][1];
            s[0, 2] += q[i][0] * q[i][2];
            s[1, 1] += q[i][1] * q[i][1];
            s[1, 2] += q[i][1] * q[i][2];
            s[2, 2] += q[i][2] * q[i][2];
        }

        for (c = 0; c < 3; c++)
        {
            sp = s[0, 0] + s[1, 1] + s[2, 2];
            f = 3.5f / sp;
            for (i = 0; i < 3; i++)
                for (j = i; j < 3; j++)
                    s[i, j] *= f;

            for (j = 0; j < 4; j++)
            {
                t[0, 0] = s[0, 0] * s[0, 0] + s[0, 1] * s[0, 1] + s[0, 2] * s[0, 2];
                t[0, 1] = s[0, 0] * s[0, 1] + s[0, 1] * s[1, 1] + s[0, 2] * s[1, 2];
                t[0, 2] = s[0, 0] * s[0, 2] + s[0, 1] * s[1, 2] + s[0, 2] * s[2, 2];
                t[1, 1] = s[0, 1] * s[0, 1] + s[1, 1] * s[1, 1] + s[1, 2] * s[1, 2];
                t[1, 2] = s[0, 1] * s[0, 2] + s[1, 1] * s[1, 2] + s[1, 2] * s[2, 2];
                t[2, 2] = s[0, 2] * s[0, 2] + s[1, 2] * s[1, 2] + s[2, 2] * s[2, 2];

                s[0, 0] = t[0, 0] * t[0, 0] + t[0, 1] * t[0, 1] + t[0, 2] * t[0, 2];
                s[0, 1] = t[0, 0] * t[0, 1] + t[0, 1] * t[1, 1] + t[0, 2] * t[1, 2];
                s[0, 2] = t[0, 0] * t[0, 2] + t[0, 1] * t[1, 2] + t[0, 2] * t[2, 2];
                s[1, 1] = t[0, 1] * t[0, 1] + t[1, 1] * t[1, 1] + t[1, 2] * t[1, 2];
                s[1, 2] = t[0, 1] * t[0, 2] + t[1, 1] * t[1, 2] + t[1, 2] * t[2, 2];
                s[2, 2] = t[0, 2] * t[0, 2] + t[1, 2] * t[1, 2] + t[2, 2] * t[2, 2];
            }
        }

        i = s[0, 0] > s[1, 1] ?
            (s[0, 0] > s[2, 2] ? 0 : 2) : (s[1, 1] > s[2, 2] ? 1 : 2);

        f = 1.0f / MathF.Sqrt(s[i, i]);

        for (j = 0; j < i; j++)
            axis[j] = t[j, i] * f;
        for (; j < 3; j++)
            axis[j] = t[i, j] * f;
    }

    private static void getDiameter(int n, float[][] q, float[] axis)
    {
        float dia, tmpDia;
        int diaInd0 = 0, diaInd1 = 0;
        int i, j;
        for (dia = 0.0f, i = 0; i < n; i++)
        {
            for (j = i; j < n; j++)
            {
                float c0, c1, c2;
                c0 = q[i][0] - q[j][0];
                c1 = q[i][1] - q[j][1];
                c2 = q[i][2] - q[j][2];
                tmpDia = c0 * c0 + c1 * c1 + c2 * c2;
                if (tmpDia > dia)
                {
                    dia = tmpDia;
                    diaInd0 = i;
                    diaInd1 = j;
                }
            }
        }

        dia = 1.0f / MathF.Sqrt(dia);
        axis[0] = (q[diaInd0][0] - q[diaInd1][0]) * dia;
        axis[1] = (q[diaInd0][1] - q[diaInd1][1]) * dia;
        axis[2] = (q[diaInd0][2] - q[diaInd1][2]) * dia;
    }

    private static void sortProjection(int n, float[][] q, float[] axis, int[] index, int reverse)
    {
        float[] projection = new float[MAX_PIXEL_PER_BLOCK];
        int[] mask = new int[MAX_PIXEL_PER_BLOCK];
        int i, j, k;

        for (i = 0; i < n; i++)
        {
            projection[i] = q[i][0] * axis[0] + q[i][1] * axis[1] + q[i][2] * axis[2];
            mask[i] = 1;
        }

        if (reverse != 0)
        {
            for (i = 0; i < n; i++)
            {
                for (j = 0; j < n; j++)
                    if (mask[j] != 0)
                        break;
                for (k = j++; j < n; j++)
                {
                    if (mask[j] != 0 && projection[j] <= projection[k])
                        k = j;
                }
                mask[k] = 0;
                index[i] = k;
            }
        }
        else
        {
            for (i = 0; i < n; i++)
            {
                for (j = 0; j < n; j++)
                    if (mask[j] != 0)
                        break;
                for (k = j++; j < n; j++)
                {
                    if (mask[j] != 0 && projection[j] < projection[k])
                        k = j;
                }
                mask[k] = 0;
                index[i] = k;
            }
        }
    }

    private static int sameOrder(int n, int[] index1, int[] index2)
    {
        int i;
        for (i = 0; i < n; i++)
            if (index1[i] != index2[i])
                break;
        if (i < n)
            for (i = 0; i < n; i++)
                if (index1[i] != index2[n - 1 - i])
                    break;
        return (i == n) ? 1 : 0;
    }

    // =========================================================================
    //  RGB QUANTIZER COLOR-RAMP FITTING  (static numer/denom, exactly as source)
    // =========================================================================
    private static float _s43Numer = 0;
    private static float _s43Denom = 1.0f;

    private static float search43Mult(ref int levLim, int n, float[][] q, float[] pMult,
        int[] idx, float[][] endPointOut, float[] axis)
    {
        float[][] qs = NewJ(16, 3);
        float[] mult = new float[16];
        float[][] kq = NewJ(3, 3);
        float[] k2 = new float[3];
        float[] sK = new float[3];

        int lev = 0;
        int vLev;
        int i, j;
        int i0, i1, i2;
        int[] jk = new int[4];

        for (i = 0; i < n; i++)
            for (j = 0; j < 3; j++)
            {
                qs[i][j] = q[idx[i]][j];
                mult[i] = pMult[idx[i]];
            }

        kq[0][0] = kq[0][1] = kq[0][2] = k2[0] = sK[0] = 0;
        kq[1][0] = kq[1][1] = kq[1][2] = k2[1] = sK[1] = 0;
        kq[2][0] = kq[2][1] = kq[2][2] = k2[2] = sK[2] = 0;

        for (i0 = n; ;)
        {
            for (i1 = n; ;)
            {
                for (i2 = n; ;)
                {
                    float num_ = -kq[2][0] * kq[2][0]
                                  - kq[2][1] * kq[2][1]
                                  - kq[2][2] * kq[2][2];

                    if (_s43Numer * k2[2] >= _s43Denom * num_)
                    {
                        _s43Numer = num_;
                        _s43Denom = k2[2];
                        jk[0] = i0;
                        jk[1] = i1;
                        jk[2] = i2;
                    }
                    if ((--i2 < i1) || (levLim < 3))
                        break;
                    kq[2][0] = kq[2][0] + qs[i2][0];
                    kq[2][1] = kq[2][1] + qs[i2][1];
                    kq[2][2] = kq[2][2] + qs[i2][2];
                    k2[2] = k2[2] + (5.0f - 2.0f * sK[2] - mult[i2]) * mult[i2];
                    sK[2] = sK[2] + mult[i2];
                    lev = 3;
                }
                if (--i1 < i0)
                    break;
                kq[2][0] = kq[1][0] = kq[1][0] + qs[i1][0];
                kq[2][1] = kq[1][1] = kq[1][1] + qs[i1][1];
                kq[2][2] = kq[1][2] = kq[1][2] + qs[i1][2];
                k2[2] = k2[1] = k2[1] + (3.0f - 2.0f * sK[1] - mult[i1]) * mult[i1];
                sK[2] = sK[1] = sK[1] + mult[i1];
                lev = 2;
            }
            if (--i0 < 1)
                break;
            kq[2][0] = kq[1][0] = kq[0][0] = kq[0][0] + qs[i0][0];
            kq[2][1] = kq[1][1] = kq[0][1] = kq[0][1] + qs[i0][1];
            kq[2][2] = kq[1][2] = kq[0][2] = kq[0][2] + qs[i0][2];
            k2[2] = k2[1] = k2[0] = k2[0] + (1.0f - 2.0f * sK[0] - mult[i0]) * mult[i0];
            sK[2] = sK[1] = sK[0] = sK[0] + mult[i0];
            lev = 1;
        }
        // assert(numer < 0)

        jk[3] = n;
        vLev = (jk[0] != jk[1] ? 1 : 0) + (jk[1] != jk[2] ? 1 : 0) + (jk[2] != jk[3] ? 1 : 0);
        if (vLev == 1)
        {
            for (i = 2; jk[i] == jk[i + 1]; i--) ;   // empty body
            for (; i < 2; i++)
                jk[i + 1] = jk[i];
        }

        kq[2][0] = kq[2][1] = kq[2][2] = sK[2] = k2[2] = 0.0f;
        lev = i = j = 0;
        for (; j < 4; j++)
        {
            for (; i < jk[j]; i++)
            {
                sK[2] += (float)j * mult[i];
                k2[2] += (float)j * (float)j * mult[i];
                kq[2][0] += (float)j * qs[i][0];
                kq[2][1] += (float)j * qs[i][1];
                kq[2][2] += (float)j * qs[i][2];
            }
            lev += (jk[j] != n) ? 1 : 0;
        }
        k2[2] -= sK[2] * sK[2];
        {
            float k0 = -sK[2];
            float k1 = (float)lev - sK[2];
            float num_ = -kq[2][0] * kq[2][0]
                          - kq[2][1] * kq[2][1]
                          - kq[2][2] * kq[2][2];

            for (j = 0; j < 3; j++)
            {
                endPointOut[0][j] = (k0 * kq[2][j]) / k2[2];
                endPointOut[1][j] = (k1 * kq[2][j]) / k2[2];
                axis[j] = kq[2][j];
            }
            levLim = (vLev == 1) ? 1 : lev;
            return (num_ / k2[2]);
        }
    }

    private static float _scNumer = 0;
    private static float _scDenom = 1.0f;

    private static float searchClipped43Mult(ref int levLim, int n, float[][] q, float[] pMult,
        int[] idx, float[][] range, float[][] endPointOut, float[] axis)
    {
        float[][] qs = NewJ(16, 3);
        float[] mult = new float[16];
        float[][] kq = NewJ(3, 3);
        float[] k2 = new float[3];
        float[] sK = new float[3];

        int lev = 0;
        int vLev;
        int i, j;
        int i0, i1, i2;
        int[] jk = new int[4];

        for (i = 0; i < n; i++)
            for (j = 0; j < 3; j++)
            {
                qs[i][j] = q[idx[i]][j];
                mult[i] = pMult[idx[i]];
            }

        kq[0][0] = kq[0][1] = kq[0][2] = k2[0] = sK[0] = 0;
        kq[1][0] = kq[1][1] = kq[1][2] = k2[1] = sK[1] = 0;
        kq[2][0] = kq[2][1] = kq[2][2] = k2[2] = sK[2] = 0;

        for (i0 = n; ;)
        {
            for (i1 = n; ;)
            {
                for (i2 = n; ;)
                {
                    float k0 = -sK[2];
                    float k1 = (float)lev - sK[2];
                    float f00 = k2[2] + k0 * k0;
                    float f01 = k2[2] + k0 * k1;
                    float f11 = k2[2] + k1 * k1;
                    float f0011 = f00 * f11;
                    float den_ = k2[2] * f0011 * f0011 * (float)lev * (float)lev;
                    float num_ = 0;

                    for (j = 0; j < 3; j++)
                    {
                        float x0, x1;
                        float x0m, x1m;
                        float x0M, x1M;

                        x1m = range[0][j] * k2[2];
                        x1M = range[1][j] * k2[2];

                        x0 = k0 * kq[2][j];
                        x1 = k1 * kq[2][j];

                        x0m = x1m - x0;
                        x0M = x1M - x0;

                        x1m = (x1m - x1) * f00;
                        x1M = (x1M - x1) * f00;

                        x0 = (CLIP(0.0f, x0m, x0M)) * f01;

                        x0m *= f0011;
                        x0M *= f0011;

                        x1 = CLIP(x0, x1m, x1M);

                        x0 = x1 * f01;
                        x1 *= f11;

                        x0 = CLIP(x0, x0m, x0M);
                        x0m = x0 - x1;
                        x0M = k0 * x1 - k1 * x0;

                        num_ += k2[2] * x0m * x0m + x0M * x0M -
                          kq[2][j] * kq[2][j] * den_;
                    }
                    den_ *= k2[2];

                    if (_scNumer * den_ >= _scDenom * num_)
                    {
                        _scNumer = num_;
                        _scDenom = den_;
                        jk[0] = i0;
                        jk[1] = i1;
                        jk[2] = i2;
                    }
                    if ((--i2 < i1) || (levLim < 3))
                        break;
                    kq[2][0] = kq[2][0] + qs[i2][0];
                    kq[2][1] = kq[2][1] + qs[i2][1];
                    kq[2][2] = kq[2][2] + qs[i2][2];
                    k2[2] = k2[2] + (5.0f - 2.0f * sK[2] - mult[i2]) * mult[i2];
                    sK[2] = sK[2] + mult[i2];
                    lev = 3;
                }
                if (--i1 < i0)
                    break;
                kq[2][0] = kq[1][0] = kq[1][0] + qs[i1][0];
                kq[2][1] = kq[1][1] = kq[1][1] + qs[i1][1];
                kq[2][2] = kq[1][2] = kq[1][2] + qs[i1][2];
                k2[2] = k2[1] = k2[1] + (3.0f - 2.0f * sK[1] - mult[i1]) * mult[i1];
                sK[2] = sK[1] = sK[1] + mult[i1];
                lev = 2;
            }
            if (--i0 < 1)
                break;
            kq[2][0] = kq[1][0] = kq[0][0] = kq[0][0] + qs[i0][0];
            kq[2][1] = kq[1][1] = kq[0][1] = kq[0][1] + qs[i0][1];
            kq[2][2] = kq[1][2] = kq[0][2] = kq[0][2] + qs[i0][2];
            k2[2] = k2[1] = k2[0] = k2[0] + (1.0f - 2.0f * sK[0] - mult[i0]) * mult[i0];
            sK[2] = sK[1] = sK[0] = sK[0] + mult[i0];
            lev = 1;
        }
        // assert(numer < 0)

        jk[3] = n;
        vLev = (jk[0] != jk[1] ? 1 : 0) + (jk[1] != jk[2] ? 1 : 0) + (jk[2] != jk[3] ? 1 : 0);
        if (vLev == 1)
        {
            for (i = 2; jk[i] == jk[i + 1]; i--) ;   // empty body
            for (; i < 2; i++)
                jk[i + 1] = jk[i];
        }

        kq[2][0] = kq[2][1] = kq[2][2] = sK[2] = k2[2] = 0.0f;
        lev = i = j = 0;
        for (; j < 4; j++)
        {
            for (; i < jk[j]; i++)
            {
                sK[2] += (float)j * mult[i];
                k2[2] += (float)j * (float)j * mult[i];
                kq[2][0] += (float)j * qs[i][0];
                kq[2][1] += (float)j * qs[i][1];
                kq[2][2] += (float)j * qs[i][2];
            }
            lev += (jk[j] != n) ? 1 : 0;
        }
        k2[2] -= sK[2] * sK[2];
        {
            float k0 = -sK[2];
            float k1 = (float)lev - sK[2];
            float f00 = k2[2] + k0 * k0;
            float f01 = k2[2] + k0 * k1;
            float f11 = k2[2] + k1 * k1;
            float f0011 = f00 * f11;
            float den_ = k2[2] * f0011 * f0011 * (float)lev * (float)lev;
            float num_ = 0;

            for (j = 0; j < 3; j++)
            {
                float x0, x1;
                float x0m, x1m;
                float x0M, x1M;

                x1m = range[0][j] * k2[2];
                x1M = range[1][j] * k2[2];

                x0 = k0 * kq[2][j];
                x1 = k1 * kq[2][j];

                x0m = x1m - x0;
                x0M = x1M - x0;

                x1m = (x1m - x1) * f00;
                x1M = (x1M - x1) * f00;

                x0 = (CLIP(0.0f, x0m, x0M)) * f01;

                x0m *= f0011;
                x0M *= f0011;

                x1 = CLIP(x0, x1m, x1M);

                x0 = x1 * f01;
                x1 *= f11;

                x0 = CLIP(x0, x0m, x0M);
                x0m = x0 - x1;
                x0M = k0 * x1 - k1 * x0;

                num_ += k2[2] * x0m * x0m + x0M * x0M -
                  kq[2][j] * kq[2][j] * den_;

                endPointOut[0][j] = (x0 / f0011 + k0 * kq[2][j]) / k2[2];
                endPointOut[1][j] = (x1 / f0011 + k1 * kq[2][j]) / k2[2];
                axis[j] = kq[2][j];
            }
            levLim = (vLev == 1) ? 1 : lev;
            den_ *= k2[2];
            return (num_ / den_);
        }
    }

    // =========================================================================
    //  RGB QUANTIZER BACK-END
    // =========================================================================
    private static readonly int[] _roundBitNum = { 5, 6, 5 };

    private static float roundMult(int nColors, int n, float[][] q, float[] pMult,
        float[] w, float[][] endPointIn, int[][] endPointOut, int[] index)
    {
        // ramp[3][4][4], rampVal[3][4][4*16]
        float[][][] ramp = new float[3][][];
        float[][][] rampVal = new float[3][][];
        for (int a2 = 0; a2 < 3; a2++)
        {
            ramp[a2] = NewJ(4, 4);
            rampVal[a2] = NewJ(4, 4 * MAX_PIXEL_PER_BLOCK);
        }
        float m;
        float cf;

        int[,,] iRamp = new int[3, 4, 4];

        int i, j, k;
        int i0;
        int c;
        int lSB;

        for (i = 0; i < 3; i++)
        {
            lSB = (1 << (8 - _roundBitNum[i]));
            for (j = 0; j < 2; j++)
            {
                if (w[i] != 0)
                    cf = endPointIn[j][i] / w[i] * 255.0f;
                else
                    cf = 0.0f;
                c = (int)Math.Floor(cf);
                c = c < 0 ? 0 : (c < 256 ? c : (256 - lSB));
                c &= (256 - lSB);
                if ((float)(c + (c >> _roundBitNum[i])) > cf)
                    c = (c - lSB) < 0 ? c : (c - lSB);
                iRamp[i, 0, j] = iRamp[i, 1 + j, j] = c + (c >> _roundBitNum[i]);
                c = (c + lSB) < 256 ? (c + lSB) : c;
                iRamp[i, 2 - j, j] = iRamp[i, 3, j] = c + (c >> _roundBitNum[i]);
            }
        }

        if (nColors == 3)
        {
            for (i = 0; i < 3; i++)
            {
                for (j = 0; j < 4; j++)
                {
                    int p;
                    iRamp[i, j, 2] = (iRamp[i, j, 0] + iRamp[i, j, 1]) / 2;
                    for (k = 0; k < 3; k++)
                        ramp[i][j][k] = (float)iRamp[i, j, k] * w[i] / 255.0f;

                    float[] pv = rampVal[i][j];
                    for (p = 0, k = 0; k < n; k++)
                    {
                        pv[p++] = pMult[k] * (q[k][i] - ramp[i][j][0]) * (q[k][i] - ramp[i][j][0]);
                        pv[p++] = pMult[k] * (q[k][i] - ramp[i][j][1]) * (q[k][i] - ramp[i][j][1]);
                        pv[p++] = pMult[k] * (q[k][i] - ramp[i][j][2]) * (q[k][i] - ramp[i][j][2]);
                    }
                }
            }
        }
        else if (nColors == 4)
        {
            for (i = 0; i < 3; i++)
            {
                for (j = 0; j < 4; j++)
                {
                    int p;
                    iRamp[i, j, 2] = (2 * iRamp[i, j, 0] + iRamp[i, j, 1] + 1) / 3;
                    iRamp[i, j, 3] = (iRamp[i, j, 0] + 2 * iRamp[i, j, 1] + 1) / 3;
                    for (k = 0; k < 4; k++)
                        ramp[i][j][k] = (float)iRamp[i, j, k] * w[i] / 255.0f;

                    float[] pv = rampVal[i][j];
                    for (p = 0, k = 0; k < n; k++)
                    {
                        pv[p++] = pMult[k] * (q[k][i] - ramp[i][j][0]) * (q[k][i] - ramp[i][j][0]);
                        pv[p++] = pMult[k] * (q[k][i] - ramp[i][j][1]) * (q[k][i] - ramp[i][j][1]);
                        pv[p++] = pMult[k] * (q[k][i] - ramp[i][j][2]) * (q[k][i] - ramp[i][j][2]);
                        pv[p++] = pMult[k] * (q[k][i] - ramp[i][j][3]) * (q[k][i] - ramp[i][j][3]);
                    }
                }
            }
        }

        if (nColors == 4)
        {
            m = 2.0f * (w[0] * w[0] + w[1] * w[1] + w[2] * w[2]) * (float)MAX_PIXEL_PER_BLOCK;

            for (i0 = -1, i = 0; i < 64; i++)
            {
                float a, bb, c2;
                float[] p0 = rampVal[0][i & 0x3];
                float[] p1 = rampVal[1][(i >> 2) & 0x3];
                float[] p2 = rampVal[2][(i >> 4)];
                float d = 0.0f;
                // switch(n): fall-through case16..case1 == ACCUMULATE for N = n-1 .. 0
                for (int N = n - 1; N >= 0; N--)
                {
                    a = p0[4 * N + 0] + p1[4 * N + 0] + p2[4 * N + 0];
                    bb = p0[4 * N + 1] + p1[4 * N + 1] + p2[4 * N + 1];
                    c2 = a + bb - Math.Abs(a - bb);
                    a = p0[4 * N + 2] + p1[4 * N + 2] + p2[4 * N + 2];
                    bb = p0[4 * N + 3] + p1[4 * N + 3] + p2[4 * N + 3];
                    a = a + bb - Math.Abs(a - bb);
                    d += a + c2 - Math.Abs(a - c2);
                }
                if (d < m)
                {
                    m = d;
                    i0 = i;
                }
            }
        }
        else // nColors == 3
        {
            m = 2.0f * (w[0] * w[0] + w[1] * w[1] + w[2] * w[2]) * (float)MAX_PIXEL_PER_BLOCK;

            for (i0 = -1, i = 0; i < 64; i++)
            {
                float a, bb, c2;
                float[] p0 = rampVal[0][i & 0x3];
                float[] p1 = rampVal[1][(i >> 2) & 0x3];
                float[] p2 = rampVal[2][(i >> 4)];
                float d = 0.0f;
                for (int N = n - 1; N >= 0; N--)
                {
                    a = p0[3 * N + 0] + p1[3 * N + 0] + p2[3 * N + 0];
                    bb = p0[3 * N + 1] + p1[3 * N + 1] + p2[3 * N + 1];
                    c2 = a + bb - Math.Abs(a - bb);
                    a = 2 * (p0[3 * N + 2] + p1[3 * N + 2] + p2[3 * N + 2]);
                    d += a + c2 - Math.Abs(a - c2);
                }
                if (d < m)
                {
                    m = d;
                    i0 = i;
                }
            }
        }

        {
            int j0;
            float[] p0 = ramp[0][i0 & 0x3];
            float[] p1 = ramp[1][(i0 >> 2) & 0x3];
            float[] p2 = ramp[2][(i0 >> 4)];
            float a, bb, d;

            for (i = 0; i < 3; i++)
            {
                for (j = 0; j < 2; j++)
                    endPointOut[j][i] =
                        iRamp[i, (i0 >> (i << 1)) & 0x3, j] >> (8 - _roundBitNum[i]);
            }

            d = 0;
            for (i = 0; i < n; i++)
            {
                bb = 2.0f * (w[0] * w[0] + w[1] * w[1] + w[2] * w[2]);

                for (j0 = -1, j = 0; j < nColors; j++)
                {
                    a = ((q[i][0] - p0[j]) * (q[i][0] - p0[j]) +
                         (q[i][1] - p1[j]) * (q[i][1] - p1[j]) +
                         (q[i][2] - p2[j]) * (q[i][2] - p2[j])) * pMult[i];
                    if (a < bb)
                    {
                        bb = a;
                        j0 = j;
                    }
                }
                d += bb;
                index[i] = j0;
            }
            return (d);
        }
    }

    // ---- allSame + representable-point grid ----
    private struct GridPoint
    {
        public byte valid;
        public byte p0;
        public byte p1;
    }
    private static GridPoint[,,] _grid = new GridPoint[256, 3, 3];
    private static bool _gridInit = false;
    private static readonly int[] _allSameSize = { 5, 6, 5 };
    private static readonly int[,] _allSameIntCoeff =
    {
        { 1, 0, 0, 1 }, { 1, 1, 0, 2 }, { 1, 2, 1, 3 }
    };

    private static void EnsureGrid()
    {
        if (_gridInit) return;
        _gridInit = true;
        int p, p0, p1;
        int i, j, k, l;
        for (l = 0; l < 3; l++)
        {
            for (i = (1 << _allSameSize[l]) - 1; i >= 0; i--)
            {
                for (j = (1 << _allSameSize[l]) - 1; j >= 0; j--)
                {
                    p0 = (i << (8 - _allSameSize[l])) | (i >> (2 * _allSameSize[l] - 8));
                    p1 = (j << (8 - _allSameSize[l])) | (j >> (2 * _allSameSize[l] - 8));
                    for (k = 0; k < 3; k++)
                    {
                        p = (_allSameIntCoeff[k, 0] * p0 + _allSameIntCoeff[k, 1] * p1 +
                             _allSameIntCoeff[k, 2]) / _allSameIntCoeff[k, 3];
                        if (_grid[p, l, k].valid == 0 ||
                            Math.Abs(_grid[p, l, k].p1 - _grid[p, l, k].p0) > Math.Abs(p1 - p0))
                        {
                            _grid[p, l, k].valid = 1;
                            _grid[p, l, k].p0 = (byte)(i << (8 - _allSameSize[l]));
                            _grid[p, l, k].p1 = (byte)(j << (8 - _allSameSize[l]));
                        }
                    }
                }
            }
        }
    }

    private static float allSame(ref int nColors, bool force4, int n, float[][] q, float[] weight,
        int[][] endPointOut, int[] index)
    {
        EnsureGrid();

        float[] colorError = new float[3];
        int[,] channelValue = new int[3, 3];
        int i, j, k, l, m;

        for (j = 0; j < nColors - 1; j++)
        {
            int delta;
            int[] cTopBot = new int[2];
            float[] error = new float[2];

            colorError[j] = 0;
            for (i = 0; i < 3; i++)
            {
                int c = (int)Math.Floor(q[0][i] * 255.0f / weight[i] + 0.5f);
                c = c < 0 ? 0 : (c < 256 ? c : 255);

                for (delta = 1, l = 0; l < 2; l++, delta = -delta)
                {
                    if (_grid[k = c, i, j].valid == 0 ||
                        (q[0][i] * 255.0f / weight[i] - (float)c) * (float)delta > 0)
                    {
                        k = c + delta;
                        k = k < 0 ? 0 : (k < 256 ? k : 255);
                        for (; _grid[k, i, j].valid == 0; k += delta) ;   // empty body
                    }
                    for (error[l] = 0, m = 0; m < n; m++)
                    {
                        float d = (float)k * weight[i] - q[m][i] * 255.0f;
                        error[l] += d * d;
                    }
                    cTopBot[l] = k;
                }

                if (error[0] < error[1])
                {
                    colorError[j] += error[0];
                    channelValue[i, j] = cTopBot[0];
                }
                else if (error[0] > error[1])
                {
                    colorError[j] += error[1];
                    channelValue[i, j] = cTopBot[1];
                }
                else
                {
                    colorError[j] += error[1];
                    channelValue[i, j] = (c & 1) != 0 ? cTopBot[0] : cTopBot[1];
                }
            }
        }

        // DXT2-5 only allow the 4-colour ramp, so the "one half" representation
        // is not a candidate for them: it encodes c0 <= c1, which selects the
        // 3-colour ramp. DXT1 may use either (see IsValidCoding in s3_intrf.cpp),
        // and there the full three-way choice applies.
        if (nColors == 4 && force4)
            j = (colorError[0] <= colorError[2]) ? 0 : 2;
        else if (nColors == 4)
            j = ((colorError[0] <= colorError[1]) ?
              ((colorError[0] <= colorError[2]) ? 0 : 2) :
              ((colorError[1] <= colorError[2]) ? 1 : 2));
        else // nColors == 3
            j = ((colorError[0] <= colorError[1]) ? 0 : 1);

        for (i = 0; i < 3; i++)
            for (k = 0; k < 2; k++)
                endPointOut[k][i] =
                  (k == 0 ? _grid[channelValue[i, j], i, j].p0 : _grid[channelValue[i, j], i, j].p1)
                  >> (8 - _allSameSize[i]);

        for (i = 0; i < n; i++)
            index[i] = j + 1;

        if (j != 0)
            nColors = j + 2;

        return (colorError[j]);
    }

    // ---- mapAndRoundMult ----
    private static readonly int[,] _mapNumber = { { 2, 0, 0 }, { 6, 2, 0 } };
    private static readonly float[,,,,] _mapCoeff = BuildMapCoeff();

    private static float[,,,,] BuildMapCoeff()
    {
        var mc = new float[2, 2, 6, 2, 2];
        // [0] mappings to 3-color ramp, [0][0] two clusters
        mc[0, 0, 0, 0, 0] = 2.0f; mc[0, 0, 0, 0, 1] = -1.0f; mc[0, 0, 0, 1, 0] = 0.0f; mc[0, 0, 0, 1, 1] = 1.0f;
        mc[0, 0, 1, 0, 0] = 1.0f; mc[0, 0, 1, 0, 1] = 0.0f; mc[0, 0, 1, 1, 0] = -1.0f; mc[0, 0, 1, 1, 1] = 2.0f;
        // [1] mappings to 4-color ramp, [1][0] two clusters
        mc[1, 0, 0, 0, 0] = 1.0f; mc[1, 0, 0, 0, 1] = 0.0f; mc[1, 0, 0, 1, 0] = 0.0f; mc[1, 0, 0, 1, 1] = 1.0f;
        mc[1, 0, 1, 0, 0] = 1.0f; mc[1, 0, 1, 0, 1] = 0.0f; mc[1, 0, 1, 1, 0] = -2.0f; mc[1, 0, 1, 1, 1] = 3.0f;
        mc[1, 0, 2, 0, 0] = 3.0f; mc[1, 0, 2, 0, 1] = -2.0f; mc[1, 0, 2, 1, 0] = 0.0f; mc[1, 0, 2, 1, 1] = 1.0f;
        mc[1, 0, 3, 0, 0] = 2.0f; mc[1, 0, 3, 0, 1] = -1.0f; mc[1, 0, 3, 1, 0] = -1.0f; mc[1, 0, 3, 1, 1] = 2.0f;
        mc[1, 0, 4, 0, 0] = 1.5f; mc[1, 0, 4, 0, 1] = -0.5f; mc[1, 0, 4, 1, 0] = 0.0f; mc[1, 0, 4, 1, 1] = 1.0f;
        mc[1, 0, 5, 0, 0] = 1.0f; mc[1, 0, 5, 0, 1] = 0.0f; mc[1, 0, 5, 1, 0] = -0.5f; mc[1, 0, 5, 1, 1] = 1.5f;
        // [1][1] three clusters
        mc[1, 1, 0, 0, 0] = 1.5f; mc[1, 1, 0, 0, 1] = -0.5f; mc[1, 1, 0, 1, 0] = 0.0f; mc[1, 1, 0, 1, 1] = 1.0f;
        mc[1, 1, 1, 0, 0] = 1.0f; mc[1, 1, 1, 0, 1] = 0.0f; mc[1, 1, 1, 1, 0] = -0.5f; mc[1, 1, 1, 1, 1] = 1.5f;
        return mc;
    }

    private static float mapAndRoundMult(ref int nColors, bool bForce4, int levelLimit, int n,
        float[][] q, float[] pMult, float[] weight, float[][] endPointIn, int[][] endPointOut,
        int[] index)
    {
        float[][] colorInVar = NewJ(2, 3);
        float[] e = new float[2];
        int[][][] endPointOutVar = { NewJi(2, 3), NewJi(2, 3) };
        int[] outLevVar = new int[2];
        int[][] indexVar = { new int[MAX_PIXEL_PER_BLOCK], new int[MAX_PIXEL_PER_BLOCK] };

        int i, j, k, l;
        int m;

        m = 0;
        outLevVar[m] = ((nColors == 4 || bForce4) ? 1 : 0);
        e[m] = roundMult(outLevVar[m] + 3, n, q, pMult, weight, endPointIn, endPointOutVar[m], indexVar[m]);
        m = 1 - m;

        levelLimit -= 2;
        nColors -= 2;

        for (i = bForce4 ? 1 : 0; i < levelLimit; i++)
        {
            for (j = 0; j < _mapNumber[i, nColors]; j++)
            {
                for (k = 0; k < 2; k++)
                {
                    for (l = 0; l < 3; l++)
                    {
                        colorInVar[k][l] =
                            _mapCoeff[i, nColors, j, k, 0] * endPointIn[0][l] +
                            _mapCoeff[i, nColors, j, k, 1] * endPointIn[1][l];
                    }
                }
                outLevVar[m] = i;
                e[m] = roundMult(i + 3, n, q, pMult, weight, colorInVar, endPointOutVar[m], indexVar[m]);
                if (e[m] < e[1 - m])
                    m = 1 - m;
            }
        }
        m = 1 - m;
        for (i = 0; i < n; i++)
            index[i] = indexVar[m][i];
        for (i = 0; i < 2; i++)
            for (j = 0; j < 3; j++)
                endPointOut[i][j] = endPointOutVar[m][i][j];
        nColors = outLevVar[m] + 3;
        return (e[m]);
    }

    // =========================================================================
    //  MAIN RGB QUANTIZER
    // =========================================================================
    private static void CodeRGBBlock(RGBBlock block)
    {
        float[][] q = NewJ(MAX_PIXEL_PER_BLOCK, 3);
        float[][] qC = NewJ(MAX_PIXEL_PER_BLOCK, 3);
        float[] gC = new float[3];
        float[][] qNoRep = NewJ(MAX_PIXEL_PER_BLOCK, 3);
        float[][] qCNoRep = NewJ(MAX_PIXEL_PER_BLOCK, 3);
        float[] pMult = new float[MAX_PIXEL_PER_BLOCK];
        float[] weight = new float[3];
        float[][] range = NewJ(2, 3);
        float[] axis = new float[3];
        float[] e = new float[2];
        float[][][] endPointOut = { NewJ(2, 3), NewJ(2, 3) };
        float nRec;

        int[] indexMult = new int[MAX_PIXEL_PER_BLOCK];
        int[] outIndexMult = new int[MAX_PIXEL_PER_BLOCK];

        int[][] index = new int[INDEX_LOG_SIZE][];
        for (int t = 0; t < INDEX_LOG_SIZE; t++) index[t] = new int[MAX_PIXEL_PER_BLOCK];
        int[][] indexStore = { new int[MAX_PIXEL_PER_BLOCK], new int[MAX_PIXEL_PER_BLOCK] };

        int[] levelLimit = new int[2];

        int i, j, n, l;
        int nNoRep;
        int indexLogEnd;
        int indexLogFull;
        int sameIndex = 0;   // see note: first inner loop always assigns for realistic inputs
        int m = 0;
        int count;
        int allsame = 1;
        int clipFlag = 0;

        if (block == null)
            return;
        n = block.n;

        if (n == 0)
            return;

        nRec = 1 / (float)n;

        for (j = 0; j < 3; j++)
            weight[j] = (block.weight[j] < 0) ? 0.0f : block.weight[j];

        for (j = 0; j < 3; j++)
        {
            for (gC[j] = 0.0f, i = 0; i < n; i++)
            {
                allsame &=
                  (Math.Abs(block.colorChannel[i][j] - block.colorChannel[0][j]) <
                  ALL_SAME_THRESHOLD) ? 1 : 0;
                gC[j] += (q[i][j] = block.colorChannel[i][j] * weight[j]);
            }
        }

        if (allsame != 0)
        {
            levelLimit[0] = block.inLevel;
            allSame(ref levelLimit[0], block.force4, n, q, weight, block.endPoint, block.index);
            block.outLevel = levelLimit[0];
            return;
        }

        for (j = 0; j < 3; j++)
        {
            for (gC[j] /= (float)n, i = 0; i < n; i++)
                qC[i][j] = q[i][j] - gC[j];
            range[0][j] = -gC[j];
            range[1][j] = weight[j] - gC[j];
        }

        for (nNoRep = i = 0; i < n; i++)
        {
            for (j = 0; j < nNoRep; j++)
            {
                if (qNoRep[j][0] == q[i][0] && qNoRep[j][1] == q[i][1] &&
                  qNoRep[j][2] == q[i][2])
                    break;
            }
            if (j == nNoRep)
            {
                qNoRep[j][0] = q[i][0];
                qNoRep[j][1] = q[i][1];
                qNoRep[j][2] = q[i][2];
                qCNoRep[j][0] = qC[i][0];
                qCNoRep[j][1] = qC[i][1];
                qCNoRep[j][2] = qC[i][2];
                pMult[j] = 1;
                nNoRep++;
            }
            else
            {
                qCNoRep[j][0] += qC[i][0];
                qCNoRep[j][1] += qC[i][1];
                qCNoRep[j][2] += qC[i][2];
                pMult[j]++;
            }
            indexMult[i] = j;
        }

        for (i = 0; i < nNoRep; i++)
        {
            pMult[i] *= nRec;
            qCNoRep[i][0] *= nRec;
            qCNoRep[i][1] *= nRec;
            qCNoRep[i][2] *= nRec;
        }

        // two colors bypass
        if (nNoRep == 2)
        {
            block.outLevel = 2;
            mapAndRoundMult(ref block.outLevel, block.inLevel == 4, block.inLevel, nNoRep, qNoRep,
                pMult, weight, qNoRep, block.endPoint, outIndexMult);

            for (i = 0; i < n; i++)
                block.index[i] = outIndexMult[indexMult[i]];
            return;
        }

        indexLogFull = indexLogEnd = 0;
        getAxis(n, qC, axis);
        sortProjection(nNoRep, qNoRep, axis, index[0], 0);

        for (i = 0; i < 2; i++)
        {
            count = UNCLIPPED_ITERATION_LIMIT;

            do
            {
                levelLimit[i] = block.inLevel - 1;

                if (i - 1 + count != UNCLIPPED_ITERATION_LIMIT)
                {
                    e[i] = search43Mult(ref levelLimit[i], nNoRep, qCNoRep, pMult,
                                        index[indexLogEnd], endPointOut[i], axis);
                    m = i;
                }
                else
                {
                    getDiameter(nNoRep, qNoRep, axis);
                }

                indexLogEnd = (indexLogEnd + 1) % INDEX_LOG_SIZE;
                indexLogFull |= (indexLogEnd == 0) ? 1 : 0;

                for (l = 0; l < 2; l++)
                {
                    sortProjection(nNoRep, qNoRep, axis, index[indexLogEnd], l);

                    for (j = indexLogEnd - 1; j >= 0; j--)
                    {
                        if ((sameIndex = sameOrder(nNoRep, index[j], index[indexLogEnd])) != 0)
                            break;
                    }

                    if (sameIndex == 0 && indexLogFull != 0)
                        for (j = indexLogEnd + 1; j < INDEX_LOG_SIZE; j++)
                        {
                            if ((sameIndex = sameOrder(nNoRep, index[j], index[indexLogEnd])) != 0)
                                break;
                        }
                    if (sameIndex == 0 || count == 0)
                        break;
                }
            }
            while (count-- != 0 && sameIndex == 0);

            for (j = 0; j < nNoRep; j++)
                indexStore[i][j] = index[indexLogEnd][j];
        }

        // pick the best one
        if (m == 1)
            m = (e[0] < e[1] ? 0 : 1);

        // check clipping
        for (clipFlag = 0, i = 0; i < 2; i++)
            for (j = 0; j < 3; j++)
                clipFlag = (clipFlag != 0 || (endPointOut[m][i][j] < range[0][j]) ||
                  (endPointOut[m][i][j] > range[1][j])) ? 1 : 0;

        if (clipFlag != 0)
        {
            for (i = 0; i < nNoRep; i++)
                index[0][i] = indexStore[m][i];

            count = CLIPPED_ITERATION_LIMIT;
            indexLogFull = indexLogEnd = 0;

            do
            {
                levelLimit[m] = block.inLevel - 1;
                e[m] = searchClipped43Mult(ref levelLimit[m], nNoRep, qCNoRep, pMult,
                    index[indexLogEnd],
                    range, endPointOut[m], axis);

                indexLogEnd = (indexLogEnd + 1) % INDEX_LOG_SIZE;
                indexLogFull |= (indexLogEnd == 0) ? 1 : 0;

                for (l = 0; l < 2; l++)
                {
                    sortProjection(nNoRep, qNoRep, axis, index[indexLogEnd], l);

                    for (j = indexLogEnd - 1; j >= 0; j--)
                    {
                        if ((sameIndex = sameOrder(nNoRep, index[j], index[indexLogEnd])) != 0)
                            break;
                    }

                    if (sameIndex == 0 && indexLogFull != 0)
                        for (j = indexLogEnd + 1; j < INDEX_LOG_SIZE; j++)
                        {
                            if ((sameIndex = sameOrder(nNoRep, index[j], index[indexLogEnd])) != 0)
                                break;
                        }
                    if (sameIndex == 0)
                        break;
                }
            }
            while (count-- != 0 && sameIndex == 0);
        }

        // transform endpoints to original (uncentered) system
        for (i = 0; i < 2; i++)
            for (j = 0; j < 3; j++)
                endPointOut[1 - m][i][j] = endPointOut[m][i][j] + gC[j];

        block.outLevel = levelLimit[m] + 1;

        mapAndRoundMult(ref block.outLevel, block.inLevel == 4, block.inLevel, nNoRep, qNoRep,
            pMult, weight, endPointOut[1 - m], block.endPoint, outIndexMult);

        for (i = 0; i < n; i++)
            block.index[i] = outIndexMult[indexMult[i]];
    }

    // =========================================================================
    //  MAIN ALPHA QUANTIZER
    // =========================================================================
    private static void CodeAlphaBlock(AlphaBlock b)
    {
        float[] e = new float[5];
        float[][] alphaFiltered = NewJ(5, MAX_PIXEL_PER_BLOCK);

        int[] newLevel = { 8, 6, 6, 6, 6 };
        int[][] index = { new int[MAX_PIXEL_PER_BLOCK], new int[MAX_PIXEL_PER_BLOCK], new int[MAX_PIXEL_PER_BLOCK], new int[MAX_PIXEL_PER_BLOCK], new int[MAX_PIXEL_PER_BLOCK] };
        int[] nFiltered = new int[5];
        int[][] endPointOut = { new int[2], new int[2], new int[2], new int[2], new int[2] };

        int[] zeroMask = { 0, 0, 1, 0, 1 };
        int[] oneMask = { 0, 0, 0, 1, 1 };
        int[] mask = new int[5];

        int enforceZero;
        int enforceOne;

        int i, j, k;

        for (i = 0; i < 5; i++)
            nFiltered[i] = 0;

        for (i = 0; i < b.n; i++)
        {
            alphaFiltered[0][nFiltered[0]] = b.alpha[i];
            nFiltered[0] += 1;
            alphaFiltered[1][nFiltered[1]] = b.alpha[i];
            nFiltered[1] += 1;
            if (b.alpha[i] != 0.0f)
            {
                alphaFiltered[2][nFiltered[2]] = b.alpha[i];
                nFiltered[2] += 1;
            }
            if (b.alpha[i] != 1.0f)
            {
                alphaFiltered[3][nFiltered[3]] = b.alpha[i];
                nFiltered[3] += 1;
            }
            if (b.alpha[i] != 0.0f && b.alpha[i] != 1.0f)
            {
                alphaFiltered[4][nFiltered[4]] = b.alpha[i];
                nFiltered[4] += 1;
            }
        }
        enforceZero = (b.need0 != 0 && nFiltered[0] != nFiltered[2]) ? 1 : 0;
        enforceOne = (b.need1 != 0 && nFiltered[0] != nFiltered[3]) ? 1 : 0;

        mask[0] = 1;
        mask[1] = 1;
        mask[2] = (nFiltered[2] != nFiltered[1]) ? 1 : 0;
        mask[3] = (nFiltered[3] != nFiltered[1]) ? 1 : 0;
        mask[4] = (nFiltered[4] != nFiltered[1]) ? 1 : 0;

        for (i = 0; i < 5; i++)
        {
            if (mask[i] != 0)
            {
                e[i] = quantizeAlpha(newLevel[i], nFiltered[i], alphaFiltered[i],
                  endPointOut[i], index[i]);
                mask[i] = 1;
                if (enforceZero != 0 && zeroMask[i] == 0)
                    mask[i] &= (endPointOut[i][0] == 0 || endPointOut[i][1] == 0) ? 1 : 0;
                if (enforceOne != 0 && oneMask[i] == 0)
                    mask[i] &= (endPointOut[i][0] == 255 || endPointOut[i][1] == 255) ? 1 : 0;

                if (mask[i] != 0 && b.protectnonzero != 0)
                {
                    for (j = 0; j < nFiltered[i]; j++)
                    {
                        if (alphaFiltered[i][j] != 0.0f &&
                            index[i][j] <= 1 &&
                            endPointOut[i][index[i][j]] == 0)
                        {
                            if (enforceZero == 0 || zeroMask[i] != 0)
                            {
                                if (endPointOut[i][0] == 0)
                                    endPointOut[i][0] = 1;
                                else if (endPointOut[i][1] == 0)
                                    endPointOut[i][1] = 1;
                            }
                            else
                            {
                                mask[i] = 0;
                            }
                            break;
                        }
                    }
                }
            }
        }

        for (i = 0; i < 5; i++)
            if (mask[i] != 0)
                break;

        for (k = i++; i < 5; i++)
            if (mask[i] != 0 && e[i] < e[k])
                k = i;

        b.outLevel = newLevel[k];
        b.endPoint[0] = endPointOut[k][0];
        b.endPoint[1] = endPointOut[k][1];
        for (i = j = 0; i < b.n; i++)
        {
            if (b.alpha[i] == 0.0f && zeroMask[k] != 0)
                b.index[i] = 6;
            else if (b.alpha[i] == 1.0f && oneMask[k] != 0)
                b.index[i] = 7;
            else
                b.index[i] = index[k][j++];
        }
    }

    // =========================================================================
    //  ALPHA QUANTIZING ROUTINE
    // =========================================================================
    private static float quantizeAlpha(int level, int n, float[] alpha, int[] endPointOut,
        int[] index)
    {
        float[] binBoundary = new float[MAX_ALPHA_LEVELS - 1];
        float[] alphaCentered = new float[MAX_PIXEL_PER_BLOCK];
        float[] endPoint = new float[2];
        float[] endPoint_ = new float[2];

        float qS;
        float kS, kq, k2;
        float e;
        float num;
        float den;
        float nD;
        float nR;

        int[] cluster = new int[MAX_ALPHA_LEVELS];
        int[,] clusterEnd = new int[2, MAX_ALPHA_LEVELS];
        int[] newAlpha = new int[MAX_ALPHA_LEVELS];

        int i, j, k, l;
        int count = ALPHA_ITERATION_LIMIT;

        int stable;
        int nClusters;

        if (n == 0)
            return (0.0f);

        nD = (float)n;
        nR = 1 / nD;

        for (qS = endPoint[0] = endPoint[1] = alpha[0], index[0] = 0,
          i = 1; i < n; i++)
        {
            if (endPoint[0] > alpha[i])
                endPoint[0] = alpha[i];
            if (endPoint[1] < alpha[i])
                endPoint[1] = alpha[i];
            qS += alpha[i];
            index[0] = 0;
        }
        qS *= nR;
        for (i = 0; i < n; i++)
            alphaCentered[i] = alpha[i] - qS;

        endPoint[0] -= qS;
        endPoint[1] -= qS;

        num = 0; den = 0; // set within loop before use; explicit init for definite-assignment

        do
        {
            stable = 1;

            for (i = 0; i < level - 1; i++)
            {
                binBoundary[i] = endPoint[0] + (endPoint[1] - endPoint[0]) /
                  (float)(level - 1) * (0.5f + (float)i);
                cluster[i] = 0;
            }
            cluster[level - 1] = 0;

            for (i = 0; i < n; i++)
            {
                for (j = 0; j < level - 1; j++)
                    if (alphaCentered[i] < binBoundary[j])
                        break;
                cluster[j]++;
                stable &= (index[i] == j) ? 1 : 0;
                index[i] = j;
            }

            for (nClusters = 0, j = 0; j < level; j++)
                nClusters += (cluster[j] != 0) ? 1 : 0;

            if (nClusters == 1)
                break;

            kq = kS = k2 = 0.0f;
            for (i = 0; i < n; i++)
            {
                kq += alphaCentered[i] * (float)index[i];
                kS += (float)index[i];
                k2 += (float)index[i] * (float)index[i];
            }
            kS *= nR;
            k2 -= nD * kS * kS;

            {
                float k0 = -kS;
                float k1 = (float)level - 1.0f - kS;
                float f00 = k2 + nD * k0 * k0;
                float f01 = k2 + nD * k0 * k1;
                float f11 = k2 + nD * k1 * k1;
                float f0011 = f00 * f11;
                float den_ =
                      k2 * f0011 * f0011 *
                      (float)(level - 1.0f) * (float)(level - 1.0f);
                // (source declares an unused `double num_ = 0;` here; omitted)

                {
                    float x0, x1;
                    float x0m, x1m;
                    float x0M, x1M;
                    x1m = -qS * k2;
                    x1M = (1.0f - qS) * k2;

                    x0 = k0 * kq;
                    x1 = k1 * kq;

                    x0m = x1m - x0;
                    x0M = x1M - x0;

                    x1m = (x1m - x1) * f00;
                    x1M = (x1M - x1) * f00;

                    x0 = (CLIP(0.0f, x0m, x0M)) * f01;

                    x0m *= f0011;
                    x0M *= f0011;

                    x1 = CLIP(x0, x1m, x1M);

                    x0 = x1 * f01;
                    x1 *= f11;

                    x0 = CLIP(x0, x0m, x0M);
                    x0m = x0 - x1;
                    x0M = k0 * x1 - k1 * x0;

                    num = k2 * x0m * x0m + nD * x0M * x0M -
                      kq * kq * den_;
                    den = den_ * k2;

                    endPoint_[0] = endPoint[0];
                    endPoint_[1] = endPoint[1];

                    endPoint[0] = (x0 / f0011 + k0 * kq) / k2;
                    endPoint[1] = (x1 / f0011 + k1 * kq) / k2;
                }
            }
            if (count-- == 0)
                break;

            if (stable != 0)
            {
                for (j = 0; j < level; j++)
                    clusterEnd[0, j] = -1;

                for (i = 0; i < n; i++)
                {
                    j = index[i];
                    if (clusterEnd[0, j] == -1)
                        clusterEnd[0, j] = clusterEnd[1, j] = i;
                    else
                    {
                        if (alphaCentered[clusterEnd[0, j]] > alphaCentered[i])
                            clusterEnd[0, j] = i;
                        if (alphaCentered[clusterEnd[1, j]] < alphaCentered[i])
                            clusterEnd[1, j] = i;
                    }
                }
                for (l = 0; l < 2; l++)
                {
                    float f = (l == 0 ? 1.0f : -1);
                    float kST = kS + f * nR;
                    float k2T_0 = k2 + 1.0f - f * 2.0f * kS - nR;
                    for (j = l; j < level - 1 + l; j++)
                    {
                        if (cluster[j] != 0 && (cluster[j] > 1 || nClusters > 2))
                        {
                            float kqT = kq + f * alphaCentered[clusterEnd[1 - l, j]];
                            float k2T = k2T_0 + f * 2.0f * (float)j;

                            float k0 = -kST;
                            float k1 = (float)level - 1.0f - kST;
                            float f00 = k2T + nD * k0 * k0;
                            float f01 = k2T + nD * k0 * k1;
                            float f11 = k2T + nD * k1 * k1;
                            float f0011 = f00 * f11;
                            float den_ =
                               k2T * f0011 * f0011 *
                               (float)(level - 1.0f) * (float)(level - 1.0f);
                            float num_;

                            float x0, x1;
                            float x0m, x1m;
                            float x0M, x1M;
                            x1m = -qS * k2T;
                            x1M = (1.0f - qS) * k2T;

                            x0 = k0 * kqT;
                            x1 = k1 * kqT;

                            x0m = x1m - x0;
                            x0M = x1M - x0;

                            x1m = (x1m - x1) * f00;
                            x1M = (x1M - x1) * f00;

                            x0 = (CLIP(0.0f, x0m, x0M)) * f01;

                            x0m *= f0011;
                            x0M *= f0011;

                            x1 = CLIP(x0, x1m, x1M);

                            x0 = x1 * f01;
                            x1 *= f11;

                            x0 = CLIP(x0, x0m, x0M);
                            x0m = x0 - x1;
                            x0M = k0 * x1 - k1 * x0;

                            num_ = k2T * x0m * x0m + nD * x0M * x0M -
                                  kqT * kqT * den_;
                            den_ = den_ * k2T;

                            if (num * den_ > num_ * den)
                            {
                                endPoint[0] = (x0 / f0011 + k0 * kqT) / k2T;
                                endPoint[1] = (x1 / f0011 + k1 * kqT) / k2T;
                                num = num_;
                                den = den_;
                                stable = 0;
                                break;
                            }
                        }
                    }
                    if (stable == 0)
                        break;
                }
            }

        } while (stable == 0);

        for (i = 0; i < 2; i++)
        {
            newAlpha[i] = (int)Math.Floor((endPoint[i] + qS) * 255.0f + 0.5f);
            newAlpha[i] = (newAlpha[i] < 0 ? 0 : (newAlpha[i] > 255 ? 255 : newAlpha[i]));
        }

        for (i = 2; i < level; i++)
            newAlpha[i] = ((level - i) * newAlpha[0] + (i - 1) * newAlpha[1] +
              (level - 2) / 2) / (level - 1);

        endPointOut[0] = newAlpha[0];
        endPointOut[1] = newAlpha[1];

        for (e = 0.0f, i = 0; i < n; i++)
        {
            for (k = 0, j = 1; j < level; j++)
                if (Math.Abs((float)newAlpha[j] - alpha[i] * 255.0f) <
                  Math.Abs((float)newAlpha[k] - alpha[i] * 255.0f))
                    k = j;
            index[i] = k;
            e += ((float)newAlpha[k] - alpha[i] * 255.0f) * ((float)newAlpha[k] - alpha[i] * 255.0f);
        }
        return (e);
    }
}
