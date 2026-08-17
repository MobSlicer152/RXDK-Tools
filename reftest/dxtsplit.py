"""Split DXT5 differences into the alpha half (bytes 0-7) and colour half (8-15)."""
import sys
import uixdiff as U

a = open(sys.argv[1], "rb").read()
b = open(sys.argv[2], "rb").read()

tot_a = tot_c = 0
for s in U.sections(a):
    if s["xpr"] == 0xFFFFFFFF:
        continue
    base = s["offset"] + s["objects"] * 8 + s["xpr"]
    for i, t in enumerate(U.textures(a, base)):
        al = co = 0
        for o in range(t["off"], t["end"]):
            if a[o] != b[o]:
                if t["fmt"] in ("DXT2/3", "DXT4/5") and (o - t["off"]) % 16 < 8:
                    al += 1
                else:
                    co += 1
        tot_a += al
        tot_c += co
        if al or co:
            n = t["end"] - t["off"]
            print(f"sec {s['id']:08x} tex[{i:2d}] {t['w']:4d}x{t['h']:<4d} {t['fmt']:<8s} "
                  f"alpha={al:5d} colour={co:5d}  of {n} bytes")
print(f"\nTOTAL alpha-half {tot_a}, colour-half {tot_c}")
