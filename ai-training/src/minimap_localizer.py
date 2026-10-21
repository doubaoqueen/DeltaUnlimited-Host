"""小地图定位器（离线验证版）：局内帧的小地图 -> 官方底图 的位置还原。

方法：
  1. 小地图 = 左上角圆形面板（圆心/半径由用户标定：center≈(165,185), r≈129，1920×1080 帧坐标）；
  2. 圆形掩膜 NCC（归一化互相关，FFT 加速 + 积分图 + 低方差屏蔽）；
  3. 多尺度搜索（小地图缩放未知），默认按最高分选尺度。

用法：
  python src/minimap_localizer.py --selftest          # 合成图自检（必须通过）
  python src/minimap_localizer.py --regress           # 用用户标注的样本跑回归
  python src/minimap_localizer.py --batch <起> <止>    # 批量定位某时间段的帧，输出 CSV + 轨迹图
  python src/minimap_localizer.py --frame <相对路径>   # 单帧定位 + 对照图
"""

import _console  # noqa: F401
import argparse
import csv
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]          # ai-training/
REPO = ROOT.parent
REC = ROOT / "datasets" / "record"
MAP_PATH = REPO / "screenshots" / "mapoverview" / "zero_dam_tiles_z4.png"
OUT_DIR = REPO / "screenshots" / "mapoverview"

# 小地图圆形面板（用户 2026-10-21 口述四点定圆：帧坐标）
MINI_CX, MINI_CY, MINI_R = 165, 185, 120

F = 2                            # 粗搜索时底图缩放倍数（4096 -> 2048）
SCALES = (150, 180, 210, 245, 285, 330, 380, 440)
MIN_STD = 0.012                  # 局部标准差下限（低方差区域不采信）
MIN_SCORE = 0.55                 # 候选有效最低分


class Localizer:
    def __init__(self, map_path: Path = MAP_PATH, f: int = F, scales=SCALES):
        self.map_img = Image.open(map_path).convert("RGB")
        self.f = f
        self.scales = scales
        self.small = np.asarray(
            self.map_img.convert("L").resize((self.map_img.width // f, self.map_img.height // f), Image.BILINEAR),
            dtype=np.float32) / 255.0

    # ---------- 掩膜 NCC ----------

    @staticmethod
    def _mask(r, size):
        yy, xx = np.mgrid[0:size, 0:size]
        return (((xx - size / 2) ** 2 + (yy - size / 2) ** 2) <= r * r).astype(np.float32)

    @staticmethod
    def _ncc_scores(img, tpl, mask):
        """掩膜 NCC 的完整得分图（不取最大值，供多峰分析）。"""
        H, W = img.shape
        th, tw = tpl.shape
        sH, sW = H + th - 1, W + tw - 1
        N = float(mask.sum())
        mT = float((tpl * mask).sum() / N)
        T0 = (tpl - mT) * mask
        tE = float((T0 * T0).sum())

        def corr(a, b):
            Fa = np.fft.rfft2(a, s=(sH, sW))
            Fb = np.fft.rfft2(b, s=(sH, sW))
            return np.fft.irfft2(Fa * np.conj(Fb), s=(sH, sW))[:H - th + 1, :W - tw + 1]

        c1 = corr(img, mask)
        c2 = corr(img * img, mask)
        cN = corr(img, T0)
        var = np.clip(c2 - c1 * c1 / N, 1e-6, None)
        score = cN / np.sqrt(var * tE)
        score[np.sqrt(var / N) < MIN_STD] = -1.0
        return score

    @staticmethod
    def crop_minimap(frame_img: Image.Image) -> Image.Image:
        return frame_img.crop((MINI_CX - MINI_R, MINI_CY - MINI_R, MINI_CX + MINI_R, MINI_CY + MINI_R))

    def localize(self, frame_path: Path, topk: int = 3):
        """返回 [(score, scale_px, cx, cy)]，按分数降序。cx/cy 为底图坐标（全分辨率）。"""
        img = Image.open(frame_path).convert("RGB")
        mini = self.crop_minimap(img)
        mg = np.asarray(mini.convert("L"), dtype=np.float32) / 255.0
        hits = []
        for region_px in self.scales:
            r_ = max(40, region_px // self.f)
            tpl = np.asarray(
                Image.fromarray((mg * 255).astype(np.uint8)).resize((r_, r_), Image.BILINEAR),
                dtype=np.float32) / 255.0
            sc = self._ncc_scores(self.small, tpl, self._mask(r_ / 2, r_))
            iy, ix = np.unravel_index(np.argmax(sc), sc.shape)
            s = float(sc[iy, ix])
            if s < MIN_SCORE:
                continue
            hits.append((s, region_px, ix * self.f + region_px // 2, iy * self.f + region_px // 2))
        hits.sort(key=lambda h: -h[0])
        return hits[:topk], img, mini

    # ---------- 自检 ----------

    def selftest(self) -> bool:
        # 从底图挖一块当"小地图"，应能找回原位。
        # 注意尺度：底图缩了 F 倍，挖 300px 的块缩到 150px 恰好等于小图尺度（不能用 120，否则比例差 25%）
        patch = np.asarray(
            self.map_img.convert("L").crop((1600, 1000, 1600 + 300, 1000 + 300)).resize((150, 150)),
            dtype=np.float32) / 255.0
        sc = self._ncc_scores(self.small, patch, self._mask(75, 150))
        iy, ix = np.unravel_index(np.argmax(sc), sc.shape)
        s = float(sc[iy, ix])
        cx, cy = ix * self.f + 150, iy * self.f + 150
        ok = s > 0.9 and abs(cx - 1750) < 20 and abs(cy - 1150) < 20
        print(f"selftest: score={s:.3f} @ ({cx},{cy}) 期望≈(1750,1150) -> {'OK' if ok else 'FAIL'}")
        return ok


# ---------- 用户标注样本（回归集）----------

LABELED = [
    # (id, 帧名, 期望位置(底图坐标), 期望尺度, 判定)
    # 2026-10-21 用户复核改判：m2a 正确（轨迹连续）；m2d 是背包界面（无小地图，由 HUD 状态门负责跳过）
    ("m1b", "full_20261012_000848_109.jpg", (2357, 1067), 150, "good"),
    ("m1c", "full_20261012_001113_824.jpg", (2474, 1110), 380, "good"),
    ("m1d", "full_20261012_001457_708.jpg", (1176, 1540), 380, "good"),
    ("m2b", "full_20261012_003010_816.jpg", (1577, 1307), 210, "good"),
    ("m2a", "full_20261012_002950_974.jpg", (1473, 1441), 210, "good"),
    ("m1a", "full_20261012_000707_949.jpg", (2496, 1362), 380, "good"),   # 2026-10-21 用户复核：全部对得上,
]


def regress(loc: Localizer):
    print(f"回归集 {len(LABELED)} 条（good=应命中 / bad=不能再选它 / uncertain=待定）")
    ok = 0
    for tag, name, truth, tscale, verdict in LABELED:
        p = REC / "full" / name
        if not p.exists():
            print(f"  {tag}: 缺帧 {name}")
            continue
        hits, img, mini = loc.localize(p)
        if not hits:
            print(f"  {tag}: 无候选（>= {MIN_SCORE}）")
            continue
        s, scale, cx, cy = hits[0]
        d = np.hypot(cx - truth[0], cy - truth[1])
        hit = d < 80
        if verdict == "good":
            status = "✅" if hit else "❌"
            ok += 1 if hit else 0
        elif verdict == "bad":
            status = "⚠️ 仍选中错误位置" if hit else "✅ 已避开"
        else:
            status = f"（{'命中' if hit else '未命中'}，待用户复核）"
        print(f"  {tag} [{verdict}]: 选中 ({cx},{cy})@{scale} s={s:.3f} | 距标注点 {d:.0f}px {status}")
        if len(hits) > 1:
            print("      次选: " + " | ".join(f"({h[2]},{h[3]})@{h[1]} {h[0]:.3f}" for h in hits[1:]))
    print(f"good 样本命中: {ok}/4")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--regress", action="store_true")
    ap.add_argument("--frame", type=str, default="")
    ap.add_argument("--batch", nargs=2, metavar=("FROM", "TO"), help="按 created 时间段批量")
    args = ap.parse_args()

    loc = Localizer()

    if args.selftest:
        sys.exit(0 if loc.selftest() else 1)

    if args.regress:
        regress(loc)
        return

    if args.frame:
        p = REC / args.frame
        hits, img, mini = loc.localize(p)
        if not hits:
            print("无候选"); return
        s, scale, cx, cy = hits[0]
        print(f"{args.frame}: ({cx},{cy})@{scale} score={s:.3f}")
        for i, (s2, scale2, cx2, cy2) in enumerate(hits[1:], 2):
            print(f"  候选{i}: ({cx2},{cy2})@{scale2} score={s2:.3f}")
        W, H = 240, 240
        canvas = Image.new("RGB", (W * 2 + 30, H + 20), (25, 25, 25))
        canvas.paste(mini.resize((W, H), Image.LANCZOS), (5, 10))
        half = scale // 2
        canvas.paste(loc.map_img.crop((cx - half, cy - half, cx + half, cy + half)).resize((W, H), Image.LANCZOS), (W + 25, 10))
        out = OUT_DIR / ("localize_" + Path(args.frame).stem + ".png")
        canvas.save(out)
        print("saved", out)
        return

    if args.batch:
        frm, to = args.batch
        rows = [r for r in csv.DictReader(open(REC / "record_manifest.csv", encoding="utf-8"))
                if frm <= r["created"] <= to]
        out_csv = ROOT / "runs" / f"localize_{frm.replace(':','').replace(' ','_')}_{to.replace(':','').replace(' ','_')}.csv"
        out_csv.parent.mkdir(parents=True, exist_ok=True)
        with open(out_csv, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["created", "relpath", "score", "scale_px", "cx", "cy"])
            for r in rows:
                p = REC / r["relpath"].replace("\\", "/")
                hits, _, _ = loc.localize(p)
                if hits:
                    s, scale, cx, cy = hits[0]
                    w.writerow([r["created"], r["relpath"], f"{s:.3f}", scale, cx, cy])
                else:
                    w.writerow([r["created"], r["relpath"], "", "", "", ""])
                f.flush()
        print(f"批量 {len(rows)} 帧 -> {out_csv}")
        return

    print("用法见文件头注释")


if __name__ == "__main__":
    main()

