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

### 1.1 这个服务是什么、为什么在 WSL 里

- "服务" = 一个**常驻后台程序**：把 Qwen3-VL-8B 模型装进你的 5070 显存，监听 `localhost:8000`。prescreen.py 把截图发给它、它回判断 JSON。它不启动，prescreen.py 就连不上接口。
- 它跑在 **WSL** 里（Windows 自带的 Linux 子系统）——因为 vLLM（推理服务框架）是 Linux 优先的生态，而 WSL 能直接共用你的 NVIDIA 驱动和 GPU。你的使用习惯不变：prescreen.py 照旧在 Windows 里跑，两边通过 localhost 自动打通。

### 1.2 启动（新手照抄版）

1. **打开一个 WSL 终端**：按 Win 键输入 **Ubuntu** 回车（打开的就是 WSL 终端）；或者在任意终端里输入 `wsl` 回车。提示符变成 `用户名@主机:~$` 的样子，就说明你"进到 Linux 里"了。
2. **粘贴下面三条命令**（整段一起复制粘贴回车也行）：

```bash
export HF_HUB_CACHE=/mnt/d/hf-cache HF_ENDPOINT=https://hf-mirror.com CC=gcc VLLM_USE_FLASHINFER_SAMPLER=0 VLLM_FLASHINFER_FORCE_TENSOR_REGISTRY=1
source ~/vllm-env/bin/activate
vllm serve /mnt/d/hf-cache/models--cyankiwi--Qwen3-VL-8B-Instruct-AWQ-4bit/snapshots/87196f7771efdd7022a8d8f094ac6e063bf87f5c --served-model-name qwen3vl-8b --max-model-len 2048 --gpu-memory-utilization 0.88 --enforce-eager --limit-mm-per-prompt '{"images": 1}' --port 8000
```

3. **这个窗口别关！** 服务就活在这个窗口里，日志实时滚动——**看进度/判断启动到哪了就是看这个窗口**。可以最小化，但用初筛期间它必须开着（关窗口 = 服务停止）。第一次 export 的几个环境变量只在当前窗口生效，所以每次重启都要完整贴三条。
4. 启动需 **3-5 分钟**（从 D 盘加载权重 + 编译内核）。**就绪标志**：日志末尾出现 Uvicorn 路由表和 `GET /v1/models 200` 字样。

懒人版（不想手动进 WSL：在任意 Windows 终端整行执行，效果完全等同，窗口同样保持打开）：

```bash
wsl -e bash -c 'export HF_HUB_CACHE=/mnt/d/hf-cache HF_ENDPOINT=https://hf-mirror.com CC=gcc VLLM_USE_FLASHINFER_SAMPLER=0 VLLM_FLASHINFER_FORCE_TENSOR_REGISTRY=1; source ~/vllm-env/bin/activate; exec vllm serve /mnt/d/hf-cache/models--cyankiwi--Qwen3-VL-8B-Instruct-AWQ-4bit/snapshots/87196f7771efdd7022a8d8f094ac6e063bf87f5c --served-model-name qwen3vl-8b --max-model-len 2048 --gpu-memory-utilization 0.88 --enforce-eager --limit-mm-per-prompt "{\"images\": 1}" --port 8000'
```

### 1.3 验证存活

| 方法 | 判读 |
|---|---|
| 浏览器开 `http://localhost:8000/v1/models` | 返回一段 JSON = 在岗；打不开 = 没起或没起完 |
| `nvidia-smi`（WSL/PowerShell 都能跑） | 显存被吃 ≈9.6GB = 模型已装上 |
| 任务管理器 GPU 标签页 | 专用 GPU 内存涨 ~9.6GB |

### 1.4 停止

| 情形 | 操作 |
|---|---|
| 正常收工 | 回到那个服务窗口，按 **Ctrl+C**（优雅退出，日志会收尾） |
| 窗口找不到了/服务假死 | 任意终端执行 `wsl -e bash -c "pkill -f vllm"` |
| 兜底（WSL 里没跑别的东西时） | `wsl --shutdown`（整个 WSL 断电，所有 WSL 内进程全停） |

### 1.5 三条铁律

1. **窗口开着 = 服务在；窗口关了 = 服务停**。没有隐藏的后台守护。
2. **服务空闲也占 9.6GB 显存**：不用就停，训练前必须停（一张 5070 装不下服务+训练）。
3. 模型权重在 `D:\hf-cache`（Windows 侧普通文件夹，WSL 里看到的是 `/mnt/d/hf-cache`）；vLLM 环境（7.6GB）在 WSL 虚拟磁盘（C 盘侧）。

## 2. 一轮完整操作（新手照抄版）

> 前提：素材已用 `dotnet run -- airecord --map 零号大坝 --season s5` 录好（用法见 `DeltaUnlimited/Cli/README.md`）——本手册从"手里有素材"开始。

**你全程只需要两个窗口，分工固定：**

| 窗口 | 是什么 | 干什么 | 什么时候碰它 |
|---|---|---|---|
| ① 服务窗口 | Ubuntu（WSL）黑窗口 | 只负责服务的开/关，开了就不用动 | 开局贴 §1.2 三条命令，收工 Ctrl+C |
| ② 工作窗口 | 你平时跑命令的普通终端（PowerShell/Git Bash 都行） | 跑初筛、看战报、跑标注 | 其余一切操作 |

**第 1 步 · 启动服务**（窗口①）——按 §1.2 贴三条命令 → 等 3-5 分钟 → 日志出现 `GET /v1/models 200` 即就绪。

**第 2 步 · 试跑 5 帧**（窗口②）——验证整条链路通不通：

```bash
cd D:/MeineArbeit/DeltaUnlimited/ai-training
.venv-train/Scripts/python.exe src/prescreen.py --limit 5
```

看到 5 行"✅可用 / ⛔淘汰"的判定输出 = 通了；报"接口连不上"→ 回窗口①看日志（多半还在启动中或没起）。

**第 3 步 · 全量开筛**（窗口②）：

```bash
.venv-train/Scripts/python.exe src/prescreen.py
```

每帧 1-3 秒，千帧量级约 20-50 分钟。**中途随时 Ctrl+C**——每帧即时落盘，重跑自动从断点续（已筛的跳过，失败帧自动重试）。⚠️ **别边开着游戏边筛**：服务占 9.6GB 显存，游戏再吃几个 G，12GB 的卡顶不住；打完一局歇着时再筛。

**第 4 步 · 看战报**（窗口②）：

```bash
.venv-train/Scripts/python.exe src/prescreen.py --report
```

打印漏斗：初筛总数 → 可用（待人工标注）→ 金帧（YOLO 素材池）→ 失败数。

**第 5 步 · 停服务**（回窗口①）按 **Ctrl+C**。初筛完它就没用了——空闲也占 9.6GB 显存，别让它白占。

**第 6 步 · 人工标注**（窗口②，**不需要服务**）：

```bash
cd D:/MeineArbeit/DeltaUnlimited/DeltaUnlimited
dotnet run -- ailabel
```

自动跳到第一个未标帧；A/S/D/F 打标、←/→ 翻页、X 丢弃（键位细节见 `DeltaUnlimited/Cli/README.md`）。**blocked（怼墙/贴箱子/栅栏）优先标**——那是模型最缺的样本。打标/VLM 预判怎么配合看 §4。

**第 7 步 · 对账**（窗口②，可选但推荐，每轮标注完跑一次）：

```bash
cd D:/MeineArbeit/DeltaUnlimited/ai-training
.venv-train/Scripts/python.exe src/audit.py
```

打印"人工 vs VLM"一致率和混淆矩阵——这是检验初筛员值不值得信的成绩单。

### 命令速抄卡

窗口①（Ubuntu/WSL，服务专用，只记开和关）：

```text
开：贴 §1.2 的三条命令（或懒人一行版）→ 等 GET /v1/models 200
关：窗口里 Ctrl+C；或任意终端执行  wsl -e bash -c "pkill -f vllm"
```

窗口②（普通终端，从试跑到收工）：

```bash
cd D:/MeineArbeit/DeltaUnlimited/ai-training
.venv-train/Scripts/python.exe src/prescreen.py --limit 5    # ② 试跑
.venv-train/Scripts/python.exe src/prescreen.py              # ③ 全量
.venv-train/Scripts/python.exe src/prescreen.py --report     # ④ 战报
cd D:/MeineArbeit/DeltaUnlimited/DeltaUnlimited
dotnet run -- ailabel                                        # ⑥ 标注
cd D:/MeineArbeit/DeltaUnlimited/ai-training
.venv-train/Scripts/python.exe src/audit.py                  # ⑦ 对账
```

### prescreen.py 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--limit N` | 0（全部） | 最多处理 N 张未筛帧；试跑/补筛用 |
| `--refresh` | 关 | 已筛过的也重筛（prompt 改版或换模型后用） |
| `--base-url` | `http://localhost:8000/v1` | vLLM 服务地址 |
| `--model` | `qwen3vl-8b` | API 里的模型名（服务端 `--served-model-name` 定的） |

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
| vLLM 服务日志 | 启动服务的那个 WSL 窗口，日志实时滚动（§1.2 第 3 步） |
| 部署踩坑全记录 | `blog/02-AI质检员-Qwen3VL部署八连坑.md`（未入库） |
