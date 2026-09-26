using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCvSharp;

namespace DeltaUnlimited.Vision.Ai;

/// <summary>双层日志（评审定的数据闭环底座）：
/// ① JSONL 永久层——每帧一行（类别/概率/耗时/帧差/预警级别/模型与 EP），无图，永久保留；
/// ② ROI 图像层——blocked 全存 + 背景每 N 帧存一张（负样本池），JPEG q80，环形按 MB 淘汰。
/// 所有写操作加锁（评审 P2-9 的教训：Logger 无锁并发写会静默丢行）。</summary>
public sealed class AiLogSink
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string _baseDir;      // logs/ai
    private readonly int _jpegQuality;
    private readonly int _backgroundEvery;
    private readonly long _ringBytes;
    private readonly string _modelTag;
    private readonly string _provider;
    private long _imagesSincePurge;

    public AiLogSink(string repoRoot, Data.AiLogConfig cfg, string modelTag, string provider)
    {
        _baseDir = Path.Combine(repoRoot, "logs", "ai");
        _jpegQuality = cfg.RoiJpegQuality;
        _backgroundEvery = Math.Max(0, cfg.BackgroundEvery);
        _ringBytes = cfg.RingMb * 1024 * 1024;
        _modelTag = Path.GetFileNameWithoutExtension(modelTag);
        _provider = provider;
    }

    /// <summary>记录一帧。roi 为设计分辨率 ROI 拷贝（可为 null：只写 JSONL）。</summary>
    public void Log(PassabilityResult r, double frameDiff, PathWarningLevel warning, Mat? roi, long frameIndex)
    {
        lock (_gate)
        {
            try
            {
                var day = DateTime.Now.ToString("yyyyMMdd");
                var dir = Path.Combine(_baseDir, day);
                Directory.CreateDirectory(dir);

                string? imagePath = WriteImageIfNeeded(dir, r, warning, roi, frameIndex);
                WriteJsonLine(dir, day, r, frameDiff, warning, frameIndex, imagePath);
                if (_ringBytes > 0 && roi is not null && ++_imagesSincePurge >= 64)
                {
                    _imagesSincePurge = 0;
                    PurgeOldImages();
                }
            }
            catch (Exception ex)
            {
                // 日志失败不能影响链路，但要留痕（写不进文件时至少打到控制台日志）
                Overlay.Logger.Warn($"AI 日志写入失败: {ex.Message}");
            }
        }
    }

    private string? WriteImageIfNeeded(string dir, PassabilityResult r, PathWarningLevel warning, Mat? roi, long frameIndex)
    {
        if (roi is null || roi.Empty()) return null;
        bool modelBlocked = r.Class == PassabilityClass.Blocked;
        bool backgroundSample = _backgroundEvery > 0 && frameIndex % _backgroundEvery == 0;
        if (!modelBlocked && warning != PathWarningLevel.Blocked && !backgroundSample) return null;

        string tag = modelBlocked || warning == PathWarningLevel.Blocked ? r.Class.ToString().ToLowerInvariant() : "background";
        string file = $"roi_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{tag}_{frameIndex}.jpg";
        string path = Path.Combine(dir, file);
        roi.ImWrite(path, new[] { (int)ImwriteFlags.JpegQuality, _jpegQuality });
        return path;
    }

    private void WriteJsonLine(string dir, string day, PassabilityResult r, double frameDiff,
        PathWarningLevel warning, long frameIndex, string? imagePath)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            frame = frameIndex,
            @class = r.Class.ToString().ToLowerInvariant(),
            probs = r.Probs,
            conf = Math.Round(r.Confidence, 4),
            latency_ms = Math.Round(r.LatencyMs, 3),
            frame_diff = Math.Round(frameDiff, 3),
            warning = warning.ToString().ToLowerInvariant(),
            model = _modelTag,
            ep = _provider,
            image = imagePath is null ? null : Path.GetFileName(imagePath),
        }, JsonOpts);
        File.AppendAllText(Path.Combine(dir, $"ai_{day}.jsonl"), line + Environment.NewLine);
    }

    /// <summary>环形淘汰：图像总量超 ring_mb 时按文件名（含时间戳）删最旧。JSONL 不参与淘汰。</summary>
    private void PurgeOldImages()
    {
        if (!Directory.Exists(_baseDir)) return;
        var files = Directory.EnumerateFiles(_baseDir, "roi_*.jpg", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FileInfo(f))
            .ToList();
        long total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= _ringBytes) break;
            total -= f.Length;
            try { f.Delete(); } catch { /* 被占用则下轮再清 */ }
        }
    }
}
