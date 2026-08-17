"""Report the distinct ARGB pixels of each A8R8G8B8 texture in an XPR0."""
import struct
import sys
from collections import Counter

buf = open(sys.argv[1], "rb").read()
assert buf[:4] == b"XPR0"
total, hdr = struct.unpack_from("<II", buf, 4)
o = 12
i = 0
while o + 20 <= hdr:
    common, data, lock, fmt, size = struct.unpack_from("<5I", buf, o)
    if common == 0 and fmt == 0:
        break
    w, h = 1 << ((fmt >> 20) & 0xF), 1 << ((fmt >> 24) & 0xF)
    kind = (fmt >> 8) & 0xFF
    if kind != 0x06:
        o += 20
        i += 1
        continue
    base = hdr + data
    c = Counter()
    for p in range(w * h):
        c[struct.unpack_from("<I", buf, base + p * 4)[0]] += 1
    print(f"tex[{i}] {w}x{h} A8R8G8B8, {len(c)} distinct")
    for v, n in c.most_common(12):
        print(f"    {v:#010x}  A={v >> 24:3d} R={(v >> 16) & 255:3d} "
              f"G={(v >> 8) & 255:3d} B={v & 255:3d}  x{n}")
    o += 20
    i += 1
