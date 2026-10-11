"""VLM 预标注流水线 · P0.5 两步缩放验证（ground.py v2）。

P0 实测结论（2026-10-11）：单图整帧直问的框不合格——小图标框大 5-8 倍、队友带数字图标
被误标为撤离点、conf 无区分度。根因：Qwen3-VL 对 1280×720 只有约 46×26 感知网格，
30px 图标 ≈1 格，结构性框不紧。

v2 对策（两步缩放）：
  第 1 步 · 粗定位：整帧只框"大区域"（小地图面板/大地图面板）和各类目标的宽松粗框
           （宁大勿小、目标必须完全在框内）；
  第 2 步 · 细框：把每个粗框从**原图**裁出（加 25% 边距），放大再问一次（宽≤1280、
           高≤2048、面积≤100 万 px 三约束，防窄高裁图爆上下文），
           "框必须紧贴目标外缘 + 显式排除带数字队友图标"；细框再映射回原图坐标。
坐标系换算（每层都要）：粗框 sent→orig（map_bbox）→ 裁图（crop_rect）→ 细框
  crop-sent→orig（map_box_to_orig）→ clamp → 退化框过滤。三层换算均有离线 selftest 钉住。

红线不变：只产出候选，不入库、不进运行时；产物落 ai-training/runs/p0_probe_<时间戳>/。

用法（ai-training/ 下，需 vLLM 服务在岗）:
  .venv-train/Scripts/python.exe src/ground.py --limit 40     # 同一批 40 帧（seed 42）与 P0 对比
  .venv-train/Scripts/python.exe src/ground.py --dry-run      # 不请求服务，只打印抽样名单
  .venv-train/Scripts/python.exe src/ground.py --selftest     # 离线自测（三层坐标换算+解析）
  .venv-train/Scripts/python.exe src/ground.py --report <目录>
"""

import argparse
import _console  # noqa: F401  —— 控制台 UTF-8 护栏（GBK 终端打印 emoji 会崩）
import base64
import csv
import io
import json
import random
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]          # ai-training/
REC = ROOT / "datasets" / "record"
REPO = ROOT.parent
PROMPT_V = "p0-v2"                                   # 提示词版本：改 PROMPT 必须升版本
MAX_W = 1280                                         # 送图最大宽
MAX_H = 2048                                         # 送图最大高（防超窄超高裁图）
MAX_AREA = 1_000_000                                 # 送图最大面积 px²（≈1280×720 整帧的已知安全量）
# 为什么限面积：2026-10-11 踩坑——296×462 的窄高裁图按"宽 1280"放大成 1280×1998，
# 图片 token 2764 超过服务端 max-model-len 2048，每帧 400 报错。面积锁死 1M px 后
# token 数与整帧同级（~900），永不再爆。此三约束与方案文档 §4 一致。
PAD = 0.25                                           # 裁图边距（粗框外扩比例）
MIN_BOX = 8                                          # 最终框最小边长（原图像素），防退化框

# 区域类：第 1 步找"大区域"，第 2 步在里面找撤离点图标。
REGION_CLASSES = {
    "minimap": "左上角的小地图面板（方形、含地形细节的区域）",
    "map_panel": "打开的大地图面板（占据屏幕中央大片区域的地图界面）",
}

# 目标类：第 1 步出宽松粗框，第 2 步紧贴细框。
# 撤离点图标外观词汇（2026-10-11 来源：用户提供的地图图例截图 + 本地 VLM 读回，用户口述确认）：
#   撤离点 = 绿色"奔跑人形/右上箭头"图标（图例名"撤离点"）；灰色同形状 = "未开启撤离点"，也算撤离点；
#   地图上的撤离点图标右下角可能出现小附加形状（表示撤离/载具要求），仍属撤离点图标本身；
#   排除：带数字(1/2/3)的标记 = 玩家自己与队友；白色方框内带小十字 = 降落点。
EXTRACT_LOOK = "绿色'奔跑人形/右上箭头'图标（灰色同形状的'未开启撤离点'也算撤离点；图标右下角带小附加形状时仍是撤离点）"

OBJ_CLASSES = {
    "extract": "撤离点标记：绿色'奔跑人形/右上箭头'图标（小地图/大地图面板上，灰色未开启版本也算），或游戏世界 HUD 上的撤离点导航标记",
    "prompt": "交互提示：游戏画面准星附近出现的按键提示（如 [F] 拾取、[F] 开门、搜刮）",
    "enemy": "敌方角色：游戏世界内可见的敌方人物模型（半身/背影/远景都算）",
    "corpse": "尸体/倒地：尸体袋或倒地角色",
    "loot": "物资高光：金光柱、掉落物高光、物资箱高亮",
    "container": "容器：可搜刮的箱子/柜子/补给箱/背包",
}

CLASS_COLORS = {
    "extract": (0, 200, 0), "prompt": (255, 220, 0), "enemy": (255, 60, 60),
    "corpse": (180, 60, 255), "loot": (255, 140, 0), "container": (0, 180, 255),
}


# ---------- 提示词 ----------

def pass1_prompt(sent_w: int, sent_h: int) -> str:
    obj_lines = "\n".join(f'- "{c}"：{d}' for c, d in OBJ_CLASSES.items())
    reg_lines = "\n".join(f'- "{c}"：{d}' for c, d in REGION_CLASSES.items())
    return f"""你是游戏画面目标检测标注助手。图1 是 {sent_w}×{sent_h} 像素的游戏截图。
这一步只做**粗定位**：框可以宽松（宁大勿小），但目标必须完全在框内。分两部分输出，只输出一个 JSON 对象，不要输出任何其他文字、不要用 markdown 代码块：
一、"regions"：找出图1 中可见的下列界面区域（没有就不列）：
{reg_lines}
二、"objects"：找出图1 中**游戏世界内**可见的下列目标（菜单按钮、结算/背包/大厅界面里的图标、HUD 血条与准星本身不算；没有就不列）：
{obj_lines}
输出格式：
{{"regions": [{{"cls": "minimap 或 map_panel", "bbox": [x1, y1, x2, y2]}}], "objects": [{{"cls": "类别名", "bbox": [x1, y1, x2, y2], "reason": "一句话依据"}}]}}
bbox 是**图1 上的像素整数坐标**（左上角 x1,y1；右下角 x2,y2），范围 x∈[0,{sent_w}]、y∈[0,{sent_h}]，且 x1<x2、y1<y2。同类可有多个实例，全部列出；都没有就输出 {{"regions": [], "objects": []}}。"""


def pass2_region_prompt(sent_w: int, sent_h: int) -> str:
    return f"""你是游戏画面目标检测标注助手。图1 是从原游戏截图裁出并放大的**地图区域**（{sent_w}×{sent_h} 像素）。
只找一种目标：{EXTRACT_LOOK}。
要求：
1. 框必须**紧贴图标外缘**，不要把整个地图面板或一大片背景框进去；
2. 排除两类（都不是撤离点，不要框）：带数字（1/2/3）的圆点/箭头图标是**玩家自己与队友的标记**；白色方框内带小十字的是**降落点**；
3. 同类可有多个实例，全部列出；没有就输出 {{"objects": []}}。
只输出 JSON：{{"objects": [{{"cls": "extract", "bbox": [x1, y1, x2, y2], "reason": "一句话依据"}}]}}
bbox 是**图1 上的像素整数坐标**，范围 x∈[0,{sent_w}]、y∈[0,{sent_h}]，且 x1<x2、y1<y2。"""


def pass2_obj_prompt(cls: str, sent_w: int, sent_h: int) -> str:
    return f"""你是游戏画面目标检测标注助手。图1 是从原游戏截图裁出并放大的局部区域（{sent_w}×{sent_h} 像素），
其中可能有一个"{OBJ_CLASSES[cls]}"（粗定位结果）。
请重新给出**紧贴目标外缘**的精确框：
1. 框内应只有目标本身，不要把大块背景/地面/墙壁框进去；
2. 若裁出的区域里其实**没有**该目标，输出空数组（不要硬凑一个框）；
3. 只找这一类，不要框其他类别。
只输出 JSON：{{"objects": [{{"cls": "{cls}", "bbox": [x1, y1, x2, y2], "reason": "一句话依据"}}]}}
bbox 是**图1 上的像素整数坐标**，范围 x∈[0,{sent_w}]、y∈[0,{sent_h}]，且 x1<x2、y1<y2。"""


# ---------- 图像与坐标 ----------

def to_b64(img: Image.Image, max_w: int = MAX_W, max_h: int = MAX_H,
           max_area: int = MAX_AREA) -> tuple[str, int, int]:
    """缩放编码；返回 (b64, w, h)。三约束：宽≤max_w、高≤max_h、面积≤max_area。
    小图会放大（细框关键），但放大后的面积不能超过整帧已知安全量——否则爆上下文（见 MAX_AREA 注释）。"""
    scale = min(max_w / img.width, max_h / img.height,
                (max_area / (img.width * img.height)) ** 0.5)
    if scale != 1:
        img = img.resize((round(img.width * scale), round(img.height * scale)), Image.LANCZOS)
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "JPEG", quality=90)
    return ("data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode(),
            img.width, img.height)


def ask(base_url: str, model: str, prompt: str, b64: str, timeout: int = 240) -> str:
    body = {
        "model": model,
        "messages": [{"role": "user", "content": [
            {"type": "text", "text": prompt},
            {"type": "image_url", "image_url": {"url": b64}},
        ]}],
        "temperature": 0,
        "max_tokens": 1000,
    }
    req = urllib.request.Request(
        base_url.rstrip("/") + "/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read())["choices"][0]["message"]["content"]
    except urllib.error.HTTPError as e:
        # 400/500 时把 vLLM 的错误详情带出来（如 Input length exceeds...），别只留一句 Bad Request
        try:
            detail = e.read().decode("utf-8", "ignore")[:300]
        except Exception:
            detail = ""
        raise RuntimeError(f"HTTP {e.code}: {detail}") from e


def _boxes(raw, allowed: set) -> list[dict]:
    out = []
    for it in raw:
        if not isinstance(it, dict):
            continue
        cls = str(it.get("cls", "")).strip().lower()
        if cls not in allowed:
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
        out.append({"cls": cls, "bbox": [x1, y1, x2, y2],
                    "reason": str(it.get("reason", ""))[:40]})
    return out


def parse_pass1(text: str) -> dict | None:
    try:
        start, end = text.find("{"), text.rfind("}")
        if start < 0 or end <= start:
            return None
        v = json.loads(text[start:end + 1])
        if not isinstance(v, dict):
            return None
        return {"regions": _boxes(v.get("regions", []), set(REGION_CLASSES)),
                "objects": _boxes(v.get("objects", []), set(OBJ_CLASSES))}
    except (json.JSONDecodeError, AttributeError):
        return None


def parse_pass2(text: str, allowed: set) -> list[dict] | None:
    try:
        start, end = text.find("{"), text.rfind("}")
        if start < 0 or end <= start:
            return None
        v = json.loads(text[start:end + 1])
        if isinstance(v, dict) and "objects" in v:
            return _boxes(v["objects"], allowed)
        if isinstance(v, list):
            return _boxes(v, allowed)
        if isinstance(v, dict):
            return []                                   # 空对象 = "没有目标"
        return None
    except (json.JSONDecodeError, AttributeError):
        return None


def map_bbox(box, sent_w, sent_h, orig_w, orig_h):
    """送图坐标 → 原图坐标。scale 按宽高分别算，避免舍入累积。"""
    sx, sy = orig_w / sent_w, orig_h / sent_h
    x1, y1, x2, y2 = box
    return [round(x1 * sx), round(y1 * sy), round(x2 * sx), round(y2 * sy)]


def clamp_box(box, w, h):
    x1, y1, x2, y2 = box
    x1, y1 = max(0, min(w, x1)), max(0, min(h, y1))
    x2, y2 = max(0, min(w, x2)), max(0, min(h, y2))
    return [x1, y1, x2, y2]


def crop_rect(box, w, h, pad=PAD):
    """粗框（原图坐标）→ 裁图矩形（外扩 pad 比例，不越界）。返回 (x, y, w, h)。"""
    x1, y1, x2, y2 = box
    bw, bh = x2 - x1, y2 - y1
    x1 = max(0, int(x1 - bw * pad))
    y1 = max(0, int(y1 - bh * pad))
    x2 = min(w, int(x2 + bw * pad))
    y2 = min(h, int(y2 + bh * pad))
    return x1, y1, x2 - x1, y2 - y1


def map_box_to_orig(box, sent_w, sent_h, crop):
    """细框（裁图放大后的送图坐标）→ 原图坐标。crop=(cx, cy, cw, ch) 为原图上的裁图矩形。"""
    cx, cy, cw, ch = crop
    x1, y1, x2, y2 = box
    sx, sy = cw / sent_w, ch / sent_h
    return [round(cx + x1 * sx), round(cy + y1 * sy), round(cx + x2 * sx), round(cy + y2 * sy)]


def draw_preview(img: Image.Image, boxes: list[dict], crops: list[tuple]) -> Image.Image:
    canvas = img.copy()
    d = ImageDraw.Draw(canvas)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 22)
    except OSError:
        font = ImageFont.load_default()
    for c in crops:                                    # 裁图区域：细白框（调试用）
        x, y, w, h = c
        d.rectangle([x, y, x + w, y + h], outline=(255, 255, 255), width=2)
    for o in boxes:
        x1, y1, x2, y2 = o["bbox"]
        color = CLASS_COLORS.get(o["cls"], (255, 255, 255))
        d.rectangle([x1, y1, x2, y2], outline=color, width=4)
        label = o["cls"]
        d.rectangle([x1, max(0, y1 - 26), x1 + 14 * len(label), y1], fill=color)
        d.text((x1 + 2, max(0, y1 - 24)), label, fill=(0, 0, 0), font=font)
    return canvas


def _to_jpeg_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "JPEG", quality=88)
    return buf.getvalue()


# ---------- 抽样与数据 ----------

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
            return 0
        if s.get("has_prompt") == "True":
            return 1
        if s.get("roi_usable") == "True":
            return 2
        return 3
    if do_random:
        rng = random.Random(seed)
        rng.shuffle(manifest)
        return manifest[:limit]
    ranked = sorted(manifest, key=lambda r: (gold_rank(r), r["relpath"]))
    rng = random.Random(seed)
    by_rank: dict[int, list] = {}
    for r in ranked:
        by_rank.setdefault(gold_rank(r), []).append(r)
    out = []
    for k in sorted(by_rank):
        rng.shuffle(by_rank[k])
        out.extend(by_rank[k])
    return out[:limit]


# ---------- 主流程 ----------

def refine(base_url, model, img, orig_w, orig_h, box, cls, stats) -> list[dict]:
    """对粗框裁图放大 → 细框 → 映射回原图。返回原图坐标的最终框列表。"""
    crop = crop_rect(box, orig_w, orig_h)
    sub = img.crop((crop[0], crop[1], crop[0] + crop[2], crop[1] + crop[3]))
    b64, sw2, sh2 = to_b64(sub, MAX_W)
    prompt = pass2_region_prompt(sw2, sh2) if cls == "region" else pass2_obj_prompt(cls, sw2, sh2)
    text = ask(base_url, model, prompt, b64)
    parsed = parse_pass2(text, {"extract"} if cls == "region" else {cls})
    if parsed is None:
        raise ValueError(f"无法解析细框输出: {text[:60]!r}")
    out = []
    for o in parsed:
        box2 = clamp_box(map_box_to_orig(o["bbox"], sw2, sh2, crop), orig_w, orig_h)
        if box2[2] - box2[0] < MIN_BOX or box2[3] - box2[1] < MIN_BOX:
            continue                                     # 退化框过滤
        out.append({"cls": o["cls"], "bbox": box2, "reason": o["reason"], "crop": crop})
    return out


def run_probe(frames, base_url, model, out_dir: Path) -> dict:
    preview_dir = out_dir / "preview"
    preview_dir.mkdir(parents=True, exist_ok=True)
    cand_path = out_dir / "candidates.jsonl"
    stats = {"images": 0, "boxes": 0, "coarse": 0, "refine_miss": 0, "errors": 0,
             "per_class": {}, "timing_ms": []}
    for i, r in enumerate(frames, 1):
        rel = r["relpath"]
        stem = Path(rel).stem
        t0 = datetime.now()
        try:
            img = Image.open(REC / rel).convert("RGB")
            orig_w, orig_h = img.size
            b64, sw, sh = to_b64(img, MAX_W)
            p1 = parse_pass1(ask(base_url, model, pass1_prompt(sw, sh), b64))
            if p1 is None:
                raise ValueError("无法解析粗定位输出")
            regions = [clamp_box(map_bbox(x["bbox"], sw, sh, orig_w, orig_h), orig_w, orig_h)
                       for x in p1["regions"]][:2]
            objects = [clamp_box(map_bbox(x["bbox"], sw, sh, orig_w, orig_h), orig_w, orig_h)
                       for x in p1["objects"]][:8]
            stats["coarse"] += len(regions) + len(objects)
            finals, crops = [], []
            for box in regions:                          # 区域 → 找撤离点图标
                for o in refine(base_url, model, img, orig_w, orig_h, box, "region", stats):
                    finals.append(o)
            for box, obj in zip(objects, p1["objects"][:8]):
                got = refine(base_url, model, img, orig_w, orig_h, box, obj["cls"], stats)
                if not got:
                    stats["refine_miss"] += 1
                finals.extend(got)
            crops = [o["crop"] for o in finals]
            for o in finals:
                o.pop("crop", None)
                stats["boxes"] += 1
                stats["per_class"][o["cls"]] = stats["per_class"].get(o["cls"], 0) + 1
            if finals:
                preview_dir.joinpath(stem + ".jpg").write_bytes(
                    _to_jpeg_bytes(draw_preview(img, finals, crops)))
            with open(cand_path, "a", newline="", encoding="utf-8") as f:
                for k, o in enumerate(finals):
                    f.write(json.dumps({
                        "id": f"{stem}_{k}", "image": rel, "cls": o["cls"],
                        "bbox": o["bbox"], "conf": None, "reason": o["reason"],
                        "prompt_v": PROMPT_V, "model": model,
                    }, ensure_ascii=False) + "\n")
            stats["images"] += 1
            stats["timing_ms"].append((datetime.now() - t0).total_seconds() * 1000)
            print(f"[{i}/{len(frames)}] {rel} → 细框 {len(finals)}"
                  f"（粗 {len(regions)}+{len(objects)}）｜{stats['per_class']}")
        except Exception as ex:
            stats["errors"] += 1
            print(f"[{i}/{len(frames)}] ❌ {rel} → {str(ex)[:70]}")
    return stats


def print_stats(stats: dict, out_dir: Path):
    print("\n🏁 两步缩放试跑统计")
    print(f"帧数 {stats['images']} | 细框 {stats['boxes']} | 粗框 {stats['coarse']}"
          f" | 细框落空 {stats['refine_miss']} | 失败 {stats['errors']}")
    if stats["boxes"]:
        print(f"每类细框数: {stats['per_class']}")
    if stats["timing_ms"]:
        ts = stats["timing_ms"]
        print(f"单帧耗时(s) 均值 {sum(ts)/len(ts)/1000:.0f} | 最大 {max(ts)/1000:.0f}")
    print(f"产物: {out_dir}")


def report_mode(path: str):
    cand = Path(path) / "candidates.jsonl"
    if not cand.exists():
        print(f"没找到 {cand}")
        return
    stats = {"images": 0, "boxes": 0, "coarse": 0, "refine_miss": 0, "errors": 0,
             "per_class": {}, "timing_ms": []}
    seen = set()
    with open(cand, encoding="utf-8") as f:
        for line in f:
            o = json.loads(line)
            seen.add(o["image"])
            stats["boxes"] += 1
            stats["per_class"][o["cls"]] = stats["per_class"].get(o["cls"], 0) + 1
    stats["images"] = len(seen)
    print_stats(stats, Path(path))


def selftest():
    """离线自测：三层坐标换算 + 解析容错。不需要服务。"""
    # 1) 粗框 sent→orig
    box = map_bbox([100, 50, 200, 100], sent_w=1280, sent_h=720, orig_w=1920, orig_h=1080)
    assert box == [150, 75, 300, 150], f"map_bbox 错误: {box}"
    assert clamp_box([-10, -10, 2000, 1200], 1920, 1080) == [0, 0, 1920, 1080]
    # 2) 裁图矩形
    assert crop_rect([150, 75, 300, 150], 1920, 1080) == (112, 56, 225, 112)
    # 3) 细框 crop-sent→orig
    crop = (112, 56, 225, 112)
    back = map_box_to_orig([0, 0, 1280, 640], sent_w=1280, sent_h=640, crop=crop)
    assert back == [112, 56, 337, 168], f"map_box_to_orig 错误: {back}"
    # 往返一致性：任意点绕一圈回到原图同一点
    cx, cy = 500, 400
    sx = (cx - crop[0]) / crop[2] * 1280
    sy = (cy - crop[1]) / crop[3] * 640
    assert map_box_to_orig([sx, sy, sx, sy], 1280, 640, crop) == [500, 400, 500, 400]
    # 4) 解析容错
    p1 = parse_pass1('```json\n{"regions":[{"cls":"minimap","bbox":[1,2,50,50]}],"objects":[{"cls":"enemy","bbox":[3,4,60,60],"reason":"人"}]}\n```')
    assert p1 is not None and len(p1["regions"]) == 1 and p1["objects"][0]["cls"] == "enemy", p1
    assert parse_pass1("{}") == {"regions": [], "objects": []}
    assert parse_pass1("废话") is None
    assert parse_pass2('{"objects": []}', {"extract"}) == []
    assert parse_pass2('{"objects": [{"cls":"extract","bbox":[5,4,3,2]}]}', {"extract"}) == []
    bad_cls = parse_pass2('{"objects": [{"cls":"enemy","bbox":[1,2,3,4]}]}', {"extract"})
    assert bad_cls == [], bad_cls
    # 5) 缩放三约束（宽/高/面积）：窄高裁图不再爆上下文（2026-10-11 踩坑回归测试）
    tall = Image.new("RGB", (296, 462))
    _, w5, h5 = to_b64(tall)
    assert w5 <= MAX_W and h5 <= MAX_H and w5 * h5 <= MAX_AREA * 1.02, (w5, h5)
    assert abs(w5 / h5 - 296 / 462) < 0.02, (w5, h5)
    full = Image.new("RGB", (1920, 1080))
    _, w6, h6 = to_b64(full)
    assert (w6, h6) == (1280, 720), (w6, h6)
    print("✅ selftest 通过：三层坐标换算 + 解析容错 + 缩放三约束")


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
        report_mode(args.report)
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
    print(f"开始两步缩放试跑：服务 {args.base_url} | 模型 {args.model} | prompt_v={PROMPT_V}")
    print(f"区域类: {', '.join(REGION_CLASSES)}")
    print(f"目标类: {', '.join(OBJ_CLASSES)}")
    stats = run_probe(frames, args.base_url, args.model, out_dir)
    print_stats(stats, out_dir)


if __name__ == "__main__":
    main()
