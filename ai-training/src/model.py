"""模型定义。类别顺序与 C# PassabilityClass 枚举严格一致：passable=0, blocked=1, no_ground=2。
预处理常量与 C# PassabilitySensor.Mean/Std 一字不差（ImageNet, RGB）。"""

CLASSES = ("passable", "blocked", "no_ground")
IMAGENET_MEAN = (0.485, 0.456, 0.406)
IMAGENET_STD = (0.229, 0.224, 0.225)
INPUT_SIZE = (112, 224)  # (H, W)，与 data/ai_vision.json 的 input_size 一致


def build_mbv3s(num_classes: int = 3):
    """MobileNetV3-Small（运行时基准 ~0.8ms/帧 @4线程桌面 CPU）。"""
    import torch.nn as nn
    from torchvision.models import mobilenet_v3_small

    m = mobilenet_v3_small(weights=None)
    m.classifier[3] = nn.Linear(1024, num_classes)
    return m


def build_tiny(num_classes: int = 3):
    """极简 CNN（下界参照：~0.2ms/帧，供算力受限的测试笔记本用）。"""
    import torch.nn as nn

    return nn.Sequential(
        nn.Conv2d(3, 16, 3, stride=2, padding=1), nn.ReLU(),
        nn.Conv2d(16, 32, 3, stride=2, padding=1), nn.ReLU(),
        nn.Conv2d(32, 64, 3, stride=2, padding=1), nn.ReLU(),
        nn.Conv2d(64, 64, 3, stride=1, padding=1), nn.ReLU(),
        nn.AdaptiveAvgPool2d(1),
        nn.Flatten(),
        nn.Linear(64, num_classes),
    )


def build(name: str, num_classes: int = 3):
    return build_tiny(num_classes) if name == "tiny" else build_mbv3s(num_classes)
