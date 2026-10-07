"""人工标签 vs VLM 初筛对账：分流混淆矩阵 + 三分类混淆矩阵 + 一致率 + 标签分布。
join 链：label_map.csv（record relpath→train relpath）+ prescreen.csv（record relpath→VLM 判断）+ manifest.csv（train relpath→人工标签）。
两类对账：
  ① 分流（一直有）：VLM roi_usable 是"该不该送人工"的二分判断——
     人工 passable/blocked → "该送"；no_ground/background/discard → "不该送"。
  ② 三分类（prompt v4 起有 label_hint 才有）：VLM 建议标签 vs 人工标签的混淆矩阵，
     用来判断"VLM 的建议值不值得当参考答案"，以及它在哪两类之间最容易混淆。
红线：无论哪种对账，VLM 判断都不是训练标签（见 docs/AI初筛操作手册.md §0）。
用法: python src/audit.py
"""

import csv
import _console  # noqa: F401  —— 控制台 UTF-8 护栏（GBK 终端打印 emoji 会崩）
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REC = ROOT / "datasets" / "record"
SEND = {"passable", "blocked"}  # 人工标签中"应该被送来人工标"的
CLASSES = ("passable", "blocked", "no_ground")


def read_csv(path: Path):
    if not path.exists():
        return []
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def main():
    label_map = {r["record_relpath"]: r["train_relpath"]
                 for r in read_csv(REC / "label_map.csv")}
    prescreen = {r["relpath"]: r for r in read_csv(REC / "prescreen.csv")}
    manifest = {r["relpath"]: r["label"] for r in read_csv(
        ROOT / "datasets" / "passability" / "manifest.csv") if r.get("label")}

    cm = Counter()
    n = agree = 0
    misses = []   # VLM 淘汰但人工标了可用类（VLM 该送没送）
    falses = []   # VLM 送标但人工判 no_ground/背景/丢弃（VLM 不该送却送了）
    for rec_rel, train_rel in label_map.items():
        human = manifest.get(train_rel)
        vlm = prescreen.get(rec_rel)
        if not human or not vlm or vlm.get("error"):
            continue
        vlm_send = vlm["roi_usable"] == "True"
        human_send = human in SEND
        n += 1
        agree += vlm_send == human_send
        cm[("送标" if vlm_send else "淘汰", human)] += 1
        if vlm_send and not human_send:
            falses.append((rec_rel, human))
        if not vlm_send and human_send:
            misses.append((rec_rel, human))

    print(f"人工标签总数: {len(manifest)} | 可对账（同时有人工标签和 VLM 判断）: {n}")
    if n:
        print(f"分流一致率: {agree}/{n} = {agree / n * 100:.1f}%")
        print("混淆矩阵（VLM 判定 × 人工标签）:")
        for (vlm, human), c in sorted(cm.items()):
            print(f"  VLM {vlm} × 人工 {human:<10} {c}")
        if misses:
            print(f"⚠ VLM 错杀 {len(misses)} 张（标了可用却被淘汰）——抽几张看看是玻璃/贴脸这类难例吗:")
            for rel, h in misses[:8]:
                print(f"    {rel} → 人工 {h}")
        if falses:
            print(f"⚠ VLM 放行 {len(falses)} 张（人工判弃权/丢弃）——同样值得抽看:")
            for rel, h in falses[:8]:
                print(f"    {rel} → 人工 {h}")
    dist = Counter(manifest.values())
    print(f"人工标签分布: {dict(dist)}")

    # ② 三分类对账（prompt v4 起才有 label_hint）
    hint_cm = Counter()
    hint_n = hint_agree = 0
    hint_missing = 0
    for rec_rel, train_rel in label_map.items():
        human = manifest.get(train_rel)
        vlm = prescreen.get(rec_rel)
        if not human or not vlm or vlm.get("error"):
            continue
        hint = (vlm.get("label_hint") or "").strip().lower()
        if hint not in CLASSES:
            hint_missing += 1
            continue
        hint_n += 1
        # 人工 background/discard 不参与三分类（它们不是模型的三类之一）
        human_cls = human if human in CLASSES else "(非三类)"
        hint_cm[(hint, human_cls)] += 1
        if hint == human:
            hint_agree += 1

    print(f"\n【三分类对账】可对账 {hint_n} 张（建议标签缺失/旧版无此列 {hint_missing} 张）")
    if hint_n:
        acc = hint_agree / hint_n * 100
        print(f"建议标签与人工标签一致率: {hint_agree}/{hint_n} = {acc:.1f}%")
        print("混淆矩阵（VLM 建议 × 人工标签）:")
        for (hint, human), c in sorted(hint_cm.items()):
            print(f"  VLM {hint:<10} × 人工 {human:<10} {c}")
        # 各类召回（人工为某类时，VLM 给对的占比）
        for cls in CLASSES:
            tot = sum(c for (h, hu), c in hint_cm.items() if hu == cls)
            ok = hint_cm.get((cls, cls), 0)
            if tot:
                print(f"  {cls:<10} 召回 {ok}/{tot} = {ok / tot * 100:.1f}%")
    elif hint_missing:
        print("  （当前 prescreen.csv 是旧版提示词，无 label_hint 列——跑 prescreen.py --refresh 重筛后即可对账）")


if __name__ == "__main__":
    main()
