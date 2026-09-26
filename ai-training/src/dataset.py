"""manifest.csv → PyTorch Dataset。表头: relpath,label,map,season,source,created。
label=background 的行跳过（负样本池，不参与监督训练）；训练集做颜色/翻转增广抗赛季光照漂移。"""

import csv
from collections import Counter
from pathlib import Path

import torch
from PIL import Image
from torch.utils.data import Dataset
from torchvision import transforms

from model import CLASSES, IMAGENET_MEAN, IMAGENET_STD, INPUT_SIZE

LABEL_TO_IDX = {name: i for i, name in enumerate(CLASSES)}


def load_manifest(csv_path: str):
    rows = []
    with open(csv_path, newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            label = (row.get("label") or "").strip().lower()
            if label in LABEL_TO_IDX:
                rows.append((row["relpath"].strip(), LABEL_TO_IDX[label]))
    return rows


def make_transform(train: bool):
    norm = transforms.Normalize(IMAGENET_MEAN, IMAGENET_STD)
    if train:
        return transforms.Compose([
            transforms.ColorJitter(0.35, 0.35, 0.25, 0.05),  # 抗赛季光照/雾效漂移
            transforms.RandomHorizontalFlip(),
            transforms.ToTensor(),
            norm,
        ])
    return transforms.Compose([transforms.ToTensor(), norm])


class PassabilityDataset(Dataset):
    def __init__(self, csv_path: str, train: bool):
        self.root = Path(csv_path).parent
        self.samples = load_manifest(csv_path)
        self.tf = make_transform(train)

    def __len__(self):
        return len(self.samples)

    def __len_by_class(self):
        return Counter(label for _, label in self.samples)

    def class_weights(self):
        """逆频率权重（类别不均衡时用）。"""
        counts = self.__len_by_class()
        total = sum(counts.values())
        return torch.tensor([total / counts[c] for c in range(len(CLASSES))], dtype=torch.float32)

    def __getitem__(self, idx):
        relpath, label = self.samples[idx]
        img = Image.open(self.root / relpath).convert("RGB")
        return self.tf(img), label


def stratified_split(csv_path: str, val_ratio: float = 0.15, seed: int = 42):
    """无 sklearn 依赖的分层切分：返回 (train_rows, val_rows)，行格式与 load_manifest 一致。"""
    import random

    rows = load_manifest(csv_path)
    rng = random.Random(seed)
    by_label = {}
    for row in rows:
        by_label.setdefault(row[1], []).append(row)
    train, val = [], []
    for _, group in sorted(by_label.items()):
        group = group[:]
        rng.shuffle(group)
        n_val = max(1, int(len(group) * val_ratio)) if len(group) >= 4 else 1
        val.extend(group[:n_val])
        train.extend(group[n_val:])
    return train, val


class RowDataset(Dataset):
    """对切分后的行子集做 Dataset（与 PassabilityDataset 同变换）。"""

    def __init__(self, rows, csv_path: str, train: bool):
        self.root = Path(csv_path).parent
        self.samples = rows
        self.tf = make_transform(train)

    def __len__(self):
        return len(self.samples)

    def __getitem__(self, idx):
        relpath, label = self.samples[idx]
        img = Image.open(self.root / relpath).convert("RGB")
        return self.tf(img), label
