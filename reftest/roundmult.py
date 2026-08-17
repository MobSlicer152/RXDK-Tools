"""Model roundMult()'s ramp search in strict float32, for one 4x4 block.

Answers: does single precision predict the reference's ramp choice, or ours?
"""
import sys
import numpy as np

# The weights are float literals in the C source even when the quantizer's own
# arithmetic is double, so they are always narrowed to float32 first.
PREC = sys.argv[1] if len(sys.argv) > 1 else "float32"
f = np.float32 if PREC == "float32" else np.float64
BITNUM = [5, 6, 5]
W = [f(np.float32(0.3086)), f(np.float32(0.6094)), f(np.float32(0.0820))]


def roundmult(ncolors, pixels, pmult):
    n = len(pixels)
    # colorChannel = v / 255, q = colorChannel * weight  (both float32)
    cc = [[f(f(v) / f(255.0)) for v in px] for px in pixels]
    q = [[f(cc[k][i] * W[i]) for i in range(3)] for k in range(n)]
    endpoint_in = q  # the two-colour bypass passes qNoRep as the endpoints

    iramp = np.zeros((3, 4, 4), dtype=np.int32)
    for i in range(3):
        lsb = 1 << (8 - BITNUM[i])
        for j in range(2):
            cf = f(f(endpoint_in[j][i] / W[i]) * f(255.0))
            c = int(np.floor(cf))
            c = 0 if c < 0 else (c if c < 256 else 256 - lsb)
            c &= 256 - lsb
            if f(c + (c >> BITNUM[i])) > cf:
                c = c if (c - lsb) < 0 else c - lsb
            iramp[i][0][j] = iramp[i][1 + j][j] = c + (c >> BITNUM[i])
            c = c + lsb if (c + lsb) < 256 else c
            iramp[i][2 - j][j] = iramp[i][3][j] = c + (c >> BITNUM[i])

    ramp = np.zeros((3, 4, 4), dtype=np.float32)
    rampval = [[[] for _ in range(4)] for _ in range(3)]
    for i in range(3):
        for j in range(4):
            iramp[i][j][2] = (2 * iramp[i][j][0] + iramp[i][j][1] + 1) // 3
            iramp[i][j][3] = (iramp[i][j][0] + 2 * iramp[i][j][1] + 1) // 3
            for k in range(4):
                ramp[i][j][k] = f(f(f(iramp[i][j][k]) * W[i]) / f(255.0))
            for k in range(n):
                for m in range(4):
                    d = f(q[k][i] - ramp[i][j][m])
                    rampval[i][j].append(f(f(pmult[k]) * f(f(d) * f(d))))

    m = f(f(f(2.0) * f(f(f(W[0] * W[0]) + f(W[1] * W[1])) + f(W[2] * W[2]))) * f(16.0))
    i0 = -1
    scores = {}
    for i in range(64):
        p0 = rampval[0][i & 3]
        p1 = rampval[1][(i >> 2) & 3]
        p2 = rampval[2][i >> 4]
        d = f(0.0)
        for N in range(n - 1, -1, -1):
            a = f(f(p0[4 * N + 0] + p1[4 * N + 0]) + p2[4 * N + 0])
            b = f(f(p0[4 * N + 1] + p1[4 * N + 1]) + p2[4 * N + 1])
            c = f(f(a + b) - f(abs(f(a - b))))
            a = f(f(p0[4 * N + 2] + p1[4 * N + 2]) + p2[4 * N + 2])
            b = f(f(p0[4 * N + 3] + p1[4 * N + 3]) + p2[4 * N + 3])
            a = f(f(a + b) - f(abs(f(a - b))))
            d = f(d + f(f(a + c) - f(abs(f(a - c)))))
        scores[i] = d
        if d < m:
            m = d
            i0 = i
    return i0, scores, iramp


if __name__ == "__main__":
    px = [(19, 56, 9)] * 8 + [(19, 57, 9)] * 8
    # deduplicated, with multiplicity
    i0, scores, iramp = roundmult(4, [(19, 56, 9), (19, 57, 9)], [8, 8])
    print(f"winning ramp combo i0 = {i0}  (R={i0 & 3} G={(i0 >> 2) & 3} B={i0 >> 4})")
    ep = [[iramp[i][(i0 >> (i * 2)) & 3][j] >> (8 - BITNUM[i]) for i in range(3)]
          for j in range(2)]
    c0 = (ep[0][0] << 11) | (ep[0][1] << 5) | ep[0][2]
    c1 = (ep[1][0] << 11) | (ep[1][1] << 5) | ep[1][2]
    print(f"c0={c0:#06x} c1={c1:#06x}   (reference: 0x19c1 / 0x11c1)")
    best = min(scores.values())
    print("\ncandidates within 1e-9 of the best:")
    for i, v in sorted(scores.items(), key=lambda kv: kv[1]):
        if float(v) - float(best) < 1e-9:
            print(f"   i={i:2d} (R={i & 3} G={(i >> 2) & 3} B={i >> 4})  d={float(v):.12e}")
