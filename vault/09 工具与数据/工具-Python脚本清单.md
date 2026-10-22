---
title: 工具-Python脚本清单
tags: [工具, Python]
status: 现行
created: 2026-10-23
related: ["[[工具-CLI命令速查]]", "[[AI-训练流水线总览]]", "[[AI-数据集与标注]]", "[[AI-模型与门禁]]", "[[模块-AI视觉感知]]", "[[模块-小地图定位器]]", "[[坑-GBK控制台崩溃]]", "[[坑-提示词上下文超限]]"]
---

# 工具-Python脚本清单

> [!info] 一句话
> `ai-training/src/` 下这些 `.py` **全是离线工具**——不出现在运行时链路里、C# 一次都不会调用它们；它们只在"采集完素材之后、把模型交给 C# 之前"这段用，跑刀时一个都不需要。

## 0. 先明确边界

| 事实 | 说明 |
|---|---|
| **不进运行时** | 主程序只认 `assets/ai/models/*.onnx` + `data/ai_vision.json`；训练侧全换掉也不影响跑刀 |
| 唯一交付物 | 一个 `.onnx` 文件（外加要手动同步的 `manifest.json` / `ai_vision.json` 的 `model` 字段） |
| 不是同一套环境 | Python 侧用 torch/torchvision/PIL/numpy（`.venv-train`）；初筛还要一个本地 vLLM 服务 |
| 允许"一次性脚本"存在 | 以 `_` 开头的几个是当时排查问题的临时脚本，**没有进流水线**，也未必能在别的机器上跑 |

## 1. 主流水线（按使用顺序）

```
airecord（C# 录制全帧）→ prescreen.py（VLM 初筛）→ ailabel（C# 人工标注）
   → dataset.py（切分/增广）→ train.py → export_onnx.py → aieval（C# 门禁）
                                     ↘ evaluate.py（torch 侧对照）
make_golden.py 在标注后、训练前跑一次：抽出金标集（永不进训练）
audit.py / import_jsonl.py 是两条质检/回流支线
```

| 脚本 | 一句话作用 | 典型用法 | 产物 |
|---|---|---|---|
| `prescreen.py` | **VLM 初筛**：把录制帧发给本地 Qwen3-VL（vLLM），产出结构化 JSON 元数据（场景/ROI 可用性/遮挡/时段/金帧/建议标签） | `python src/prescreen.py --limit 5`、`--refresh` 重筛 | `datasets/record/prescreen.csv`（**不是训练标签**） |
| `ground.py` | **VLM 预标注探索**（P0.5 两步缩放）：先整帧框大区域，再把每个粗框裁出来放大细框，三层坐标换算回原图 | `python src/ground.py --limit 40`、`--dry-run`、`--selftest`、`--report <目录>` | `ai-training/runs/p0_probe_<时间戳>/`（**只产候选，不入库**） |
| `dataset.py` | `manifest.csv` → PyTorch Dataset：`background` 行跳过、按"采集会话（分钟）"整段切分防泄漏、颜色/JPEG/翻转增广 | 被 `train.py`/`evaluate.py` import，不单独跑 | — |
| `train.py` | 训练入口（MobileNetV3-Small，输入 112×224，默认 ImageNet 预训练骨干；`--scratch` 仅作对照） | `python src/train.py --data datasets/passability --epochs 30 --model mbv3s` | `runs/<model>_best.pt`（含类别表/输入尺寸） |
| `export_onnx.py` | checkpoint → ONNX（opset 17，固定输入 `1×3×112×224`） | `python src/export_onnx.py --ckpt runs/mbv3s_best.pt --out ../assets/ai/models/passability_v1.onnx` | ONNX + manifest 片段（**要手动贴回**两处配置） |
| `evaluate.py` | torch 侧离线评估（混淆矩阵 + macro-F1），与 C# `aieval` 同口径 | `python src/evaluate.py --ckpt runs/mbv3s_best.pt --data datasets/passability/manifest.csv` | 控制台报告 |
| `make_golden.py` | **金标集划分**：按分钟会话整段抽（同类配额，默认 45/35/20），并把抽中的样本从训练清单移除 → 保证"考卷不进课本" | `python src/make_golden.py`、`--quota passable:60,blocked:40,no_ground:25` | `assets/ai/golden/`（图 + `labels.csv`） |
| `audit.py` | 人工标签 vs VLM 初筛**对账**：分流混淆矩阵 + 三分类混淆矩阵 + 一致率 + 标签分布 | `python src/audit.py` | 控制台（判断"VLM 建议值不值得当参考"） |
| `import_jsonl.py` | 运行日志回流：把 `logs/ai/**/ai_*.jsonl` 里带图的行汇总成人工复标队列 | `python src/import_jsonl.py` | `review_queue.csv` |
| `smoke_test.py` | 端到端冒烟：伪造小数据集 → 训练 1 epoch（CPU）→ 导出 → 评估 → 清理，证明链路可执行（不验精度） | `python src/smoke_test.py` | 全绿/报错；约 1 分钟 |

## 2. 地图与定位（离线验证）

| 脚本 | 一句话作用 | 典型用法 | 产物 |
|---|---|---|---|
| `stitch_map_tiles.py` | 下载官网地图瓦片并拼成整图（需 Referer；支持室外 `map_db` 与楼层 `daba_0f/1f/2f`） | `python ai-training/src/stitch_map_tiles.py --layer map_db --z 4 --nx 16 --ny 12 --tile 256 --out screenshots/mapoverview/zero_dam_tiles_z4.png` | 4096×3072（室外）或 4096×4096（楼层）底图 |
| `minimap_localizer.py` | 小地图定位的**离线验证版**：圆形掩膜 NCC（FFT + 积分图 + 低方差屏蔽）+ 多尺度，默认按最高分选尺度 | `python src/minimap_localizer.py --selftest` / `--regress` / `--batch <起> <止>` / `--frame <相对路径>` | CSV + 轨迹图（C# 版已落地，见 [[模块-小地图定位器]]） |
| `track_plot.py` | 读 `localizetrack` 输出的 CSV，在官方底图上画轨迹并统计速度（`M_PER_PX = 0.40`，待真值校准） | `python ai-training/src/track_plot.py`（自动选 `logs/` 最新 CSV）或指定 CSV | `screenshots/mapoverview/track_*.png` |

## 3. 模型定义与基础设施

| 脚本 | 作用 |
|---|---|
| `model.py` | 模型定义 + 三类契约常量：`CLASSES = ("passable","blocked","no_ground")`、`IMAGENET_MEAN/STD`、`INPUT_SIZE = (112,224)`；`build_mbv3s` / `build_tiny` / `build`。**不是 CLI**，被 train/eval/export import |
| `_console.py` | 控制台 UTF-8 护栏：`import _console` 即把 stdout/stderr 切 UTF-8 + 替换符，避免 GBK 终端打 emoji 直接崩（[[坑-GBK控制台崩溃]]） |

## 4. 临时脚本（以 `_` 开头，不进流水线）

| 脚本 | 当年干什么 | 现状 |
|---|---|---|
| `_align_marks2.py` | 截图 ↔ 官方底图对齐 v2：求缩放+平移，把标好的像素坐标换算成世界坐标 | 出生点/撤离点标定用过一次（[[标定-出生点与撤离点]]），路径写死在 `D:\MeineArbeit\...` |
| `_spawn_marks.py` | 出生点标记检测：亮且与干净底图不同的像素 + 白色簇聚类 | 同上，依赖 `_align_marks2` |
| `_verify_marks.py` | 把检测到的出生点/撤离点画在底图上并编号，供人工确认 | 同上，产出 `marks_verify.png` |
| `_make_vault_assets.py` | 生成知识库配图：复制关键截图（缩小）+ 画项目总览示意图 | 一次性用过；仓库路径写死 |

> [!warning] 临时脚本不要当工具用
> 这四个里有硬编码的本机绝对路径（`D:\MeineArbeit\DeltaUnlimited\...`），换机器直接报错。它们的历史价值在"算法怎么推的"，不在"能不能再跑"——真正要复用的是 `minimap_localizer.py` 和 C# 的 `Vision/Localization/`。

## 5. 环境与前置条件

| 用途 | 需要什么 |
|---|---|
| 训练/导出/评估 | Python + `torch`/`torchvision`/`onnx`/`pillow`；文档里常用 `.venv-train/Scripts/python.exe` |
| `prescreen.py` / `ground.py` | 额外需要**本地 vLLM 服务**在岗（Qwen3-VL），见 `docs/AI初筛操作手册.md`；服务不在就报连接错误 |
| `stitch_map_tiles.py` | 外网访问官网瓦片（带 Referer 头） |
| `track_plot.py` | 只要 pillow；输入是 `logs/localizetrack_*.csv` |

## 6. 三条"红线"写在脚本注释里

1. **VLM 不是标签**（`prescreen.py` 开头）：VLM 只做分流与元数据，正式标签永远由人工在 `ailabel` 里打。
2. **预标注不入库**（`ground.py` 开头）：只产出候选，产物落在 `runs/`，不进 `data/`、不进运行时。
3. **金标集不进训练**（`make_golden.py` 开头）：按会话整段抽走并从训练清单删除，否则金标成绩虚高、门禁形同虚设（[[坑-验证集泄漏]]）。

## 出处

- 代码：`ai-training/src/prescreen.py:1-33`（定位、红线、prompt 版本 v4、字段表）、`ground.py:1-50`（两步缩放、三约束、临时产物目录）、`minimap_localizer.py:1-37`（方法与参数）、`track_plot.py:1-26`（输入输出、M_PER_PX）、`stitch_map_tiles.py:1-25`（瓦片 URL/图层/尺寸）
- 代码：`ai-training/src/train.py:1-31`（入口/产物/逐类 F1 修正）、`export_onnx.py:1-5`（导出后要手动同步两处）、`evaluate.py:1-11`（与 aieval 同口径）、`dataset.py:1-8`（会话级切分与防泄漏）、`make_golden.py:1-12`（配额/幂等/金标不进训练）、`audit.py:1-10`（两类对账）、`import_jsonl.py:1-7`（日志回流）、`smoke_test.py:1-3`（端到端冒烟）
- 代码：`ai-training/src/model.py:1-38`（三类契约常量 + 两个骨干）、`_console.py:1-22`
- 代码（临时脚本）：`_align_marks2.py`、`_spawn_marks.py`、`_verify_marks.py`、`_make_vault_assets.py` 文件头注释与硬编码路径
- 文档：`docs/模块说明.md` §15（命令与训练管线）、`docs/AI训练学习路径.md`、`docs/AI初筛操作手册.md`、`docs/AI训练侦查报告.md`、`docs/VLM预标注流水线方案.md`
- 文档：`DeltaUnlimited/Cli/README.md` §C（配套 Python 侧四件套）
