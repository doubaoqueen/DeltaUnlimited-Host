# AI 感知金标集（赛季门禁用）

- 用途：**赛季更新后模型失能的确定性检测**。启动时若本目录存在 labels.csv 且 ≥10 张有效图，AiVision 会先在此跑分，
  macro-F1 低于 `data/ai_vision.json → drift.min_macro_f1`（默认 0.85）→ 自动禁用模块。
- 内容：**约 100 张**人工确认过标签的 ROI 图（设计分辨率 ROI 裁剪原图即可，运行时传感器会自行缩放）。
  覆盖当赛季各时段/光照/天气下的三类样本（passable / blocked / no_ground），比例大致均衡。
- 生成方式：用 `aicollect` 采集 → 人工筛选最稳的 ~100 张拷入本目录 → 填写 labels.csv（首行表头不可删）。
- labels.csv 格式：`filename,label,map,season,source,created`；label ∈ passable / blocked / no_ground；
  文件可与 csv 同目录，或放 `passable/`、`blocked/`、`no_ground/` 子目录（评估器两种布局都认）。
- 离线验证：`dotnet run -- aieval`（缺省即跑本目录）——与运行期门禁同一套评估代码。
- 注意：本目录会提交进仓库（小体积），但**不要**放全帧大图；图片为游戏截图素材，遵循项目素材红线（仅本地仓库使用）。
