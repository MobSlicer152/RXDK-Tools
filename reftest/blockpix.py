"""Print the 16 pixels of one 4x4 DXT block from a swizzled A8R8G8B8 texture in an XPR."""
import struct
import sys


def swizzle(x, y, w, h):
    """Morton-encode (x, y) for a w*h surface."""
    i = 0
    bit = 0
    bx = by = 0
    while (1 << bx) < w or (1 << by) < h:
        if (1 << bx) < w:
            i |= ((x >> bx) & 1) << bit
            bit += 1
            bx += 1
        if (1 << by) < h:
            i |= ((y >> by) & 1) << bit
            bit += 1
            by += 1
    return i


buf = open(sys.argv[1], "rb").read()
block = int(sys.argv[2])
total, hdr = struct.unpack_from("<II", buf, 4)
common, data, lock, fmt, size = struct.unpack_from("<5I", buf, 12)
w, h = 1 << ((fmt >> 20) & 0xF), 1 << ((fmt >> 24) & 0xF)
base = hdr + data
bx = (block % (w // 4)) * 4
by = (block // (w // 4)) * 4
print(f"{w}x{h}, block {block} at ({bx},{by})")
seen = {}
for dy in range(4):
    row = []
    for dx in range(4):
        p = swizzle(bx + dx, by + dy, w, h)
        v = struct.unpack_from("<I", buf, base + p * 4)[0]
        row.append(f"({v >> 16 & 255:3d},{v >> 8 & 255:3d},{v & 255:3d}|a{v >> 24:3d})")
        seen[v] = seen.get(v, 0) + 1
    print("   " + " ".join(row))
print(f"{len(seen)} distinct")
