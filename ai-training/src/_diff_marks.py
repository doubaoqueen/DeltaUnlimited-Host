"""标记提取 v3（临时）：标注图与干净 overview 对齐到地图空间后做差分 → 提取所有标记。

三张截图同一视图（k≈1.15, A≈(18,197)），差分可同时找出撤离点与出生点标记（不依赖颜色）。
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
S = 2                                   # 地图空间降采样倍数
W_, H_ = MAP_W // S, MAP_H // S


def to_map_gray(path, k, A):
    """把截图按 map_px = A + p*k 重采样到地图空间（灰度）。"""
    im = Image.open(path).convert("L")
    # 逆变换：p = (map*S - A)/k
    a, b, c = S / k, 0.0, -A[0] / k
    d, e, f = 0.0, S / k, -A[1] / k
    return np.asarray(im.transform((W_, H_), Image.AFFINE, (a, b, c, d, e, f), resample=Image.BILINEAR),
                      dtype=np.float32)


def main():
    overview = OUT / "zero_dam_overview.png"
    k0, A0, s0, _ = find_alignment(overview)
    ov = to_map_gray(overview, k0, A0)
    print(f"基准 overview: k={k0:.3f} A=({A0[0]:.0f},{A0[1]:.0f}) score={s0:.3f}")

    all_marks = {}
    for name in ("zero_dam_extract.png", "zero_dam_spqwn.png"):
        p = OUT / name
        k, A, s, _ = find_alignment(p)
        img = to_map_gray(p, k, A)
        diff = np.abs(img - ov)
        # 标记：与干净图差异显著
        mask = diff > 28
        cs = detect_clusters(mask, min_area=60, gap=4)
        print(f"\n{name}: k={k:.3f} A=({A[0]:.0f},{A[1]:.0f}) score={s:.3f} → 差异簇 {len(cs)}")
        marks = []
        for cx, cy, area in cs:
            mx, my = cx * S, cy * S          # 回到原尺寸底图坐标
            wx, wy = px_to_world(mx, my)
            marks.append({"map": [round(mx), round(my)], "world": [round(wx), round(wy)], "area": area})
            print(f"  底图({mx:.0f},{my:.0f}) 世界({wx:.0f},{wy:.0f}) area={area}")
        all_marks[name] = marks
    json.dump(all_marks, open(OUT / "marks_diff.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("\nsaved marks_diff.json")


if __name__ == "__main__":
    main()
