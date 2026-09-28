"""checkpoint → ONNX。用法:
  python src/export_onnx.py --ckpt runs/mbv3s_best.pt \
      --out ../DeltaUnlimited/assets/ai/models/passability_v1.onnx
导出后手动同步两处：data/ai_vision.json 的 model 字段、assets/ai/models/manifest.json 的 file/classes。
"""

import argparse
import json
from pathlib import Path

import torch

from model import build


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    ckpt = torch.load(args.ckpt, map_location="cpu", weights_only=False)
    model = build(ckpt["model"], num_classes=len(ckpt["classes"]), pretrained=False)
    model.load_state_dict(ckpt["state"])
    model.eval()

    h, w = ckpt["input_size"]
    dummy = torch.randn(1, 3, h, w)
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(model, dummy, str(out), input_names=["input"], output_names=["logits"],
                      dynamo=False, opset_version=17)

    # 顺手生成该版本的 manifest 片段（贴回 assets/ai/models/manifest.json）
    meta = {
        "file": out.name,
        "classes": list(ckpt["classes"]),
        "input": {"height": h, "width": w, "layout": "NCHW", "color": "RGB"},
        "preprocess": {"mean": [0.485, 0.456, 0.406], "std": [0.229, 0.224, 0.225]},
        "val_macro_f1": ckpt.get("val_macro_f1"),
        "confusion": ckpt.get("confusion"),
    }
    print(f"✅ 导出: {out.resolve()}（{out.stat().st_size/1e6:.2f}MB）")
    print("同步提示：data/ai_vision.json → \"model\": \"" + "/".join(out.parts[-3:]) + "\"")
    print("manifest 片段: " + json.dumps(meta, ensure_ascii=False))


if __name__ == "__main__":
    main()
