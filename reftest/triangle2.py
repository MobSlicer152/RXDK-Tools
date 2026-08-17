"""Search for the x87 evaluation model that reproduces skinbld's resampled pixels.

The original ran on x87, where a product can stay in an 80-bit register while the
variable it lands in is a 32-bit float. That double-rounding is invisible in a
straight C# transcription, so this brute-forces which values were narrowed.
"""
import itertools
import struct
import sys

import numpy as np

EPSILON = 1e-5
f32 = np.float32


def narrow(v, on):
    return float(f32(v)) if on else float(v)


class Op:
    """Arithmetic that either rounds to 32-bit after every operation (x87 with
    PC=24) or keeps the full register width (PC=53/64)."""

    def __init__(self, on):
        self.on = on

    def __call__(self, v):
        return narrow(v, self.on)

    def mul(self, a, b):
        return narrow(a * b, self.on)

    def add(self, a, b):
        return narrow(a + b, self.on)

    def sub(self, a, b):
        return narrow(a - b, self.on)


def read_bmp(path):
    b = open(path, "rb").read()
    off = struct.unpack_from("<I", b, 10)[0]
    w, h = struct.unpack_from("<ii", b, 18)
    stride = (w * 3 + 3) & ~3
    rows = [[(b[off + y * stride + x * 3 + 2], b[off + y * stride + x * 3 + 1],
              b[off + y * stride + x * 3]) for x in range(w)] for y in range(abs(h))]
    if h > 0:
        rows.reverse()
    return w, abs(h), rows


def setup_triangle(src_lim, dst_lim, nl):
    op = Op(nl)
    scale = op(op(dst_lim) / op(src_lim))
    two_scale_inv = op(op(0.5) / scale)

    out, accum_dst, accum_weight = [], 0, 0.0
    for src in range(src_lim):
        entries = []
        for up in (0, 1):
            f_src = op.sub(op(src + up), 0.5)
            dst_min = op.mul(f_src, scale)
            dst_lim_f = op.add(dst_min, scale)

            n_dst = int(np.floor(dst_min))
            while float(n_dst) < dst_lim_f:
                dst0, dst1 = float(n_dst), op.add(float(n_dst), 1.0)
                u_dst = n_dst % dst_lim

                if u_dst != accum_dst:
                    if accum_weight > EPSILON:
                        entries.append((accum_dst, float(f32(accum_weight))))
                    accum_weight = 0.0
                    accum_dst = u_dst

                dst0, dst1 = max(dst0, dst_min), min(dst1, dst_lim_f)
                weight = op.sub(op.mul(op.add(dst0, dst1), two_scale_inv), f_src)
                scaled = op.mul(op.sub(dst1, dst0),
                                op.sub(1.0, weight) if up else weight)
                accum_weight = op.add(accum_weight, scaled)
                n_dst += 1

        if accum_weight > EPSILON:
            entries.append((accum_dst, float(f32(accum_weight))))
        accum_weight = 0.0
        out.append(entries)
    return out


def resample(rows, sw, sh, dw, dh, nl, nw, np_):
    """nw: narrow fWeight (yWeight * xWeight).  np_: narrow src * fWeight."""
    xf, yf = setup_triangle(sw, dw, nl), setup_triangle(sh, dh, nl)
    acc = [[[0.0] * 3 for _ in range(dw)] for _ in range(dh)]
    inv = float(f32(1.0 / 255.0))
    for sy in range(sh):
        src = [[float(f32(v * inv)) for v in px] for px in rows[sy]]
        for sx in range(sw):
            for y, wy in yf[sy]:
                for x, wx in xf[sx]:
                    w = narrow(wy * wx, nw)
                    for c in range(3):
                        p = narrow(src[sx][c] * w, np_)
                        acc[y][x][c] = float(f32(acc[y][x][c] + p))
    return acc


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


def surface(path, off, w, h):
    b = open(path, "rb").read()
    out = [[None] * w for _ in range(h)]
    for i in range(w * h):
        x, y = unswizzle(i, w, h)
        out[y][x] = (b[off + i * 4 + 2], b[off + i * 4 + 1], b[off + i * 4])
    return out


ref_path = sys.argv[1]
cases = [(sys.argv[i], int(sys.argv[i + 1], 0)) for i in range(2, len(sys.argv), 2)]

ENCODERS = {
    # Every operation rounds to 32 bits, as a straight C# transcription does.
    "float32": lambda v: int(f32(f32(v * f32(255.0)) + f32(0.5))),
    # The product and the dither add stay in an x87 register; only F2I's FLOAT
    # parameter narrows the result before the truncating fistp.
    "narrowed": lambda v: int(f32(v * 255.0 + 0.5)),
    # ... and the same but with the narrowing elided altogether.
    "register": lambda v: int(v * 255.0 + 0.5),
}

for nl, nw, np_ in itertools.product((True, False), repeat=3):
    for name, enc in ENCODERS.items():
        bad = total = 0
        for src_path, off in cases:
            sw, sh, rows = read_bmp(src_path)
            ref = surface(ref_path, off, 32, 32)
            acc = resample(rows, sw, sh, 32, 32, nl, nw, np_)
            bad += sum(1 for y in range(32) for x in range(32) for c in range(3)
                       if enc(acc[y][x][c]) != ref[y][x][c])
            total += 32 * 32 * 3
        print(f"setup={nl:d} weight={nw:d} product={np_:d} encode={name:9s}: "
              f"{bad} of {total} channels differ")
