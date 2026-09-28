"""checkpoint 离线评估（混淆矩阵 + macro-F1）。用法:
  python src/evaluate.py --ckpt runs/mbv3s_best.pt --data datasets/passability/manifest.csv
ONNX 成品的评估走主工程 dotnet run -- aieval（同一套金标集口径）。
"""

import argparse
from collections import Counter
from pathlib import Path

import torch
from torch.utils.data import DataLoader

from dataset import RowDataset, load_manifest
from model import CLASSES, build


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--data", default="datasets/passability/manifest.csv")
    ap.add_argument("--split", default="all", choices=["all", "val"])
    args = ap.parse_args()

    ckpt = torch.load(args.ckpt, map_location="cpu", weights_only=False)
    model = build(ckpt["model"], num_classes=len(ckpt["classes"]), pretrained=False)
    model.load_state_dict(ckpt["state"])
    model.eval()

    csv_path = args.data if args.data.endswith("manifest.csv") else str(Path(args.data) / "manifest.csv")
    rows = load_manifest(csv_path) if args.split == "all" else stratified_val_only(csv_path)
    if not rows:
        raise SystemExit("无样本")
    ds = RowDataset(rows, csv_path, train=False)
    loader = DataLoader(ds, batch_size=64)

    n = len(CLASSES)
    cm = [[0] * n for _ in range(n)]
    with torch.no_grad():
        for xb, yb in loader:
            pred = model(xb).argmax(1)
            for y, p in zip(yb.tolist(), pred.tolist()):
                cm[y][p] += 1

    print("混淆矩阵（行=真实, 列=预测 | " + ", ".join(CLASSES) + "）:")
    for i, name in enumerate(CLASSES):
        print(f"  {name:<10} {cm[i]}")
    f1s = []
    for c in range(n):
        tp = cm[c][c]
        fp = sum(cm[i][c] for i in range(n)) - tp
        fn = sum(cm[c]) - tp
        f1s.append(0.0 if tp == 0 and (tp + fp + fn) > 0 else (2 * tp / (2 * tp + fp + fn) if (tp + fp + fn) > 0 else 1.0))
    dist = Counter(label for _, label in rows)
    print(f"macro-F1 {sum(f1s)/n:.4f} | 各类 F1 {[round(f, 3) for f in f1s]} | 样本分布 {dict((CLASSES[k], v) for k, v in dist.items())}")


def stratified_val_only(csv_path: str):
    from dataset import stratified_split

    _, val = stratified_split(csv_path)
    return val


if __name__ == "__main__":
    main()
