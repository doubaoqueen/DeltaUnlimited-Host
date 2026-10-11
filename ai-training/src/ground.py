"""VLM 预标注流水线 · P0 能力试跑（ground.py 最小版）。

把 record/full 的整帧发给本地 Qwen3-VL（vLLM 服务，单图入参），让它对固定类别
做"目标检测 + 出框"，并把框画回原图存预览，另写 candidates.jsonl 供统计/肉眼审。

这是"能力试跑"：目的不是入库，而是回答一个前置问题——"VLM 在这批真实素材上
能不能框得住、漏不漏、值不值得为它做 P1/P2 的正式流水线"。因此本脚本**不写任何
训练标签、不写 templates、不进运行时**，产物全部落在 ai-training/runs/p0_probe_<时间戳>/。

红线：VLM 只产出候选，入库必须经过（未来的）图形审批；此处连审批都没有，纯观察。

用法（ai-training/ 下，需先启动 vLLM 服务，见 docs/AI初筛操作手册.md §1）:
  .venv-train/Scripts/python.exe src/ground.py --limit 10          # 试跑 10 张（默认金帧优先）
  .venv-train/Scripts/python.exe src/ground.py --limit 40          # 跑 40 张
  .venv-train/Scripts/python.exe src/ground.py --random            # 不按金帧优先，全量随机抽
  .venv-train/Scripts/python.exe src/ground.py --dry-run           # 不请求服务，只打印抽样名单
  .venv-train/Scripts/python.exe src/ground.py --selftest          # 离线自测（坐标换算 + 解析）
  .venv-train/Scripts/python.exe src/ground.py --report <目录>     # 重打某次产物的统计
"""

import argparse
import _console  # noqa: F401  —— 控制台 UTF-8 护栏（GBK 终端打印 emoji 会崩）
import base64
import csv
import io
import json
import random
import urllib.request
from datetime import datetime
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]          # ai-training/
REC = ROOT / "datasets" / "record"
REPO = ROOT.parent
PROMPT_V = "p0-v1"                                   # 提示词版本：改 PROMPT 必须升版本
MAX_W = 1280                                         # 送模型前缩到 ≤1280 宽（省显存，与 prescreen 一致）

# 类别表：cls → 中文描述。这是"搜/打"相关识别的最初候选集，先跑一遍看模型行不行。
# 说明：描述显式排除菜单/结算/背包里的图标（沿用 prescreen v2 的教训，防止把 UI 图标当成游戏内目标）。
CLASSES = {
    "extract": "撤离点标记：小地图/罗盘上的撤离点导航标记，或打开地图时地图面板上的撤离点图标（含'撤离点/绿色撤离'字样）",
    "prompt": "交互提示：游戏画面准星附近出现的按键提示（如 [F] 拾取、[F] 开门、搜刮）",
    "enemy": "敌方角色：游戏世界内可见的敌方人物模型（半身/背影/远景都算）",
    "corpse": "尸体/倒地：尸体袋或倒地角色",
    "loot": "物资高光：金光柱、掉落物高光、物资箱高亮",
    "container": "容器：可搜刮的箱子/柜子/补给箱/背包",
}

# 每类预览框的颜色（BGR→RGB 直接用），稳定可读
CLASS_COLORS = {
    "extract": (0, 200, 0), "prompt": (255, 220, 0), "enemy": (255, 60, 60),
    "corpse": (180, 60, 255), "loot": (255, 140, 0), "container": (0, 180, 255),
}


def _prompt(sent_w: int, sent_h: int) -> str:
    cls_lines = "\n".join(f'- "{c}"：{d}' for c, d in CLASSES.items())
    return f"""你是游戏画面目标检测标注助手。图1 是 {sent_w}×{sent_h} 像素的游戏截图。
找出图1 中**游戏世界内**可见的下列类别目标，只输出一个 JSON 对象，不要输出任何其他文字、不要用 markdown 代码块：
{cls_lines}
排除：菜单按钮、结算/背包/大厅界面里的图标、头像、HUD 血条与准星本身。
输出格式：
{{"objects": [{{"cls": "类别名", "bbox": [x1, y1, x2, y2], "conf": 0.0到1.0的小数, "reason": "一句话依据"}}]}}
bbox 是**图1 上的像素整数坐标**（左上角 x1,y1；右下角 x2,y2），范围 x∈[0,{sent_w}]、y∈[0,{sent_h}]，且 x1<x2、y1<y2。
同一类可有多个实例，全部列出；没有就输出 {{"objects": []}}。
conf 是对"这个框确实属于该类"的把握；reason 用中文，10 字内。"""


def load_cfg():
    return json.loads((REPO / "data" / "ai_vision.json").read_text(encoding="utf-8"))


def to_b64(img: Image.Image, max_w: int) -> tuple[str, int, int]:
    if img.width > max_w:
        img = img.resize((max_w, round(img.height * max_w / img.width)))
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "JPEG", quality=85)
    return ("data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode(),
            img.width, img.height)


def ask(base_url: str, model: str, b64: str, sent_w: int, sent_h: int, timeout: int = 240) -> str:
    body = {
        "model": model,
        "messages": [{"role": "user", "content": [
            {"type": "text", "text": _prompt(sent_w, sent_h)},
            {"type": "image_url", "image_url": {"url": b64}},
        ]}],
        "temperature": 0,
        "max_tokens": 900,
    }
    req = urllib.request.Request(
        base_url.rstrip("/") + "/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read())["choices"][0]["message"]["content"]


def parse_objects(text: str) -> list[dict] | None:
    """从模型输出里抠出 objects 数组（容忍 markdown 围栏/前后废话），并做类型矫正。"""
    try:
        start, end = text.find("{"), text.rfind("}")
        if start < 0 or end <= start:
            return None
        v = json.loads(text[start:end + 1])
        if isinstance(v, list):
            raw = v
        elif isinstance(v, dict) and "objects" in v:
            raw = v["objects"]
        elif isinstance(v, dict):
            return []          # 空对象 {} = 模型说"没有目标"，不算解析失败
        else:
            return None
        if not isinstance(raw, list):
            return None
        out = []
        for it in raw:
            if not isinstance(it, dict):
                continue
            cls = str(it.get("cls", "")).strip().lower()
            if cls not in CLASSES:
                continue
            bbox = it.get("bbox")
            if not isinstance(bbox, (list, tuple)) or len(bbox) != 4:
                continue
            try:
                x1, y1, x2, y2 = (float(t) for t in bbox)
            except (TypeError, ValueError):
                continue
            if x1 >= x2 or y1 >= y2:
                continue
            try:
                conf = min(1.0, max(0.0, float(it.get("conf", 0))))
            except (TypeError, ValueError):
                conf = 0.0
            out.append({"cls": cls, "bbox": [x1, y1, x2, y2], "conf": conf,
                        "reason": str(it.get("reason", ""))[:40]})
        return out
    except (json.JSONDecodeError, AttributeError):
        return None


def map_bbox(box, sent_w, sent_h, orig_w, orig_h):
    """把"送图坐标系"的框还原到原图坐标系。scale 分别按宽高算，避免舍入累积。
    —— 方案 §4 的红线：不写这一步会系统性偏 (orig_w/sent_w) 倍。"""
    sx, sy = orig_w / sent_w, orig_h / sent_h
    x1, y1, x2, y2 = box
    return [round(x1 * sx), round(y1 * sy), round(x2 * sx), round(y2 * sy)]


def clamp_box(box, w, h):
    x1, y1, x2, y2 = box
    x1, y1 = max(0, min(w, x1)), max(0, min(h, y1))
    x2, y2 = max(0, min(w, x2)), max(0, min(h, y2))
    return [x1, y1, x2, y2]


def draw_preview(img: Image.Image, boxes: list[dict]) -> Image.Image:
    """在原图上画框 + 类别 + conf，返回新图（不改原图）。"""
    canvas = img.copy()
    d = ImageDraw.Draw(canvas)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 22)
    except OSError:
        font = ImageFont.load_default()
    for o in boxes:
        x1, y1, x2, y2 = o["bbox"]
        color = CLASS_COLORS.get(o["cls"], (255, 255, 255))
        d.rectangle([x1, y1, x2, y2], outline=color, width=4)
        label = f"{o['cls']} {o['conf']:.2f}"
        d.rectangle([x1, max(0, y1 - 26), x1 + 14 * len(label), y1], fill=color)
        d.text((x1 + 2, max(0, y1 - 24)), label, fill=(0, 0, 0), font=font)
    return canvas


def load_screened() -> dict:
    p = REC / "prescreen.csv"
    screened = {}
    if p.exists():
        with open(p, newline="", encoding="utf-8") as f:
            for row in csv.DictReader(f):
                screened[row["relpath"]] = row
    return screened


def load_manifest() -> list[dict]:
    with open(REC / "record_manifest.csv", newline="", encoding="utf-8") as f:
        return [r for r in csv.DictReader(f) if (REC / r["relpath"]).exists()]


def sample_frames(manifest, screened, limit, do_random, seed):
    def gold_rank(r):
        s = screened.get(r["relpath"], {})
        if s.get("has_enemy") == "True" or s.get("has_loot_signal") == "True":
            return 0          # 敌人/物资——目标检测最该测的
        if s.get("has_prompt") == "True":
            return 1          # 交互提示（注意：初筛该项历史上偏过敏）
        if s.get("roi_usable") == "True":
            return 2
        return 3
    if do_random:
        rng = random.Random(seed)
        rng.shuffle(manifest)
        return manifest[:limit]
    ranked = sorted(manifest, key=lambda r: (gold_rank(r), r["relpath"]))
    rng = random.Random(seed)
    # 档内洗牌，保持"金帧优先"的档位次序
    by_rank: dict[int, list] = {}
    for r in ranked:
        by_rank.setdefault(gold_rank(r), []).append(r)
    out = []
    for k in sorted(by_rank):
        rng.shuffle(by_rank[k])
        out.extend(by_rank[k])
    return out[:limit]


def run_probe(frames, base_url, model, out_dir: Path) -> dict:
    preview_dir = out_dir / "preview"
    preview_dir.mkdir(parents=True, exist_ok=True)
    cand_path = out_dir / "candidates.jsonl"
    stats = {"images": 0, "boxes": 0, "errors": 0, "per_class": {},
             "confs": [], "timing_ms": []}
    for i, r in enumerate(frames, 1):
        rel = r["relpath"]
        stem = Path(rel).stem
        t0 = datetime.now()
        try:
            img = Image.open(REC / rel).convert("RGB")
            b64, sent_w, sent_h = to_b64(img, MAX_W)
            text = ask(base_url, model, b64, sent_w, sent_h)
            objs = parse_objects(text)
            if objs is None:
                raise ValueError(f"无法解析模型输出: {text[:80]!r}")
            orig_w, orig_h = img.size
            mapped = []
            for o in objs:
                box = clamp_box(map_bbox(o["bbox"], sent_w, sent_h, orig_w, orig_h), orig_w, orig_h)
                o["bbox"] = box
                mapped.append(o)
                stats["boxes"] += 1
                stats["per_class"][o["cls"]] = stats["per_class"].get(o["cls"], 0) + 1
                stats["confs"].append(o["conf"])
            if mapped:
                preview_dir.joinpath(stem + ".jpg").write_bytes(
                    _to_jpeg_bytes(draw_preview(img, mapped)))
            with open(cand_path, "a", newline="", encoding="utf-8") as f:
                for k, o in enumerate(mapped):
                    f.write(json.dumps({
                        "id": f"{stem}_{k}", "image": rel, "cls": o["cls"],
                        "bbox": o["bbox"], "conf": round(o["conf"], 3),
                        "reason": o["reason"], "prompt_v": PROMPT_V, "model": model,
                    }, ensure_ascii=False) + "\n")
            stats["images"] += 1
            stats["timing_ms"].append((datetime.now() - t0).total_seconds() * 1000)
            print(f"[{i}/{len(frames)}] {rel} → {len(mapped)} 框"
                  + (f"｜{stats['per_class']}" if mapped else "｜空"))
        except Exception as ex:
            stats["errors"] += 1
            print(f"[{i}/{len(frames)}] ❌ {rel} → {str(ex)[:70]}")
    return stats


def _to_jpeg_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "JPEG", quality=88)
    return buf.getvalue()


def print_stats(stats: dict, out_dir: Path):
    print("\n🏁 试跑统计")
    print(f"帧数 {stats['images']} | 出框 {stats['boxes']} | 失败 {stats['errors']}")
    if stats["boxes"]:
        print(f"每类框数: {stats['per_class']}")
        cs = stats["confs"]
        print(f"conf 均值 {sum(cs)/len(cs):.2f} | 最小 {min(cs):.2f} | 最大 {max(cs):.2f}")
    if stats["timing_ms"]:
        ts = stats["timing_ms"]
        print(f"单帧耗时(ms) 均值 {sum(ts)/len(ts):.0f} | 最大 {max(ts):.0f}")
    print(f"产物: {out_dir}")


def selftest():
    """离线自测：坐标换算 + 解析容错。不需要服务、不需要网络。"""
    box = map_bbox([100, 50, 200, 100], sent_w=1280, sent_h=720, orig_w=1920, orig_h=1080)
    assert box == [150, 75, 300, 150], f"map_bbox 错误: {box}"
    assert clamp_box([-10, -10, 2000, 1200], 1920, 1080) == [0, 0, 1920, 1080]
    parsed = parse_objects("```json\n{\"objects\": [{\"cls\":\"enemy\",\"bbox\":[1,2,3,4],\"conf\":0.9,\"reason\":\"人\"}]}\n```")
    assert parsed and parsed[0]["cls"] == "enemy" and parsed[0]["bbox"] == [1, 2, 3, 4], parsed
    assert parse_objects("{}") == []          # 空对象 = 无目标
    assert parse_objects('{"objects": []}') == []
    assert parse_objects("完全是废话") is None
    bad = parse_objects('{"objects": [{"cls":"enemy","bbox":[5,4,3,2]}]}')  # x1>=x2 丢弃
    assert bad == [], bad
    print("✅ selftest 通过：坐标换算 + 解析容错")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=40, help="最多跑 N 张（默认 40）")
    ap.add_argument("--random", action="store_true", help="不按金帧优先，全量随机抽")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--dry-run", action="store_true", help="不请求服务，只打印抽样名单")
    ap.add_argument("--selftest", action="store_true", help="离线自测（坐标换算+解析）后退出")
    ap.add_argument("--report", type=str, default="", help="重打指定产物目录的统计")
    ap.add_argument("--base-url", default="http://localhost:8000/v1")
    ap.add_argument("--model", default="qwen3vl-8b")
    args = ap.parse_args()

    if args.selftest:
        selftest()
        return
    if args.report:
        cand = Path(args.report) / "candidates.jsonl"
        if not cand.exists():
            print(f"没找到 {cand}")
            return
        stats = {"images": 0, "boxes": 0, "errors": 0, "per_class": {}, "confs": [], "timing_ms": []}
        seen = set()
        with open(cand, encoding="utf-8") as f:
            for line in f:
                o = json.loads(line)
                seen.add(o["image"])
                stats["boxes"] += 1
                stats["per_class"][o["cls"]] = stats["per_class"].get(o["cls"], 0) + 1
                stats["confs"].append(o["conf"])
        stats["images"] = len(seen)
        print_stats(stats, Path(args.report))
        return

    screened = load_screened()
    manifest = load_manifest()
    frames = sample_frames(manifest, screened, args.limit, args.random, args.seed)
    if not frames:
        print("没有可跑的帧（清单为空？）。")
        return
    print(f"抽样 {len(frames)} 帧（{'随机' if args.random else '金帧优先'}，seed={args.seed}）")
    for r in frames:
        s = screened.get(r["relpath"], {})
        tags = [k for k in ("has_enemy", "has_loot_signal", "has_prompt") if s.get(k) == "True"]
        print(f"  - {r['relpath']}" + (f"  [初筛金帧:{','.join(tags)}]" if tags else ""))
    if args.dry_run:
        print("dry-run：以上为抽样名单，未请求服务。去掉 --dry-run 正式跑。")
        return

    out_dir = ROOT / "runs" / f"p0_probe_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
    out_dir.mkdir(parents=True, exist_ok=True)
    print(f"开始试跑：服务 {args.base_url} | 模型 {args.model} | prompt_v={PROMPT_V}")
    print(f"类别: {', '.join(CLASSES)}")
    stats = run_probe(frames, args.base_url, args.model, out_dir)
    print_stats(stats, out_dir)


if __name__ == "__main__":
    main()
