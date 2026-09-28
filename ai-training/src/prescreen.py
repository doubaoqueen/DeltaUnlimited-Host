"""素材初筛质检员：把 record/full 的帧发给本地 Qwen3-VL（vLLM 服务），返回结构化 JSON 元数据，
旁挂写入 record/prescreen.csv。训练 manifest 与正式标签完全不受影响。
红线：VLM 只做分流与元数据（scene / roi_usable / 遮挡 / 金帧标记），它的判断不是训练标签；
正式标签永远由人工在 ailabel 中打。

用法（ai-training/ 下，需先启动 vLLM 服务，见 docs/AI初筛操作手册.md）:
  python src/prescreen.py --limit 5     # 试跑 5 张未筛帧
  python src/prescreen.py               # 处理全部未筛帧
  python src/prescreen.py --refresh     # 忽略已有结果重筛
"""

import argparse
import base64
import csv
import io
import json
import random
import urllib.error
import urllib.request
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]              # ai-training/
REC = ROOT / "datasets" / "record"
REPO = ROOT.parent
FIELDS = ["relpath", "scene", "roi_usable", "occlusion", "time_weather",
          "has_enemy", "has_loot_signal", "has_prompt", "quality", "confidence", "error"]

PROMPT = """你是游戏画面数据质检员。图1是 1920×1080 的游戏整帧，图2是从整帧中央偏下裁出的"地面区域"小图（训练模型实际只看这块区域）。
按以下定义逐项判断，只输出一个 JSON 对象，不要输出任何其他文字、不要用 markdown 代码块：
- scene：图1 的界面类型。对局内=第一人称游戏画面且带 HUD；大厅/加载/死亡/结算/收获=对应菜单或结算界面。
- roi_usable：只看图2——能否据此判断"往前走是否会被挡"。看到明确的地面（含障碍物挡路，如墙/箱子）都算 true；只有天空、被面板盖住、贴脸怼墙到无法分辨时才为 false。注意：图1 的 HUD（小地图/罗盘/血条）不在图2 里，与本项无关。
- occlusion：只评价图2 区域：无|UI覆盖(面板/弹窗盖住了图2)|贴脸遮挡(怼墙)|天空|大面积特效。
- time_weather：图1 的时段天气：白天|黄昏|夜战|雾天|未知。
- has_enemy：图1 的游戏世界内可见的敌方角色模型。结算/背包/大厅界面的头像、图标不算。
- has_loot_signal：图1 的游戏世界内可见的金光柱/物资箱高光/尸体袋。结算界面和背包里的物品图标不算。
- has_prompt：图1 游戏画面准星附近出现的交互键位提示（如 [F] 拾取/开门）。菜单按钮、结算界面的图标不算。
- quality：图1 画质：清晰|模糊|黑屏|过曝。
- confidence：你对以上判断的自信程度，0.0到1.0。
只输出 JSON：{"scene": "...", "roi_usable": true或false, "occlusion": "无|UI覆盖|贴脸遮挡|天空|大面积特效", "time_weather": "白天|黄昏|夜战|雾天|未知", "has_enemy": true或false, "has_loot_signal": true或false, "has_prompt": true或false, "quality": "清晰|模糊|黑屏|过曝", "confidence": 0.0到1.0的小数}"""


def load_cfg():
    cfg = json.loads((REPO / "data" / "ai_vision.json").read_text(encoding="utf-8"))
    runtime = json.loads((REPO / "data" / "runtime.json").read_text(encoding="utf-8"))
    return cfg, (runtime.get("design_width", 1920), runtime.get("design_height", 1080))


def to_b64(img: Image.Image, max_w: int) -> str:
    if img.width > max_w:
        img = img.resize((max_w, round(img.height * max_w / img.width)))
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "JPEG", quality=85)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


def ask(base_url: str, model: str, full_b64: str, roi_b64: str, timeout: int = 180) -> str:
    body = {
        "model": model,
        "messages": [{"role": "user", "content": [
            {"type": "text", "text": PROMPT},
            {"type": "image_url", "image_url": {"url": full_b64}},
            {"type": "image_url", "image_url": {"url": roi_b64}},
        ]}],
        "temperature": 0,
        "max_tokens": 220,
    }
    req = urllib.request.Request(
        base_url.rstrip("/") + "/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read())["choices"][0]["message"]["content"]


def parse_verdict(text: str) -> dict | None:
    """从模型输出里抠出 JSON（容忍 markdown 围栏和前后废话），并做类型矫正。"""
    try:
        start, end = text.find("{"), text.rfind("}")
        if start < 0 or end <= start:
            return None
        v = json.loads(text[start:end + 1])

        def as_bool(x):
            return x if isinstance(x, bool) else str(x).strip().lower() == "true"

        try:
            conf = min(1.0, max(0.0, float(v.get("confidence", 0))))
        except (TypeError, ValueError):
            conf = 0.0
        return {
            "scene": str(v.get("scene", "其他")),
            "roi_usable": as_bool(v.get("roi_usable")),
            "occlusion": str(v.get("occlusion", "未知")),
            "time_weather": str(v.get("time_weather", "未知")),
            "has_enemy": as_bool(v.get("has_enemy")),
            "has_loot_signal": as_bool(v.get("has_loot_signal")),
            "has_prompt": as_bool(v.get("has_prompt")),
            "quality": str(v.get("quality", "未知")),
            "confidence": f"{conf:.2f}",
        }
    except (json.JSONDecodeError, AttributeError):
        return None


def load_screened(path: Path) -> dict:
    screened = {}
    if path.exists():
        with open(path, newline="", encoding="utf-8") as f:
            for row in csv.DictReader(f):
                screened[row["relpath"]] = row
    return screened


def print_report(out: Path):
    """读取 prescreen.csv 全量打印漏斗统计（--report 模式与跑完收尾共用）。"""
    all_rows = load_screened(out)
    if not all_rows:
        print("prescreen.csv 还是空的——先跑一轮初筛。")
        return
    usable = [r for r in all_rows.values() if r.get("roi_usable") == "True"]
    gold = [r for r in all_rows.values()
            if any(r.get(k) == "True" for k in ("has_enemy", "has_loot_signal", "has_prompt"))]
    errs = [r for r in all_rows.values() if r.get("error")]
    scenes: dict = {}
    for r in all_rows.values():
        scenes[r.get("scene", "?")] = scenes.get(r.get("scene", "?"), 0) + 1
    print(f"初筛总 {len(all_rows)} → 可用 {len(usable)}（待人工标注）| 金帧 {len(gold)}（YOLO 素材池）| 失败 {len(errs)}")
    print(f"场景分布: {scenes}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="最多处理 N 张（0=全部未筛帧）")
    ap.add_argument("--refresh", action="store_true", help="忽略已有结果，全部重筛")
    ap.add_argument("--report", action="store_true", help="只打印当前漏斗统计，不筛新帧")
    ap.add_argument("--base-url", default="http://localhost:8000/v1")
    ap.add_argument("--model", default="qwen3vl-8b")
    args = ap.parse_args()

    out = REC / "prescreen.csv"
    if args.report:
        print_report(out)
        return

    cfg, (design_w, design_h) = load_cfg()
    rx, ry, rw, rh = cfg["roi"]
    screened = load_screened(out)

    rows = []
    with open(REC / "record_manifest.csv", newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    todo = [r for r in rows
            if (REC / r["relpath"]).exists()
            and (args.refresh or r["relpath"] not in screened)]
    if args.limit > 0:
        todo = todo[:args.limit]
    if not todo:
        print(f"没有待筛帧（清单 {len(rows)} 行，已筛 {len(screened)}）。--refresh 可重筛。")
        return
    print(f"质检员开始工作：待筛 {len(todo)} 帧（已筛 {len(screened)}）| 服务 {args.base_url} | 模型 {args.model}")

    if args.refresh:
        keep = [r for r in screened.values() if r["relpath"] not in {t["relpath"] for t in todo}]
        with open(out, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=FIELDS)
            w.writeheader()
            w.writerows(keep)
        screened = load_screened(out)

    new_file = not out.exists()
    ok = fail = 0
    for i, r in enumerate(todo, 1):
        row = {"relpath": r["relpath"], "scene": "", "roi_usable": "", "occlusion": "",
               "time_weather": "", "has_enemy": "", "has_loot_signal": "", "has_prompt": "",
               "quality": "", "confidence": "", "error": ""}
        try:
            img = Image.open(REC / r["relpath"]).convert("RGB")
            full_b64 = to_b64(img, max_w=1280)
            roi_b64 = to_b64(img.crop((rx, ry, rx + rw, ry + rh)), max_w=648)
            text = ask(args.base_url, args.model, full_b64, roi_b64)
            verdict = parse_verdict(text)
            if verdict is None:
                raise ValueError(f"无法解析模型输出: {text[:80]!r}")
            row.update(verdict)
            ok += 1
            flag = "✅可用" if verdict["roi_usable"] else "⛔淘汰"
            gold = [k for k in ("has_enemy", "has_loot_signal", "has_prompt") if verdict[k]]
            print(f"[{i}/{len(todo)}] {flag} {verdict['scene']}｜{verdict['occlusion']}｜"
                  f"{verdict['time_weather']}｜conf {verdict['confidence']}"
                  + (f"｜金帧:{','.join(gold)}" if gold else ""))
        except Exception as ex:  # 单帧失败不中断：记 error 列，下一帧继续
            row["error"] = str(ex).replace(",", "；")[:200]
            fail += 1
            print(f"[{i}/{len(todo)}] ❌ {row['relpath']} → {row['error'][:60]}")

        if new_file and i == 1:
            with open(out, "w", newline="", encoding="utf-8") as f:
                csv.DictWriter(f, fieldnames=FIELDS).writeheader()
            new_file = False
        with open(out, "a", newline="", encoding="utf-8") as f:
            csv.DictWriter(f, fieldnames=FIELDS).writerow(row)

    # 收尾漏斗统计（覆盖 prescreen.csv 全量，含历史）
    print(f"\n🏁 本轮：成功 {ok} / 失败 {fail}")
    print_report(out)
    print(f"产出: {out}（旁挂元数据，不影响训练 manifest）")


if __name__ == "__main__":
    main()
