# C# 重构指南（Python 原型 → C# 产品）

> 版本：v1.0
> 背景：Python 工作区作为"规格 + 数据 + 工具"保留；C# 项目作为真正的产品实现。
> 核心理念：**数据格式 = 接口**。JSON schema 与语言无关，两边共用，重构不重来。

---

## 0. 为什么转 C# 是合理的（诚实评估）

| 维度 | 说明 |
|---|---|
| 不吃亏的 | 截图（GDI/WGC）、模板匹配（OpenCV）、OCR（PaddleOCR）底层都是 C/C++，Python 只是壳 |
| Python 吃亏的 | 编排循环开销、GIL、GC 暂停、帧数据拷贝；winrt(WGC) 绑定别扭；GUI+透明覆盖层生态弱 |
| C# 优势 | 原生 WinRT（WGC 截图、DirectComposition）、WPF 透明点击穿透覆盖层、内存可控、单文件发布、长期无人值守更稳 |
| 反面 | 手写量大（P/Invoke、结构体、线程）；开发慢 |

**结论**：目标 = 真实可用 + 低配机器 + GUI/overlay + 长期运行 → C# 合理（BetterGI 同路线）。
**Python 工作区不放弃**：它是规格/数据/工具的家，重构期间的实验台。

---

## 1. 资产盘点与去向

| 资产 | 内容 | 去向 |
|---|---|---|
| **设计规格** | `docs/项目大纲.md`、`docs/实施清单.md`、`docs/界面元素清单.md` | 拷贝进 C# 项目 docs/（或引用本目录） |
| **游戏知识数据** | `data/game_ops.json`（键位+复合动作）、`docs/key-operation.json`（全量按键）、`data/elements.json`（元素识别表格式） | **原样拷贝**，schema 即接口 |
| **素材** | `screenshots/*.png`、`assets/templates/` | 原样拷贝 |
| **开发工具** | `scripts/get_coords.py`（点坐标）、`scripts/gen_template.py`、离线识别实验 | **留在 Python**（开发期工具，C# 不需要） |
| **参考实现** | nodes 引擎 / capture / win_input 的代码逻辑 | C# 重写（规格已在此文档化，照着重写即可） |
| **本 Python 工程** | 运行环境 + 测试 + 工作流 | 保留为实验台（新想法先用 Python 验证，再移植） |

---

## 2. C# 技术选型（模块映射）

| 模块 | Python 旧实现 | C# 选型 | 说明 |
|---|---|---|---|
| 截图 | capture/gdi、wgc（占位） | **Windows.Graphics.Capture**（原生 WinRT） | 硬件加速，全屏可截，BetterGI 同款 |
| 键鼠 | win_input/sendinput（ctypes SendInput） | **SendInput P/Invoke**（照搬结构体定义） | 直接翻译，布局已在 Python 版验证过 |
| 视觉-模板/颜色 | nodes/perception（cv2） | **OpenCvSharp4** | NuGet: OpenCvSharp4 + runtime.win |
| 视觉-OCR | RapidOCR | **Sdcb.PaddleOCR**（.NET 绑定） | 或 PaddleOCR 官方 C++ 部署 |
| 节点引擎 | nodes/core（registry/graph/executor） | **自研同款**（读同一 JSON schema） | 照 Python 规格翻译，含分支/跳过/停止语义 |
| GUI | ui/（PySide6 计划） | **WPF（.NET 8）** | BetterGI 式开关面板 + 透明覆盖层（点击穿透） |
| JSON | json 标准库 | **System.Text.Json** | 序列化元素表/操作表/流程图 |
| 测试 | pytest | **xUnit** | |

环境：VS2022 Community（已装）+ .NET 8 LTS。

---

## 3. C# 项目结构建议

```
DeltaUnlimited.CSharp/
├── DeltaUnlimited.sln
├── src/
│   ├── DU.Core/               # 与 UI 无关的纯逻辑（可单测）
│   │   ├── Nodes/             # 节点引擎：Registry / Graph / Executor / Node 基类
│   │   ├── Capture/           # Windows.Graphics.Capture 封装
│   │   ├── Input/             # SendInput P/Invoke
│   │   ├── Vision/            # 模板匹配 / 颜色 / DetectElement 分发
│   │   ├── Ocr/               # PaddleOCR 封装
│   │   ├── Data/              # elements.json / game_ops.json 模型类
│   │   └── Agents/            # 状态机 / 调度（沿用 L2-L4 分层）
│   └── DU.App/                # WPF：任务开关面板 + 日志 + 预览（M1 目标）
├── data/                      # ← 从 Python 工作区拷贝（见 §5 数据同步）
├── assets/templates/
├── screenshots/
├── docs/                      # ← 拷贝大纲/实施清单
└── tests/DU.Core.Tests/
```

> 分层照搬 Python 版（L0 基础 → L1 感知 → L2 逻辑 → L3 任务 → L4 编排），
> 因为这套分层已经过验证，直接翻译比重新发明快得多。

---

## 4. 重构路线（Phase 0 → 4）

### Phase 0：搭骨架 + 数据先行（1 天）
- [ ] `dotnet new sln` + 建 DU.Core / DU.App / Tests 三个项目
- [ ] **先定义数据模型**：把 elements.json / game_ops.json / workflow JSON 的 C# 模型类写出来并加反序列化测试
- [ ] 拷贝 data/、assets/、screenshots/、docs/ 进来
- **验收**：C# 能正确读 Python 版产出的全部 JSON（schema 冻结）

### Phase 1：捕获 + 输入冒烟（1~2 天）
- [ ] WGC 截图 → `byte[]`/`Mat`（参考 BetterGI 的 WGC 流程：GraphicsCaptureItem → Direct3D11CaptureFramePool）
- [ ] SendInput 键鼠封装（P/Invoke，结构体照 Python 版翻译）
- [ ] 控制台冒烟：截一帧存盘；--fire 自测键鼠
- **验收**：截图画质/帧率 > Python 版；键鼠可动

### Phase 2：节点引擎翻译（2~3 天）
- [ ] Node / Registry / Graph / Executor（含拓扑序、分支跳过、停止信号）
- [ ] 跑通与 Python 版**同一份** demo_smoke.json（结果一致）
- **验收**：xUnit 测试覆盖，两个引擎行为等价

### Phase 3：视觉节点 + 静态验证（2~3 天）
- [ ] TemplateMatch / ColorDetect / DetectElement（读 elements.json，四策略分发）
- [ ] 用 screenshots/ 里的截图做静态验证（复用 Python 版调好的 threshold/关键词）
- **验收**：hall_match_button / loot_prompt 等元素在截图文件上验证通过

### Phase 4：GUI + 第一个真实闭环（M1 目标，1 周+）
- [ ] WPF 面板：任务开关（绑定 workflow JSON）+ 日志 + 截图预览 + 紧急停止
- [ ] 状态机 + knife_run workflow v1（真实游戏半自动跑通）
- [ ] 后续：搜刮循环 OCR（Sdcb.PaddleOCR）→ M2 拟人化/安全守卫

---

## 5. 数据同步策略（两个项目共存的关键）

**原则：JSON schema 是契约，Python 是"实验数据源"，C# 是"运行时数据主"。**

- 现在：把 Python 工作区的 `data/`、`assets/`、`screenshots/` 整体拷贝进 C# 项目（一次性）
- 以后：
  - 游戏内新截图 → 存 C# 项目的 `screenshots/`（运行时主）
  - 用 Python 工具离线实验（调 threshold/关键词）→ 调好后**同步更新两份** elements.json（或让 C# 项目直接读 Python 工作区的绝对路径，二选一，推荐后者做开发期联调，发布前拷贝进 C# 项目）
- 开发期联调技巧：C# 的 `data/` 路径做成可配置，指向 Python 工作区目录，这样只维护一份

---

## 6. 直接可复用的设计（Python 踩坑换来的经验，别重踩）

1. **分层 L0-L4**：基础 → 感知 → 逻辑 → 任务 → 编排，改动局部化
2. **三张数据表**：`game_ops.json`（键位/复合动作）、`elements.json`（元素识别表）、`workflows/*.json`（流程）——全部数据驱动
3. **识别可插拔**：DetectElement + 四策略（coord/template/ocr/color），换识别手段只改 JSON
4. **混合识别（BetterGI 式）**：固定坐标为主 + OCR 读文字 + 少量模板——不为每种材料存模板，拾取认"拾取提示"
5. **复合动作**（pick_up 等）：键位引用操作名，改键不改逻辑
6. **安全设计从第一天带**：紧急停止、连续失败停机、会话限额（M2 落地，接口先留）
7. **拟人化参数**放配置（延迟/随机性），别写死在动作里

> 这份指南 + Python 工作区文档 = C# 实现的完整规格书。祝重构顺利 🚀
