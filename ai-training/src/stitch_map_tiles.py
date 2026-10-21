"""官方地图瓦片拼接（通用）：下载指定图层的瓦片并拼成整图。

前提：瓦片来自官网地图工具 `game.gtimg.cn/images/dfm/cp/a20240729directory/img/<图层>/<z>_<x>_<y>.jpg`，
需带 Referer 头。图层示例：`map_db`（零号大坝室外）、`daba_0f`/`daba_1f`/`daba_2f`（行政辖区楼层）。

用法（项目根目录下）:
  python ai-training/src/stitch_map_tiles.py --layer daba_1f --z 4 --nx 8 --ny 8 --tile 512
  python ai-training/src/stitch_map_tiles.py --layer map_db  --z 4 --nx 16 --ny 12 --tile 256 --out screenshots/mapoverview/zero_dam_tiles_z4.png

说明：
  - 室外图 `map_db` 已验证 16×12 块 256px = 4096×3072（见 docs/小地图定位P0验证.md §2）；
  - 楼层图（`daba_*f`）为 8×8 块 512px = 4096×4096（2026-10-22 探测所得）；
  - 缺瓦片（404）填黑；建议对结果做一次非黑占比检查。
"""
import _console  # noqa: F401
import argparse
import io
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image

BASE = "https://game.gtimg.cn/images/dfm/cp/a20240729directory/img"
HDR = {"Referer": "https://df.qq.com/cp/a20240729directory/", "User-Agent": "Mozilla/5.0"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--layer", required=True, help="图层名，如 map_db / daba_1f")
    ap.add_argument("--z", type=int, default=4)
    ap.add_argument("--nx", type=int, required=True)
    ap.add_argument("--ny", type=int, required=True)
    ap.add_argument("--tile", type=int, default=256, help="瓦片边长（室外 256，楼层 512）")
    ap.add_argument("--out", default="", help="输出路径（默认 screenshots/mapoverview/<layer>_z<z>.png）")
    args = ap.parse_args()

    root = Path(__file__).resolve().parents[2]
    out = Path(args.out) if args.out else root / "screenshots" / "mapoverview" / f"{args.layer}_z{args.z}.png"
    out.parent.mkdir(parents=True, exist_ok=True)

    canvas = np.zeros((args.ny * args.tile, args.nx * args.tile, 3), dtype=np.uint8)
    ok = miss = 0
    for ty in range(args.ny):
        for tx in range(args.nx):
            url = f"{BASE}/{args.layer}/{args.z}_{tx}_{ty}.jpg"
            try:
                req = urllib.request.Request(url, headers=HDR)
                with urllib.request.urlopen(req, timeout=20) as r:
                    img = Image.open(io.BytesIO(r.read())).convert("RGB")
                if img.size != (args.tile, args.tile):
                    img = img.resize((args.tile, args.tile))
                canvas[ty * args.tile:(ty + 1) * args.tile, tx * args.tile:(tx + 1) * args.tile] = np.array(img)
                ok += 1
            except Exception:
                miss += 1
        print(f"row {ty + 1}/{args.ny} ok={ok} miss={miss}")

    Image.fromarray(canvas).save(out)
    nonblack = float((canvas.max(axis=2) > 8).mean())
    print(f"saved {out} | 瓦片 ok={ok} miss={miss} | 非黑占比 {nonblack*100:.1f}%")


if __name__ == "__main__":
    main()
