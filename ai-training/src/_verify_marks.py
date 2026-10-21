"""标记标注验证图（临时）：在地图上画出检测到的出生点/撤离点，编号供用户确认。"""
import _console  # noqa: F401
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

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


ov = to_map_rgb(OUT / "zero_dam_overview.png", *find_alignment(OUT / "zero_dam_overview.png")[:2])

# 出生点：亮色差异簇
k, A, _, _ = find_alignment(OUT / "zero_dam_spqwn.png")
img = to_map_rgb(OUT / "zero_dam_spqwn.png", k, A)
gray = img.mean(axis=2)
spawn_mask = (gray > 190) & ((gray - ov.mean(axis=2)) > 50)
spawns = [(cx * S, cy * S, a) for cx, cy, a in detect_clusters(spawn_mask, min_area=25, gap=3)]
spawns.sort(key=lambda t: (t[1], t[0]))

# 撤离点：绿色簇
k2, A2, _, _ = find_alignment(OUT / "zero_dam_extract.png")
im2 = np.asarray(Image.open(OUT / "zero_dam_extract.png").convert("RGB")).astype(int)
r, g, b = im2[:, :, 0], im2[:, :, 1], im2[:, :, 2]
green = (g > 110) & (g > r * 1.35) & (g > b * 1.35)
csh = detect_clusters(green, min_area=40, gap=6)
extracts = [(A2[0] + cx * k2, A2[1] + cy * k2, a) for cx, cy, a in csh]

base = Image.open(OUT / "zero_dam_tiles_z4.png").convert("RGB")
try:
    font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 34)
except OSError:
    font = ImageFont.load_default()
d = ImageDraw.Draw(base)

data = {"spawn_points": [], "extract_points": []}
for i, (mx, my, area) in enumerate(spawns, 1):
    wx, wy = px_to_world(mx, my)
    data["spawn_points"].append({"id": f"S{i}", "map": [round(mx), round(my)], "world": [round(wx), round(wy)]})
    d.ellipse([mx - 16, my - 16, mx + 16, my + 16], outline=(255, 80, 80), width=4)
    d.text((mx + 18, my - 20), f"S{i}", fill=(255, 160, 160), font=font)

for i, (mx, my, area) in enumerate(extracts, 1):
    wx, wy = px_to_world(mx, my)
    data["extract_points"].append({"id": f"E{i}", "map": [round(mx), round(my)], "world": [round(wx), round(wy)]})
    d.ellipse([mx - 22, my - 22, mx + 22, my + 22], outline=(0, 255, 0), width=5)
    d.text((mx + 24, my - 20), f"E{i}", fill=(120, 255, 120), font=font)

base.save(OUT / "marks_verify.png")
json.dump(data, open(OUT / "marks_verify.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(f"出生点 {len(spawns)} 个、撤离点 {len(extracts)} 个")
for s in data["spawn_points"]:
    print(" ", s)
for e in data["extract_points"]:
    print(" ", e)
print("saved marks_verify.png / .json")
