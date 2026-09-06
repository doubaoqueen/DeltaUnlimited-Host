using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>crop：从截图裁模板小图（支持缩放换算到设计分辨率）。</summary>
public static class CropCommand
{
    public static void Run(string repoRoot, string[] cmdArgs)
    {
        if (cmdArgs.Length < 6) throw new ArgumentException("用法: crop <x> <y> <w> <h> <源图相对路径> [输出相对路径] [缩放系数]");
        int x = int.Parse(cmdArgs[1]);
        int y = int.Parse(cmdArgs[2]);
        int w = int.Parse(cmdArgs[3]);
        int h = int.Parse(cmdArgs[4]);
        string srcRel = cmdArgs[5];
        string outRel = cmdArgs.Length > 6 ? cmdArgs[6] : "screenshots/cropped.png";
        double scale = cmdArgs.Length > 7 && double.TryParse(cmdArgs[7], out double sc) && sc > 0 ? sc : 1.0;

        using var img = Cv2.ImRead(Path.Combine(repoRoot, srcRel), ImreadModes.Color);
        if (img.Empty()) throw new FileNotFoundException($"图片读取失败: {srcRel}");
        using var cropped = new Mat(img, new Rect(x, y, w, h));

        Mat saveMat = cropped;
        Mat? resized = null;
        if (Math.Abs(scale - 1.0) > 0.001)
        {
            resized = new Mat();
            Cv2.Resize(cropped, resized, new Size((int)Math.Round(w * scale), (int)Math.Round(h * scale)), 0, 0, InterpolationFlags.Linear);
            saveMat = resized;
        }

        string outAbs = Path.Combine(repoRoot, outRel);
        var d = Path.GetDirectoryName(outAbs);
        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        try
        {
            if (!Cv2.ImWrite(outAbs, saveMat)) throw new IOException($"保存失败: {outAbs}");
            Console.WriteLine($"✅ 裁剪完成: {outRel}（{saveMat.Width}x{saveMat.Height}，缩放 x{scale}）");
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
