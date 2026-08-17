"""Print dimensions / bit depth / compression for the given BMP files."""
import struct
import sys
import glob

for pat in sys.argv[1:]:
    for p in sorted(glob.glob(pat)):
        d = open(p, "rb").read(54)
        if d[:2] != b"BM":
            print(f"{p}: not a BMP")
            continue
        hsz = struct.unpack_from("<I", d, 14)[0]
        w, h = struct.unpack_from("<ii", d, 18)
        bpp = struct.unpack_from("<H", d, 28)[0]
        comp = struct.unpack_from("<I", d, 30)[0]
        print(f"{p:52s} {w:4d}x{abs(h):<4d} {bpp:2d}bpp comp={comp} hdr={hsz} "
              f"{'bottom-up' if h > 0 else 'top-down'}")
