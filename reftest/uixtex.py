"""List every texture in one section's embedded XPR."""
import sys
import uixdiff as U

buf = open(sys.argv[1], "rb").read()
want = int(sys.argv[2], 0)
for s in U.sections(buf):
    if s["id"] != want:
        continue
    base = s["offset"] + s["objects"] * 8 + s["xpr"]
    tex = U.textures(buf, base)
    print(f"{len(tex)} textures in section {want:08x}")
    for i, t in enumerate(tex):
        print(f"[{i:3d}] {t['w']:4d}x{t['h']:<4d} {t['fmt']:<8s} mips={t['mips']} "
              f"fmt={t['raw']:#010x} data=[{t['off']:#08x}..{t['end']:#08x})")
