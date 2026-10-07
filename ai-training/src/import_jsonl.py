"""运行日志 → 人工复标队列（数据闭环最后一步）。

扫描 logs/ai/**/ai_*.jsonl，把其中带 image 字段的行（模型判 blocked 的 ROI 图与
背景负样本）汇总为 review_queue.csv（路径 + 模型预测 + 置信度 + 时间）。人工逐张核对后：
  - 标注正确的 → relpath/label 追加进 datasets/passability/manifest.csv
  - 标注错误的 → 把图挪到 images/<正确类别>/ 再追加
用法:  python src/import_jsonl.py [--logs ../logs/ai] [--out review_queue.csv]
"""

import argparse
import _console  # noqa: F401  —— 控制台 UTF-8 护栏（GBK 终端打印 emoji 会崩）
import csv
import json
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--logs", default="../logs/ai")
    ap.add_argument("--out", default="review_queue.csv")
    args = ap.parse_args()

    logs = Path(args.logs)
    if not logs.exists():
        raise SystemExit(f"日志目录不存在: {logs.resolve()}")

    rows, seen = [], set()
    for jl in sorted(logs.rglob("ai_*.jsonl")):
        for line in jl.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            img = rec.get("image")
            if not img:
                continue
            path = jl.parent / img
            if not path.exists() or str(path) in seen:
                continue
            seen.add(str(path))
            rows.append({
                "path": str(path),
                "model_class": rec.get("class", ""),
                "conf": rec.get("conf", ""),
                "frame_diff": rec.get("frame_diff", ""),
                "ts": rec.get("ts", ""),
            })

    with open(args.out, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["path", "model_class", "conf", "frame_diff", "ts", "human_label"])
        w.writeheader()
        for r in rows:
            r["human_label"] = ""
            w.writerow(r)
    print(f"✅ 复标队列 {len(rows)} 张 → {args.out}（填 human_label 列后：正确的行追加进 manifest.csv）")


if __name__ == "__main__":
    main()
