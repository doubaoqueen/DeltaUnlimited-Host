"""标记检测（临时）：在用户标注截图中找色块簇（撤离=绿色图标，出生点待测颜色）。"""
import _console  # noqa: F401
import colorsys
from collections import Counter, deque
from pathlib import Path

import numpy as np
from PIL import Image

OUT = Path(r"D:\MeineArbeit\DeltaUnlimited\screenshots\mapoverview")


def clusters_from_mask(mask, min_area=40, gap=6):
    """简易连通域（BFS，带 gap 容差粗化）：返回 [(cx, cy, area)]。"""
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return []
    # 粗网格合并：把坐标按 gap 取整后视为同格
    buckets = {}
    for x, y in zip(xs, ys):
        buckets.setdefault((x // gap, y // gap), []).append((x, y))
    # 邻接粗格再做并查集
    parent = {}

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb

    for k in buckets:
        parent[k] = k
    for (gx, gy) in list(buckets):
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                nb = (gx + dx, gy + dy)
                if nb in parent and nb != (gx, gy):
                    union((gx, gy), nb)
    groups = {}
    for k, pts in buckets.items():
        groups.setdefault(find(k), []).extend(pts)
    out = []
    for pts in groups.values():
        if len(pts) < min_area:
            continue
        arr = np.array(pts)
        out.append((float(arr[:, 0].mean()), float(arr[:, 1].mean()), len(pts)))
    out.sort(key=lambda t: -t[2])
    return out


def analyze(name, pred_desc, pred):
    p = OUT / name
    im = Image.open(p).convert("RGB")
    a = np.asarray(im)
    mask = pred(a[:, :, 0].astype(int), a[:, :, 1].astype(int), a[:, :, 2].astype(int))
    print(f"\n=== {name} {im.size} | 条件: {pred_desc} → 像素 {int(mask.sum())}")
    cs = clusters_from_mask(mask)
    print(f"簇数量: {len(cs)}（前 12）")
    for cx, cy, area in cs[:12]:
        print(f"  ({cx:.0f},{cy:.0f}) area={area}")
    return cs


# 撤离：绿色图标（高饱和绿）
analyze("zero_dam_extract.png", "G>110 且 G 明显大于 R/B",
        lambda r, g, b: (g > 110) & (g > r * 1.35) & (g > b * 1.35))

# 出生点：先看整体高饱和色分布，猜颜色
p = OUT / "zero_dam_spqwn.png"
im = Image.open(p).convert("RGB")
a = np.asarray(im)[::4, ::4]  # 降采样统计
h = Counter()
for r, g, b in a.reshape(-1, 3):
    rr, gg, bb = r / 255, g / 255, b / 255
    hh, ll, ss = colorsys.rgb_to_hls(rr, gg, bb)
    if ss > 0.6 and ll > 0.25:
        h["%d" % int(hh * 360) if False else f"hue{int(hh*12)*30:03d}"] += 1
print("\n=== zero_dam_spqwn.png 高饱和色相分布（30°一档）===")
for k, v in h.most_common(8):
    print(f"  {k}: {v}")
