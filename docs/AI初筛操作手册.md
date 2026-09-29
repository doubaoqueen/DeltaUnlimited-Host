# AI 初筛操作手册（prescreen.py + Qwen3-VL 服务）

> 状态：**操作手册**（2026-09-29）。对象：用本地 Qwen3-VL-8B（vLLM 服务）给 `airecord` 录制的素材做自动初筛的人。
> 一句话定位：**质检员**——它决定"哪些帧值得你人工看"，并顺手打上环境元数据；它**不是标注员**，它的判断永远不是训练标签。

## 0. 红线（先读这个）

1. **VLM 判断 ≠ 训练标签。** prescreen.csv 是旁挂元数据，训练端（`train.py`/`dataset.py`）只认 `passability/manifest.csv` 里**人工打的**标签。两者互不可替代。
2. **金标集不许用 VLM 结果。** 金标集是门禁的"期末考卷"，混入 VLM 判断即失效。
3. **它不碰游戏。** 服务只回答"发给它的图"，不截屏、不按键、不知道游戏存在；也永远不进游戏运行时（跑在游戏里的是你训练出来的 MobileNetV3 小模型）。
4. **数据不出机器。** 服务在 localhost，图片不上传任何云端。
5. **显存互斥。** 服务占约 9.6GB 显存——**训练前必须先停服务**，训练完再起。

## 1. 服务管理（前提）

| 操作 | 命令 |
|---|---|
| 启动（WSL 内常驻） | 见下方完整命令 |
| 停止 | `wsl -e bash -c "pkill -f vllm"` |
| 验证存活 | 浏览器开 `http://localhost:8000/v1/models`，或 `nvidia-smi` 看显存是否 ≈9.6GB |

启动命令（最终配方，直接复用；在任意终端执行）：

```bash
wsl -e bash -c 'export HF_HUB_CACHE=/mnt/d/hf-cache HF_ENDPOINT=https://hf-mirror.com CC=gcc VLLM_USE_FLASHINFER_SAMPLER=0 VLLM_FLASHINFER_FORCE_TENSOR_REGISTRY=1; source ~/vllm-env/bin/activate; exec vllm serve /mnt/d/hf-cache/models--cyankiwi--Qwen3-VL-8B-Instruct-AWQ-4bit/snapshots/87196f7771efdd7022a8d8f094ac6e063bf87f5c --served-model-name qwen3vl-8b --max-model-len 2048 --gpu-memory-utilization 0.88 --enforce-eager --limit-mm-per-prompt "{\"images\": 1}" --port 8000'
```

- 就绪标志：日志末尾出现 Uvicorn 路由表 + `GET /v1/models 200`；启动全程约 3-5 分钟（从 D 盘加载权重 + 编译内核，首次编译最慢）。
- 模型权重在 `D:\hf-cache`（普通文件夹）；vLLM 环境（7.6GB）在 WSL 虚拟磁盘（C 盘）里。
- **显存互斥**：服务运行期间不能训练（一张 5070 装不下两个）；反之训练前 `pkill -f vllm`。

## 2. 基本用法（ai-training/ 目录下）

```bash
.venv-train/Scripts/python.exe src/prescreen.py --limit 5    # 试跑 5 张（第一次先跑这个）
.venv-train/Scripts/python.exe src/prescreen.py              # 处理全部未筛帧
.venv-train/Scripts/python.exe src/prescreen.py --refresh    # 忽略已有结果全部重筛
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `--limit N` | 0（全部） | 最多处理 N 张未筛帧；试跑/补筛用 |
| `--refresh` | 关 | 已筛过的也重筛（prompt 改版或换模型后用） |
| `--base-url` | `http://localhost:8000/v1` | vLLM 服务地址 |
| `--model` | `qwen3vl-8b` | API 里的模型名（服务端 `--served-model-name` 定的） |

速率预期：每帧 1-3 秒，732 帧全量约 15-35 分钟。**中途可随时 Ctrl+C**——每帧即时落盘，重跑自动从断点续（已筛的自动跳过）。

## 3. 产物：prescreen.csv（旁挂文件）

位置 `ai-training/datasets/record/prescreen.csv`，**只增不改训练 manifest**。字段：

| 字段 | 含义 | 用途 |
|---|---|---|
| relpath | 帧（对应 record_manifest） | 主键 |
| scene | 对局内/大厅/加载/死亡/结算/收获/其他 | 非对局 → 淘汰 |
| roi_usable | 框内是否为可判定的地面画面 | **决定"要不要送人工"** |
| occlusion | 无/UI覆盖/贴脸遮挡/天空/大面积特效 | 遮挡帧多数应标 no_ground |
| time_weather | 白天/黄昏/夜战/雾天/未知 | 环境元数据（覆盖矩阵的账本） |
| has_enemy / has_loot_signal / has_prompt | 金帧标记 | **YOLO 素材池**（N5 画框优先从这挑） |
| quality | 清晰/模糊/黑屏/过曝 | 黑屏过曝淘汰 |
| confidence | 0-1 | ⚠️ 实测区分度低（v2 prompt 首轮全 ≥0.8），**勿作复核优先级依据**；改 prompt 后重新观察 |
| error | 失败原因（空=成功） | 非空行不计入漏斗 |

**漏斗读法**（脚本结束会打印）：`初筛总数 → roi_usable=true（待人工）+ 金帧（YOLO 池）`。原则：**被淘汰的帧不是丢了**，只是不值得人工看，随时 `--refresh` 可重筛。

## 4. 与 ailabel 的配合流程

当前版本（手动两段式）：

1. `prescreen.py` 跑完 → 打开 prescreen.csv 或看漏斗统计，心里有数；
2. `ailabel` 打标照常——**注意：ailabel 目前仍按 manifest 顺序出帧，尚未按 prescreen 结果过滤排序**（联动为待办）。人工打标时把 VLM 标签当"参考答案"看：它说 roi_usable=false 的帧，大概率是 no_ground 或直接 X 丢弃。

打标完成后对账：把 manifest 的人工标签与 prescreen 的预判对一遍（抽 30-50 张即可），统计 VLM 的方向性准确率——这决定下一轮你敢把多少决定权交给它。

## 5. 注意事项（血泪清单）

1. **别让 VLM 碰金标集**（红线 2，重复一遍因为最致命）。
2. **锚定效应**：看完 VLM 预判再打标，人会被它带偏——如果发现打标速度变快但"和它一致率"高得可疑，停下来盲标几张校准自己。
3. **抽检义务**：VLM 会一本正经地胡说（幻觉率 1-5%），每轮全量初筛后必须抽检；错误集中在哪类（比如夜战全判错），就在下一轮 prompt 里补描述。
4. **prompt 是版本化的**：改 PROMPT 后旧结果不可比，必须 `--refresh` 重筛。
   **v2（2026-09-29）修订记录**：首轮 v1 prompt 出现两类语义漂移——① `occlusion=UI覆盖` 占 81%（模型把整帧的正常 HUD 当成了遮挡）；② 金帧检测过敏（结算界面的物品图标被算成 has_prompt）。v2 把 occlusion 显式限定为"只评价图2 区域"，金帧三项显式排除"菜单/结算/背包里的图标"，并把各字段的判定定义写进 prompt。
   **v3（2026-09-29）修订记录**：audit.py 对账发现"VLM 错杀 24 帧，其中 19 帧人工标 blocked"——模型把 roi_usable 理解成"是否看到地面"，于是"满屏是墙"的帧被淘汰，但这些帧恰恰要标 blocked。v3 把 roi_usable 定义改为"能否判断'往前走是否会被挡'（看到明确障碍也算 true）"。经验：**roi_usable 的语义是"可判定性"而非"有地面"，prompt 每一次歧义都会变成系统性漏检**。
5. **图片分辨率固定发 1280 宽**：更大会爆显存/变慢，更小会丢判断依据——改了就要重筛。
6. **服务空闲也占 9.6GB 显存**：不用的时候停掉，别让训练排队等一个闲着的服务。
7. **失败帧下次自动重试**：单帧失败（网络抖动/解析失败）记 error 列继续下一张；error 非空的帧被视为"未判定"，下次再跑 prescreen.py 会自动重试，无需手动补筛（`--refresh` 只在 prompt 改版/换模型需要全量重判时用）。重试成功后 CSV 里新旧行并存，按 relpath 后行覆盖前行，统计不重复计数。

## 6. 故障排查

| 症状 | 原因 | 处理 |
|---|---|---|
| 接口连不上 | 服务没起/还在启动/被停了 | 看启动日志；等 3-5 分钟；或重新执行启动命令 |
| 启动报 "Failed to find C compiler" | WSL 缺 gcc | `wsl -u root -e bash -c "apt-get install -y gcc libc6-dev"` |
| 启动报 "No available memory for the cache blocks" 或 "less than desired GPU memory utilization" | Windows 桌面显存占用波动，预算不满足 | 关掉占显存的窗口；或把 `--gpu-memory-utilization` 再降 0.03 试一次 |
| 启动报 "Could not find nvcc" | flashinfer JIT 被触发 | 确认两个 `VLLM_*` 环境变量已 export（见 §1 配方） |
| 大量 "无法解析模型输出" | 模型没听话输出了散文 | 一般是 max_tokens 被截断或显存吃紧；重启服务再试 |
| 全部请求超时 | 服务假死 | `pkill -f vllm` 重启 |

## 7. 去哪找东西

| 东西 | 位置 |
|---|---|
| 初筛脚本 | `ai-training/src/prescreen.py` |
| 初筛结果 | `ai-training/datasets/record/prescreen.csv` |
| 模型权重 | `D:\hf-cache`（普通文件夹） |
| vLLM 服务日志 | 工具会话 stdout（启动时的后台任务） |
| 部署踩坑全记录 | `blog/02-AI质检员-Qwen3VL部署八连坑.md`（未入库） |
