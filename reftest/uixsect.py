"""Print the XSK0 header and section directory of one or more skins."""
import struct
import sys
import uixdiff as U

for p in sys.argv[1:]:
    buf = open(p, "rb").read()
    rec, count = U.u16(buf, 4), U.u16(buf, 6)
    app = buf[8:16].split(b"\0")[0].decode("ascii", "replace")
    builtin = U.u32(buf, 16)
    print(f"{p}  ({len(buf)} bytes)")
    print(f"  rec={rec} sections={count} app='{app}' builtin={builtin}")
    for s in U.sections(buf):
        print(f"    id={s['id']:08x} kind={s['kind']} objs={s['objects']:3d} "
              f"off={s['offset']:#08x} xpr=+{s['xpr']:#x} size={s['size']}")
    print()
