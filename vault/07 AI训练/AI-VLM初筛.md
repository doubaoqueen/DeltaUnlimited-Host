---
title: AI-VLM初筛
tags: [AI, VLM, 初筛, 数据]
status: 现行
created: 2026-10-23
related: ["[[AI-训练流水线总览]]", "[[AI-数据集与标注]]", "[[AI-VLM预标注探索]]", "[[知识点-轻量CNN与MobileNet]]", "[[坑-提示词上下文超限]]", "[[模块-AI视觉感知]]"]
---

# AI-VLM 初筛（prescreen + Qwen3-VL）

> [!info] 一句话
> 给训练数据请了个**质检员**：本地跑 Qwen3-VL-8B，先替人把录制素材过一遍，决定"哪些帧值得你人工看"、顺手打上环境元数据、把有敌人/物资/交互提示的帧挑进"金帧池"——但它的判断**永远不是训练标签**。

## 为什么需要它

`airecord` 是"零按键自动存全帧"：正常打游戏就录，1-2 秒一张。好处是不打断游戏、素材量大；坏处是**大部分帧是废的**——大厅、匹配、加载、结算、死亡回放、开背包、一整屏天空。这些帧送去人工标注，人会被大量无信息画面磨掉耐心，然后开始瞎按。

初筛干的活，就是把这堆素材先分成三堆：

| 分到哪 | 判断依据 | 后续 |
|---|---|---|
| **待人工标注** | `roi_usable=true`（ROI 区域能判断"往前走会不会被挡"） | 进 `ailabel` 逐张打标 |
| **金帧池** | `has_enemy` / `has_loot_signal` / `has_prompt` 任一为真 | 将来 YOLO 画框优先从这挑（N5 阶段） |
| **淘汰** | 场景不对（大厅/结算…）、ROI 被面板盖住、黑屏过曝 | 不送人工，但**不是丢了**：`--refresh` 随时可重筛 |

blog 里的说法很准：人工标注量从 100% 降到"只看筛剩的帧"，**降 60-80%**（blog 自述口径 ❓未在正式文档中量化）。

## 这个"服务"是什么

不是双击运行的脚本，是**常驻后台程序**：

```
prescreen.py（传送带）
   │  HTTP 请求：图1 整帧 + 图2 ROI 小图 + 提示词
   ▼
localhost:8000 ← vLLM 服务（WSL 里，常驻，占约 9.6GB 显存）
   │  Qwen3-VL 在显卡里看图，得出 JSON 结论
   ▼
返回 {"scene": "对局内", "roi_usable": true, ...}
```

| 件 | 值 |
|---|---|
| 模型 | Qwen3-VL-8B-Instruct-**AWQ 4bit**（社区量化版，权重 7.1GB，存 `D:\hf-cache`） |
| 引擎 | vLLM 0.30.0（WSL2 Ubuntu，Python 3.12 虚拟环境） |
| 显卡 | RTX 5070 12GB；服务常驻显存 **≈9.6GB** |
| 关键参数 | `--max-model-len 2048`、`--gpu-memory-utilization 0.88`、`--enforce-eager`、`--limit-mm-per-prompt '{"images": 1}'` |
| 启动耗时 | **3-5 分钟**（加载权重 + 编译内核）；就绪标志 = 日志出现 `GET /v1/models 200` |
| 速度 | 每帧 1-3 秒；千帧量级 20-50 分钟 |

为什么是 8B-AWQ：官方**没有**发 8B 的 AWQ 版；FP8 约 9GB 在 12GB 卡上太紧；BF16 要 18-22GB 根本装不下；云端 API 又违反"数据不出机器"红线。AWQ 4bit 是这张卡上的舒适区。

## prescreen.csv：一行一帧的旁挂元数据

位置 `ai-training/datasets/record/prescreen.csv`，**只增不改训练 manifest**。

| 字段 | 含义 | 用途 |
|---|---|---|
| `relpath` | 帧路径（对应 record_manifest） | 主键 |
| `scene` | 对局内/大厅/加载/死亡/结算/收获/其他 | 非对局 → 淘汰 |
| `roi_usable` | ROI 区域是否**可判定**（看到明确障碍也算可判定） | **决定要不要送人工** |
| `occlusion` | 无 / UI覆盖 / 贴脸遮挡 / 天空 / 大面积特效 | 遮挡帧多数应标 no_ground |
| `time_weather` | 白天/黄昏/夜战/雾天/未知 | 覆盖矩阵的账本 |
| `has_enemy` / `has_loot_signal` / `has_prompt` | 金帧标记 | YOLO 素材池 |
| `quality` | 清晰/模糊/黑屏/过曝 | 黑屏过曝淘汰 |
| `confidence` | 0-1 | ⚠️ 实测**区分度低**，勿作复核优先级依据 |
| `label_hint` | passable / blocked / no_ground / 无法判断 | v4 起的"建议标签"，ailabel 右侧面板与人工标签直接对照 |
| `prompt_v` | 提示词版本（当前 v4） | 版本可比性：改提示词后旧结果不可比 |
| `error` | 失败原因（空=成功） | 非空行不计入漏斗，下次自动重试 |

漏斗统计（`--report` 随时可看）。当前这份 CSV 的实际样子：

```
初筛总 2581 → 可用 1954（待人工标注）| 金帧 1808（YOLO 素材池）| 失败 0
场景分布: {对局内: 2213, 大厅/加载: 261, 大厅: 33, 大厅/加载/死亡/结算/收获: 58, 结算: 14, 收获: 2}
VLM 建议标签分布: {(旧版无此列): 2581}
提示词版本分布: {(旧版): 2581}
```

> [!warning] 现在这份 CSV 是旧版
> 两个证据：① 表头只有 11 列，**没有 `label_hint` / `prompt_v`**；② 场景字段出现了 `大厅/加载/死亡/结算/收获` 这种"一次给一长串"的值，说明当时 prompt 还没把枚举值钉死。
>
> 要拿到"建议标签"，得在服务在岗时 `--refresh` 全量重筛（2581 帧约 20-50 分钟）。表头升级时脚本会自动补齐并留 `.bak` 备份，旧行新列留空。
>
> 另外注意"金帧 1808 / 2581"这个比例高得可疑——这正是 v2 修订记录里说的"**金帧检测过敏**"（结算界面的物品图标被算成 `has_prompt`）。旧数据的金帧池只能当粗筛，不能当准数。

## 提示词是版本化的（这条最容易被低估）

同一个模型，提示词写得含糊，输出就会系统性跑偏，而且**你不会察觉**——直到人工打标时发现"它怎么老把这些帧淘汰掉"。所以 `PROMPT` 有版本号（`PROMPT_V`），写进每一行，改版必须 `--refresh` 重筛（旧结果不可比）。

三次修订（v1 → v4，当前第 4 版），每一次都是被数据打脸打出来的：

| 版本 | 修订原因 |
|---|---|
| v1 → **v2**（2026-09-29） | ① `occlusion=UI覆盖` 占 **81%**——模型把整帧正常的 HUD 当成了遮挡；② 金帧检测过敏（结算界面的物品图标被算成 `has_prompt`）。对策：occlusion 显式限定"只评价图 2 区域"，金帧三项显式排除"菜单/结算/背包里的图标" |
| v2 → **v3**（2026-09-29） | `audit.py` 对账发现 **VLM 错杀 24 帧，其中 19 帧人工标 blocked**——模型把 `roi_usable` 理解成"是否看到地面"，于是"满屏是墙"的帧被淘汰，而这些帧恰恰要标 blocked。对策：把 `roi_usable` 改定义为"**能否判断往前走是否会被挡**（看到明确障碍也算 true）" |
| v3 → **v4**（2026-09-30） | 新增 `label_hint`（建议标签）与 `prompt_v` 两列，供 ailabel 现场对照与 `audit.py` 三分类对账 |

一句话总结这条经验：**`roi_usable` 的语义是"可判定性"而不是"有地面"，prompt 每一次歧义都会变成系统性漏检。**

## 和 ailabel 的配合：手动两段式

1. `prescreen.py` 跑完 → 看漏斗 / 翻 CSV，心里有数；
2. `dotnet run -- ailabel` 打标——右侧「🤖 VLM 初筛」面板实时显示当前帧的初筛判断（`roi_usable` / 建议标签与你的标签"一致/分歧" / scene / occlusion / 天气 / quality / conf / 金帧标记），面板标题固定标注「参考，不是训练标签」；
3. 标完跑 `python src/audit.py` 对账：出"分流一致率 + 混淆矩阵 + 三分类一致率 + 各类召回"。

⚠️ 已知待办：出帧顺序**还没按初筛结果过滤/排序**（目前仍是 manifest 顺序），所以"被淘汰的帧"也会依次出现——只是面板会告诉你它被淘汰了。

## 五条红线（先读这个）

> [!danger] 红线
> 1. **VLM 判断 ≠ 训练标签。** prescreen.csv 是旁挂元数据，训练端只认 `manifest.csv` 里人工打的标签。
> 2. **金标集不许用 VLM 结果。** 混入即失效。
> 3. **它不碰游戏。** 服务只回答"发给它的图"，不截屏、不按键、不知道游戏存在。
> 4. **数据不出机器。** localhost only，不上传云端（素材里可能有队友昵称）。
> 5. **显存互斥。** 服务占约 9.6GB，**训练前必须先停服务**，训练完再起。

## 注意事项（血泪清单）

| 坑 | 说明 |
|---|---|
| **锚定效应** | 看完 VLM 预判再打标，人会被它带偏。如果发现"速度变快、和它一致率高得可疑"，停下来**盲标几张**校准自己 |
| **必须抽检** | VLM 会一本正经地胡说（幻觉率 1-5%）。每轮全量初筛后抽检；错误集中在哪类（比如夜战全判错），就补进下一轮 prompt |
| **图片固定发 1280 宽** | 更大爆显存/变慢，更小丢判断依据——改了就要重筛 |
| **别边开游戏边筛** | 服务 9.6GB + 游戏几个 G，12GB 卡顶不住 |
| **失败帧自动重试** | 单帧失败记 `error` 继续下一张；error 非空视为"未判定"，下次自动重试，无需手动补 |
| **conf 不能当优先级** | v2 首轮全部 ≥0.8，错的框也一样高（P0 实测 0.95-0.98）。它只记录，别拿它排复核顺序 |

## 启停速查

```powershell
# 启动（PowerShell 一行版；窗口保持打开，3-5 分钟后看 GET /v1/models 200）
wsl -e bash -c 'export HF_HUB_CACHE=/mnt/d/hf-cache HF_ENDPOINT=https://hf-mirror.com CC=gcc VLLM_USE_FLASHINFER_SAMPLER=0 VLLM_FLASHINFER_FORCE_TENSOR_REGISTRY=1; source ~/vllm-env/bin/activate; exec vllm serve <模型快照路径> --served-model-name qwen3vl-8b --max-model-len 2048 --gpu-memory-utilization 0.88 --enforce-eager --limit-mm-per-prompt "{\"images\": 1}" --port 8000'

# 停止（窗口 Ctrl+C 更优雅；找不到窗口就 pkill）
wsl -e bash -c "pkill -f vllm"
```

判活三招：浏览器开 `http://localhost:8000/v1/models`；`nvidia-smi` 看显存（≈9.6GB）；任务管理器 GPU 标签页。**任务管理器进程列表里看不到它**是正常的——它跑在 WSL 里，藏在 `VmmemWSL` 后面，而且"待命"时 GPU 利用率是 0%（占着显存不等于在算）。

## 出处

- 文档：`docs/AI初筛操作手册.md` §0 红线 / §1 服务管理 / §2 流程 / §3 字段 / §5 注意事项 / §6 排查；`ai-training/README.md`「数据闭环」⑨、结果查看表；`docs/模块说明.md` §15 命令与训练管线
- 代码：`ai-training/src/prescreen.py`（`PROMPT` / `FIELDS` / `parse_verdict` / `migrate_header` / `print_report`）、`audit.py`
- 博客：`blog/02-AI质检员-Qwen3VL部署八连坑.md`（部署账本、八连坑、「任务管理器里为什么看不见」）
- 数据：`ai-training/datasets/record/prescreen.csv`（当前 2581 行，旧版 11 列表头）
