"""Map a section's object-table resource IDs back to names using sk_res.h."""
import re
import sys
import uixdiff as U


def load_names(header):
    names = {}
    for line in open(header, encoding="utf-8", errors="replace"):
        m = re.match(r"\s*#define\s+(\S+)\s+(0x[0-9A-Fa-f]+|\d+)", line)
        if m:
            names.setdefault(int(m.group(2), 0), m.group(1))
    return names


buf = open(sys.argv[1], "rb").read()
want = int(sys.argv[2], 0)
names = load_names(sys.argv[3])

for s in U.sections(buf):
    if s["id"] != want:
        continue
    for i in range(s["objects"]):
        o = s["offset"] + i * 8
        rid, blob = U.u32(buf, o), U.u32(buf, o + 4)
        print(f"[{i:3d}] id={rid:08x} off={blob:#010x} {names.get(rid, '?')}")
