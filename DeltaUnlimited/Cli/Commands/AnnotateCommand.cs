using DeltaUnlimited.Data;
using DeltaUnlimited.Vision;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>annotate：静态识别冒烟（输出标注图）。</summary>
public static class AnnotateCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 2) throw new ArgumentException("用法: annotate <元素名> [截图相对路径(默认 screenshots/hall.png)]");

        string elementName = cmdArgs[1];
        string imageRel = cmdArgs.Length > 2 ? cmdArgs[2] : "screenshots/hall.png";

        var table = data.LoadElements();
        if (!table.Elements.TryGetValue(elementName, out var def))
            throw new ArgumentException($"元素表里没有 “{elementName}”。现有: {string.Join(", ", table.Elements.Keys)}");

        string imagePath = Path.Combine(repoRoot, imageRel);
        string outPath = Path.Combine(repoRoot, "screenshots", "annotated", $"{elementName}.png");

        switch (def.Strategy)
        {
            case "coord":
            {
                int? x = def.Params.X, y = def.Params.Y;
                if (x is null || y is null)
                    throw new InvalidDataException($"元素 {elementName} 的 x/y 还没填（elements.json），无法标注");
                ImageAnnotator.AnnotatePoint(imagePath, outPath, x.Value, y.Value, elementName);
                Console.WriteLine($"✅ coord 标注完成: ({x}, {y})  输出: {outPath}");
                break;
            }

            case "template":
            {
                string? tplRel = def.Params.Template;
                if (string.IsNullOrEmpty(tplRel)) throw new InvalidDataException($"元素 {elementName} 的 template 路径没填");
                string tplPath = Path.Combine(repoRoot, tplRel);
                double threshold = def.Params.Threshold ?? 0.8;
                var r = ImageAnnotator.AnnotateTemplate(imagePath, outPath, tplPath, threshold, elementName);
                if (r.Found)
                    Console.WriteLine($"✅ template 命中! 置信度 {r.Confidence:F3}  中心 ({r.CenterX}, {r.CenterY})  框 {r.W}x{r.H}  输出: {outPath}");
                else
                    Console.WriteLine($"❌ 未命中（最佳置信度 {r.Confidence:F3} < 阈值 {threshold}）。输出: {outPath}");
                break;
            }

            default:
                throw new NotSupportedException($"annotate 暂不支持 strategy={def.Strategy}（现支持 coord/template；ocr 需要接入 OCR 引擎，color 待实现）");
        }
    }
}
