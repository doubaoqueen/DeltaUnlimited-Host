"""截图↔底图对齐 v2（临时）：以干净 overview 为基准，求缩放+平移，再换算标记坐标。

坐标关系：map_px = A + p * k（p=截图像素，k=底图px/截图px）
  - 参考图 ref 是底图缩小 REF_F 倍；
  - 模板（截图中部区域）缩小 k/REF_F 倍后在 ref 上 1:1 匹配。
"""
import _console  # noqa: F401
import json
import numpy as np
from pathlib import Path
from PIL import Image

OUT = Path(r"D:\MeineArbeit\DeltaUnlimited\screenshots\mapoverview")
MAP = Image.open(OUT / "zero_dam_tiles_z4.png").convert("L")
REF_F = 2
ref = np.asarray(MAP.resize((MAP.width // REF_F, MAP.height // REF_F), Image.BILINEAR), dtype=np.float32) / 255.0


def ncc_valid(img, tpl):
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
    mT = float(tpl.mean())
    t = tpl - mT
    tE = float((t * t).sum())
    num = corr - mT * s1
    return num / np.sqrt(var * tE)


def find_alignment(screenshot_path, k_min=0.7, k_max=2.2, k_step=0.05, crop_frac=0.6):
    """返回 (k, A, score)：map_px = A + p*k。用截图中部区域做模板。"""
    im = Image.open(screenshot_path).convert("L")
    w, h = im.size
    cw, ch = int(w * crop_frac), int(h * crop_frac)
    x0, y0 = (w - cw) // 2, (h - ch) // 2
    crop = im.crop((x0, y0, x0 + cw, y0 + ch))

    best = None
    k = k_min
    while k <= k_max + 1e-9:
        # 截图像素 -> ref 像素的缩放：k / REF_F
        sc = k / REF_F
        tw, th = max(40, int(cw * sc)), max(40, int(ch * sc))
        if tw >= ref.shape[1] or th >= ref.shape[0]:
            k += k_step
            continue
        tpl = np.asarray(crop.resize((tw, th), Image.BILINEAR), dtype=np.float32) / 255.0
        score = ncc_valid(ref, tpl)
        iy, ix = np.unravel_index(np.argmax(score), score.shape)
        s = float(score[iy, ix])
        if best is None or s > best[0]:
            best = (s, k, ix, iy, x0, y0, cw, ch)
        k += k_step

    s, k, ix, iy, x0, y0, cw, ch = best
    # ref 像素 (ix,iy) 对应底图 (ix*REF_F, iy*REF_F)，对应截图 (x0, y0)
    A = np.array([ix * REF_F - x0 * k, iy * REF_F - y0 * k])
    return k, A, s, (x0, y0, cw, ch)


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


W_, H_ = 81086.304688, 80988.5
CXW, CYW = 358155.687500, 750191.750000
BJ = 128
XB2, YB2 = W_ / BJ, H_ / BJ


def px_to_world(px, py):
    lng = px / 16.0
    lat = -py / 16.0
    return CXW - (BJ - lng) * XB2, (-BJ - lat) * YB2 - CYW


if __name__ == "__main__":
    # 1) 基准：干净 overview（也应与标注图同一视图/缩放）
    base = OUT / "zero_dam_overview.png"
    if base.exists():
        k0, A0, s0, crop0 = find_alignment(base)
        print(f"[基准] zero_dam_overview.png: score={s0:.3f} k={k0:.2f} A=({A0[0]:.0f},{A0[1]:.0f})")

    # 2) 逐个标注图：各自对齐（更稳）+ 检测标记
    results = {}
    for name, desc, pred in [
        ("zero_dam_extract.png", "绿色", lambda r, g, b: (g > 110) & (g > r * 1.35) & (g > b * 1.35)),
    ]:
        p = OUT / name
        k, A, s, crop = find_alignment(p)
        print(f"\n[{name}] score={s:.3f} k={k:.3f} A=({A[0]:.0f},{A[1]:.0f})")
        a = np.asarray(Image.open(p).convert("RGB")).astype(int)
        mask = pred(a[:, :, 0], a[:, :, 1], a[:, :, 2])
        cs = detect_clusters(mask)
        print(f"  {desc}簇: {len(cs)}")
        pts = []
        for cx, cy, area in cs:
            mx, my = A[0] + cx * k, A[1] + cy * k
            wx, wy = px_to_world(mx, my)
            pts.append({"shot": [round(cx), round(cy)], "map": [round(mx), round(my)],
                        "world": [round(wx), round(wy)], "area": area})
            print(f"  截图({cx:.0f},{cy:.0f}) → 底图({mx:.0f},{my:.0f}) → 世界({wx:.0f},{wy:.0f}) area={area}")
        results[name] = pts
    json.dump(results, open(OUT / "marks_aligned.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("\nsaved marks_aligned.json")
