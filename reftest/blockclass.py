"""Classify every differing DXT colour block: endpoints only, indices only, or both."""
import struct
import sys
from collections import Counter
import uixdiff as U

a = open(sys.argv[1], "rb").read()
b = open(sys.argv[2], "rb").read()

kinds = Counter()
chan_delta = Counter()
nblocks_total = 0

for s in U.sections(a):
    if s["xpr"] == 0xFFFFFFFF:
        continue
    base = s["offset"] + s["objects"] * 8 + s["xpr"]
    for t in U.textures(a, base):
        if t["fmt"] not in ("DXT1", "DXT2/3", "DXT4/5"):
            continue
        stride = 8 if t["fmt"] == "DXT1" else 16
        skip = 0 if t["fmt"] == "DXT1" else 8
        n = (t["w"] // 4) * (t["h"] // 4)
        nblocks_total += n
        for i in range(n):
            o = t["off"] + i * stride + skip
            ra, rb = a[o:o + 8], b[o:o + 8]
            if ra == rb:
                continue
            ep_diff = ra[:4] != rb[:4]
            ix_diff = ra[4:] != rb[4:]
            kinds["endpoints+indices" if ep_diff and ix_diff else
                  "endpoints only" if ep_diff else "indices only"] += 1
            if ep_diff:
                for w in range(2):
                    ca = struct.unpack_from("<H", ra, w * 2)[0]
                    cb = struct.unpack_from("<H", rb, w * 2)[0]
                    if ca == cb:
                        continue
                    for name, sh, mask in (("R", 11, 0x1F), ("G", 5, 0x3F), ("B", 0, 0x1F)):
                        d = ((cb >> sh) & mask) - ((ca >> sh) & mask)
                        if d:
                            chan_delta[f"c{w}.{name} {d:+d}"] += 1

print(f"{sum(kinds.values())} differing blocks of {nblocks_total}")
for k, v in kinds.most_common():
    print(f"  {k:20s} {v}")
print("\nendpoint channel deltas:")
for k, v in chan_delta.most_common(20):
    print(f"  {k:12s} {v}")
