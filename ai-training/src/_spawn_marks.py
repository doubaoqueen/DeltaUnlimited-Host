"""出生点标记检测（临时）：亮度差 + 白色簇。

出生点图标无彩色（整图 0 个高饱和像素）→ 用"亮且与干净图不同"的像素找标记。
"""
import _console  # noqa: F401
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _align_marks2 import find_alignment, detect_clusters, px_to_world

OUT = Path(r"D:\MeineArbeit\DeltaUnlimited\screenshots\mapoverview")
MAP_W, MAP_H = 4096, 3072
S = 2
W_, H_ = MAP_W // S, MAP_H // S


def to_map_rgb(path, k, A):
    im = Image.open(path).convert("RGB")
    a, b, c = S / k, 0.0, -A[0] / k
    d, e, f = 0.0, S / k, -A[1] / k
    return np.asarray(im.transform((W_, H_), Image.AFFINE, (a, b, c, d, e, f), resample=Image.BILINEAR),
                      dtype=np.float32)


def main():
    ov_p = OUT / "zero_dam_overview.png"
    k0, A0, s0, _ = find_alignment(ov_p)
    ov = to_map_rgb(ov_p, k0, A0).mean(axis=2)

    for name in ("zero_dam_spqwn.png", "zero_dam_extract.png"):
        p = OUT / name
        k, A, s, _ = find_alignment(p)
        img = to_map_rgb(p, k, A)
        gray = img.mean(axis=2)
        bright = gray > 190
        differs = (gray - ov) > 50
        mask = bright & differs
        print(f"\n{name}: k={k:.3f} 亮且差异像素={int(mask.sum())}")
        cs = detect_clusters(mask, min_area=25, gap=3)
        print(f"  簇: {len(cs)}")
        for cx, cy, area in cs[:20]:
            mx, my = cx * S, cy * S
            wx, wy = px_to_world(mx, my)
            print(f"  底图({mx:.0f},{my:.0f}) 世界({wx:.0f},{wy:.0f}) area={area}")


if __name__ == "__main__":
    main()
