"""轨迹可视化：读取 localizetrack 输出的 CSV，在官方底图上画出轨迹并统计速度。

用法（项目根目录下）:
  .venv-train\\Scripts\\python.exe ai-training\\src\\track_plot.py                # 自动选 logs/ 下最新的 CSV
  .venv-train\\Scripts\\python.exe ai-training\\src\\track_plot.py <csv路径>       # 指定 CSV

输出:
  screenshots/mapoverview/track_<CSV名>.png   轨迹图（绿=起点，黄=终点，红线=轨迹，红叉=被拒帧）
  控制台打印：接受/跳过统计、速度统计（中位/p90/最大）、被拒原因分类
"""
import _console  # noqa: F401
import csv
import math
import sys
from collections import Counter
from datetime import datetime
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]          # ai-training/
REPO = ROOT.parent
MAP = REPO / "screenshots" / "mapoverview" / "zero_dam_tiles_z4.png"
OUT = REPO / "screenshots" / "mapoverview"

M_PER_PX = 0.40   # 底图像素→米（按 4096px ≈ 1560m 估算，待真值校准）


def latest_csv() -> Path:
    logs = REPO / "logs"
    files = sorted(logs.glob("localizetrack_*.csv"), key=lambda p: p.stat().st_mtime)
    if not files:
        raise SystemExit("logs/ 下没有 localizetrack_*.csv —— 先跑 localizetrack 命令")
    return files[-1]


def main():
    csv_path = Path(sys.argv[1]) if len(sys.argv) > 1 else latest_csv()
    rows = list(csv.DictReader(open(csv_path, encoding="utf-8-sig")))
    acc = [r for r in rows if r["accepted"] == "1" and r["cx"]]
    held = [r for r in rows if r["accepted"] != "1"]
    print(f"CSV: {csv_path.name}")
    print(f"总 {len(rows)} 帧 | 接受 {len(acc)} | 跳过/拒绝 {len(held)}")

    # 速度统计（按接受帧的相邻位移）
    speeds = []
    prev = None
    for r in acc:
        t = datetime.strptime(r["created"], "%Y-%m-%d %H:%M:%S")
        x, y = float(r["cx"]), float(r["cy"])
        if prev:
            dt = (t - prev[0]).total_seconds()
            if dt > 0:
                speeds.append(math.hypot(x - prev[1], y - prev[2]) * M_PER_PX / dt)
        prev = (t, x, y)
    if speeds:
        s = sorted(speeds)
        print(f"速度: 中位 {s[len(s)//2]:.2f} m/s | p90 {s[int(len(s)*0.9)]:.2f} | 最大 {s[-1]:.2f}")

    reasons = Counter()
    for r in held:
        note = r.get("note", "")
        key = ("UI 界面（无小地图）" if "UI 界面" in note else
               "超速/瞬移被拒" if "超速" in note else
               "无定位结果" if "无定位" in note else "其他")
        reasons[key] += 1
    if reasons:
        print("被拒原因:", dict(reasons))

    # 画图
    if not MAP.exists():
        raise SystemExit(f"缺底图: {MAP}")
    img = Image.open(MAP).convert("RGB").resize((1400, 1050), Image.LANCZOS)
    d = ImageDraw.Draw(img)
    kx, ky = 1400 / 4096, 1050 / 3072
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 20)
    except OSError:
        font = ImageFont.load_default()

    pts = [(float(r["cx"]) * kx, float(r["cy"]) * ky) for r in acc]
    if len(pts) > 1:
        d.line(pts, fill=(255, 60, 60), width=4)
    for r in held:
        if r["cx"]:
            x, y = float(r["cx"]) * kx, float(r["cy"]) * ky
            d.line([x - 6, y - 6, x + 6, y + 6], fill=(255, 0, 255), width=3)
            d.line([x - 6, y + 6, x + 6, y - 6], fill=(255, 0, 255), width=3)
    if pts:
        d.ellipse([pts[0][0] - 9, pts[0][1] - 9, pts[0][0] + 9, pts[0][1] + 9], outline=(0, 255, 0), width=4)
        d.text((pts[0][0] + 12, pts[0][1] - 10), "start", fill=(0, 255, 0), font=font)
        d.ellipse([pts[-1][0] - 9, pts[-1][1] - 9, pts[-1][0] + 9, pts[-1][1] + 9], outline=(255, 255, 0), width=4)
        d.text((pts[-1][0] + 12, pts[-1][1] - 10), "end", fill=(255, 255, 0), font=font)

    out = OUT / f"track_{csv_path.stem}.png"
    img.save(out)
    print("轨迹图:", out)


if __name__ == "__main__":
    main()
