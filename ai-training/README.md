# ai-training —— 离线训练环境（Python 只活在这里，运行时零 Python）

职责：把 C# 采集的可通行性数据训练成三分类小 CNN，导出 ONNX 放回主工程。模块说明见 `docs/模块说明.md` §15（AI 视觉感知模块），训练总体方案见 `docs/AI训练侦查报告.md`。

## 训练环境（本机已就绪：系统 Python 3.14 自带 torch 2.11+cu128，RTX 5070 验证可用）

本机路线（已验证）：
```bash
# 系统 Python 3.14（C:\Users\Administrator\AppData\Local\Python\pythoncore-3.14-64）
# 已装 torch 2.11.0+cu128 / torchvision / numpy / onnx / onnxruntime（GPU 实测可用）
# 只差 pillow —— 用 --system-site-packages 的 venv 补齐，不污染系统 Python：
"C:\Users\Administrator\AppData\Local\Python\pythoncore-3.14-64\python.exe" -m venv .venv-train --system-site-packages
.venv-train\Scripts\python.exe -m pip install pillow -i https://pypi.tuna.tsinghua.edu.cn/simple

# 训练/导出/评估/冒烟一律用 .venv-train\Scripts\python.exe
.venv-train\Scripts\python.exe src\smoke_test.py   # 链路自检
```

备选（从零重建 conda 环境）：`conda create -n img-train python=3.10` →
`pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128` →
`pip install onnx onnxruntime numpy pillow`。
**铁律：torch 必须 ≥ cu128**——RTX 5070 是 Blackwell（sm_120），旧 cu 版本不识别该卡。

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
          → runs/mbv3s_best.pt（--model tiny 时为 runs/tiny_best.pt；文件名随 --model 档位）
④ 导出   python src/export_onnx.py --ckpt runs/mbv3s_best.pt \
            --out ../assets/ai/models/passability_v1.onnx
          → 手动同步 data/ai_vision.json 的 model 字段 + assets/ai/models/manifest.json
⑤ 验证   dotnet run -- aieval                       （金标集离线门禁）
⑥ 重建金标集：新版本稳定后挑 ~100 张更新 assets/ai/golden/
⑦ 矛盾回流：python src/import_jsonl.py              （logs/ai 运行日志 → 人工复标队列）
⑧ 链路自检：python src/smoke_test.py                （伪造小数据集跑通 train→export→evaluate，不依赖真实素材）
⑨ VLM 初筛（可选，需 vLLM 服务）：python src/prescreen.py → record/prescreen.csv
   （自动分流+环境元数据+金帧池；操作手册与红线见 docs/AI初筛操作手册.md；VLM 判断不是训练标签）
```

## 结果查看（怎么知道干到哪了）

| 想看什么 | 怎么看 |
|---|---|
| **初筛漏斗统计** | `python src/prescreen.py --report`（ai-training/ 下，随时可反复执行）——可用数/金帧数/场景分布 |
| 初筛逐帧明细 | Excel 或 VS Code 打开 `datasets/record/prescreen.csv`（一行一帧；Excel 里"数据→筛选"可按场景/可用性切片） |
| **人工 vs VLM 对账** | `python src/audit.py`——分流一致率/混淆矩阵/标签分布（每轮标注完跑一次） |
| 标注进度 | `datasets/passability/manifest.csv` 的行数 - 1（表头）= 已人工标注帧数 |
| 训练效果 | `train.py` 每 epoch 打印 val macro-F1 与各类 F1；checkpoint 里带混淆矩阵；`dotnet run -- aieval` 跑金标集门禁 |
| **vLLM 服务启停** | 启动：下面代码块整行执行（3-5 分钟就绪，服务窗口保持打开）；停止：服务窗口 `Ctrl+C` 或 `wsl -e bash -c "pkill -f vllm"`；验证：浏览器开 `http://localhost:8000/v1/models`；⚠️ 训练前必须先停服务 |
| 服务状态 | 浏览器开 `http://localhost:8000/v1/models` 有响应 = vLLM 在岗；`nvidia-smi` 看显存（服务约 9.6GB） |
| 详细说明 | 初筛字段含义/注意事项/故障排查见 `docs/AI初筛操作手册.md`；训练方案见 `docs/AI训练侦查报告.md` |

```bash
# vLLM 服务启动（懒人一行版，任意 Windows 终端可执行；WSL 新手分步版见 docs/AI初筛操作手册.md §1）
wsl -e bash -c 'export HF_HUB_CACHE=/mnt/d/hf-cache HF_ENDPOINT=https://hf-mirror.com CC=gcc VLLM_USE_FLASHINFER_SAMPLER=0 VLLM_FLASHINFER_FORCE_TENSOR_REGISTRY=1; source ~/vllm-env/bin/activate; exec vllm serve /mnt/d/hf-cache/models--cyankiwi--Qwen3-VL-8B-Instruct-AWQ-4bit/snapshots/87196f7771efdd7022a8d8f094ac6e063bf87f5c --served-model-name qwen3vl-8b --max-model-len 2048 --gpu-memory-utilization 0.88 --enforce-eager --limit-mm-per-prompt "{\"images\": 1}" --port 8000'
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
