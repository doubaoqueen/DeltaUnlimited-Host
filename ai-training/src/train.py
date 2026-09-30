"""训练入口。用法（ai-training/ 下）:
  python src/train.py --data datasets/passability --epochs 30 --model mbv3s
  （默认加载 ImageNet 预训练骨干；--scratch 从零训练仅作对照实验）
产物: runs/<model>_best.pt（含类别表/输入尺寸，供 export_onnx.py 与 evaluate.py 使用）
"""

import argparse
import random
import time
from collections import Counter
from pathlib import Path

import torch
from torch import nn
from torch.utils.data import DataLoader

from dataset import RowDataset, compute_class_weights, stratified_split
from model import CLASSES, INPUT_SIZE, build


def per_class_f1(cm, n: int) -> list[float]:
    f1s = []
    for c in range(n):
        tp = cm[c][c]
        fp = sum(cm[i][c] for i in range(n)) - tp
        fn = sum(cm[c]) - tp
        f1s.append(0.0 if tp == 0 and (fp + fn) > 0 else (2 * tp / (2 * tp + fp + fn) if (tp + fp + fn) > 0 else 1.0))
    return f1s


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="datasets/passability/manifest.csv")
    ap.add_argument("--model", default="mbv3s", choices=["mbv3s", "tiny"])
    ap.add_argument("--epochs", type=int, default=30)
    ap.add_argument("--batch-size", type=int, default=64)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--num-workers", type=int, default=4)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--weight-decay", type=float, default=0.0, help="AdamW 权重衰减（抗过拟合）")
    ap.add_argument("--weight-cap", type=float, default=0.0, help="类别逆频权重上限，0=不设限；小类样本极少时防损失被其垄断")
    ap.add_argument("--scratch", action="store_true", help="不从 ImageNet 预训练起步（对照实验用）")
    args = ap.parse_args()

    random.seed(args.seed)
    torch.manual_seed(args.seed)

    csv_path = args.data if args.data.endswith("manifest.csv") else str(Path(args.data) / "manifest.csv")
    train_rows, val_rows = stratified_split(csv_path)
    if not train_rows or not val_rows:
        raise SystemExit(f"数据不足：train={len(train_rows)} val={len(val_rows)}（先 aicollect/ailabel 采集）")
    dist = Counter(label for _, label in train_rows)
    print(f"train={len(train_rows)} val={len(val_rows)} 分布={dict((CLASSES[k], v) for k, v in dist.items())}")

    device = "cuda" if torch.cuda.is_available() else "cpu"
    train_ds = RowDataset(train_rows, csv_path, train=True)
    val_ds = RowDataset(val_rows, csv_path, train=False)
    train_loader = DataLoader(train_ds, batch_size=args.batch_size, shuffle=True,
                              num_workers=args.num_workers, pin_memory=device == "cuda")
    val_loader = DataLoader(val_ds, batch_size=args.batch_size, num_workers=2)

    model = build(args.model, pretrained=not args.scratch).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    class_w = compute_class_weights(train_rows)
    if args.weight_cap > 0:
        class_w = class_w.clamp(max=args.weight_cap)
    loss_fn = nn.CrossEntropyLoss(weight=class_w.to(device))
    n_cls = len(CLASSES)

    out_dir = Path("runs")
    out_dir.mkdir(exist_ok=True)
    best_f1 = -1.0
    for epoch in range(1, args.epochs + 1):
        model.train()
        t0 = time.time()
        for xb, yb in train_loader:
            xb, yb = xb.to(device), yb.to(device)
            opt.zero_grad()
            loss = loss_fn(model(xb), yb)
            loss.backward()
            opt.step()
        sched.step()

        model.eval()
        cm = [[0] * n_cls for _ in range(n_cls)]
        with torch.no_grad():
            for xb, yb in val_loader:
                pred = model(xb.to(device)).argmax(1).cpu()
                for y, p in zip(yb.tolist(), pred.tolist()):
                    cm[y][p] += 1
        f1s = per_class_f1(cm, n_cls)
        f1 = sum(f1s) / n_cls
        print(f"epoch {epoch:3d}/{args.epochs}  loss {loss.item():.4f}  val_macro_f1 {f1:.4f}  "
              f"各类 {[round(x, 3) for x in f1s]}  ({time.time()-t0:.1f}s)")
        if f1 > best_f1:
            best_f1 = f1
            torch.save({"model": args.model, "classes": CLASSES, "input_size": INPUT_SIZE,
                        "state": model.state_dict(), "val_macro_f1": f1, "confusion": cm},
                       out_dir / f"{args.model}_best.pt")
    print(f"✅ 最佳 val_macro_f1={best_f1:.4f} → {out_dir / (args.model + '_best.pt')}（下一步: export_onnx.py）")


if __name__ == "__main__":
    main()
