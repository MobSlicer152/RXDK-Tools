"""Compare the strings of a kind==1 section between two skins."""
import sys
import uixdiff as U


def strings(buf, want):
    for s in U.sections(buf):
        if s["id"] != want:
            continue
        out = []
        for i in range(s["objects"]):
            o = s["offset"] + i * 8
            rid, blob = U.u32(buf, o), U.u32(buf, o + 4)
            if blob == 0xFFFFFFFF:
                out.append((rid, None))
                continue
            base = s["offset"] + s["objects"] * 8 + blob
            # optional icon table
            icons = 0
            p = base
            if U.u16(buf, p) == 0xE801:
                icons = U.u16(buf, p + 2)
                p += 4 + icons * 12
            end = p
            while U.u16(buf, end) != 0:
                end += 2
            out.append((rid, buf[p:end].decode("utf-16-le", "replace"), icons))
        return out
    return []


a = strings(open(sys.argv[1], "rb").read(), int(sys.argv[3], 0))
b = strings(open(sys.argv[2], "rb").read(), int(sys.argv[3], 0))
print(f"{len(a)} vs {len(b)} objects")
def show(v):
    if v[1] is None:
        return "<absent>"
    return f"{v[1].encode('unicode_escape').decode('ascii')!r} icons={v[2]} len={len(v[1])}"


for i, (x, y) in enumerate(zip(a, b)):
    if x != y:
        print(f"[{i:3d}] id={x[0]:08x}")
        print(f"   ref {show(x)}")
        print(f"   act {show(y)}")
