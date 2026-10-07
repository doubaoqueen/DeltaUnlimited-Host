"""控制台编码护栏（import 即生效）。

背景：Windows 默认控制台编码是 GBK（cp936），脚本打印 emoji（✅⛔📦…）或部分中文时
会抛 UnicodeEncodeError 直接中断——初筛手册要求"在普通终端跑 prescreen.py"，一旦命中即整条流程失败。

用法：在 CLI 脚本的 import 区加一行
    import _console  # noqa: F401  —— 控制台 UTF-8 护栏
"""

import sys


def ensure_utf8() -> None:
    """把 stdout/stderr 切到 UTF-8（无法识别字符用替换符，不抛异常）。旧终端不支持则静默跳过。"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


ensure_utf8()
