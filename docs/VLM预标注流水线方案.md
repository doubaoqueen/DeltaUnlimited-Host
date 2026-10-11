# VLM 预标注流水线方案 v2（方案稿）

> 状态：**P0 已开工**（`ai-training/src/ground.py` 最小版已就绪，离线 `--selftest`/`--dry-run` 通过；待 vLLM 服务在岗时试跑 40 帧，肉眼验收后再决定 P1-P3）。用户已定 4 项：① 提框在 Python 侧；② 审批做图形界面（复用 ailabel 架构、不臃肿）；③ 定位不止"裁 UI 模板"，而是**为将来的 YOLO 检测标注建立 VLM 预标注流水线**（搜/打相关的角色/物品/容器识别都靠它省力）；④ 高置信度可自动通过，队列按优先级排序。
> 配套：`docs/AI训练侦查报告.md`（YOLO 是 N5 阶段主战役）、`docs/K0采集清单.md`（素材来源）、`DeltaUnlimited/Cli/README.md`（现有工具）。

## 1. 目标与定位

**一句话**：让本地 VLM（Qwen3-VL-8B，vLLM 服务）**先跑一遍粗标注**，人只在图形界面里"确认/微调/驳回"——从而把两类标注成本降一个量级：

| 用途 | 产出 | 谁最终受益 |
|---|---|---|
| **UI 元素模板** | 裁好的小图 + `elements.json` 条目（template 策略） | 界面自动化（现有链路、跑刀局内 UI） |
| **目标检测数据集** | 框 + 类别（YOLO 格式 `labels/*.txt` + `data.yaml`） | 将来"搜/打"相关识别（敌人/AI/尸体袋/光柱/容器） |

**边界（红线不变）**：离线工具，**不进运行时**；模型只产出候选，**入库必须经过审批**（高置信度可自动通过，但仍在队列里可被复核/回滚）；不碰战斗逻辑、不读内存。

## 2. 分层架构（关键：Python 提框、C# 审批，各司其职）

```
① Python 提框（ai-training/src/ground.py，批量、离线）
     输入：tasks.json（找什么 + 看哪张图）→ 输出：candidates.jsonl + 预览图（框画在原图上）
        │
        ▼
② C# 图形审批（新命令 `aiground`，复用 ailabel 的窗体/成像/存储模式）
     读 candidates.jsonl → 按优先级排序 → 人确认/微调/驳回 → 落 decisions.csv（即时写盘）
        │
        ▼
③ 导出（两条出口，互不干扰）
     A. UI 模板：approve 的元素 → assets/templates/ + data/elements.json（再跑 tplprobe 复核）
     B. 检测数据集：approve 的框 → ai-training/datasets/detect/{images,labels}/ + data.yaml（YOLO 直用）
```

**为什么这样分**：vLLM 调用/图片编码/提示词版本化在 Python 侧已经成熟（`prescreen.py` 现成）；而**图形审批**要的是看大图、按键盘、即时落盘、防误操作——`ailabel` 那套（`AiLabelForm` + `AiLabelStore` + `AiLabelImaging`）已经验证过，复用比重写划算。

## 3. 数据与文件约定（沿用项目现有风格）

| 文件 | 内容 |
|---|---|
| `ai-training/datasets/annotate/tasks.json` | 任务清单：`[{ "image": "screenshots/k0/map.png", "ask": "地图上表示撤离点的图标", "cls": "extract_icon", "region": [x,y,w,h]? }]` |
| `ai-training/datasets/annotate/candidates.jsonl` | 每行一个候选：`{ id, image, cls, bbox:[x,y,w,h]（**设计分辨率 1920×1080 坐标**）, conf, reason, prompt_v, model }` |
| `ai-training/datasets/annotate/preview/<id>.png` | 原图 + 框 + 类别文字（给人肉眼审） |
| `ai-training/datasets/annotate/decisions.csv` | 人工决定：`id, decision(accept/reject/adjust), bbox_adjusted, cls_final, note`（**审批闸门只认这个文件**） |
| `assets/templates/<cls>.png` | 出口 A：通过审批的 UI 模板 |
| `ai-training/datasets/detect/` | 出口 B：`images/`、`labels/`、`data.yaml`（YOLO 标准结构） |

## 4. 提示词与输出契约

- 提示词要求**只输出 JSON**（沿用 prescreen 的版本化惯例，新增 `prompt_v`，改提示词必须 `--refresh`）；
- 单类多实例：返回数组（地图上多个同类图标 → 全部返回）；
- 每条含 `bbox`（原图像素）+ `conf` + 一句 `reason`（便于人工快速判断"它为什么说这是撤离点"）；
- **坐标系必须显式处理**：发给模型前按需缩到 ≤1280 宽（省显存），返回框乘以 `原宽/送图宽` 还原到设计分辨率——**不写这一步会系统性偏 1/3**（会写进代码注释 + 单测钉住换算函数）。

## 5. 图形审批界面（`aiground`，复用 ailabel 架构）

- **布局**：左=原图（框叠加，当前候选高亮）+ 右=裁出的候选图 + 信息区（类别/置信度/模型理由/prompt 版本）+ 底部键位条；
- **键位**（与 ailabel 一致的肌肉记忆）：
  - `Enter/空格` = 通过（accept）；`X` = 驳回（reject）；
  - `方向键` = 微调框（Shift+方向 = 大步长）；`[` `]` = 缩小/放大；
  - `←/→` = 上/下一个候选，`N` = 下一个待审；`G` = 跳转（行号/文件名）；`Esc` = 退出；
  - 数字键 `1..9` = 改类别（对应 `classes.txt` 顺序）；
- **优先级排序**（用户要求）：默认按 `conf` 降序——**最可能对的排最前**；`--auto-accept 0.9` 时 ≥0.9 的直接标记为 accept 并跳过（仍留在 `decisions.csv`，可事后 `--review-accepted` 复核或一键回滚）；
- **即时落盘**（ailabel 的教训）：每次决定立即写 `decisions.csv`，断电/崩溃不丢；
- **不臃肿**：不新建框架，直接复用 `AiLabelImaging`（LoadFrame/DrawRoiBox/ToBitmap）+ 相同的"Store 管数据、Form 只管展示"分层；新增的只有"框的可视化与微调"。

## 6. 导出与门禁

- **出口 A（UI 模板）**：`aiground export-templates` → 拷图 + 写 `elements.json`（template 策略）→ 自动跑 `tplprobe` 复核（≥0.85 才算通过，未达标列在报告里人工决定）；
- **出口 B（YOLO）**：`aiground export-yolo` → 生成标准结构 + `data.yaml` + 统计（各类框数/尺寸分布）→ **训练前必须抽检**（随机 20 张人眼过一遍，防系统性偏差）；
- 两个出口都**只处理 accept 行**，且在导出时打印"本次纳入 N 条、跳过 M 条（未审/驳回）"。

## 7. 分阶段实施与验收

| 阶段 | 内容 | 验收标准 | 预估 |
|---|---|---|---|
| **P0 能力试跑**（先做这个） | `ground.py` 最小版：给定 N 张图 + 类别描述 → 出框 + 预览图 + 统计（框数/置信度分布/耗时） | 你肉眼判断"框得准不准，值不值得继续"——**这一步只花 1 小时，但决定后面要不要做** | 1h |
| P1 批量与候选 | 批量任务、断点续跑、`candidates.jsonl` 契约、坐标换算单测 | 100 张图跑完无人工干预，候选可追溯 | 2h |
| P2 图形审批 | `aiground` 窗体（复用 ailabel）+ 优先级排序 + 高置信度自动通过 + 即时落盘 | 你能在 10 分钟内审完 200 条候选 | 3-4h |
| P3 导出 | 出口 A（模板+tplprobe 复核）/ 出口 B（YOLO 数据集） | 导出的模板能直接进链路；YOLO 数据集能过 `yolo train` 的格式校验 | 2h |

## 8. 风险与对策

| 风险 | 对策 |
|---|---|
| VLM 对**细碎游戏图标**定位不准 | 预览图人工审 + `tplprobe` 数值复核；不达标一律退回候选，不写库 |
| 坐标系缩放搞错（系统性偏 1/3） | 换算函数单测钉住 + 预览图上画"图上框 vs 还原框"双框自检 |
| 显存冲突（服务 9.6GB） | 工具只在**不玩游戏**时跑（与初筛同一纪律）；不做成常驻 |
| 高置信度自动通过引入错误 | 自动通过项单独标记（`auto=true`），支持 `--review-accepted` 复核与一键回滚 |
| 数据集缺类别/极端不平衡 | 导出时打印类别分布与框尺寸分布；训练前抽检 20 张 |

## 9. 与现有资产的关系

- 复用：`prescreen.py`（vLLM 调用/图片编码/版本化）、`ailabel` 三件套（窗体/Store/Imaging）、`tplprobe`/`annotate`（模板复核）、`airecord`（素材来源）；
- 不重复造：不新建 HTTP 客户端框架、不新建图像库、不新建审批数据模型（沿用 manifest/csv 风格）。
