# ai-training —— 离线训练环境（Python 只活在这里，运行时零 Python）

职责：把 C# 采集的可通行性数据训练成三分类小 CNN，导出 ONNX 放回主工程。与 docs/ai视觉感知设计.md 对应。

## 环境搭建（Miniconda，环境名 img-train，Python 3.10）

```bash
conda create -n img-train python=3.10 -y
conda activate img-train

# PyTorch（RTX 5070 = Blackwell，需 cu128 及以上；仅 CPU 环境去掉 --index-url 行）
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128
pip install onnx onnxruntime numpy pillow
```

国内网络：torch 走官方 cu128 源较稳；其余包若超时可用清华镜像 `-i https://pypi.tuna.tsinghua.edu.cn/simple`（注意该镜像 cp310 轮子齐全）。

## 数据闭环（对应设计文档 §5）

```
① 采集   两条路线任选/混用：
   a) dotnet run -- aicollect --map 零号大坝 --season sX
      （边玩边标：1=可通行 2=不可通行 0=无地面 b=背景；注意控制台要持有焦点）
   b) dotnet run -- airecord --map 零号大坝 --season sX   ← 推荐：零按键不打断游戏
      （自动存全帧到 datasets/record/，标签留空；帧差跳静止帧；全帧将来直接喂 YOLO）
      然后 dotnet run -- ailabel  （图形界面逐张打标，1/2/0/b=标签 X=丢弃 N=下一个未标，
      裁 ROI 自动写入下方 passability 目录，复标自动搬移旧文件）
          → ai-training/datasets/passability/{images/, manifest.csv}
② 人工补标/清洗：直接改 manifest.csv（label 列）；错误样本删行+删图
③ 训练   python src/train.py --data datasets/passability --epochs 30
          → runs/passability_best.pt
④ 导出   python src/export_onnx.py --ckpt runs/passability_best.pt \
            --out ../DeltaUnlimited/assets/ai/models/passability_v1.onnx
          → 手动同步 data/ai_vision.json 的 model 字段 + assets/ai/models/manifest.json
⑤ 验证   dotnet run -- aieval                       （金标集离线门禁）
⑥ 重建金标集：新版本稳定后挑 ~100 张更新 assets/ai/golden/
⑦ 矛盾回流：python src/import_jsonl.py              （logs/ai 运行日志 → 人工复标队列）
```

## 目录约定

| 路径 | 入库 | 说明 |
|---|---|---|
| src/*.py | ✅ | 训练/导出/评估/回流脚本 |
| env/img-train.yml | ✅ | conda 环境描述 |
| datasets/passability/manifest.csv | ✅ | 标签清单（relpath,label,map,season,source,created） |
| datasets/passability/images/ | ❌ | 真实图像，gitignore（另盘备份） |
| datasets/record/*.csv | ✅ | 录制清单与标注映射（relpath,label,map,season,created） |
| datasets/record/full/ | ❌ | 录制全帧归档（gitignore；ailabel 的 ROI 裁剪与将来 YOLO 素材来源） |
| runs/ | ❌ | checkpoint 与训练曲线，gitignore |

## 红线（与项目总红线一致）

- 模型只做**感知**（可通行性三分类），不做策略、不输出动作；
- 类别顺序与 C# `PassabilityClass` 枚举一致：passable=0, blocked=1, no_ground=2（改顺序两端必须同步）；
- 预处理常量（ImageNet mean/std）与 C# `PassabilitySensor` 一字不差。
