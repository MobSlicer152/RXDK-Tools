// Xbox texture swizzle (Morton/Z-order), scalar port of libxgraphics/swizzler.
//
// The XDK ships hand-written SSE/MMX kernels, but they all implement the same
// mapping: a full-texture swizzle places the linear texel (x,y) at the offset
// SwizzleU(x) | SwizzleV(y), where SwizzleU/V deposit the x/y bits into the
// U/V bit lanes defined by GetMasks2. This scalar implementation reproduces the
// asm kernels' output exactly for the full-texture (pRect/pPoint NULL, Pitch 0)
// case that the bundler's WriteSwizzledTextureData uses.

namespace Rxdk.Bundler;

internal static class Swizzle
{
    private static uint Log2(uint v) => (uint)System.Numerics.BitOperations.TrailingZeroCount(v);

    /// <summary>Port of GetMasks2 — the U/V bit lanes for a WxH power-of-two texture.</summary>
    private static void GetMasks2(uint width, uint height, out uint maskU, out uint maskV)
    {
        uint logWidth = Log2(width);
        uint logHeight = Log2(height);
        uint log = Math.Min(logWidth, logHeight);

        uint lowerMask = (1u << (int)(log << 1)) - 1;
        uint upperMask = ~lowerMask;

        maskU = (logWidth > logHeight) ? (0x55555555u | upperMask) : (0x55555555u & lowerMask);
        maskV = (logWidth < logHeight) ? (0xAAAAAAAAu | upperMask) : (0xAAAAAAAAu & lowerMask);

        uint limit = (1u << (int)(logWidth + logHeight)) - 1;
        maskU &= limit;
        maskV &= limit;
    }

    /// <summary>Deposit the low bits of <paramref name="value"/> into the set-bit positions of <paramref name="mask"/> (pdep).</summary>
    private static uint Deposit(uint value, uint mask)
    {
        uint result = 0;
        int bit = 0;
        for (int i = 0; i < 32; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                if ((value & (1u << bit)) != 0)
                    result |= 1u << i;
                bit++;
            }
        }
        return result;
    }

    /// <summary>
    /// Full-texture 2D swizzle. Source is tightly packed (pitch = width*bpp);
    /// destination is the swizzled buffer of the same byte size.
    /// </summary>
    public static byte[] SwizzleRect2D(byte[] src, uint width, uint height, uint bpp)
    {
        var dest = new byte[(int)(width * height * bpp)];
        GetMasks2(width, height, out uint maskU, out uint maskV);

        for (uint y = 0; y < height; y++)
        {
            uint sv = Deposit(y, maskV);
            for (uint x = 0; x < width; x++)
            {
                uint offset = (Deposit(x, maskU) | sv) * bpp;
                uint srcOff = (y * width + x) * bpp;
                for (uint b = 0; b < bpp; b++)
                    dest[offset + b] = src[srcOff + b];
            }
        }
        return dest;
    }
}
