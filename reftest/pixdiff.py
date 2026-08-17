"""Compare A8R8G8B8 textures in two XPRs, reporting per-channel deltas and positions.

A8R8G8B8 is stored swizzled (Morton order), so the linear index is decoded back
to (x, y) for pattern spotting.
"""
import struct
import sys
from collections import Counter

CH = {0: "B", 1: "G", 2: "R", 3: "A"}


def unswizzle(i, w, h):
    """Morton-decode index i for a w*h surface."""
    x = y = 0
    bx = by = 0
    v = i
    while v:
        if w > (1 << bx):
            x |= (v & 1) << bx
            v >>= 1
            bx += 1
        if h > (1 << by) and v:
            y |= (v & 1) << by
            v >>= 1
            by += 1
    return x, y


def texs(buf):
    total, hdr = struct.unpack_from("<II", buf, 4)
    o, i = 12, 0
    out = []
    while o + 20 <= hdr:
        common, data, lock, fmt, size = struct.unpack_from("<5I", buf, o)
        if common == 0 and fmt == 0:
            break
        out.append((1 << ((fmt >> 20) & 0xF), 1 << ((fmt >> 24) & 0xF),
                    (fmt >> 8) & 0xFF, hdr + data))
        o += 20
    return out


a = open(sys.argv[1], "rb").read()
b = open(sys.argv[2], "rb").read()

for i, (w, h, kind, off) in enumerate(texs(a)):
    if kind != 0x06:
        continue
    chans = Counter()
    pos = []
    for p in range(w * h):
        for c in range(4):
            o = off + p * 4 + c
            if a[o] != b[o]:
                chans[f"{CH[c]} {b[o] - a[o]:+d}"] += 1
                pos.append((p, c, a[o], b[o]))
    print(f"tex[{i}] {w}x{h}: {len(pos)} differing channel bytes of {w * h * 4}")
    for k, v in chans.most_common(8):
        print(f"     {k:8s} x{v}")
    for p, c, va, vb in pos[:10]:
        x, y = unswizzle(p, w, h)
        edge = []
        if x == 0:
            edge.append("left")
        if x == w - 1:
            edge.append("right")
        if y == 0:
            edge.append("top")
        if y == h - 1:
            edge.append("bottom")
        print(f"     ({x:4d},{y:4d}) {CH[c]} ref={va:3d} act={vb:3d} "
              f"{'/'.join(edge) or ''}")
    if len(pos) > 10:
        print("     ...")
