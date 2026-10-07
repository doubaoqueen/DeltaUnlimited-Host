# AI 感知金标集（赛季门禁用）

- 用途：**赛季更新后模型失能的确定性检测**。启动时若本目录存在 `labels.csv` 且 ≥10 张有效图，`AiVision` 会先在此跑分：
  - macro-F1 < `drift.min_macro_f1`（默认 0.85）但 ≥ `drift.min_macro_f1_observe`（默认 0.60）→ **本会话强制仅观察**（enforce 失效）；
  - macro-F1 < 观察线 → **自动禁用模块**（并写回 `enabled=false`）。
- 现状（2026-09-30 核对）：**376 张** ROI 图 + `labels.csv`（377 行含表头），类别分布 passable 250 / blocked 106 / no_ground 20。
  当前 v1 模型在本集上 macro-F1 ≈ **0.695**（落观察档，enforce 保持关闭）——blocked 召回是主要短板。
- 生成方式（每赛季重建）：`airecord` 录制 → `prescreen.py` VLM 初筛 → `make_golden.py --quota passable:45,blocked:35,no_ground:20`
  分层抽样拷入本目录 → 人工逐张确认标签 → 写 `labels.csv`。**金标集必须独立于训练集，且绝不可用 VLM 判断当标签。**
- `labels.csv` 格式：`file,label`；label ∈ passable / blocked / no_ground（每一行一图，**表头不可删**）。
  图片可与 csv 同目录，或放 `passable/`、`blocked/`、`no_ground/` 子目录（评估器两种布局都认）。
- 离线验证：`dotnet run -- aieval`（缺省即跑本目录）——与运行期门禁同一套评估代码。
- 注意：本目录会提交进仓库（小体积），但**不要**放全帧大图（`full_*.jpg` 已 gitignore）；图片为游戏截图素材，遵循项目素材红线（仅本地仓库使用）。
