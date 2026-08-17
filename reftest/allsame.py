"""Model s3_quant.cpp's allSame() exactly, to see which ramp it selects."""
import sys

SIZE = [5, 6, 5]
COEFF = [(1, 0, 0, 1), (1, 1, 0, 2), (1, 2, 1, 3)]

grid = [[[None] * 3 for _ in range(3)] for _ in range(256)]
for l in range(3):
    for i in range((1 << SIZE[l]) - 1, -1, -1):
        for j in range((1 << SIZE[l]) - 1, -1, -1):
            p0 = (i << (8 - SIZE[l])) | (i >> (2 * SIZE[l] - 8))
            p1 = (j << (8 - SIZE[l])) | (j >> (2 * SIZE[l] - 8))
            for k in range(3):
                a, b, c, d = COEFF[k]
                p = (a * p0 + b * p1 + c) // d
                g = grid[p][l][k]
                if g is None or abs(g[1] - g[0]) > abs(p1 - p0):
                    grid[p][l][k] = (i << (8 - SIZE[l]), j << (8 - SIZE[l]))


def all_same(n_colors, pixels, weight):
    """pixels: list of (r,g,b) 0..255, all equal. Returns (j, endpoints, errors)."""
    n = len(pixels)
    q = [[c / 255.0 * weight[i] for i, c in enumerate(px)] for px in pixels]
    color_error = [0.0, 0.0, 0.0]
    channel_value = [[0] * 3 for _ in range(3)]

    for j in range(n_colors - 1):
        color_error[j] = 0.0
        for i in range(3):
            x = q[0][i] * 255.0 / weight[i]
            c = int((x + 0.5) // 1)
            c = 0 if c < 0 else (c if c < 256 else 255)
            c_top_bot = [0, 0]
            error = [0.0, 0.0]
            delta = 1
            for l in range(2):
                k = c
                if grid[k][i][j] is None or (x - float(c)) * float(delta) > 0:
                    k = c + delta
                    k = 0 if k < 0 else (k if k < 256 else 255)
                    while grid[k][i][j] is None:
                        k += delta
                error[l] = 0.0
                for m in range(n):
                    d = float(k) * weight[i] - q[m][i] * 255.0
                    error[l] += d * d
                c_top_bot[l] = k
                delta = -delta
            if error[0] < error[1]:
                color_error[j] += error[0]
                channel_value[i][j] = c_top_bot[0]
            elif error[0] > error[1]:
                color_error[j] += error[1]
                channel_value[i][j] = c_top_bot[1]
            else:
                color_error[j] += error[1]
                channel_value[i][j] = c_top_bot[0] if (c & 1) else c_top_bot[1]

    if n_colors == 4:
        j = (0 if color_error[0] <= color_error[2] else 2) \
            if color_error[0] <= color_error[1] else \
            (1 if color_error[1] <= color_error[2] else 2)
    else:
        j = 0 if color_error[0] <= color_error[1] else 1

    ep = [[0] * 3 for _ in range(2)]
    for i in range(3):
        for k in range(2):
            ep[k][i] = grid[channel_value[i][j]][i][j][k] >> (8 - SIZE[i])
    out_colors = j + 2 if j != 0 else n_colors
    return j, ep, color_error, out_colors


if __name__ == "__main__":
    rgb = tuple(int(v) for v in sys.argv[1].split(","))
    w = (1.0, 1.0, 1.0)
    for lvl in (4, 3):
        j, ep, err, oc = all_same(lvl, [rgb] * 16, w)
        c0 = (ep[0][0] << 11) | (ep[0][1] << 5) | ep[0][2]
        c1 = (ep[1][0] << 11) | (ep[1][1] << 5) | ep[1][2]
        print(f"inLevel={lvl}: j={j} index={j + 1} outColors={oc} "
              f"c0={c0:#06x} c1={c1:#06x}")
        print(f"    colorError={err}")
