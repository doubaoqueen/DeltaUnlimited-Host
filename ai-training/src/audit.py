"""人工标签 vs VLM 初筛对账：分流混淆矩阵 + 一致率 + 标签分布。
join 链：label_map.csv（record relpath→train relpath）+ prescreen.csv（record relpath→VLM 判断）+ manifest.csv（train relpath→人工标签）。
VLM 只有 roi_usable（"该不该送人工"）一个二分判断，所以对账也用二分：
  人工 passable/blocked → "该送"；no_ground/background/discard → "不该送"。
VLM 对 passable vs blocked 的区分不参与对账（它从来没被问过这个问题）。
用法: python src/audit.py
"""

import csv
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REC = ROOT / "datasets" / "record"
SEND = {"passable", "blocked"}  # 人工标签中"应该被送来人工标"的


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


if __name__ == "__main__":
    main()
