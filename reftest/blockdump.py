"""Dump the colour half of DXT5 blocks for one texture in both files, side by side."""
import struct
import sys
import uixdiff as U


def rgb565(v):
    return ((v >> 11) & 0x1F) * 8, ((v >> 5) & 0x3F) * 4, (v & 0x1F) * 8


a = open(sys.argv[1], "rb").read()
b = open(sys.argv[2], "rb").read()
sec = int(sys.argv[3], 0)
idx = int(sys.argv[4], 0)
limit = int(sys.argv[5]) if len(sys.argv) > 5 else 16

for s in U.sections(a):
    if s["id"] != sec:
        continue
    base = s["offset"] + s["objects"] * 8 + s["xpr"]
    t = U.textures(a, base)[idx]
    nblocks = (t["w"] // 4) * (t["h"] // 4)
    stride = 8 if t["fmt"] == "DXT1" else 16
    skip = 0 if t["fmt"] == "DXT1" else 8
    print(f"{t['w']}x{t['h']} {t['fmt']}, {nblocks} blocks at {t['off']:#x}")
    shown = 0
    for i in range(nblocks):
        o = t["off"] + i * stride + skip
        ra, rb = a[o:o + 8], b[o:o + 8]
        if ra == rb and shown >= 0 and limit > 0:
            continue
        c0a, c1a = struct.unpack_from("<HH", ra, 0)
        c0b, c1b = struct.unpack_from("<HH", rb, 0)
        print(f"  block {i:4d}")
        print(f"    ref c0={c0a:#06x}{rgb565(c0a)} c1={c1a:#06x}{rgb565(c1a)} "
              f"idx={ra[4]:02x}{ra[5]:02x}{ra[6]:02x}{ra[7]:02x}")
        print(f"    act c0={c0b:#06x}{rgb565(c0b)} c1={c1b:#06x}{rgb565(c1b)} "
              f"idx={rb[4]:02x}{rb[5]:02x}{rb[6]:02x}{rb[7]:02x}")
        shown += 1
        if shown >= limit:
            print("  ...")
            break
