# assets/templates/ —— 模板匹配小图库

放 L1 `TemplateMatch` 节点用的模板图片（从游戏截图中裁剪的小图）。

## 命名规范

- 语义化命名，例如：`extract_button.png`、`loot_box.png`、`confirm_btn.png`
- 保持**与目标分辨率一致**的原尺寸裁剪（不缩放）
- 裁剪得越"独有"越好（避免与画面其他部分相似导致误匹配）

## 当前文件

- `example_button.png`：脚本生成的示例图（`python scripts/gen_template.py`），
  用于跑通 `workflows/demo_vision.json` 视觉链路，不会匹配到真实屏幕（演示用）。
