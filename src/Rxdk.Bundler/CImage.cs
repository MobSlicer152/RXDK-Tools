// Port of the XDK bundler's CImage (loadimage.cpp): loads source art into a
// 32-bit A8R8G8B8-friendly surface, and LoadImageFromImage converts/resamples a
// source surface into a destination surface (the D3DX-free blit path).
//
// Currently ported: TGA (all bit depths the XDK supports) + palette expansion,
// and the same-size blit for 32-bit source surfaces (the common case: a 24/32-bit
// TGA/BMP feeding a linear or swizzled texture). Resampling (mip generation and
// non-power-of-two -> power-of-two resize) and 16-bit/L8 source conversion route
// through the D3DX-free CXD3DXBlt filter and are not ported yet; those paths
// throw rather than emit bytes that might not match the XDK output.

namespace Rxdk.Bundler;

internal sealed class CImage
{
    public byte[] Data = Array.Empty<byte>();
    public uint Format = D3DFmt.UNKNOWN;
    public uint Width;
    public uint Height;
    public uint Pitch;
    public uint[]? Palette; // 256 entries, each 0xAARRGGBB

    public CImage() { }

    /// <summary>Matches CImage(w,h,format): always allocates a width*4*height surface.</summary>
    public CImage(uint width, uint height, uint format)
    {
        Format = format;
        Width = width;
        Height = height;
        Pitch = width * 4;
        Data = new byte[Pitch * height];
    }

    public static CImage LoadFromFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var img = new CImage();
        // Try loaders in the same order as CImage::Load (BMP, TGA).
        if (img.LoadBMP(bytes)) return img;
        img = new CImage();
        if (img.LoadTGA(bytes)) return img;
        throw new BundlerException($"Unsupported or unhandled image format: {Path.GetFileName(path)}");
    }

    // --- BMP / DIB (loadimage.cpp LoadBMP / LoadDIB) --------------------------
    private const uint BI_RGB = 0, BI_RLE8 = 1, BI_RLE4 = 2, BI_BITFIELDS = 3;

    private bool LoadBMP(byte[] d)
    {
        if (d.Length < 14) return false;
        ushort bfType = BitConverter.ToUInt16(d, 0);
        uint bfSize = BitConverter.ToUInt32(d, 2);
        if (bfType != (('B') | ('M' << 8)) || bfSize > d.Length) return false;
        return LoadDIB(d, 14, (uint)(d.Length - 14));
    }

    // pvOff = offset of the DIB (info header) within d; cb = bytes remaining.
    private bool LoadDIB(byte[] d, int pvOff, uint cb)
    {
        if (cb < 40) return false;

        uint biSize = BitConverter.ToUInt32(d, pvOff + 0);
        if (biSize < 40) return false;
        int biWidth = BitConverter.ToInt32(d, pvOff + 4);
        int biHeightRaw = BitConverter.ToInt32(d, pvOff + 8);
        ushort biPlanes = BitConverter.ToUInt16(d, pvOff + 12);
        ushort biBitCount = BitConverter.ToUInt16(d, pvOff + 14);
        uint biCompression = BitConverter.ToUInt32(d, pvOff + 16);
        uint biClrUsed = BitConverter.ToUInt32(d, pvOff + 32);

        uint dwWidth = (uint)biWidth;
        uint dwHeight = (uint)(biHeightRaw > 0 ? biHeightRaw : -biHeightRaw);
        uint dwClrUsed = biClrUsed;
        if (biBitCount <= 8 && dwClrUsed == 0) dwClrUsed = 1u << biBitCount;

        uint dwOffset = biSize + dwClrUsed * 4;
        if (dwOffset > cb) return false;
        if (biPlanes != 1) return false;
        if (biHeightRaw < 0 && biCompression != BI_RGB && biCompression != BI_BITFIELDS) return false;

        uint format = D3DFmt.UNKNOWN;
        switch (biCompression)
        {
            case BI_RGB:
            case BI_RLE4:
            case BI_RLE8:
                format = biBitCount switch
                {
                    1 or 4 or 8 => D3DFmt.P8,
                    16 => D3DFmt.X1R5G5B5,
                    24 or 32 => D3DFmt.X8R8G8B8,
                    _ => uint.MaxValue,
                };
                if (format == uint.MaxValue) return false;
                break;
            case BI_BITFIELDS:
                if (biSize < 108) return false; // sizeof(BITMAPV4HEADER)
                uint mR = BitConverter.ToUInt32(d, pvOff + 40);
                uint mG = BitConverter.ToUInt32(d, pvOff + 44);
                uint mB = BitConverter.ToUInt32(d, pvOff + 48);
                uint mA = BitConverter.ToUInt32(d, pvOff + 52);
                format = biBitCount switch
                {
                    16 when mB == 0x00ff && mG == 0x00ff && mR == 0x00ff && mA == 0xff00 => D3DFmt.A8L8,
                    16 when mB == 0x001f && mG == 0x07e0 && mR == 0xf800 && mA == 0x0000 => D3DFmt.R5G6B5,
                    16 when mB == 0x001f && mG == 0x03e0 && mR == 0x7c00 && mA == 0x0000 => D3DFmt.X1R5G5B5,
                    16 when mB == 0x001f && mG == 0x03e0 && mR == 0x7c00 && mA == 0x8000 => D3DFmt.A1R5G5B5,
                    16 when mB == 0x000f && mG == 0x00f0 && mR == 0x0f00 && mA == 0xf000 => D3DFmt.A4R4G4B4,
                    24 when mB == 0x0000ff && mG == 0x00ff00 && mR == 0xff0000 && mA == 0x000000 => D3DFmt.X8R8G8B8,
                    32 when mB == 0x0000ff && mG == 0x00ff00 && mR == 0xff0000 && mA == 0x00000000 => D3DFmt.X8R8G8B8,
                    32 when mB == 0x0000ff && mG == 0x00ff00 && mR == 0xff0000 && mA == 0xff000000 => D3DFmt.A8R8G8B8,
                    _ => D3DFmt.UNKNOWN,
                };
                break;
            default:
                return false; // JPEG/PNG compression not supported
        }
        if (format == D3DFmt.UNKNOWN) return false;

        // Palette (RGBQUAD table follows the info header).
        if (format == D3DFmt.P8)
        {
            Palette = new uint[256];
            int prgb = pvOff + (int)biSize;
            uint dw;
            for (dw = 0; dw < dwClrUsed; dw++)
            {
                uint b = d[prgb + 0], g = d[prgb + 1], r = d[prgb + 2];
                Palette[dw] = (0xffu << 24) | (r << 16) | (g << 8) | b;
                prgb += 4;
            }
            for (; dw < 256; dw++) Palette[dw] = 0xFFFFFFFF;
        }

        uint dwWidthBytes;
        uint dwSrcInc;
        switch (biBitCount)
        {
            case 1: dwWidthBytes = dwWidth; dwSrcInc = ((dwWidth >> 3) + 3) & ~3u; break;
            case 4: dwWidthBytes = dwWidth; dwSrcInc = ((dwWidth >> 1) + 3) & ~3u; break;
            default:
                dwWidthBytes = dwWidth * (uint)(biBitCount >> 3);
                dwSrcInc = (dwWidthBytes + 3) & ~3u;
                break;
        }

        Format = format;
        Pitch = (dwWidthBytes + 3) & ~3u;
        Width = dwWidth;
        Height = dwHeight;

        int srcBase = pvOff + (int)dwOffset;

        // 24-bit -> X8R8G8B8: expand 3 bytes to 0x00RRGGBB DWORDs.
        if (biBitCount == 24 && format == D3DFmt.X8R8G8B8)
        {
            dwWidthBytes = dwWidth * 4;
            Pitch = (dwWidthBytes + 3) & ~3u;
            Data = new byte[dwHeight * Pitch];

            int src = srcBase;
            int dstRow;
            int strideDst;
            if (biHeightRaw < 0) { dstRow = 0; strideDst = (int)Pitch; }
            else { dstRow = (int)(Pitch * (dwHeight - 1)); strideDst = -(int)Pitch; }

            for (uint i = 0; i < dwHeight; i++)
            {
                int dst = dstRow;
                for (uint j = 0; j < dwWidth; j++)
                {
                    Data[dst + 0] = d[src + 0]; // B
                    Data[dst + 1] = d[src + 1]; // G
                    Data[dst + 2] = d[src + 2]; // R
                    Data[dst + 3] = 0;          // X (alpha 0, as the XDK does)
                    dst += 4;
                    src += 3;
                }
                dstRow += strideDst;
                src += (int)(dwSrcInc - dwWidth * 3);
            }
            return true;
        }

        Data = new byte[dwHeight * Pitch];

        // Top-down (negative height) with >=8bpp: already in correct order.
        if (biHeightRaw < 0 && biBitCount >= 8)
        {
            Array.Copy(d, srcBase, Data, 0, (int)(dwHeight * Pitch));
            return true;
        }

        int destInc;
        int destIdx;
        if (biHeightRaw < 0) { destInc = (int)Pitch; destIdx = 0; }
        else { destInc = -(int)Pitch; destIdx = (int)((dwHeight - 1) * Pitch); }
        int srcIdx = srcBase;
        int destLim = (int)(dwHeight * Pitch);

        if (biCompression == BI_RLE4 || biCompression == BI_RLE8)
        {
            DecodeBmpRle(d, srcIdx, biCompression == BI_RLE8, dwWidth);
            return true;
        }

        if (biBitCount == 1)
        {
            while (destIdx >= 0 && destIdx < destLim)
            {
                for (uint i = 0; i < dwWidth; i++)
                    Data[destIdx + i] = (byte)((d[srcIdx + (int)(i >> 3)] >> (int)(7 - (i & 7))) & 1);
                destIdx += destInc;
                srcIdx += (int)dwSrcInc;
            }
            return true;
        }
        if (biBitCount == 4)
        {
            while (destIdx >= 0 && destIdx < destLim)
            {
                for (uint i = 0; i < dwWidth; i++)
                    Data[destIdx + i] = (byte)((i & 1) != 0 ? d[srcIdx + (int)(i >> 1)] & 0x0f : d[srcIdx + (int)(i >> 1)] >> 4);
                destIdx += destInc;
                srcIdx += (int)dwSrcInc;
            }
            return true;
        }

        while (destIdx >= 0 && destIdx < destLim)
        {
            Array.Copy(d, srcIdx, Data, destIdx, (int)dwWidthBytes);
            destIdx += destInc;
            srcIdx += (int)dwSrcInc;
        }
        return true;
    }

    // RLE4/RLE8 decode into Data (always bottom-up per the BMP spec).
    private void DecodeBmpRle(byte[] d, int src, bool rle8, uint dwWidth)
    {
        int pitch = (int)Pitch;
        int destLineRow = (int)((Height - 1) * Pitch); // top visual row sits at the highest offset
        int dest = destLineRow;
        int destMin = 0;

        while (dest >= destMin)
        {
            if (d[src] == 0)
            {
                switch (d[src + 1])
                {
                    case 0: // end of line
                        destLineRow -= pitch;
                        dest = destLineRow;
                        break;
                    case 1: // end of bitmap
                        dest = destMin - pitch;
                        break;
                    case 2: // delta
                        dest += d[src + 2] - d[src + 3] * pitch;
                        src += 2;
                        break;
                    default:
                        int count = d[src + 1];
                        if (rle8)
                        {
                            Array.Copy(d, src + 2, Data, dest, count);
                            dest += count;
                            src += (count + 1) & ~1;
                        }
                        else
                        {
                            for (int i = 0; i < count; i++)
                                Data[dest + i] = (byte)((i & 1) != 0 ? d[src + 2 + (i >> 1)] & 0x0f : d[src + 2 + (i >> 1)] >> 4);
                            dest += count;
                            src += ((count >> 1) + 1) & ~1;
                        }
                        break;
                }
            }
            else
            {
                int count = d[src];
                if (rle8)
                {
                    for (int i = 0; i < count; i++) Data[dest + i] = d[src + 1];
                    dest += count;
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        Data[dest + i] = (byte)((i & 1) != 0 ? d[src + 1] & 0x0f : d[src + 1] >> 4);
                    dest += count;
                }
            }
            src += 2;
        }
    }

    // --- TGA (loadimage.cpp LoadTGA) -----------------------------------------
    private bool LoadTGA(byte[] d)
    {
        if (d.Length < 18) return false;

        byte idLength = d[0];
        byte colormapType = d[1];
        byte imageType = d[2];
        ushort colorMapIndex = BitConverter.ToUInt16(d, 3);
        ushort colorMapLength = BitConverter.ToUInt16(d, 5);
        byte colorMapBits = d[7];
        ushort width = BitConverter.ToUInt16(d, 12);
        ushort height = BitConverter.ToUInt16(d, 14);
        byte pixelDepth = d[16];
        byte imageDescriptor = d[17];

        if ((colormapType & ~0x01) != 0) return false;
        if ((imageType & ~0x0b) != 0) return false;
        if (width == 0 || height == 0) return false;

        uint colorMapBytes = (uint)((colorMapBits + 7) >> 3);
        uint colorMapFormat = D3DFmt.UNKNOWN;
        if (colormapType != 0)
        {
            colorMapFormat = colorMapBits switch
            {
                15 => D3DFmt.X1R5G5B5,
                16 => D3DFmt.A1R5G5B5,
                24 => D3DFmt.X8R8G8B8,
                32 => D3DFmt.A8R8G8B8,
                _ => uint.MaxValue,
            };
            if (colorMapFormat == uint.MaxValue) return false;
        }

        uint uBytes = (uint)((pixelDepth + 7) >> 3);
        uint format;
        switch (imageType & 0x03)
        {
            case 1:
                if (colormapType == 0) return false;
                if (pixelDepth != 8) return false;
                format = D3DFmt.P8;
                break;
            case 2:
                format = pixelDepth switch
                {
                    15 => D3DFmt.X1R5G5B5,
                    16 => D3DFmt.A1R5G5B5,
                    24 => D3DFmt.X8R8G8B8,
                    32 => D3DFmt.A8R8G8B8,
                    _ => uint.MaxValue,
                };
                if (format == uint.MaxValue) return false;
                break;
            case 3:
                if (pixelDepth != 8) return false;
                format = D3DFmt.L8;
                break;
            default:
                return false;
        }

        bool rle = (imageType & 0x08) != 0;
        bool topToBottom = (imageDescriptor & 0x20) == 0x20;
        bool leftToRight = (imageDescriptor & 0x10) != 0x10;

        int pos = 18 + idLength;
        if (pos > d.Length) return false;

        // Palette
        if (format == D3DFmt.P8)
        {
            if (colorMapIndex + colorMapLength > 256) return false;
            Palette = new uint[256];
            for (int i = 0; i < 256; i++) Palette[i] = 0xFFFFFFFF;

            int pb = pos;
            for (int c = 0; c < colorMapLength; c++)
            {
                uint uA = 0, uR = 0, uG = 0, uB = 0;
                switch (colorMapFormat)
                {
                    case D3DFmt.X1R5G5B5:
                    {
                        ushort u = BitConverter.ToUInt16(d, pb);
                        uA = 0xff; uR = (uint)((u >> 10) & 0x1f); uG = (uint)((u >> 5) & 0x1f); uB = (uint)(u & 0x1f);
                        uR = (uR << 3) | (uR >> 2); uG = (uG << 3) | (uG >> 2); uB = (uB << 3) | (uB >> 2);
                        pb += 2; break;
                    }
                    case D3DFmt.A1R5G5B5:
                    {
                        ushort u = BitConverter.ToUInt16(d, pb);
                        uA = (uint)((u >> 15) * 0xff); uR = (uint)((u >> 10) & 0x1f); uG = (uint)((u >> 5) & 0x1f); uB = (uint)(u & 0x1f);
                        uR = (uR << 3) | (uR >> 2); uG = (uG << 3) | (uG >> 2); uB = (uB << 3) | (uB >> 2);
                        pb += 2; break;
                    }
                    case D3DFmt.X8R8G8B8:
                        uA = 0xff; uR = d[pb + 2]; uG = d[pb + 1]; uB = d[pb + 0]; pb += 3; break;
                    case D3DFmt.A8R8G8B8:
                    {
                        uint u = BitConverter.ToUInt32(d, pb);
                        uA = (u >> 24) & 0xff; uR = (u >> 16) & 0xff; uG = (u >> 8) & 0xff; uB = u & 0xff; pb += 4; break;
                    }
                }
                Palette[colorMapIndex + c] = (uA << 24) | (uR << 16) | (uG << 8) | uB;
            }
        }
        pos += (int)(colorMapLength * colorMapBytes);
        if (pos > d.Length) return false;

        Format = format;
        Width = width;
        Height = height;
        Pitch = (uint)(width * uBytes);

        // Decode pixel data into a tightly packed buffer (uBytes per pixel).
        uint cbImage = (uint)(width * height * uBytes);
        var img = new byte[cbImage];

        if (!rle && topToBottom && leftToRight)
        {
            int n = (int)Math.Min((uint)(d.Length - pos), cbImage);
            Array.Copy(d, pos, img, 0, n);
        }
        else
        {
            int destYStride = topToBottom ? (int)Pitch : -(int)Pitch;
            int destYStart = topToBottom ? 0 : (int)((height - 1) * Pitch);

            int src = pos;
            for (uint y = 0; y < height; y++)
            {
                int destY = destYStart + (int)y * destYStride;
                int destX = leftToRight ? destY : destY + (int)Pitch - (int)uBytes;

                for (uint x = 0; x < width;)
                {
                    bool runLength;
                    uint count;
                    if (rle)
                    {
                        if (src >= d.Length) return false;
                        runLength = (d[src] & 0x80) != 0;
                        count = (uint)((d[src] & 0x7f) + 1);
                        src++;
                    }
                    else
                    {
                        runLength = false;
                        count = width;
                    }

                    x += count;
                    while (count-- > 0)
                    {
                        if (src + uBytes > d.Length) return false;
                        Array.Copy(d, src, img, destX, (int)uBytes);
                        if (!runLength) src += (int)uBytes;
                        destX = leftToRight ? destX + (int)uBytes : destX - (int)uBytes;
                    }
                    if (runLength) src += (int)uBytes;
                }
            }
        }

        // 24-bit X8R8G8B8: expand in place to 32-bit (B,G,R,0xFF per texel).
        if (format == D3DFmt.X8R8G8B8)
        {
            var expanded = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                expanded[i * 4 + 0] = img[i * 3 + 0]; // B
                expanded[i * 4 + 1] = img[i * 3 + 1]; // G
                expanded[i * 4 + 2] = img[i * 3 + 2]; // R
                expanded[i * 4 + 3] = 0xff;           // A
            }
            img = expanded;
            Pitch = width * 4u;
        }

        Data = img;
        return true;
    }

    /// <summary>Port of CImage::Depalettize — expand a P8 surface to A8R8G8B8.</summary>
    public void Depalettize()
    {
        if (Palette == null) return;

        var dst = new byte[Width * Height * 4];
        uint srcInc = (Width + 3) & ~3u;
        int src = 0, d = 0;
        for (uint y = 0; y < Height; y++)
        {
            for (uint x = 0; x < Width; x++)
            {
                uint argb = Palette[Data[src++]];
                dst[d++] = (byte)(argb & 0xff);         // B
                dst[d++] = (byte)((argb >> 8) & 0xff);  // G
                dst[d++] = (byte)((argb >> 16) & 0xff); // R
                dst[d++] = (byte)((argb >> 24) & 0xff); // A
            }
            src += (int)(srcInc - Width);
        }

        Palette = null;
        Data = dst;
        Format = D3DFmt.A8R8G8B8;
        Pitch = Width * 4;
    }

    /// <summary>Port of LoadImageFromImage's IsUvl.</summary>
    private static bool IsUvl(uint format)
    {
        switch (format)
        {
            case D3DFmt.V16U16:
            case D3DFmt.V8U8:
            case D3DFmt.L6V5U5:
            case D3DFmt.X8L8V8U8:
            case D3DFmt.Q8W8V8U8:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Port of LoadImageFromImage (loadimage.cpp): builds the D3DX_BLT structs from the
    /// CImage surfaces exactly as the XDK bundler does and drives the ported CXD3DXBlt
    /// filter. Dest region/subregion cover the whole destination; src subregion covers
    /// the whole source.
    /// </summary>
    public static void Blt(CImage dest, CImage src, uint filter, uint colorKey)
    {
        uint dwRealFilter = filter;

        if (D3DX.DEFAULT == dwRealFilter)
        {
            if (IsUvl(src.Format))
                dwRealFilter = D3DX.FILTER_POINT | D3DX.FILTER_DITHER;
            else
                dwRealFilter = D3DX.FILTER_TRIANGLE | D3DX.FILTER_DITHER;
        }

        var destBlt = new D3DX_BLT
        {
            pData = dest.Data,
            dataOffset = 0,
            RowPitch = dest.Pitch,
            SlicePitch = 0,
            Format = dest.Format,
            ColorKey = 0,
            pPalette = dest.Palette,
        };

        destBlt.Region.Left = 0;
        destBlt.Region.Right = dest.Width;
        destBlt.Region.Top = 0;
        destBlt.Region.Bottom = dest.Height;
        destBlt.Region.Front = 0;
        destBlt.Region.Back = 1;

        destBlt.SubRegion.Left = 0;
        destBlt.SubRegion.Top = 0;
        destBlt.SubRegion.Right = dest.Width;
        destBlt.SubRegion.Bottom = dest.Height;
        destBlt.SubRegion.Front = 0;
        destBlt.SubRegion.Back = 1;

        var srcBlt = new D3DX_BLT
        {
            pData = src.Data,
            dataOffset = 0,
            RowPitch = src.Pitch,
            SlicePitch = 0,
            Format = src.Format,
            ColorKey = colorKey,
            pPalette = src.Palette,
        };

        srcBlt.SubRegion.Left = 0;
        srcBlt.SubRegion.Top = 0;
        srcBlt.SubRegion.Right = src.Width;
        srcBlt.SubRegion.Bottom = src.Height;
        srcBlt.SubRegion.Front = 0;
        srcBlt.SubRegion.Back = 1;

        int hr = new CXD3DXBlt().Blt(destBlt, srcBlt, dwRealFilter);
        if (CXD3DXBlt.FAILED(hr))
            throw new BundlerException(
                $"CXD3DXBlt failed (hr=0x{hr:X8}) converting source format 0x{src.Format:X} " +
                $"({src.Width}x{src.Height}) to dest format 0x{dest.Format:X} ({dest.Width}x{dest.Height}).");
    }
}
