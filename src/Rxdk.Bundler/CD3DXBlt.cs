// Faithful, byte-exact C# port of the XDK Bundler tool's CD3DXBlt.h / CD3DXBlt.cpp.
//
// PRECISION CAVEAT: the XDK bundler runs with _controlfp(_PC_24) — the x87 FPU forced to
// single-precision "to maintain bit-for-bit output". This port keeps the original C
// `double` filter math, which C# evaluates at full precision, so a small fraction of
// resampled pixels differ by +/-1 from the golden .xpr (e.g. a 256x256->256x128 downscale).
// Non-resized conversions are byte-exact; resized/mip output is visually identical.
// 100% byte-exact would require emulating _PC_24 (single-precision intermediates).
//
// The blitter picks a resample filter (same/copy/none/point/box/linear/triangle) and
// drives the per-format codecs (CD3DXCodec.cs) to read source rows into float
// D3DXCOLOR[], resample in float, and write via the destination codec. Weight tables
// and accumulation math are translated line-for-line to preserve the exact output.
//
// "D3DXCOLOR*" pointers become an (array, int offset) pair. The variable-length
// triangle-filter description is kept as a raw byte buffer walked exactly like the
// original pointer arithmetic.

namespace Rxdk.Bundler;

internal sealed class CXD3DXBlt
{
    // ---- HRESULT ----
    public const int S_OK = 0;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int D3DERR_INVALIDCALL = unchecked((int)0x8876086C);

    public static bool FAILED(int hr) => hr < 0;
    public static bool SUCCEEDED(int hr) => hr >= 0;

    private CXD3DXCodec? m_pSrc;
    private CXD3DXCodec? m_pDest;
    private uint m_dwFilter;

    public CXD3DXBlt()
    {
        m_pDest = null;
        m_pSrc = null;
    }

    public int Blt(D3DX_BLT pDestBlt, D3DX_BLT pSrcBlt, uint dwFilter)
    {
        int hr;

        m_pDest = null;
        m_pSrc = null;
        m_dwFilter = dwFilter;

        // Validate filter
        switch (dwFilter & 0xffff)
        {
            case D3DX.FILTER_NONE:
            case D3DX.FILTER_POINT:
            case D3DX.FILTER_LINEAR:
            case D3DX.FILTER_TRIANGLE:
            case D3DX.FILTER_BOX:
                break;

            default:
                return D3DERR_INVALIDCALL;
        }

        if ((dwFilter & (0xffff0000u & ~(D3DX.FILTER_MIRROR | D3DX.FILTER_DITHER))) != 0)
        {
            return D3DERR_INVALIDCALL;
        }

        pDestBlt.bDither = (dwFilter & D3DX.FILTER_DITHER) != 0;

        try
        {
            // Find codecs
            m_pDest = CXD3DXCodec.Create(pDestBlt);
            if (m_pDest == null || (m_pSrc = CXD3DXCodec.Create(pSrcBlt)) == null)
            {
                hr = E_FAIL;
                goto LDone;
            }

            // Make sure compatible image types
            if (m_pDest.m_dwType != m_pSrc.m_dwType)
            {
                hr = E_FAIL;
                goto LDone;
            }

            // Find blitter
            if (FAILED(BltSame()) &&
                FAILED(BltCopy()) &&
                FAILED(BltNone()) &&
                FAILED(BltPoint()) &&
                FAILED(BltBox2D()) &&
                FAILED(BltBox3D()) &&
                FAILED(BltLinear2D()) &&
                FAILED(BltLinear3D()) &&
                FAILED(BltTriangle2D()) &&
                FAILED(BltTriangle3D()))
            {
                hr = E_FAIL;
                goto LDone;
            }

            hr = S_OK;

        LDone:
            // The C++ codec destructors flush cached writes (YUV/DXT) on delete.
            if (m_pDest != null) m_pDest.Finish();
            if (m_pSrc != null) m_pSrc.Finish();
            return hr;
        }
        finally
        {
            m_pDest = null;
            m_pSrc = null;
        }
    }

    private int BltSame()
    {
        if (m_pDest!.m_Format != m_pSrc!.m_Format)
            return E_FAIL;

        if (m_pSrc.m_bColorKey)
            return E_FAIL;

        if (m_pDest.m_uWidth != m_pSrc.m_uWidth ||
            m_pDest.m_uHeight != m_pSrc.m_uHeight ||
            m_pDest.m_uDepth != m_pSrc.m_uDepth)
        {
            return E_FAIL;
        }

        // SUPPORT_DXT (BltSame_DXTn) was not compiled for the bundler.

        if (m_pDest.m_bPalettized && !ReferenceEquals(m_pDest.m_pPalette, m_pSrc.m_pPalette) &&
            PaletteDiffers(m_pDest.m_pPalette, m_pSrc.m_pPalette))
        {
            return E_FAIL;
        }

        for (uint uZ = 0; uZ < m_pDest.m_uDepth; uZ++)
        {
            int pbDest = m_pDest.m_pbOffset + (int)(uZ * m_pDest.m_uSlice);
            int pbSrc = m_pSrc.m_pbOffset + (int)(uZ * m_pSrc.m_uSlice);

            for (uint uY = 0; uY < m_pDest.m_uHeight; uY++)
            {
                Array.Copy(m_pSrc.m_pbData, pbSrc, m_pDest.m_pbData, pbDest, (int)m_pDest.m_uWidthBytes);

                pbDest += (int)m_pDest.m_uPitch;
                pbSrc += (int)m_pSrc.m_uPitch;
            }
        }

        return S_OK;
    }

    // Faithful to the original memcmp of 256*sizeof(PALETTEENTRY) == 1024 bytes,
    // which spans the first 64 D3DXCOLOR entries of the converted palettes.
    private static bool PaletteDiffers(D3DXCOLOR[] a, D3DXCOLOR[] b)
    {
        for (int i = 0; i < 64; i++)
        {
            if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
                return true;
        }
        return false;
    }

    private int BltCopy()
    {
        if (m_pDest!.m_uWidth != m_pSrc!.m_uWidth ||
            m_pDest.m_uHeight != m_pSrc.m_uHeight ||
            m_pDest.m_uDepth != m_pSrc.m_uDepth)
        {
            return E_FAIL;
        }

        var pColor = new D3DXCOLOR[m_pDest.m_uWidth];

        for (uint uZ = 0; uZ < m_pDest.m_uDepth; uZ++)
        {
            for (uint uY = 0; uY < m_pDest.m_uHeight; uY++)
            {
                m_pSrc.Decode(uY, uZ, pColor, 0);
                m_pDest.Encode(uY, uZ, pColor, 0);
            }
        }

        return S_OK;
    }

    private int BltNone()
    {
        if ((m_dwFilter & 0xff) != D3DX.FILTER_NONE)
            return E_FAIL;

        uint uWidthMax = (m_pDest!.m_uWidth > m_pSrc!.m_uWidth) ? m_pDest.m_uWidth : m_pSrc.m_uWidth;
        uint uHeightMin = (m_pDest.m_uHeight < m_pSrc.m_uHeight) ? m_pDest.m_uHeight : m_pSrc.m_uHeight;
        uint uDepthMin = (m_pDest.m_uDepth < m_pSrc.m_uDepth) ? m_pDest.m_uDepth : m_pSrc.m_uDepth;

        var pColor = new D3DXCOLOR[uWidthMax];
        var pBlack = new D3DXCOLOR[m_pDest.m_uWidth];
        // new D3DXCOLOR[] is zero-initialized, matching memset(..., 0x00, ...).

        uint uY, uZ;

        for (uZ = 0; uZ < uDepthMin; uZ++)
        {
            for (uY = 0; uY < uHeightMin; uY++)
            {
                m_pSrc.Decode(uY, uZ, pColor, 0);
                m_pDest.Encode(uY, uZ, pColor, 0);
            }

            for (uY = uHeightMin; uY < m_pDest.m_uHeight; uY++)
                m_pDest.Encode(uY, uZ, pBlack, 0);
        }

        for (uZ = uDepthMin; uZ < m_pDest.m_uDepth; uZ++)
        {
            for (uY = 0; uY < m_pDest.m_uHeight; uY++)
                m_pDest.Encode(uY, uZ, pBlack, 0);
        }

        return S_OK;
    }

    private int BltPoint()
    {
        if ((m_dwFilter & 0xff) != D3DX.FILTER_POINT)
            return E_FAIL;

        var pSrc = new D3DXCOLOR[m_pSrc!.m_uWidth];
        var pDest = new D3DXCOLOR[m_pDest!.m_uWidth];

        uint uSrcXInc = (m_pSrc.m_uWidth << 16) / m_pDest.m_uWidth;
        uint uSrcYInc = (m_pSrc.m_uHeight << 16) / m_pDest.m_uHeight;
        uint uSrcZInc = (m_pSrc.m_uDepth << 16) / m_pDest.m_uDepth;

        uint uSrcZ = 0;
        uint uDestZ = 0;

        while (uDestZ < m_pDest.m_uDepth)
        {
            uint uSrcY = 0;
            uint uDestY = 0;
            uint uSrcYLast = unchecked((uint)-1);

            while (uDestY < m_pDest.m_uHeight)
            {
                uint uSrcX = 0;
                uint uDestX = 0;

                if (((uSrcYLast ^ uSrcY) >> 16) != 0)
                {
                    m_pSrc.Decode(uSrcY >> 16, uSrcZ >> 16, pSrc, 0);
                    uSrcYLast = uSrcY;
                }

                while (uDestX < m_pDest.m_uWidth)
                {
                    pDest[uDestX] = pSrc[uSrcX >> 16];

                    uSrcX += uSrcXInc;
                    uDestX++;
                }

                m_pDest.Encode(uDestY, uDestZ, pDest, 0);

                uSrcY += uSrcYInc;
                uDestY++;
            }

            uSrcZ += uSrcZInc;
            uDestZ++;
        }

        return S_OK;
    }

    private int BltBox2D()
    {
        int hr;

        if ((m_dwFilter & 0xff) != D3DX.FILTER_BOX)
            return E_FAIL;

        if (m_pDest!.m_dwType != CodecType.CODEC_RGB || m_pSrc!.m_dwType != CodecType.CODEC_RGB)
            return E_FAIL;

        if (!(m_pDest.m_uWidth == (m_pSrc.m_uWidth >> 1)) &&
            !(1 == m_pDest.m_uWidth && 1 == m_pSrc.m_uWidth))
        {
            return E_FAIL;
        }

        if (!(m_pDest.m_uHeight == (m_pSrc.m_uHeight >> 1)) &&
            !(1 == m_pDest.m_uHeight && 1 == m_pSrc.m_uHeight))
        {
            return E_FAIL;
        }

        if (!(1 == m_pDest.m_uDepth && 1 == m_pSrc.m_uDepth))
            return E_FAIL;

        // Optimized filters
        if ((m_dwFilter & D3DX.FILTER_DITHER) == 0 && (m_pSrc.m_Format == m_pDest.m_Format) &&
            (m_pSrc.m_uWidth >= 2) && (m_pSrc.m_uHeight >= 2))
        {
            hr = E_NOTIMPL;

            switch (m_pSrc.m_Format)
            {
                case D3DFmt.A8R8G8B8: hr = BltBox2D_A8R8G8B8(); break;
                case D3DFmt.X8R8G8B8: hr = BltBox2D_X8R8G8B8(); break;
                case D3DFmt.R5G6B5: hr = BltBox2D_R5G6B5(); break;
                case D3DFmt.X1R5G5B5: hr = BltBox2D_X1R5G5B5(); break;
                case D3DFmt.A1R5G5B5: hr = BltBox2D_A1R5G5B5(); break;
                case D3DFmt.A4R4G4B4: hr = BltBox2D_A4R4G4B4(); break;
                case D3DFmt.A8: hr = BltBox2D_A8(); break;
                case D3DFmt.P8: hr = BltBox2D_P8(); break;
                case D3DFmt.L8: hr = BltBox2D_A8(); break;
                case D3DFmt.A8L8: hr = BltBox2D_A8L8(); break;
            }

            if (SUCCEEDED(hr))
                return S_OK;
        }

        // Generic filter
        var pDest = new D3DXCOLOR[m_pDest.m_uWidth];

        D3DXCOLOR[] pSrcArr;
        int pxyz, pxYz, pXyz, pXYz;

        if (1 == m_pSrc.m_uHeight)
        {
            pSrcArr = new D3DXCOLOR[m_pSrc.m_uWidth];
            pxyz = 0;
            pxYz = pxyz;
        }
        else
        {
            pSrcArr = new D3DXCOLOR[m_pSrc.m_uWidth * 2];
            pxyz = (int)(m_pSrc.m_uWidth * 0);
            pxYz = (int)(m_pSrc.m_uWidth * 1);
        }

        if (1 == m_pSrc.m_uWidth)
        {
            pXyz = pxyz;
            pXYz = pxYz;
        }
        else
        {
            pXyz = pxyz + 1;
            pXYz = pxYz + 1;
        }

        for (uint uY = 0; uY < m_pDest.m_uHeight; uY++)
        {
            uint uY2 = uY << 1;

            m_pSrc.Decode(uY2 + 0, 0, pSrcArr, pxyz);

            if (pxYz != pxyz)
                m_pSrc.Decode(uY2 + 1, 0, pSrcArr, pxYz);

            for (uint uX = 0; uX < m_pDest.m_uWidth; uX++)
            {
                uint uX2 = uX << 1;
                pDest[uX] = (pSrcArr[pxyz + (int)uX2] + pSrcArr[pXyz + (int)uX2] +
                             pSrcArr[pxYz + (int)uX2] + pSrcArr[pXYz + (int)uX2]) * 0.25f;
            }

            m_pDest.Encode(uY, 0, pDest, 0);
        }

        return S_OK;
    }

    private int BltBox3D()
    {
        if ((m_dwFilter & 0xff) != D3DX.FILTER_BOX)
            return E_FAIL;

        if (m_pDest!.m_dwType != CodecType.CODEC_RGB || m_pSrc!.m_dwType != CodecType.CODEC_RGB)
            return E_FAIL;

        if (!(m_pDest.m_uWidth == (m_pSrc.m_uWidth >> 1)) &&
            !(1 == m_pDest.m_uWidth && 1 == m_pSrc.m_uWidth))
        {
            return E_FAIL;
        }

        if (!(m_pDest.m_uHeight == (m_pSrc.m_uHeight >> 1)) &&
            !(1 == m_pDest.m_uHeight && 1 == m_pSrc.m_uHeight))
        {
            return E_FAIL;
        }

        if (!(m_pDest.m_uDepth == (m_pSrc.m_uDepth >> 1)))
            return E_FAIL;

        var pDest = new D3DXCOLOR[m_pDest.m_uWidth];

        D3DXCOLOR[] pSrcArr;
        int pxyz, pxyZ, pxYz, pxYZ, pXyz, pXyZ, pXYz, pXYZ;

        if (1 == m_pSrc.m_uHeight)
        {
            pSrcArr = new D3DXCOLOR[m_pSrc.m_uWidth * 2];
            pxyz = (int)(m_pSrc.m_uWidth * 0);
            pxyZ = (int)(m_pSrc.m_uWidth * 1);
            pxYz = pxyz;
            pxYZ = pxyZ;
        }
        else
        {
            pSrcArr = new D3DXCOLOR[m_pSrc.m_uWidth * 4];
            pxyz = (int)(m_pSrc.m_uWidth * 0);
            pxyZ = (int)(m_pSrc.m_uWidth * 1);
            pxYz = (int)(m_pSrc.m_uWidth * 2);
            pxYZ = (int)(m_pSrc.m_uWidth * 3);
        }

        if (1 == m_pSrc.m_uWidth)
        {
            pXyz = pxyz;
            pXyZ = pxyZ;
            pXYz = pxYz;
            pXYZ = pxYZ;
        }
        else
        {
            pXyz = pxyz + 1;
            pXyZ = pxyZ + 1;
            pXYz = pxYz + 1;
            pXYZ = pxYZ + 1;
        }

        for (uint uZ = 0; uZ < m_pDest.m_uDepth; uZ++)
        {
            uint uZ2 = uZ << 1;

            for (uint uY = 0; uY < m_pDest.m_uHeight; uY++)
            {
                uint uY2 = uY << 1;

                m_pSrc.Decode(uY2 + 0, uZ2 + 0, pSrcArr, pxyz);

                if (pxyZ != pxyz)
                    m_pSrc.Decode(uY2 + 0, uZ2 + 1, pSrcArr, pxyZ);

                if (pxYz != pxyz)
                    m_pSrc.Decode(uY2 + 1, uZ2 + 0, pSrcArr, pxYz);

                if (pxYZ != pxyZ && pxYZ != pxYz)
                    m_pSrc.Decode(uY2 + 1, uZ2 + 1, pSrcArr, pxYZ);

                for (uint uX = 0; uX < m_pDest.m_uWidth; uX++)
                {
                    uint uX2 = uX << 1;

                    pDest[uX] = (pSrcArr[pxyz + (int)uX2] + pSrcArr[pXyz + (int)uX2] + pSrcArr[pxyZ + (int)uX2] + pSrcArr[pXyZ + (int)uX2] +
                                 pSrcArr[pxYz + (int)uX2] + pSrcArr[pXYz + (int)uX2] + pSrcArr[pxYZ + (int)uX2] + pSrcArr[pXYZ + (int)uX2]) * 0.125f;
                }

                m_pDest.Encode(uY, uZ, pDest, 0);
            }
        }

        return S_OK;
    }

    // -----------------------------------------------------------------------
    // LF - Linear filter
    // -----------------------------------------------------------------------

    private struct LF_To
    {
        public uint uFrom0;
        public float fWeight0;
        public uint uFrom1;
        public float fWeight1;
    }

    private static LF_To[] LF_SetupLinear(uint uSrcLim, uint uDstLim, bool bRepeat)
    {
        var pTo = new LF_To[uDstLim];
        float fScale = (float)uSrcLim / (float)uDstLim;

        for (uint u = 0; u < uDstLim; u++)
        {
            float fSrc = (float)u * fScale - 0.5f;
            float fSrcFloor = MathF.Floor(fSrc);

            int iSrcA = CXD3DXCodec.F2I(fSrcFloor);
            int iSrcB = iSrcA + 1;

            if (iSrcA < 0)
                iSrcA = bRepeat ? (int)uSrcLim - 1 : 0;

            if ((uint)iSrcB >= uSrcLim)
                iSrcB = bRepeat ? 0 : (int)uSrcLim - 1;

            pTo[u].uFrom0 = (uint)iSrcA;
            pTo[u].fWeight0 = 1.0f - (fSrc - fSrcFloor);

            pTo[u].uFrom1 = (uint)iSrcB;
            pTo[u].fWeight1 = 1.0f - pTo[u].fWeight0;
        }

        return pTo;
    }

    private int BltLinear2D()
    {
        if (m_pDest!.m_dwType != CodecType.CODEC_RGB || m_pSrc!.m_dwType != CodecType.CODEC_RGB)
            return E_FAIL;

        if ((m_dwFilter & 0xff) != D3DX.FILTER_LINEAR)
            return E_FAIL;

        bool bRepeatX = (m_dwFilter & D3DX.FILTER_MIRROR_U) == 0;
        bool bRepeatY = (m_dwFilter & D3DX.FILTER_MIRROR_V) == 0;

        LF_To[] pbXFilter = LF_SetupLinear(m_pSrc.m_uWidth, m_pDest.m_uWidth, bRepeatX);
        LF_To[] pbYFilter = LF_SetupLinear(m_pSrc.m_uHeight, m_pDest.m_uHeight, bRepeatY);

        var pDest = new D3DXCOLOR[m_pDest.m_uWidth];
        var pSrc = new D3DXCOLOR[m_pSrc.m_uWidth * 2];

        int pxyz = (int)(m_pSrc.m_uWidth * 0);
        int pxYz = (int)(m_pSrc.m_uWidth * 1);

        uint uY = 0;
        int pToYidx = 0;

        uint uFrom0 = unchecked((uint)-1);
        uint uFrom1 = unchecked((uint)-1);

        while (uY < m_pDest.m_uHeight)
        {
            uint uX = 0;
            int pToXidx = 0;

            LF_To pToY = pbYFilter[pToYidx];

            if (pToY.uFrom0 != uFrom0)
            {
                if (pToY.uFrom0 != uFrom1)
                {
                    uFrom0 = pToY.uFrom0;
                    m_pSrc.Decode(uFrom0, 0, pSrc, pxyz);
                }
                else
                {
                    uFrom0 = uFrom1;
                    uFrom1 = unchecked((uint)-1);

                    int t = pxyz; pxyz = pxYz; pxYz = t;
                }
            }

            if (pToY.uFrom1 != uFrom1)
            {
                uFrom1 = pToY.uFrom1;
                m_pSrc.Decode(uFrom1, 0, pSrc, pxYz);
            }

            while (uX < m_pDest.m_uWidth)
            {
                LF_To pToX = pbXFilter[pToXidx];

                pDest[uX] = ((pSrc[pxyz + (int)pToX.uFrom0] * pToX.fWeight0 +
                              pSrc[pxyz + (int)pToX.uFrom1] * pToX.fWeight1) * pToY.fWeight0 +
                             (pSrc[pxYz + (int)pToX.uFrom0] * pToX.fWeight0 +
                              pSrc[pxYz + (int)pToX.uFrom1] * pToX.fWeight1) * pToY.fWeight1);

                pToXidx++;
                uX++;
            }

            m_pDest.Encode(uY, 0, pDest, 0);

            pToYidx++;
            uY++;
        }

        return S_OK;
    }

    private int BltLinear3D()
    {
        if (m_pDest!.m_dwType != CodecType.CODEC_RGB || m_pSrc!.m_dwType != CodecType.CODEC_RGB)
            return E_FAIL;

        if ((m_dwFilter & 0xff) != D3DX.FILTER_LINEAR)
            return E_FAIL;

        bool bRepeatX = (m_dwFilter & D3DX.FILTER_MIRROR_U) == 0;
        bool bRepeatY = (m_dwFilter & D3DX.FILTER_MIRROR_V) == 0;
        bool bRepeatZ = (m_dwFilter & D3DX.FILTER_MIRROR_W) == 0;

        LF_To[] pbXFilter = LF_SetupLinear(m_pSrc.m_uWidth, m_pDest.m_uWidth, bRepeatX);
        LF_To[] pbYFilter = LF_SetupLinear(m_pSrc.m_uHeight, m_pDest.m_uHeight, bRepeatY);
        LF_To[] pbZFilter = LF_SetupLinear(m_pSrc.m_uDepth, m_pDest.m_uDepth, bRepeatZ);

        var pDest = new D3DXCOLOR[m_pDest.m_uWidth];
        var pSrc = new D3DXCOLOR[m_pSrc.m_uWidth * 4];

        int pxyz = (int)(m_pSrc.m_uWidth * 0);
        int pxYz = (int)(m_pSrc.m_uWidth * 1);
        int pxyZ = (int)(m_pSrc.m_uWidth * 2);
        int pxYZ = (int)(m_pSrc.m_uWidth * 3);

        uint uZ = 0;
        int pToZidx = 0;

        while (uZ < m_pDest.m_uDepth)
        {
            uint uY = 0;
            int pToYidx = 0;
            uint uFrom0 = unchecked((uint)-1);
            uint uFrom1 = unchecked((uint)-1);

            LF_To pToZ = pbZFilter[pToZidx];

            while (uY < m_pDest.m_uHeight)
            {
                uint uX = 0;
                int pToXidx = 0;

                LF_To pToY = pbYFilter[pToYidx];

                if (pToY.uFrom0 != uFrom0)
                {
                    if (pToY.uFrom0 != uFrom1)
                    {
                        uFrom0 = pToY.uFrom0;

                        m_pSrc.Decode(uFrom0, pToZ.uFrom0, pSrc, pxyz);
                        m_pSrc.Decode(uFrom0, pToZ.uFrom1, pSrc, pxyZ);
                    }
                    else
                    {
                        uFrom0 = uFrom1;
                        uFrom1 = unchecked((uint)-1);

                        int t;
                        t = pxyz; pxyz = pxYz; pxYz = t;
                        t = pxyZ; pxyZ = pxYZ; pxYZ = t;
                    }
                }

                if (pToY.uFrom1 != uFrom1)
                {
                    uFrom1 = pToY.uFrom1;

                    m_pSrc.Decode(uFrom1, pToZ.uFrom0, pSrc, pxYz);
                    m_pSrc.Decode(uFrom1, pToZ.uFrom1, pSrc, pxYZ);
                }

                while (uX < m_pDest.m_uWidth)
                {
                    LF_To pToX = pbXFilter[pToXidx];

                    pDest[uX] = ((pSrc[pxyz + (int)pToX.uFrom0] * pToX.fWeight0 +
                                  pSrc[pxyz + (int)pToX.uFrom1] * pToX.fWeight1) * pToY.fWeight0 +
                                 (pSrc[pxYz + (int)pToX.uFrom0] * pToX.fWeight0 +
                                  pSrc[pxYz + (int)pToX.uFrom1] * pToX.fWeight1) * pToY.fWeight1) * pToZ.fWeight0 +

                                ((pSrc[pxyZ + (int)pToX.uFrom0] * pToX.fWeight0 +
                                  pSrc[pxyZ + (int)pToX.uFrom1] * pToX.fWeight1) * pToY.fWeight0 +
                                 (pSrc[pxYZ + (int)pToX.uFrom0] * pToX.fWeight0 +
                                  pSrc[pxYZ + (int)pToX.uFrom1] * pToX.fWeight1) * pToY.fWeight1) * pToZ.fWeight1;

                    pToXidx++;
                    uX++;
                }

                m_pDest.Encode(uY, uZ, pDest, 0);

                pToYidx++;
                uY++;
            }

            pToZidx++;
            uZ++;
        }

        return S_OK;
    }

    // -----------------------------------------------------------------------
    // TF - Triangle filter
    //
    // The filter description is a variable-length serialized structure. It is kept
    // as a raw byte[] and walked with the same offset arithmetic as the original
    // pointer casts:
    //   TF_Filter { UINT uSize; TF_From pFrom[]; }   header = 4 bytes
    //   TF_From   { UINT uSize; TF_To pTo[]; }        header = 4 bytes
    //   TF_To     { UINT uTo; FLOAT fWeight; }        = 8 bytes
    // -----------------------------------------------------------------------

    private const float TF_EPSILON = 0.00001f;
    private const uint TF_uFilterSize = 4;
    private const uint TF_uFromSize = 4;
    private const uint TF_uToSize = 8;

    private sealed class TF_Row
    {
        public D3DXCOLOR[]? pclr;
        public float fWeight;
        public TF_Row? pNext;
    }

    private static uint TF_R32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static void TF_W32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    private static float TF_RF(byte[] b, int o) => BitConverter.ToSingle(b, o);
    private static void TF_WF(byte[] b, int o, float f)
    {
        uint bits = BitConverter.SingleToUInt32Bits(f);
        TF_W32(b, o, bits);
    }

    private static byte[]? TF_SetupTriangle(uint uSrcLim, uint uDstLim, bool bRepeat)
    {
        if (uSrcLim == 0 || uDstLim == 0)
            return null;

        float fScale = (float)uDstLim / (float)uSrcLim;
        float f2ScaleInv = 0.5f / fScale;

        uint uSizeMax = TF_uFilterSize + TF_uFromSize + TF_uToSize;
        uint uRepeat = bRepeat ? 1u : 0u;

        for (uint uSrc = 0; uSrc < uSrcLim; uSrc++)
        {
            float fSrc = (float)uSrc - 0.5f;
            float fDstMin = fSrc * fScale;
            float fDstLim = fDstMin + fScale;

            uSizeMax += TF_uFromSize + TF_uToSize +
                (uint)(fDstLim - fDstMin + uRepeat + 1) * TF_uToSize * 2;
        }

        var pbFilter = new byte[uSizeMax];

        uint uSize = TF_uFilterSize;

        uint uAccumDst = 0;
        float fAccumWeight = 0.0f;

        for (uint uSrc = 0; uSrc < uSrcLim; uSrc++)
        {
            uint uSizeFrom = uSize;
            int pFromOff = (int)uSize;
            uSize += TF_uFromSize;

            for (uint uPass = 0; uPass < 2; uPass++)
            {
                float fSrc = ((float)uSrc + uPass) - 0.5f;

                float fDstMin = fSrc * fScale;
                float fDstLim = fDstMin + fScale;

                if (!bRepeat)
                {
                    if (fDstMin < 0.0f)
                        fDstMin = 0.0f;
                    if (fDstLim > (float)uDstLim)
                        fDstLim = (float)uDstLim;
                }

                int nDst = (int)MathF.Floor(fDstMin);

                while ((float)nDst < fDstLim)
                {
                    float fDst0 = (float)nDst;
                    float fDst1 = fDst0 + 1.0f;

                    uint uDst;
                    if (nDst < 0)
                        uDst = (uint)nDst + uDstLim;
                    else if (nDst >= (int)uDstLim)
                        uDst = (uint)nDst - uDstLim;
                    else
                        uDst = (uint)nDst;

                    if (uDst != uAccumDst)
                    {
                        if (fAccumWeight > TF_EPSILON)
                        {
                            int pToOff = (int)uSize;
                            uSize += TF_uToSize;

                            TF_W32(pbFilter, pToOff, uAccumDst);
                            TF_WF(pbFilter, pToOff + 4, fAccumWeight);
                        }

                        fAccumWeight = 0.0f;
                        uAccumDst = uDst;
                    }

                    if (fDst0 < fDstMin)
                        fDst0 = fDstMin;
                    if (fDst1 > fDstLim)
                        fDst1 = fDstLim;

                    float fWeight;
                    if (!bRepeat && fSrc < 0.0f)
                        fWeight = 1.0f;
                    else if (!bRepeat && fSrc + 1.0f >= (float)uSrcLim)
                        fWeight = 0.0f;
                    else
                        fWeight = (fDst0 + fDst1) * f2ScaleInv - fSrc;

                    fAccumWeight += (fDst1 - fDst0) * (uPass != 0 ? 1.0f - fWeight : fWeight);
                    nDst++;
                }
            }

            if (fAccumWeight > TF_EPSILON)
            {
                int pToOff = (int)uSize;
                uSize += TF_uToSize;

                TF_W32(pbFilter, pToOff, uAccumDst);
                TF_WF(pbFilter, pToOff + 4, fAccumWeight);
            }

            fAccumWeight = 0.0f;

            TF_W32(pbFilter, pFromOff, uSize - uSizeFrom);
        }

        TF_W32(pbFilter, 0, uSize);

        return pbFilter;
    }

    private int BltTriangle2D()
    {
        if (m_pDest!.m_dwType != CodecType.CODEC_RGB || m_pSrc!.m_dwType != CodecType.CODEC_RGB)
            return E_FAIL;

        if (m_pDest.m_uDepth != 1 || m_pSrc.m_uDepth != 1)
            return E_FAIL;

        bool bRepeatX = (m_dwFilter & D3DX.FILTER_MIRROR_U) == 0;
        bool bRepeatY = (m_dwFilter & D3DX.FILTER_MIRROR_V) == 0;

        byte[]? pbXFilter = TF_SetupTriangle(m_pSrc.m_uWidth, m_pDest.m_uWidth, bRepeatX);
        byte[]? pbYFilter = TF_SetupTriangle(m_pSrc.m_uHeight, m_pDest.m_uHeight, bRepeatY);

        if (pbXFilter == null || pbYFilter == null)
            return E_FAIL;

        int xFromLim = (int)TF_R32(pbXFilter, 0);
        int yFromLim = (int)TF_R32(pbYFilter, 0);

        var ppRowActive = new TF_Row?[m_pDest.m_uHeight];
        uint uRowsActive = 0;
        TF_Row? pRowFree = null;

        var pclrSrc = new D3DXCOLOR[m_pSrc.m_uWidth];

        uint uSrcRow = 0;

        for (int pYFrom = (int)TF_uFilterSize; pYFrom < yFromLim; )
        {
            int pYToLim = pYFrom + (int)TF_R32(pbYFilter, pYFrom);
            int pYTo0 = pYFrom + (int)TF_uFromSize;

            if (pYTo0 < pYToLim)
            {
                // Create necessary accumulation rows
                for (int pYTo = pYTo0; pYTo < pYToLim; pYTo += (int)TF_uToSize)
                {
                    uint uTo = TF_R32(pbYFilter, pYTo);
                    TF_Row? pRow = ppRowActive[uTo];
                    if (pRow == null)
                    {
                        if (pRowFree != null)
                        {
                            pRow = pRowFree;
                            pRowFree = pRow.pNext;
                        }
                        else
                        {
                            pRow = new TF_Row();
                            pRow.pclr = new D3DXCOLOR[m_pDest.m_uWidth];
                        }

                        Array.Clear(pRow.pclr!, 0, (int)m_pDest.m_uWidth);
                        pRow.fWeight = 0.0f;
                        pRow.pNext = null;

                        ppRowActive[uTo] = pRow;
                        uRowsActive++;
                    }
                }

                // Read source pixels
                m_pSrc.Decode(uSrcRow, 0, pclrSrc, 0);

                // Process a row from the source image
                int pclrSrcXidx = 0;
                for (int pXFrom = (int)TF_uFilterSize; pXFrom < xFromLim; )
                {
                    int pXToLim = pXFrom + (int)TF_R32(pbXFilter, pXFrom);
                    int pXTo0 = pXFrom + (int)TF_uFromSize;

                    for (int pYTo = pYTo0; pYTo < pYToLim; pYTo += (int)TF_uToSize)
                    {
                        uint yTo = TF_R32(pbYFilter, pYTo);
                        float yWeight = TF_RF(pbYFilter, pYTo + 4);
                        TF_Row pRow = ppRowActive[yTo]!;
                        D3DXCOLOR[] rpclr = pRow.pclr!;

                        for (int pXTo = pXTo0; pXTo < pXToLim; pXTo += (int)TF_uToSize)
                        {
                            uint xTo = TF_R32(pbXFilter, pXTo);
                            float xWeight = TF_RF(pbXFilter, pXTo + 4);
                            float fWeight = yWeight * xWeight;

                            rpclr[xTo].r += pclrSrc[pclrSrcXidx].r * fWeight;
                            rpclr[xTo].g += pclrSrc[pclrSrcXidx].g * fWeight;
                            rpclr[xTo].b += pclrSrc[pclrSrcXidx].b * fWeight;
                            rpclr[xTo].a += pclrSrc[pclrSrcXidx].a * fWeight;
                        }
                    }

                    pclrSrcXidx++;
                    pXFrom = pXToLim;
                }

                // Write completed accumulation rows
                for (int pYTo = pYTo0; pYTo < pYToLim; pYTo += (int)TF_uToSize)
                {
                    uint yTo = TF_R32(pbYFilter, pYTo);
                    float yWeight = TF_RF(pbYFilter, pYTo + 4);
                    TF_Row pRow = ppRowActive[yTo]!;
                    pRow.fWeight += yWeight;

                    if (pRow.fWeight + TF_EPSILON >= 1.0f)
                    {
                        m_pDest.Encode(yTo, 0, pRow.pclr!, 0);

                        ppRowActive[yTo] = null;
                        pRow.pNext = pRowFree;
                        pRowFree = pRow;

                        uRowsActive--;
                    }
                }
            }

            uSrcRow++;
            pYFrom = pYToLim;
        }

        // Make sure that all accumulation rows have been written out.
        if (uRowsActive != 0)
        {
            for (uint uRow = 0; uRow < m_pDest.m_uHeight; uRow++)
            {
                if (ppRowActive[uRow] != null)
                {
                    m_pDest.Encode(uRow, 0, ppRowActive[uRow]!.pclr!, 0);

                    if (--uRowsActive == 0)
                        break;
                }
            }
        }

        return S_OK;
    }

    private int BltTriangle3D()
    {
        if (m_pDest!.m_dwType != CodecType.CODEC_RGB || m_pSrc!.m_dwType != CodecType.CODEC_RGB)
            return E_FAIL;

        bool bRepeatX = (m_dwFilter & D3DX.FILTER_MIRROR_U) == 0;
        bool bRepeatY = (m_dwFilter & D3DX.FILTER_MIRROR_V) == 0;
        bool bRepeatZ = (m_dwFilter & D3DX.FILTER_MIRROR_W) == 0;

        byte[]? pbXFilter = TF_SetupTriangle(m_pSrc.m_uWidth, m_pDest.m_uWidth, bRepeatX);
        byte[]? pbYFilter = TF_SetupTriangle(m_pSrc.m_uHeight, m_pDest.m_uHeight, bRepeatY);
        byte[]? pbZFilter = TF_SetupTriangle(m_pSrc.m_uDepth, m_pDest.m_uDepth, bRepeatZ);

        if (pbXFilter == null || pbYFilter == null || pbZFilter == null)
            return E_FAIL;

        int xFromLim = (int)TF_R32(pbXFilter, 0);
        int yFromLim = (int)TF_R32(pbYFilter, 0);
        int zFromLim = (int)TF_R32(pbZFilter, 0);

        var ppSliceActive = new TF_Row?[m_pDest.m_uDepth];
        uint uSlicesActive = 0;
        TF_Row? pSliceFree = null;
        TF_Row? pSlice = null;

        var pclrSrc = new D3DXCOLOR[m_pSrc.m_uWidth];

        uint uSrcSlice = 0;
        for (int pZFrom = (int)TF_uFilterSize; pZFrom < zFromLim; )
        {
            int pZToLim = pZFrom + (int)TF_R32(pbZFilter, pZFrom);
            int pZTo0 = pZFrom + (int)TF_uFromSize;

            // Create necessary accumulation slices
            for (int pZTo = pZTo0; pZTo < pZToLim; pZTo += (int)TF_uToSize)
            {
                uint zTo = TF_R32(pbZFilter, pZTo);
                pSlice = ppSliceActive[zTo];
                if (pSlice == null)
                {
                    if (pSliceFree != null)
                    {
                        pSlice = pSliceFree;
                        pSliceFree = pSlice.pNext;
                    }
                    else
                    {
                        pSlice = new TF_Row();
                        pSlice.pclr = new D3DXCOLOR[m_pDest.m_uWidth * m_pDest.m_uHeight];
                    }

                    Array.Clear(pSlice.pclr!, 0, (int)(m_pDest.m_uWidth * m_pDest.m_uHeight));
                    pSlice.fWeight = 0.0f;
                    pSlice.pNext = null;

                    ppSliceActive[zTo] = pSlice;
                    uSlicesActive++;
                }
            }

            uint uSrcRow = 0;
            for (int pYFrom = (int)TF_uFilterSize; pYFrom < yFromLim; )
            {
                int pYToLim = pYFrom + (int)TF_R32(pbYFilter, pYFrom);
                int pYTo0 = pYFrom + (int)TF_uFromSize;

                // Read source pixels
                m_pSrc.Decode(uSrcRow, uSrcSlice, pclrSrc, 0);

                // Process a row from the source image
                int pclrSrcXidx = 0;

                for (int pXFrom = (int)TF_uFilterSize; pXFrom < xFromLim; )
                {
                    int pXToLim = pXFrom + (int)TF_R32(pbXFilter, pXFrom);
                    int pXTo0 = pXFrom + (int)TF_uFromSize;

                    for (int pZTo = pZTo0; pZTo < pZToLim; pZTo += (int)TF_uToSize)
                    {
                        uint zTo = TF_R32(pbZFilter, pZTo);
                        float zWeight = TF_RF(pbZFilter, pZTo + 4);

                        for (int pYTo = pYTo0; pYTo < pYToLim; pYTo += (int)TF_uToSize)
                        {
                            uint yTo = TF_R32(pbYFilter, pYTo);
                            float yWeight = TF_RF(pbYFilter, pYTo + 4);
                            D3DXCOLOR[] slclr = ppSliceActive[zTo]!.pclr!;
                            int pclrDest = (int)(yTo * m_pDest.m_uWidth);

                            for (int pXTo = pXTo0; pXTo < pXToLim; pXTo += (int)TF_uToSize)
                            {
                                uint xTo = TF_R32(pbXFilter, pXTo);
                                float xWeight = TF_RF(pbXFilter, pXTo + 4);
                                float fWeight = zWeight * yWeight * xWeight;

                                slclr[pclrDest + (int)xTo].r += pclrSrc[pclrSrcXidx].r * fWeight;
                                slclr[pclrDest + (int)xTo].g += pclrSrc[pclrSrcXidx].g * fWeight;
                                slclr[pclrDest + (int)xTo].b += pclrSrc[pclrSrcXidx].b * fWeight;
                                slclr[pclrDest + (int)xTo].a += pclrSrc[pclrSrcXidx].a * fWeight;
                            }
                        }
                    }

                    pclrSrcXidx++;
                    pXFrom = pXToLim;
                }

                uSrcRow++;
                pYFrom = pYToLim;
            }

            // Write completed accumulation slices
            for (int pZTo = pZTo0; pZTo < pZToLim; pZTo += (int)TF_uToSize)
            {
                uint zTo = TF_R32(pbZFilter, pZTo);
                float zWeight = TF_RF(pbZFilter, pZTo + 4);
                pSlice = ppSliceActive[zTo]!;
                pSlice.fWeight += zWeight;

                if (pSlice.fWeight + TF_EPSILON >= 1.0f)
                {
                    for (uint uRow = 0; uRow < m_pDest.m_uHeight; uRow++)
                        m_pDest.Encode(uRow, zTo, pSlice.pclr!, (int)(uRow * m_pDest.m_uWidth));

                    ppSliceActive[zTo] = null;
                    pSlice.pNext = pSliceFree;
                    pSliceFree = pSlice;

                    uSlicesActive--;
                }
            }

            uSrcSlice++;
            pZFrom = pZToLim;
        }

        // Make sure that all accumulation slices have been written out.
        if (uSlicesActive != 0)
        {
            for (uint uSlice = 0; uSlice < m_pDest.m_uDepth; uSlice++)
            {
                if (ppSliceActive[uSlice] != null)
                {
                    // Faithful to the original: uses the last 'pSlice' pointer here.
                    for (uint uRow = 0; uRow < m_pDest.m_uHeight; uRow++)
                        m_pDest.Encode(uRow, uSlice, pSlice!.pclr!, (int)(uRow * m_pDest.m_uWidth));

                    if (--uSlicesActive == 0)
                        break;
                }
            }
        }

        return S_OK;
    }

    // -----------------------------------------------------------------------
    // Optimized box filters (2x2 -> 1, same format, no dither)
    // -----------------------------------------------------------------------

    private int BltBox2D_A8R8G8B8()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pulDest = m_pDest.m_pbOffset;
        int pulSrc = m_pSrc.m_pbOffset;
        int pulSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pulSrc < pulSrcLim)
        {
            int pul = pulDest;
            int pulA = pulSrc;
            int pulB = pulA + (int)m_pSrc.m_uPitch;
            int pulALim = pulA + (int)(m_pSrc.m_uWidth * 4);

            while (pulA < pulALim)
            {
                uint a0 = R(src, pulA + 0), a1 = R(src, pulA + 4);
                uint b0 = R(src, pulB + 0), b1 = R(src, pulB + 4);

                uint v = (((((a0 & 0x00ff00ff) + (a1 & 0x00ff00ff) +
                             (b0 & 0x00ff00ff) + (b1 & 0x00ff00ff)) + 0x00020002) >> 2) & 0x00ff00ff) |

                         (((((a0 & 0xff00ff00) >> 2) + ((a1 & 0xff00ff00) >> 2) +
                            ((b0 & 0xff00ff00) >> 2) + ((b1 & 0xff00ff00) >> 2)) + (0x02000200 >> 2)) & 0xff00ff00);

                W(dst, pul, v);
                pul += 4;

                pulA += 8;
                pulB += 8;
            }

            pulDest += (int)m_pDest.m_uPitch;
            pulSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    private int BltBox2D_X8R8G8B8()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pulDest = m_pDest.m_pbOffset;
        int pulSrc = m_pSrc.m_pbOffset;
        int pulSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pulSrc < pulSrcLim)
        {
            int pul = pulDest;
            int pulA = pulSrc;
            int pulB = pulA + (int)m_pSrc.m_uPitch;
            int pulALim = pulA + (int)(m_pSrc.m_uWidth * 4);

            while (pulA < pulALim)
            {
                uint a0 = R(src, pulA + 0), a1 = R(src, pulA + 4);
                uint b0 = R(src, pulB + 0), b1 = R(src, pulB + 4);

                uint v = ((((a0 & 0x00ff00ff) + (a1 & 0x00ff00ff) +
                            (b0 & 0x00ff00ff) + (b1 & 0x00ff00ff) + 0x00020002) & (0x00ff00ff << 2)) |

                          (((a0 & 0x0000ff00) + (a1 & 0x0000ff00) +
                            (b0 & 0x0000ff00) + (b1 & 0x0000ff00) + 0x00000200) & (0x0000ff00 << 2))) >> 2;

                W(dst, pul, v);
                pul += 4;

                pulA += 8;
                pulB += 8;
            }

            pulDest += (int)m_pDest.m_uPitch;
            pulSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    private int BltBox2D_R5G6B5()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pusDest = m_pDest.m_pbOffset;
        int pusSrc = m_pSrc.m_pbOffset;
        int pusSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pusSrc < pusSrcLim)
        {
            int pus = pusDest;
            int pusA = pusSrc;
            int pusB = pusA + (int)m_pSrc.m_uPitch;
            int pusALim = pusA + (int)(m_pSrc.m_uWidth * 2);

            while (pusA < pusALim)
            {
                uint a0 = RS(src, pusA + 0), a1 = RS(src, pusA + 2);
                uint b0 = RS(src, pusB + 0), b1 = RS(src, pusB + 2);

                ushort v = (ushort)(((((a0 & 0xf81f) + (a1 & 0xf81f) +
                                       (b0 & 0xf81f) + (b1 & 0xf81f) + 0x1002) & (0xf81f << 2)) |

                                     (((a0 & 0x07e0) + (a1 & 0x07e0) +
                                       (b0 & 0x07e0) + (b1 & 0x07e0) + 0x0040) & (0x07e0 << 2))) >> 2);

                WS(dst, pus, v);
                pus += 2;

                pusA += 4;
                pusB += 4;
            }

            pusDest += (int)m_pDest.m_uPitch;
            pusSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    private int BltBox2D_X1R5G5B5()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pusDest = m_pDest.m_pbOffset;
        int pusSrc = m_pSrc.m_pbOffset;
        int pusSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pusSrc < pusSrcLim)
        {
            int pus = pusDest;
            int pusA = pusSrc;
            int pusB = pusA + (int)m_pSrc.m_uPitch;
            int pusALim = pusA + (int)(m_pSrc.m_uWidth * 2);

            while (pusA < pusALim)
            {
                uint a0 = RS(src, pusA + 0), a1 = RS(src, pusA + 2);
                uint b0 = RS(src, pusB + 0), b1 = RS(src, pusB + 2);

                ushort v = (ushort)(((((a0 & 0x7c1f) + (a1 & 0x7c1f) +
                                       (b0 & 0x7c1f) + (b1 & 0x7c1f) + 0x0802) & (0x7c1f << 2)) |

                                     (((a0 & 0x03e0) + (a1 & 0x03e0) +
                                       (b0 & 0x03e0) + (b1 & 0x03e0) + 0x0040) & (0x03e0 << 2))) >> 2);

                WS(dst, pus, v);
                pus += 2;

                pusA += 4;
                pusB += 4;
            }

            pusDest += (int)m_pDest.m_uPitch;
            pusSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    private int BltBox2D_A1R5G5B5()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pusDest = m_pDest.m_pbOffset;
        int pusSrc = m_pSrc.m_pbOffset;
        int pusSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pusSrc < pusSrcLim)
        {
            int pus = pusDest;
            int pusA = pusSrc;
            int pusB = pusA + (int)m_pSrc.m_uPitch;
            int pusALim = pusA + (int)(m_pSrc.m_uWidth * 2);

            while (pusA < pusALim)
            {
                uint a0 = RS(src, pusA + 0), a1 = RS(src, pusA + 2);
                uint b0 = RS(src, pusB + 0), b1 = RS(src, pusB + 2);

                ushort v = (ushort)(((((a0 & 0x7c1f) + (a1 & 0x7c1f) +
                                       (b0 & 0x7c1f) + (b1 & 0x7c1f) + 0x0802) & (0x7c1f << 2)) |

                                     (((a0 & 0x83e0) + (a1 & 0x83e0) +
                                       (b0 & 0x83e0) + (b1 & 0x83e0) + 0x10040) & (0x83e0 << 2))) >> 2);

                WS(dst, pus, v);
                pus += 2;

                pusA += 4;
                pusB += 4;
            }

            pusDest += (int)m_pDest.m_uPitch;
            pusSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    private int BltBox2D_A4R4G4B4()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pusDest = m_pDest.m_pbOffset;
        int pusSrc = m_pSrc.m_pbOffset;
        int pusSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pusSrc < pusSrcLim)
        {
            int pus = pusDest;
            int pusA = pusSrc;
            int pusB = pusA + (int)m_pSrc.m_uPitch;
            int pusALim = pusA + (int)(m_pSrc.m_uWidth * 2);

            while (pusA < pusALim)
            {
                uint a0 = RS(src, pusA + 0), a1 = RS(src, pusA + 2);
                uint b0 = RS(src, pusB + 0), b1 = RS(src, pusB + 2);

                ushort v = (ushort)(((((a0 & 0x0f0f) + (a1 & 0x0f0f) +
                                       (b0 & 0x0f0f) + (b1 & 0x0f0f) + 0x0202) & (0x0f0f << 2)) |

                                     (((a0 & 0xf0f0) + (a1 & 0xf0f0) +
                                       (b0 & 0xf0f0) + (b1 & 0xf0f0) + 0x2020) & (0xf0f0 << 2))) >> 2);

                WS(dst, pus, v);
                pus += 2;

                pusA += 4;
                pusB += 4;
            }

            pusDest += (int)m_pDest.m_uPitch;
            pusSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    private int BltBox2D_A8()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pubDest = m_pDest.m_pbOffset;
        int pubSrc = m_pSrc.m_pbOffset;
        int pubSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pubSrc < pubSrcLim)
        {
            int pub = pubDest;
            int pubA = pubSrc;
            int pubB = pubA + (int)m_pSrc.m_uPitch;
            int pubALim = pubA + (int)m_pSrc.m_uWidth;

            while (pubA < pubALim)
            {
                dst[pub] = (byte)(((uint)src[pubA + 0] + (uint)src[pubA + 1] +
                                   (uint)src[pubB + 0] + (uint)src[pubB + 1] + 0x02) >> 2);
                pub++;

                pubA += 2;
                pubB += 2;
            }

            pubDest += (int)m_pDest.m_uPitch;
            pubSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    private int BltBox2D_A8L8()
    {
        byte[] dst = m_pDest!.m_pbData;
        byte[] src = m_pSrc!.m_pbData;

        int pusDest = m_pDest.m_pbOffset;
        int pusSrc = m_pSrc.m_pbOffset;
        int pusSrcLim = m_pSrc.m_pbOffset + (int)(m_pSrc.m_uPitch * m_pSrc.m_uHeight);

        while (pusSrc < pusSrcLim)
        {
            int pus = pusDest;
            int pusA = pusSrc;
            int pusB = pusA + (int)m_pSrc.m_uPitch;
            int pusALim = pusA + (int)(m_pSrc.m_uWidth * 2);

            while (pusA < pusALim)
            {
                uint a0 = RS(src, pusA + 0), a1 = RS(src, pusA + 2);
                uint b0 = RS(src, pusB + 0), b1 = RS(src, pusB + 2);

                ushort v = (ushort)(((((a0 & 0x00ff) + (a1 & 0x00ff) +
                                       (b0 & 0x00ff) + (b1 & 0x00ff) + 0x0002) & (0x00ff << 2)) |

                                     (((a0 & 0xff00) + (a1 & 0xff00) +
                                       (b0 & 0xff00) + (b1 & 0xff00) + 0x0200) & (0xff00 << 2))) >> 2);

                WS(dst, pus, v);
                pus += 2;

                pusA += 4;
                pusB += 4;
            }

            pusDest += (int)m_pDest.m_uPitch;
            pusSrc += (int)(m_pSrc.m_uPitch + m_pSrc.m_uPitch);
        }

        return S_OK;
    }

    // #if 0 in the source (not implemented)
    private int BltBox2D_P8() => E_NOTIMPL;

    // ---- byte[] little-endian helpers for the optimized box filters ----
    private static uint R(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static void W(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    private static uint RS(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8));
    private static void WS(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
}
