"""端到端冒烟：伪造小数据集 → 训练 1 epoch(CPU) → 导出 ONNX → 离线评估 → 清理。
目的：证明 manifest→train→export→evaluate 全链路可执行（不验证精度，精度看真实数据的 val_macro_f1）。
用法: python src/smoke_test.py   （需 torch/torchvision/onnx/pillow；纯 CPU 约 1 分钟）"""

import random
import _console  # noqa: F401  —— 控制台 UTF-8 护栏（GBK 终端打印 emoji 会崩）
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]   # ai-training/
DS = ROOT / "datasets" / "_smoke"
LABELS = ("passable", "blocked", "no_ground")


def fabricate(n_sessions: int = 12):
    """伪造 3 类 × 12 个"分钟会话"的小数据集：文件名沿用 roi_<日期>_<时分秒>_<毫秒> 格式，
    让 stratified_split 的会话解析逻辑也被真实覆盖。图像为随机噪声，与标签无关联（不影响链路验证）。"""
    rng = random.Random(7)
    if DS.exists():
        shutil.rmtree(DS)
    for c in LABELS:
        (DS / "images" / c).mkdir(parents=True)
    lines = ["relpath,label,map,season,source,created"]
    for j in range(n_sessions):                      # 每分钟一个会话，会话内三类各一张
        for i, c in enumerate(LABELS):
            fname = f"roi_20260928_10{j:02d}00_{i:03d}.jpg"
            rel = f"images\\{c}\\{fname}"
            img = Image.effect_noise((64, 128), rng.uniform(5, 40)).convert("RGB")
            img.save(DS / rel)
            lines.append(f"{rel},{c},零号大坝,s5,smoke,2026-09-28 10:{j:02d}:00")
    (DS / "manifest.csv").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"📦 伪造数据集: {n_sessions * len(LABELS)} 张 → {DS}")


def run(args):
    print("$ python", " ".join(args))
    r = subprocess.run([sys.executable] + args, cwd=ROOT)
    if r.returncode != 0:
        sys.exit(f"❌ 步骤失败: {' '.join(args)}")


def main():
    fabricate()
    run(["src/train.py", "--data", "datasets/_smoke", "--epochs", "1",
         "--batch-size", "8", "--num-workers", "0", "--model", "tiny"])
    run(["src/export_onnx.py", "--ckpt", "runs/tiny_best.pt", "--out", "runs/_smoke.onnx"])
    run(["src/evaluate.py", "--ckpt", "runs/tiny_best.pt", "--data", "datasets/_smoke/manifest.csv"])
    shutil.rmtree(DS, ignore_errors=True)
    (ROOT / "runs" / "tiny_best.pt").unlink(missing_ok=True)
    (ROOT / "runs" / "_smoke.onnx").unlink(missing_ok=True)
    print("✅ 冒烟通过：manifest → train → export → evaluate 全链路可执行")


if __name__ == "__main__":
    main()
