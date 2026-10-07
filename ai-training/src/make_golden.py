"""金标集划分器：从已标注全帧里按类别配额整段抽出，写入 assets/ai/golden，
并把对应 ROI 样本从训练清单中移除——金标集永不进训练（门禁考卷独立性的前提）。

抽法：以"分钟会话"为最小单位（同一分钟内的相邻帧近似重复，整段同进同出，
否则模型在训练里见过几乎一样的帧，金标成绩会虚高）。会话洗牌后贪心取，
凑满各类配额为止；抽中的会话里所有三类帧都进金标（避免混合会话半进半出）。

幂等：重复运行不会二次缩减训练集（已抽过的帧自动跳过），只会重写金标目录。
用法（ai-training/ 下）:
  python src/make_golden.py                                        # 默认配额 45/35/20
  python src/make_golden.py --quota passable:60,blocked:40,no_ground:25
"""

import argparse
import _console  # noqa: F401  —— 控制台 UTF-8 护栏（GBK 终端打印 emoji 会崩）
import csv
import random
import shutil
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]              # ai-training/
REC = ROOT / "datasets" / "record"
TRAIN_MANIFEST = ROOT / "datasets" / "passability" / "manifest.csv"
LABEL_MAP = REC / "label_map.csv"
GOLDEN = ROOT.parent / "assets" / "ai" / "golden"
CLASSES = ["passable", "blocked", "no_ground"]


def session_key(rel: str) -> str:
    """full\\full_20260928_232739_514.jpg → 20260928_2327（分钟会话，与 dataset.py 同口径）。"""
    parts = Path(rel).stem.split("_")                   # [full, 日期, 时分秒, 毫秒]
    return f"{parts[1]}_{parts[2][:4]}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--quota", default="passable:45,blocked:35,no_ground:20",
                    help="各类配额，如 passable:45,blocked:35,no_ground:20")
    ap.add_argument("--seed", type=int, default=42)
    args = ap.parse_args()
    quota = {}
    for item in args.quota.split(","):
        k, v = item.split(":")
        quota[k.strip()] = int(v)

    # 1) 已标注且文件在盘的三类全帧
    rows = list(csv.DictReader(open(REC / "record_manifest.csv", encoding="utf-8")))
    labeled = [r for r in rows if r["label"] in CLASSES and (REC / r["relpath"]).exists()]

    # 2) 会话 → 帧
    sessions = defaultdict(list)
    for r in labeled:
        sessions[session_key(r["relpath"])].append(r)

    # 3) 会话贪心抽取（稳定排序后洗牌，保证同 seed 可复现）
    rng = random.Random(args.seed)
    session_ids = sorted(sessions)
    rng.shuffle(session_ids)
    golden, remain = [], dict(quota)
    for sid in session_ids:
        if all(v <= 0 for v in remain.values()):
            break
        take = [(r["relpath"], r["label"]) for r in sessions[sid]]
        counted = Counter(l for _, l in take)
        if not any(remain[c] > 0 and counted.get(c, 0) > 0 for c in CLASSES):
            continue
        golden.extend(take)
        for c in CLASSES:
            remain[c] -= counted.get(c, 0)

    # 4) 写金标目录：labels.csv（file,label）+ 全帧 jpg；清掉上次运行留下的多余图
    GOLDEN.mkdir(parents=True, exist_ok=True)
    keep_names = set()
    with open(GOLDEN / "labels.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["file", "label"])
        for rel, label in sorted(golden):
            name = Path(rel).name
            keep_names.add(name)
            shutil.copy2(REC / rel, GOLDEN / name)
            w.writerow([name, label])
    for stale in GOLDEN.glob("*.jpg"):
        if stale.name not in keep_names:
            stale.unlink()

    # 5) 把金标帧的 ROI 样本从训练侧移除（清单行 + ROI 文件 + label_map 条目）
    train_map = {}
    if LABEL_MAP.exists():
        for r in csv.DictReader(open(LABEL_MAP, encoding="utf-8")):
            train_map[r["record_relpath"]] = r["train_relpath"]
    golden_records = {rel for rel, _ in golden}
    to_remove = {train_map[rel] for rel in golden_records if rel in train_map}

    shutil.copy2(TRAIN_MANIFEST, TRAIN_MANIFEST.with_suffix(".csv.bak"))
    lines = TRAIN_MANIFEST.read_text(encoding="utf-8").splitlines()
    head, body = lines[0], lines[1:]
    removed_rows = [l for l in body if l.split(",")[0] in to_remove]
    kept = [l for l in body if l.split(",")[0] not in to_remove]
    TRAIN_MANIFEST.write_text("\n".join([head] + kept) + ("\n" if kept else ""), encoding="utf-8")
    for train_rel in to_remove:
        roi = ROOT / "datasets" / "passability" / train_rel
        if roi.exists():
            roi.unlink()

    if LABEL_MAP.exists():
        map_rows = [r for r in csv.DictReader(open(LABEL_MAP, encoding="utf-8"))
                    if r["record_relpath"] not in golden_records]
        with open(LABEL_MAP, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=["record_relpath", "train_relpath"])
            w.writeheader()
            w.writerows(map_rows)

    # 6) 报告
    golden_dist = Counter(l for _, l in golden)
    train_dist = Counter(l.split(",")[1] for l in kept)
    print(f"金标集 {len(golden)} 帧 → {GOLDEN}")
    print(f"  分布: {dict(sorted(golden_dist.items()))}（配额 {quota}）")
    if any(remain[c] > 0 for c in CLASSES):
        print(f"  ⚠ 未凑满: { {c: v for c, v in remain.items() if v > 0} }（同类帧不够，已尽力）")
    print(f"训练清单: 移除 {len(removed_rows)} 行 ROI 样本（备份 → manifest.csv.bak）")
    print(f"  现有: {dict(sorted(train_dist.items()))}，总 {len(kept)}")


if __name__ == "__main__":
    main()
