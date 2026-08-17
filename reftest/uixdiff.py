"""Localize byte differences between two .uix skins down to individual textures.

Walks the XSK0 section directory, parses each embedded XPR0's resource headers,
and reports the differing-byte count per section and per texture.
"""
import struct
import sys

FMT_NAMES = {
    0x00: "L8", 0x02: "A1R5G5B5", 0x04: "A4R4G4B4", 0x05: "R5G6B5",
    0x06: "A8R8G8B8", 0x0C: "DXT1", 0x0E: "DXT2/3", 0x0F: "DXT4/5",
    0x12: "LIN_A8R8G8B8", 0x1A: "A8",
}


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def u16(b, o):
    return struct.unpack_from("<H", b, o)[0]


def sections(buf):
    assert buf[:4] == b"XSK0", "not a skin"
    rec, count = u16(buf, 4), u16(buf, 6)
    out = []
    for i in range(count):
        o = 20 + i * rec
        out.append({
            "id": u32(buf, o),
            "kind": u16(buf, o + 4),
            "objects": u16(buf, o + 6),
            "offset": u32(buf, o + 8),
            "xpr": u32(buf, o + 12),
            "size": u32(buf, o + 16),
        })
    return out


def textures(buf, base):
    """Resource headers of the XPR0 at 'base'. Yields (name-ish, start, end)."""
    if buf[base:base + 4] != b"XPR0":
        return []
    total, hdr = u32(buf, base + 4), u32(buf, base + 8)
    out, o = [], base + 12
    prev = None
    while o + 20 <= base + hdr:
        common, data, lock, fmt, size = struct.unpack_from("<5I", buf, o)
        # A texture header has Common != 0 (refcount/type bits) and a plausible format.
        if common == 0 and data == 0 and fmt == 0:
            break
        kind = (fmt >> 8) & 0xFF
        if kind not in FMT_NAMES and not (0 <= kind <= 0x3F):
            break
        w = 1 << ((fmt >> 20) & 0xF)
        h = 1 << ((fmt >> 24) & 0xF)
        mips = (fmt >> 16) & 0xF
        out.append({
            "off": base + hdr + data,
            "fmt": FMT_NAMES.get(kind, hex(kind)),
            "w": w, "h": h, "mips": mips, "raw": fmt,
            "hdroff": o,
        })
        prev = o
        o += 20
    # texture data runs to the next texture's start (they are laid out in order)
    for i, t in enumerate(out):
        t["end"] = out[i + 1]["off"] if i + 1 < len(out) else base + total
    return out


def main(ref_path, act_path):
    a = open(ref_path, "rb").read()
    b = open(act_path, "rb").read()
    if len(a) != len(b):
        print(f"SIZE MISMATCH ref={len(a)} act={len(b)}")
    n = min(len(a), len(b))
    diff = bytearray(1 if a[i] != b[i] else 0 for i in range(n))
    total = sum(diff)
    print(f"{total} differing bytes of {n}\n")

    for s in sections(a):
        start = s["offset"]
        end = start + s["size"]
        d = sum(diff[start:end])
        tag = f"section id={s['id']:08x} kind={s['kind']} objs={s['objects']:3d} " \
              f"[{start:#08x}..{end:#08x}) xpr=+{s['xpr']:#x}"
        if d == 0:
            print(f"  OK   {tag}")
            continue
        print(f"  DIFF {tag}  {d} bytes")
        xprbase = start + s["objects"] * 8 + s["xpr"]
        for i, t in enumerate(textures(a, xprbase)):
            td = sum(diff[t["off"]:t["end"]])
            hd = sum(diff[t["hdroff"]:t["hdroff"] + 20])
            if td or hd:
                print(f"        tex[{i:2d}] {t['w']:4d}x{t['h']:<4d} {t['fmt']:<9s} "
                      f"mips={t['mips']} [{t['off']:#08x}..{t['end']:#08x}) "
                      f"data={td} hdr={hd}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
