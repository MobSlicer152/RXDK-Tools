"""Model D3DX's triangle-filter resample at two precisions.

The port accumulates in float32 to imitate the original's x87 24-bit precision
control word. This checks that choice against the reference pixels by running
the same algorithm at both precisions and comparing 24x24 -> 32x32 output.
"""
import struct
import sys

import numpy as np

EPSILON = 1e-5


def read_bmp(path):
    b = open(path, "rb").read()
    off = struct.unpack_from("<I", b, 10)[0]
    w, h = struct.unpack_from("<ii", b, 18)
    bpp = struct.unpack_from("<H", b, 28)[0]
    assert bpp == 24, bpp
    stride = (w * 3 + 3) & ~3
    rows = []
    for y in range(abs(h)):
        o = off + y * stride
        rows.append([(b[o + x * 3 + 2], b[o + x * 3 + 1], b[o + x * 3])
                     for x in range(w)])
    if h > 0:
        rows.reverse()
    return w, abs(h), rows


def setup_triangle(src_lim, dst_lim, T):
    """Per source index, the list of (dest index, weight) the filter produces."""
    scale = T(dst_lim) / T(src_lim)
    two_scale_inv = T(0.5) / scale

    out = []
    accum_dst = 0
    accum_weight = T(0.0)

    for src in range(src_lim):
        entries = []
        for up in (0, 1):
            f_src = T(src + up) - T(0.5)
            dst_min = f_src * scale
            dst_lim_f = dst_min + scale

            n_dst = int(np.floor(dst_min))
            while T(n_dst) < dst_lim_f:
                dst0, dst1 = T(n_dst), T(n_dst) + T(1.0)
                u_dst = n_dst % dst_lim

                if u_dst != accum_dst:
                    if accum_weight > EPSILON:
                        entries.append((accum_dst, accum_weight))
                    accum_weight = T(0.0)
                    accum_dst = u_dst

                dst0 = max(dst0, dst_min)
                dst1 = min(dst1, dst_lim_f)

                weight = (dst0 + dst1) * two_scale_inv - f_src
                accum_weight += (dst1 - dst0) * (T(1.0) - weight if up else weight)
                n_dst += 1

        if accum_weight > EPSILON:
            entries.append((accum_dst, accum_weight))
        accum_weight = T(0.0)
        out.append(entries)
    return out


def resample(rows, sw, sh, dw, dh, T):
    xf = setup_triangle(sw, dw, T)
    yf = setup_triangle(sh, dh, T)

    acc = [[[T(0.0)] * 3 for _ in range(dw)] for _ in range(dh)]
    inv = T(1.0) / T(255.0)
    for sy in range(sh):
        src = [[T(v) * inv for v in px] for px in rows[sy]]
        for sx in range(sw):
            for y, wy in yf[sy]:
                for x, wx in xf[sx]:
                    w = wy * wx
                    for c in range(3):
                        acc[y][x][c] += src[sx][c] * w

    return acc


def encode(acc, T):
    return [[tuple(int(v * T(255.0) + T(0.5)) for v in px) for px in row]
            for row in acc]


def unswizzle(i, w, h):
    x = y = bx = by = 0
    while i:
        if w > (1 << bx):
            x |= (i & 1) << bx
            i >>= 1
            bx += 1
        if h > (1 << by) and i:
            y |= (i & 1) << by
            i >>= 1
            by += 1
    return x, y


def surface(path, uix_off, w, h):
    """Read a swizzled A8R8G8B8 surface out of a .uix at a byte offset."""
    b = open(path, "rb").read()
    out = [[None] * w for _ in range(h)]
    for i in range(w * h):
        x, y = unswizzle(i, w, h)
        o = uix_off + i * 4
        out[y][x] = (b[o + 2], b[o + 1], b[o])
    return out


src_path, ref_path, act_path, off = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4], 0)
sw, sh, rows = read_bmp(src_path)
ref = surface(ref_path, off, 32, 32)
act = surface(act_path, off, 32, 32)

CH = "RGB"
raw = {}
for name, T in (("float32", np.float32), ("float64", np.float64)):
    acc = resample(rows, sw, sh, 32, 32, T)
    raw[name] = acc
    got = encode(acc, T)
    dr = [(x, y, c) for y in range(32) for x in range(32) for c in range(3)
          if got[y][x][c] != ref[y][x][c]]
    da = sum(1 for y in range(32) for x in range(32) for c in range(3)
             if got[y][x][c] != act[y][x][c])
    print(f"{name}: {len(dr)} channels differ from reference, {da} from our output")
    for x, y, c in dr:
        print(f"     ({x:3d},{y:3d}) {CH[c]} ref={ref[y][x][c]:3d} got={got[y][x][c]:3d} "
              f"exact={float(acc[y][x][c]) * 255.0:.9f}")

print("\nwhat the reference implies at the float32 disputes:")
for y in range(32):
    for x in range(32):
        for c in range(3):
            v32 = float(raw["float32"][y][x][c]) * 255.0
            v64 = float(raw["float64"][y][x][c]) * 255.0
            if int(v32 + 0.5) != ref[y][x][c]:
                print(f"     ({x:3d},{y:3d}) {CH[c]} ref={ref[y][x][c]:3d} "
                      f"f32={v32:.9f} f64={v64:.9f}")
