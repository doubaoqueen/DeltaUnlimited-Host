# AI 训练新人学习路径 v1

状态：**学习指南**（2026-09-27）。为没有 CV/机器学习开发经验的新成员准备的"最小必要知识 + 边干边学"路线，配合 `docs/AI训练侦查报告.md` 使用。

## 0. 怎么用这份文档

**先跑通一遍管线，再按需补知识。** 仓库已经把"采集→训练→导出→门禁→回流"的全链路建好了（`ai-training/`），对你来说最好的教材就是这条管线本身：亲手跑通一遍，比看十篇教程有用。遇到看不懂的概念再回到本文查属于哪一层、学什么。

**总原则：本项目的 AI 是"感知传感器"，不学策略、不学强化学习、不碰战斗辅助。** 你需要的一切知识都围绕"把图喂给小模型、拿到一个分类/框、交回给 C# 状态机"这一件事。

## 1. 知识地图（四层）

| 层 | 学什么 | 对应仓库环节 | 达标标准（会做什么） | 优先级 |
|---|---|---|---|---|
| 0 项目与工具 | 项目文档、CLI 命令、Git 协作 | `docs/*`、`Cli/`、日常提交 | 能独立完成一次 aicollect 采集并提交 | 开工前置 |
| 1 图像与数据 | 图像基础、数据集工程、标注 | `aicollect`、`manifest.csv` | 能组织一次符合覆盖矩阵的采集，能清洗数据、建评测集 | **最先补** |
| 2 模型与训练 | 训练在发生什么、指标含义 | `ai-training/src/*.py` | 能读懂 train.py 每一行、看懂训练日志、独立微调一次 YOLO | 第一个采集夜之后 |
| 3 部署与 C# 衔接 | ONNX、预处理一致性、推理后处理 | `Vision/Ai/*` | 能解释模型从 ONNX 到 PathWarning 的完整链路 | 观察期（observe-only） |

## 2. 各层展开

### 第 0 层：项目与工具（开工前置）

- **文档阅读顺序**：`docs/项目大纲.md` §1/§7（红线，必读）→ `docs/仓库交互手册.md` → `docs/跑刀方案.md` → `docs/导航设计.md` → `ai-training/README.md` → `docs/AI训练侦查报告.md`。
- **CLI 常用命令**：`capture`（截屏）、`aicollect`（采集标注）、`annotate`（标注自测）、`aieval`（金标集门禁）、`patrol`（巡逻）。全部入口见 `Cli/Usage.cs`，逐个试一遍。
- **Git 协作**：分支与提交规范（本仓库用 `feat(ai):` / `fix:` / `docs:` 前缀 + 中文摘要）、push 到远端、PR 流程。

### 第 1 层：图像与数据（最先补，比模型知识更重要）

- **数字图像基础**：像素、通道、**BGR vs RGB**（OpenCvSharp 读图是 BGR，模型训练用 RGB——仓库里 `PassabilitySensor.FillChw` 做了这个转换，这是最常见的坑）、分辨率与 resize、JPEG 有损压缩（为什么采集端 q80、训练端要加压缩增广）。
- **任务三兄弟**：分类（这张图是什么——可通行性）、检测（框出来在哪——避战 YOLO）、分割（逐像素——远期）。各输出什么格式、跑刀里各用在哪。
- **数据集工程**（本层核心）：
  - train/val 划分与**分层切分**（`dataset.py: stratified_split`）；
  - **类别不均衡**：passable 天然最多、no_ground 最少 → `class_weights` 逆频率加权（已在用）；
  - **数据泄漏**：同一局的相邻帧高度相似，随机按行切分会同时落进 train 和 val → val 指标偏乐观（仓库已知局限，见侦查报告 §1）；金标集必须**独立采集**、绝不参与训练；
  - **负样本与难例**：背景帧（b 键）、贴脸墙、UI 遮挡是"白送的性能"，不主动采就永远缺；
  - **金标集**概念：一小份人工反复核对过的"期末考卷"，用来做版本门禁与赛季漂移检测。
- **标注实操**：aicollect 四个键的语义、manifest.csv 各列含义（`map,season,source` 是后面做分组评估的命根子）、进阶的 X-AnyLabeling / SAM2 传播标注（等 YOLO 阶段再学）。

### 第 2 层：模型与训练（够用即可，不用会推导公式）

- **神经网络最小概念集**：卷积网络怎么"看图"（边缘→纹理→物体逐层抽象）、分类头、损失函数（cross-entropy）、epoch / batch / 学习率。不需要手推反向传播。
- **会读训练日志**（对着 `train.py` 学）：loss 降不下去说明什么、val macro-F1 是什么、混淆矩阵怎么看出"哪两类互相认错"。
- **评价指标**：accuracy 为什么不够用（类别不均衡时骗人）、macro-F1（仓库门禁用它）、mAP@50（检测）、IoU、P-R 曲线与阈值选择（`min_confidence=0.70` 应该由曲线定，不是拍脑袋）。
- **数据增广**：ColorJitter/翻转/JPEG 噪声为什么能抗赛季光照漂移（`dataset.py: make_transform`）。
- **PyTorch 读懂级**：Dataset/DataLoader/train loop 三件套——拿着 `train.py` 逐行对照，能改超参、能加一个增广就算达标。
- **Ultralytics YOLO**（N5 阶段再学也来得及）：`yolo train / val / predict / export` 四个命令 + 数据集 YAML 格式 + 从 COCO 预训练微调的套路。

### 第 3 层：部署与 C# 衔接

- **ONNX 是什么**：训练框架（PyTorch）与运行语言（C#）之间的中间格式——所以"运行时零 Python"。
- **Execution Provider 概念**：DirectML（任意 DX12 GPU，Windows 零依赖）vs CPU，`OnnxRunner` 的 auto 回退逻辑。
- **预处理一致性铁律**：训练端与推理端的 mean/std、通道序、输入尺寸必须一字不差——仓库现成对照：`ai-training/src/model.py` 顶部常量 ↔ `Vision/Ai/PassabilitySensor.cs` 的 Mean/Std。改任何一端必须同步另一端（改类别顺序同理）。
- **C# 调用链**（能讲清楚这条链就算达标）：`PatrolCommand` 采样帧 → `AiVision.Observe` → `PassabilitySensor.PredictFull`（归一化→裁 ROI→缩放→推理）→ `PassabilityFilter`（时域滤波+动静分离）→ `PathWarning` →（enforce 时）既有 `Escalate` 守卫；`RecordGuardFired` 回填真值 → `DriftMonitor` 矛盾率。
- **检测后处理**（YOLO 阶段）：置信度过滤、NMS（非极大值抑制，去重叠框）是什么、为什么 YOLO26 的 NMS-free 导出对 C# 友好。

## 3. 边干边学：你的第一个任务（K0 采集夜 → v1 上线）

1. **环境**：按 `ai-training/README.md` 建 conda 环境 `img-train`（torch cu128 + onnx）。
2. **采集夜 ×3-5**：推荐 `dotnet run -- airecord --map 零号大坝 --season sX`（零按键自动存全帧，正常打游戏即可）→ 录完 `dotnet run -- ailabel` 图形界面逐张打标（1/2/0/b=标签，X=丢弃，N=下一个未标）。备选：`aicollect` 边玩边标（键位 1/2/0/b 同义，但控制台要抢焦点、会打断游戏）。按侦查报告 §3.2 覆盖矩阵打标（目标每类 1500+，no_ground 和 blocked 别偷懒）。同时 OBS 录整局。
3. **清洗**：人工过一遍 manifest.csv，删错标、删糊图。
4. **训练**：`python src/train.py --data datasets/passability --epochs 30`，看懂每行日志。
5. **导出**：`python src/export_onnx.py --ckpt runs/mbv3s_best.pt --out ../DeltaUnlimited/assets/ai/models/passability_v1.onnx`，同步 `data/ai_vision.json` 的 `model` 字段 + `assets/ai/models/manifest.json`。
6. **门禁**：从**新采集**（不是训练集）里挑 ~100 张建金标集 → `dotnet run -- aieval` 过 macro-F1 ≥ 0.85。
7. **观察**：observe-only（默认）跑几局，看 `logs/ai` 与巡逻收尾统计；`python src/import_jsonl.py` 回流复标。
8. **迭代 v2**：带着回流数据 + 分组评估改进，重复 4-7。
9. **（达标后）enforce**：确认误报率可接受，再开 `ai_vision.json → avoid.enforce`。

跑完这 9 步，第 1、2 层的知识你就都"用会了"，第 3 层也通了，后面 YOLO 避战只是换个任务类型重复同样套路。

## 4. 学习资源清单

- **《动手学深度学习》中文版（d2l.ai，免费）**：选读卷积神经网络部分（约第 4-7 章），概念为主不用全啃。
- **Ultralytics 官方文档**（docs.ultralytics.com）：train/val/export 与数据集格式，N5 阶段的主教材。
- **OpenCV 基础**：BGR/颜色空间、resize、matchTemplate（对应仓库 `TemplateMatcher`）——任意中文入门教程即可。
- **ONNX Runtime 文档**：Execution Providers 一章（DirectML）。
- **项目内教材（最优）**：`ai-training/src/` 六个 py、`Vision/Ai/` 全部 C#、`docs/跑刀方案.md`——全是为你这个场景写的，比任何外部教程都贴身。

## 5. 新人最容易踩的坑（血泪清单）

1. **BGR/RGB 搞反**：症状是"训练指标很好，实机表现像瞎了"。仓库已对齐，别破坏。
2. **只改训练端或只改推理端的常量**：mean/std、输入尺寸、类别顺序，三处必须同步（model.py ↔ PassabilitySensor ↔ ai_vision.json）。
3. **金标集混进训练集**：门禁立刻形同虚设，赛季漂移检测失效。
4. **val 指标虚高**：相邻帧泄漏（见 §1 层说明），别被 99% 的 val F1 冲昏头，以金标集 + 真机观察为准。
5. **只采"好走的地方"**：blocked/no_ground 缺失 = 模型在最需要它的地方失明。
6. **一训练完就开 enforce**：必须 observe-only 观察期先行，矛盾率说话。
7. **把模型当决策器**：红线。模型只产出预警信号，动作控制权永远在 C# 状态机与人工。
8. **数据集图随手传云/外发**：录屏含他人信息；`images/` 不进 git（已 gitignore），按 README 另盘备份。
