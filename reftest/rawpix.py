"""Report the differing pixels of uncompressed textures inside two .uix skins.

Uncompressed formats isolate the resampler and alpha merge from the DXT
quantiser, so any difference here is a plain arithmetic mismatch.
"""
import sys

import uixdiff as U

CH = {0: "B", 1: "G", 2: "R", 3: "A"}
RAW = {"A8R8G8B8", "LIN_A8R8G8B8", "L8", "A8", "A1R5G5B5", "A4R4G4B4", "R5G6B5"}


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


a = open(sys.argv[1], "rb").read()
b = open(sys.argv[2], "rb").read()

for s in U.sections(a):
    if s["xpr"] == 0xFFFFFFFF:
        continue
    base = s["offset"] + s["objects"] * 8 + s["xpr"]
    for i, t in enumerate(U.textures(a, base)):
        if t["fmt"] not in RAW:
            continue
        bad = [o for o in range(t["off"], t["end"]) if a[o] != b[o]]
        if not bad:
            continue
        print(f"sec {s['id']:08x} tex[{i:2d}] {t['w']}x{t['h']} {t['fmt']}: "
              f"{len(bad)} of {t['end'] - t['off']} bytes")
        for o in bad:
            k = o - t["off"]
            x, y = unswizzle(k // 4, t["w"], t["h"])
            print(f"     ({x:4d},{y:4d}) {CH[k % 4]} ref={a[o]:3d} act={b[o]:3d} "
                  f"delta={b[o] - a[o]:+d}")
