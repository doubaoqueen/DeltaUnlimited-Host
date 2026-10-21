"""截图↔底图对齐 + 标记坐标换算（临时）。

思路：用户截图与官方底图是同一张地图的不同缩放/平移 → 多尺度 NCC 求仿射（缩放+平移），
再把标记点换算到底图坐标 → 世界坐标。
"""
import _console  # noqa: F401
import json
from pathlib import Path
import numpy as np
from PIL import Image

OUT = r"D:\MeineArbeit\DeltaUnlimited\screenshots\mapoverview"
MAP = Image.open(rf"{OUT}\zero_dam_tiles_z4.png").convert("L")
REF_F = 2
ref = np.asarray(MAP.resize((MAP.width // REF_F, MAP.height // REF_F), Image.BILINEAR), dtype=np.float32) / 255.0


def ncc(img, tpl):
    H, W = img.shape
    th, tw = tpl.shape
    sH, sW = H + th - 1, W + tw - 1
    FI = np.fft.rfft2(img, s=(sH, sW))
    FT = np.fft.rfft2(tpl, s=(sH, sW))
    corr = np.fft.irfft2(FI * np.conj(FT), s=(sH, sW))[:H - th + 1, :W - tw + 1]
    ii = np.pad(img, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    ii2 = np.pad(img * img, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    s1 = ii[th:, tw:] - ii[:-th, tw:] - ii[th:, :-tw] + ii[:-th, :-tw]
    s2 = ii2[th:, tw:] - ii2[:-th, tw:] - ii2[th:, :-tw] + ii2[:-th, :-tw]
    n = th * tw
    var = np.clip(s2 - s1 * s1 / n, 1e-6, None)
    t = tpl - tpl.mean()
    tE = float((t * t).sum())
    num = corr - tpl.mean() * s1
    return num / np.sqrt(var * tE), (th, tw)


def align(screenshot_path, crop_frac=0.55):
    """返回 map_px = A + p * t（p 为截图像素坐标）。"""
    im = Image.open(screenshot_path).convert("L")
    w, h = im.size
    cw, ch = int(w * crop_frac), int(h * crop_frac)
    x0, y0 = (w - cw) // 2, (h - ch) // 2
    crop = im.crop((x0, y0, x0 + cw, y0 + ch))

    best = None
    for width in (360, 480, 640, 820, 1000):
        s = width / cw
        th = int(ch * s)
        tpl = np.asarray(crop.resize((width, th), Image.BILINEAR), dtype=np.float32) / 255.0
        if tpl.shape[0] >= ref.shape[0] or tpl.shape[1] >= ref.shape[1]:
            continue
        score, _ = ncc(ref, tpl)
        iy, ix = np.unravel_index(np.argmax(score), score.shape)
        sc = float(score[iy, ix])
        if best is None or sc > best[0]:
            best = (sc, width, s, ix, iy, x0, y0)
    sc, width, s, ix, iy, x0, y0 = best
    # tpl 像素 u 在截图中的位置: x0 + u / s；在底图中: (ix + u) * REF_F
    k = REF_F / s                     # 底图像素 / 截图像素
    A = np.array([ix * REF_F - x0 * k, iy * REF_F - y0 * k])
    print(f"{Path(screenshot_path).name}: score={sc:.3f} 缩放 k={k:.4f} 偏移 A=({A[0]:.0f},{A[1]:.0f}) 截取=({x0},{y0},{cw},{ch})")
    return k, A, sc


def detect_clusters(mask, min_area=40, gap=6):
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return []
    buckets = {}
    for x, y in zip(xs, ys):
        buckets.setdefault((x // gap, y // gap), []).append((x, y))
    parent = {k: k for k in buckets}

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for (gx, gy) in list(buckets):
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                nb = (gx + dx, gy + dy)
                if nb in parent:
                    ra, rb = find((gx, gy)), find(nb)
                    if ra != rb:
                        parent[ra] = rb
    groups = {}
    for k, pts in buckets.items():
        groups.setdefault(find(k), []).extend(pts)
    out = []
    for pts in groups.values():
        if len(pts) < min_area:
            continue
        arr = np.array(pts)
        out.append((float(arr[:, 0].mean()), float(arr[:, 1].mean()), len(pts)))
    return sorted(out, key=lambda t: -t[2])


# 世界坐标换算（与主项目一致）
W_, H_ = 81086.304688, 80988.5
CXW, CYW = 358155.687500, 750191.750000
BJ = 128
XB2, YB2 = W_ / BJ, H_ / BJ


def px_to_world(px, py):
    lng = px / 16.0
    lat = -py / 16.0
    wx = CXW - (BJ - lng) * XB2
    wy = (-BJ - lat) * YB2 - CYW
    return wx, wy


def analyze_marks(name, pred_desc, pred):
    p = rf"{OUT}\{name}"
    im = Image.open(p).convert("RGB")
    a = np.asarray(im).astype(int)
    mask = pred(a[:, :, 0], a[:, :, 1], a[:, :, 2])
    cs = detect_clusters(mask)
    print(f"\n{name}: {pred_desc} → 簇 {len(cs)}")
    k, A, sc = align(p)
    result = []
    for cx, cy, area in cs:
        mx = A[0] + cx * k
        my = A[1] + cy * k
        wx, wy = px_to_world(mx, my)
        result.append({"px": [round(cx), round(cy)], "map": [round(mx), round(my)],
                       "world": [round(wx), round(wy)], "area": area})
        print(f"  截图({cx:.0f},{cy:.0f}) → 底图({mx:.0f},{my:.0f}) → 世界({wx:.0f},{wy:.0f})")
    return result


extract = analyze_marks("zero_dam_extract.png", "绿色高饱和",
                        lambda r, g, b: (g > 110) & (g > r * 1.35) & (g > b * 1.35))
json.dump(extract, open(rf"{OUT}\zero_dam_extract_points.json", "w", encoding="utf-8"),
          ensure_ascii=False, indent=1)
print("saved zero_dam_extract_points.json")


