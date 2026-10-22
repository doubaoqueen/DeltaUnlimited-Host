"""生成知识库图片资源（临时）：复制关键截图（缩小） + 画项目总览示意图。"""
import _console  # noqa: F401
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

REPO = Path(r"D:\MeineArbeit\DeltaUnlimited")
SRC = REPO / "screenshots" / "mapoverview"
OUT = REPO / "vault" / "assets"
OUT.mkdir(parents=True, exist_ok=True)


def shrink(src: Path, dst_name: str, width: int):
    if not src.exists():
        print("缺:", src)
        return
    im = Image.open(src).convert("RGB")
    h = round(im.height * width / im.width)
    im.resize((width, h), Image.LANCZOS).save(OUT / dst_name, optimize=True)
    print("saved", OUT / dst_name, (width, h))


# 地图与标定图
shrink(SRC / "zero_dam_tiles_z4.png", "map_zero_dam.png", 1600)
shrink(SRC / "marks_verify.png", "map_points.png", 1600)
track = sorted(SRC.glob("track_visual.png"))
if track:
    shrink(track[0], "track_sample.png", 1400)
else:
    tracks = sorted(SRC.glob("track_localizetrack_*.png"))
    if tracks:
        shrink(tracks[0], "track_sample.png", 1400)

# 一张局内 HUD 帧（展示小地图位置）
frame = REPO / "ai-training" / "datasets" / "record" / "full" / "full_20261012_003010_816.jpg"
shrink(frame, "hud_frame.png", 1280)

# ---------- 项目总览示意图 ----------
W, H = 1600, 1000
img = Image.new("RGB", (W, H), (24, 26, 30))
d = ImageDraw.Draw(img)
try:
    f_big = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 40)
    f_mid = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 30)
    f_small = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 24)
except OSError:
    f_big = f_mid = f_small = ImageFont.load_default()


def box(x, y, w, h, title, lines, color):
    d.rounded_rectangle([x, y, x + w, y + h], radius=14, outline=color, width=3, fill=(34, 37, 43))
    d.text((x + 18, y + 12), title, fill=color, font=f_mid)
    for i, ln in enumerate(lines):
        d.text((x + 18, y + 58 + i * 30), ln, fill=(210, 214, 220), font=f_small)


def arrow(x1, y1, x2, y2, color=(120, 160, 220)):
    d.line([x1, y1, x2, y2], fill=color, width=3)
    import math
    ang = math.atan2(y2 - y1, x2 - x1)
    L = 16
    for s in (+0.5, -0.5):
        d.line([x2, y2, x2 - L * math.cos(ang + s), y2 - L * math.sin(ang + s)], fill=color, width=3)


d.text((40, 28), "DeltaUnlimited 项目全景（示意）", fill=(255, 255, 255), font=f_big)
d.text((44, 84), "看得见的自动化：截图 + 键鼠，不读内存、不注入进程、不碰战斗对抗", fill=(150, 200, 255), font=f_small)

box(60, 150, 420, 210, "① 感知（看见什么）", [
    "截图：CaptureService",
    "识别：OCR / 模板 / 坐标兜底",
    "视觉：小地图定位器",
    "AI：可通行性（观察期）",
], (90, 200, 140))
box(560, 150, 420, 210, "② 决策（该做什么）", [
    "chain 引擎：界面流程 2Hz",
    "伺服循环：走位 8Hz",
    "规则：遇险即停给人",
    "经济：捡/丢/撤判断",
], (240, 190, 90))
box(1060, 150, 480, 210, "③ 执行（动手）", [
    "拟人化键鼠：贝塞尔轨迹",
    "点击安全网：落点校验",
    "急停/暂停：随时可打断",
    "会话限额：每日 4 小时",
], (200, 120, 200))

box(60, 440, 420, 200, "④ 数据表（可改的规则）", [
    "screens.json：界面识别",
    "elements.json：元素定位",
    "runtime.json：标定参数",
    "zero_dam_points.json：点位",
], (120, 200, 220))
box(560, 440, 420, 200, "⑤ 工具链（离线）", [
    "airecord：录制素材",
    "ailabel / aiground：标注",
    "localize：定位诊断",
    "track_plot：轨迹可视化",
], (220, 220, 120))
box(1060, 440, 480, 200, "⑥ 训练（离线）", [
    "prescreen：VLM 初筛",
    "train.py：训练小模型",
    "export_onnx + 门禁评估",
    "结果：observe-only 上线",
], (120, 200, 220))

box(60, 720, 1480, 200, "⑦ 红线与约束（任何功能都不能越过的线）", [
    "不读内存、不注入进程、不改游戏文件；只做屏幕与鼠标键盘层面的自动化，效果等于一个“看得见的助手”。",
    "不包含任何战斗/击杀/瞄准辅助；对局内遇到危险一律暂停交人；低频、拟人、人在场，风险自担。",
    "数据不出机器：AI 训练与推理全在本机，不上云。",
], (230, 90, 90))

arrow(480, 255, 560, 255)
arrow(980, 255, 1060, 255)
arrow(480, 540, 560, 540)
arrow(980, 540, 1060, 540)
arrow(270, 360, 270, 440, (150, 150, 150))
arrow(770, 360, 770, 440, (150, 150, 150))
arrow(1300, 360, 1300, 440, (150, 150, 150))
d.line([800, 640, 800, 720], fill=(150, 150, 150), width=3)

img.save(OUT / "project_overview.png", optimize=True)
print("saved", OUT / "project_overview.png", img.size)
