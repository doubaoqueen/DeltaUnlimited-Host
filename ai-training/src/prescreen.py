"""素材初筛质检员：把 record/full 的帧发给本地 Qwen3-VL（vLLM 服务），返回结构化 JSON 元数据，
旁挂写入 record/prescreen.csv。训练 manifest 与正式标签完全不受影响。
红线：VLM 只做分流与元数据（scene / roi_usable / 遮挡 / 金帧标记），它的判断不是训练标签；
正式标签永远由人工在 ailabel 中打。

用法（ai-training/ 下，需先启动 vLLM 服务，见 docs/AI初筛操作手册.md）:
  python src/prescreen.py --limit 5     # 试跑 5 张未筛帧
  python src/prescreen.py               # 处理全部未筛帧
  python src/prescreen.py --refresh     # 忽略已有结果重筛（prompt 改版/换模型后用）
默认增量：已成功判定的帧跳过；判定失败（error 非空）的帧下次运行自动重试，无需手动补筛。
"""

import argparse
import _console  # noqa: F401  —— 控制台 UTF-8 护栏（GBK 终端打印 emoji 会崩）
import base64
import csv
import io
import json
import random
import shutil
import urllib.error
import urllib.request
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]              # ai-training/
REC = ROOT / "datasets" / "record"
REPO = ROOT.parent
PROMPT_V = "v4"                                          # 提示词版本：改 PROMPT 必须升版本并 --refresh 重筛
FIELDS = ["relpath", "scene", "roi_usable", "occlusion", "time_weather",
          "has_enemy", "has_loot_signal", "has_prompt", "quality", "confidence", "error",
          "label_hint", "prompt_v"]

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
- label_hint：仅看图2，你认为这帧**应该被打成哪个训练标签**（只是给人工的建议，最终由人工定）：
  passable=图2 有明显可通行的地面/通路；blocked=前方被挡（墙壁/箱子/栅栏/贴脸怼墙）；
  no_ground=无法判断地面（天空/面板遮挡/一团模糊）；若 roi_usable=false 且看不出类别，给 no_ground。
只输出 JSON：{"scene": "...", "roi_usable": true或false, "occlusion": "无|UI覆盖|贴脸遮挡|天空|大面积特效", "time_weather": "白天|黄昏|夜战|雾天|未知", "has_enemy": true或false, "has_loot_signal": true或false, "has_prompt": true或false, "quality": "清晰|模糊|黑屏|过曝", "confidence": 0.0到1.0的小数, "label_hint": "passable|blocked|no_ground"}"""


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
        hint = str(v.get("label_hint", "")).strip().lower()
        if hint not in ("passable", "blocked", "no_ground"):
            hint = "无法判断"
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
            "label_hint": hint,
            "prompt_v": PROMPT_V,
        }
    except (json.JSONDecodeError, AttributeError):
        return None


def migrate_header(out: Path) -> None:
    """老版 CSV 缺新列（label_hint/prompt_v）时补齐表头（旧行新列留空），并留 .bak 备份。
    不补齐的话 DictWriter 与旧表头会错位——比缺少字段更危险。"""
    if not out.exists():
        return
    with open(out, newline="", encoding="utf-8") as f:
        rows = list(csv.reader(f))
    if not rows or rows[0] == FIELDS:
        return
    bak = out.with_name(out.name + ".bak")
    shutil.copy2(out, bak)
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(FIELDS)
        for r in rows[1:]:
            if not r or not r[0].strip():
                continue
            w.writerow((r + [""] * len(FIELDS))[:len(FIELDS)])
    print(f"ℹ prescreen.csv 表头已升级到 {len(FIELDS)} 列（旧行 label_hint/prompt_v 留空，需 --refresh 重筛补）｜备份: {bak.name}")


def load_screened(path: Path) -> dict:
    screened = {}
    if path.exists():
        with open(path, newline="", encoding="utf-8") as f:
            for row in csv.DictReader(f):
                screened[row["relpath"]] = row
    return screened


def _judged(row: dict | None) -> bool:
    """该帧是否已有成功判定。error 非空的失败行不算数——下次运行自动重试；
    重试成功后新行在 CSV 里排在旧行之后，读入按 relpath 后行覆盖前行，报告不会重复计数。"""
    return row is not None and not row.get("error")


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

    hints: dict = {}
    for r in all_rows.values():
        h = (r.get("label_hint") or "").strip() or "(旧版无此列)"
        hints[h] = hints.get(h, 0) + 1
    print(f"VLM 建议标签分布: {hints}（参考用，非训练标签；空=(旧版无此列)，需 --refresh 重筛补齐）")
    versions: dict = {}
    for r in all_rows.values():
        v = (r.get("prompt_v") or "").strip() or "(旧版)"
        versions[v] = versions.get(v, 0) + 1
    print(f"提示词版本分布: {versions}（改 PROMPT 后旧结果不可比，需 --refresh 全量重筛）")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="最多处理 N 张（0=全部未筛帧）")
    ap.add_argument("--refresh", action="store_true", help="忽略已有结果，全部重筛")
    ap.add_argument("--report", action="store_true", help="只打印当前漏斗统计，不筛新帧")
    ap.add_argument("--base-url", default="http://localhost:8000/v1")
    ap.add_argument("--model", default="qwen3vl-8b")
    args = ap.parse_args()

    out = REC / "prescreen.csv"
    migrate_header(out)
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
            and (args.refresh or not _judged(screened.get(r["relpath"])))]
    if args.limit > 0:
        todo = todo[:args.limit]
    if not todo:
        done = sum(1 for v in screened.values() if _judged(v))
        print(f"没有待筛帧（清单 {len(rows)} 行，成功判定 {done} 帧）。--refresh 可重筛。")
        return
    done = sum(1 for v in screened.values() if _judged(v))
    print(f"质检员开始工作：待筛 {len(todo)} 帧（历史成功判定 {done}）| 服务 {args.base_url} | 模型 {args.model}")

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
               "quality": "", "confidence": "", "error": "", "label_hint": "", "prompt_v": ""}
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
            print(f"[{i}/{len(todo)}] {flag} 建议={verdict['label_hint']} {verdict['scene']}｜{verdict['occlusion']}｜"
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
