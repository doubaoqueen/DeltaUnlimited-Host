"""知识库自检（临时）：去 BOM + 检查 wiki 链接有效性 + front-matter 完整性 + 规模统计。

用法: python ai-training/src/_vault_check.py [--fix-bom]
"""
import _console  # noqa: F401
import re
import sys
from pathlib import Path

VAULT = Path(r"D:\MeineArbeit\DeltaUnlimited\vault")
LINK = re.compile(r"\[\[([^\]|#]+)(?:#[^\]|]+)?(?:\|[^\]]+)?\]\]")
FENCE = re.compile(r"```.*?```", re.S)
INLINE = re.compile(r"`[^`\n]*`")

# 文档里故意写的示例链接，不参与断链检查
IGNORE = {"笔记名", "笔记名\\", "关联笔记1", "关联笔记2", "某笔记", "新决策",
          "图片名.png", "链接", "某笔记.md", "01 项目总览.md"}


def strip_code(text: str) -> str:
    return INLINE.sub("", FENCE.sub("", text))


def main():
    fix = "--fix-bom" in sys.argv
    notes = sorted(VAULT.rglob("*.md"))
    fixed = 0
    for p in notes:
        raw = p.read_bytes()
        if raw.startswith(b"\xef\xbb\xbf"):
            if fix:
                p.write_bytes(raw[3:])
                fixed += 1
    if fix:
        print(f"已去除 BOM：{fixed} 个文件")

    notes = sorted(VAULT.rglob("*.md"))
    names = {p.stem for p in notes}
    assets = {p.name for p in (VAULT / "assets").glob("*")}
    canvases = {p.name for p in VAULT.glob("*.canvas")}

    total_chars = 0
    broken, no_fm = [], []
    for p in notes:
        text = p.read_text(encoding="utf-8", errors="replace")
        total_chars += len(text)
        if not text.startswith("---"):
            no_fm.append(p.name)
        body = strip_code(text)
        for m in LINK.finditer(body):
            target = m.group(1).strip()
            if not target or target in IGNORE:
                continue
            if target in names or Path(target).name in assets or target.endswith(".canvas"):
                continue
            broken.append((str(p.relative_to(VAULT)), target))

    print(f"\n笔记 {len(notes)} 篇 | 约 {total_chars/1000:.0f}k 字符 | 图片 {len(assets)} | 画布 {len(canvases)}")
    print(f"缺 front-matter：{len(no_fm)}")
    for n in no_fm:
        print("   ", n)
    print(f"断链：{len(broken)}")
    for src, tgt in broken:
        print(f"    {src}  ->  [[{tgt}]]")


if __name__ == "__main__":
    main()
