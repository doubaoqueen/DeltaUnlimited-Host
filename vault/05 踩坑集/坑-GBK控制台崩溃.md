---
title: 坑-GBK控制台崩溃
tags: [踩坑]
status: 已解决
created: 2026-10-23
related: ["[[工具-Python脚本清单]]", "[[AI-VLM初筛]]", "[[坑-提示词上下文超限]]", "[[知识点-工程实践]]", "[[如何维护这个知识库]]"]
---

# 坑：脚本打印一个 ✅ 就崩了 —— GBK 控制台的 UnicodeEncodeError

> [!bug] 症状
> 在 Windows 的普通终端里跑 `prescreen.py`（VLM 初筛），前面几百张图都算得好好的，跑到打印结果那一行时直接抛异常中断：

```
UnicodeEncodeError: 'gbk' codec can't encode character '\u2705' in position 0: illegal multibyte sequence
```

> 一次都没跑完整。而且这不是偶发——脚本里到处用 emoji 当"一眼可见的状态标记"，所以几乎必然命中。

## 根因

- Windows 中文系统的**控制台默认编码是 GBK（cp936）**；
- Python 的 `print` 按这个编码往终端写；
- **emoji（✅ ⛔ 📦 …）不在 GBK 字符集里** → 编码失败 → Python 抛 `UnicodeEncodeError` → 脚本中断。

注意这跟业务逻辑一点关系都没有：算得好好的，死在"输出"这一层。而初筛手册又明确要求"在普通终端里跑 prescreen.py"，等于把这个坑摆在了主路径上。

## 修复方式

新增一个小文件 `ai-training/src/_console.py`，**import 即生效**：把 `sys.stdout` / `sys.stderr` 重新配置成 UTF-8。

```python
import _console  # noqa: F401  —— 控制台 UTF-8 护栏
```

三个设计细节：

| 细节 | 为什么 |
|---|---|
| 用 `errors="replace"` | 真的画不出的字符用替换符，**绝不抛异常**——日志类输出宁可掉一个字符，也不能让程序死 |
| `try/except: pass` | 老终端不支持 `reconfigure()` 就静默跳过，不能因为"护栏"本身把程序搞崩 |
| 挂在 import 区 | 一行、无参数、无调用点，不会忘 |

首批挂了 **8 个脚本**（`prescreen` / `dataset` / `train` / `audit` / `export_onnx` / `make_golden` / `smoke_test` / `import_jsonl`）。之后新增的脚本也照此办理——2026-10-23 复核，`ai-training/src/` 下共 **16 个**脚本带这一行。

## 教训（以后怎么避免）

> [!tip] 跨平台/跨终端的输出，先设编码
> 这是"一行 import"的事；漏了就是 100% 崩，而且崩在离问题最远的地方（输出层）。

1. **`errors="replace"` 比严格模式合适。** 面向人看的日志，容错优先级高于"字符绝对正确"。
2. **报错信息里出现 codec / encode / decode 字样时，先想编码，别急着改逻辑。** "程序坏了"和"终端读不懂"是两种病。
3. 这类环境欠账要**一处写、处处挂**（护栏文件 + 固定 import 习惯），不要每个脚本复制一遍 `sys.stdout.reconfigure(...)`。

## 出处

- Git：`fe79292`（新增 `_console.py` 并在 8 个脚本挂载）
- 代码：`ai-training/src/_console.py`（文件头注释完整记录了这个坑）
- 复核：`ai-training/src/*.py` 中 `import _console` 的脚本共 16 个（2026-10-23 统计）
- 文档：`docs/AI初筛操作手册.md` §2（"在普通终端跑 prescreen.py"的主路径要求）
