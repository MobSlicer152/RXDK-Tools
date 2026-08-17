"""One-off: retype the ported S3TC quantizer from double to float.

The XDK tools run the x87 in 24-bit-mantissa mode, so every intermediate in the
original C is already single precision. Rewriting the port's `double` as `float`
reproduces that exactly.
"""
import re
import sys

path = sys.argv[1]
src = open(path, encoding="utf-8-sig").read()
out = []

LITERAL = re.compile(r"(?<![\w.])(\d+\.\d*|\.\d+|\d+\.)(?![\w.])")


def suffix(match):
    text = match.group(1)
    if text.endswith("."):
        text += "0"
    return text + "f"


for line in src.split("\n"):
    stripped = line.lstrip()
    if stripped.startswith("//") or stripped.startswith("*") or stripped.startswith("/*"):
        out.append(line)
        continue

    code, sep, comment = line.partition("//")
    code = re.sub(r"\bdouble\b", "float", code)
    code = LITERAL.sub(suffix, code)
    out.append(code + sep + comment)

open(path, "w", encoding="utf-8", newline="").write("\n".join(out))
print("rewrote", path)
