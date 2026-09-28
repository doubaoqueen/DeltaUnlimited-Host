"""manifest.csv → PyTorch Dataset。表头: relpath,label,map,season,source,created。
label=background 的行跳过（负样本池，不参与监督训练）；训练集做颜色/JPEG/翻转增广抗赛季漂移。

切分按"采集会话"（文件名时间戳按分钟分桶）整段进行：相邻帧几乎相同，逐行随机切分会让
val 混入 train 的近邻帧 → 指标虚高、best checkpoint 选错。"""

import csv
import io
import random
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


def _session_key(relpath: str) -> str:
    """从文件名 roi_<日期>_<时分秒>_<毫秒> 提取会话键（精确到分钟）。解析失败退化为逐帧独立键。"""
    stem = Path(relpath).stem
    parts = stem.split("_")
    if len(parts) >= 3 and parts[1].isdigit() and parts[2].isdigit():
        return f"{parts[1]}_{parts[2][:4]}"
    return stem


def stratified_split(csv_path: str, val_ratio: float = 0.15, seed: int = 42):
    """按会话整段切分（防相邻帧泄漏），贪心保证每个类别在 val 达到配额。
    返回 (train_rows, val_rows)。某类别只存在于单个会话时该会话整体给 val（打印警告）。"""
    rows = load_manifest(csv_path)
    rng = random.Random(seed)
    by_session: dict[str, list] = {}
    for row in rows:
        by_session.setdefault(_session_key(row[0]), []).append(row)
    sessions = list(by_session.values())
    rng.shuffle(sessions)

    total = Counter(label for _, label in rows)
    quota = {c: max(1, int(total[c] * val_ratio)) for c in total}
    val_counts: Counter = Counter()
    train, val = [], []
    for group in sessions:
        labels_here = {label for _, label in group}
        if any(val_counts[c] < quota[c] for c in labels_here):
            val.extend(group)
            val_counts.update(label for _, label in group)
        else:
            train.extend(group)

    for c in range(len(CLASSES)):
        if total[c] and val_counts[c] == 0:
            print(f"⚠ 类别 {CLASSES[c]} 只出现在同一会话，整段给了 val（train 缺该类，权重按 1 处理）")
    return train, val


class RandomJpeg:
    """以概率 p 把 PIL 图重压缩为随机质量 JPEG——对齐采集端 q80 存盘与实况码流的压缩差异。"""

    def __init__(self, p: float = 0.5, q_range: tuple = (35, 90)):
        self.p, self.q_range = p, q_range

    def __call__(self, img: Image.Image) -> Image.Image:
        if random.random() > self.p:
            return img
        buf = io.BytesIO()
        img.convert("RGB").save(buf, "JPEG", quality=random.randint(*self.q_range))
        buf.seek(0)
        return Image.open(buf).convert("RGB")


def make_transform(train: bool):
    """与 C# PassabilitySensor 逐字对齐：先 Resize 到模型输入尺寸，再增广，最后 ToTensor+ImageNet 归一化。
    注意 Resize 必须存在——推理端永远把 ROI 缩到 INPUT_SIZE，训练端不缩就是 train/serve 偏斜。"""
    norm = transforms.Normalize(IMAGENET_MEAN, IMAGENET_STD)
    if train:
        return transforms.Compose([
            transforms.Resize(INPUT_SIZE),
            transforms.ColorJitter(0.35, 0.35, 0.25, 0.05),  # 抗赛季光照/雾效漂移
            RandomJpeg(0.5, (35, 90)),                        # 抗 JPEG 压缩差异
            transforms.RandomHorizontalFlip(),
            transforms.ToTensor(),
            norm,
        ])
    return transforms.Compose([
        transforms.Resize(INPUT_SIZE),
        transforms.ToTensor(),
        norm,
    ])


def compute_class_weights(rows):
    """逆频率权重（类别不均衡时用）；某类缺失时按 1 处理（防除零）。rows: [(relpath, label_idx), ...]"""
    counts = Counter(label for _, label in rows)
    total = sum(counts.values())
    return torch.tensor([total / max(1, counts[c]) for c in range(len(CLASSES))], dtype=torch.float32)


class RowDataset(Dataset):
    """对切分后的行子集做 Dataset。类别权重请用模块级 compute_class_weights(rows)。"""

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
