namespace DeltaUnlimited.Vision.Signals;

/// <summary>感知信号五要素（跑刀工程提案 §2）：检测区域、阈值（随机范围，铁律）、
/// 防抖窗口、失败回落、真机出处。每个信号落数据表时固定这五项——先以代码默认值承载，
/// K0 标定后迁入 data/signals.json（或 runtime.json 的 signals 段）。</summary>
public sealed class SignalSpec
{
    /// <summary>信号名（如 hit_flash / loot_glow_gold）。</summary>
    public string Name { get; set; } = "";

    /// <summary>检测区域描述（zones 表区域名或矩形），OCR/取样的 ROI。</summary>
    public string Region { get; set; } = "";

    /// <summary>阈值随机范围下界（铁律：生产永远在该范围内取样，不得用固定值）。</summary>
    public double ThresholdMin { get; set; }

    /// <summary>阈值随机范围上界。</summary>
    public double ThresholdMax { get; set; }

    /// <summary>连续 N 帧确认窗口（防误触）。</summary>
    public int ConfirmFrames { get; set; } = 3;

    /// <summary>触发后的失败回落（如"AbortToPause 人工接管"）。</summary>
    public string Fallback { get; set; } = "";

    /// <summary>真机出处（实测注释文化：哪一局、什么画面标定的）。</summary>
    public string Source { get; set; } = "";
}

/// <summary>单通道比值检测器：输入某统计量（如红屏占比/光柱色占比），
/// 连续 N 帧 ≥ 阈值才触发；阈值每次触发后重新取样（随机范围铁律 + 防同阈值反复误触）。
/// 纯逻辑（无 IO）：rng 注入即可单测。</summary>
public sealed class RatioDetector
{
    private readonly SignalSpec _spec;
    private readonly Random _rng;
    private double _threshold;
    private int _run;

    public RatioDetector(SignalSpec spec, Random rng)
    {
        _spec = spec;
        _rng = rng;
        _threshold = Sample();
    }

    /// <summary>当前生效阈值（测试/诊断用）。</summary>
    public double CurrentThreshold => _threshold;

    private double Sample()
    {
        double lo = Math.Min(_spec.ThresholdMin, _spec.ThresholdMax);
        double hi = Math.Max(_spec.ThresholdMin, _spec.ThresholdMax);
        return lo + _rng.NextDouble() * (hi - lo);
    }

    /// <summary>喂入一帧统计量，返回是否触发（连续 ConfirmFrames 帧达标）。
    /// 触发后自动清零并重取样阈值。</summary>
    public bool Update(double ratio)
    {
        if (ratio >= _threshold)
        {
            _run++;
            if (_run >= Math.Max(1, _spec.ConfirmFrames))
            {
                _run = 0;
                _threshold = Sample();
                return true;
            }
        }
        else
        {
            _run = 0;
        }
        return false;
    }

    /// <summary>人工复位（进入新场景/换局时调用）。</summary>
    public void Reset()
    {
        _run = 0;
        _threshold = Sample();
    }
}

/// <summary>物资光柱检测（金/紫双通道）：任一通道连续确认即触发。
/// 廉价档信号——HSV 颜色统计由捕获层算好占比后喂进来，这里只做判定（提案 §2）。</summary>
public sealed class LootGlow
{
    private readonly RatioDetector _gold;
    private readonly RatioDetector _purple;

    public LootGlow(RatioDetector gold, RatioDetector purple)
    {
        _gold = gold;
        _purple = purple;
    }

    /// <summary>喂入金/紫占比，返回是否触发（任一通道达标）。</summary>
    public bool Update(double goldRatio, double purpleRatio)
    {
        bool g = _gold.Update(goldRatio);
        bool p = _purple.Update(purpleRatio);
        return g || p;
    }

    public void Reset()
    {
        _gold.Reset();
        _purple.Reset();
    }
}

/// <summary>廉价档感知信号的默认五要素（待 K0 标定的占位值 + 出处说明）。
/// 数值全部为随机范围；真机标定后替换并迁入数据文件。</summary>
public static class DefaultSignals
{
    /// <summary>受击红屏：屏幕边缘 ROI 红色像素占比（零训练，提案 §2 廉价档）。</summary>
    public static SignalSpec HitFlash() => new()
    {
        Name = "hit_flash",
        Region = "两侧边缘条（各宽 W/16，y 25%-75%），RedFlashDetector.EdgeRedRatio",
        ThresholdMin = 0.20,
        ThresholdMax = 0.30,
        ConfirmFrames = 3,
        Fallback = "AbortToPause 人工接管（不自动逃跑）",
        Source = "K0 两批 704 帧离线标定（2026-10-24）：正常帧 p90=0.10/p99≈0.40（含红脸场景），"
                 + "受击尖峰 0.63-0.80（00:08:09/19/57 等簇）；阈值取间隙中带，误报代价=暂停交人工（安全侧）",
    };

    /// <summary>金光柱通道（物资箱高光）。</summary>
    public static SignalSpec LootGlowGold() => new()
    {
        Name = "loot_glow_gold",
        Region = "准星附近 ROI",
        ThresholdMin = 0.05,
        ThresholdMax = 0.12,
        ConfirmFrames = 3,
        Fallback = "不触发走近子流程（错过不亏，误触才亏）",
        Source = "待 K0 标定（容器高光/光柱样本，注意显卡色彩设置会偏色）",
    };

    /// <summary>紫光柱通道（高级物资光柱）。</summary>
    public static SignalSpec LootGlowPurple() => new()
    {
        Name = "loot_glow_purple",
        Region = "准星附近 ROI",
        ThresholdMin = 0.03,
        ThresholdMax = 0.08,
        ConfirmFrames = 3,
        Fallback = "不触发走近子流程",
        Source = "待 K0 标定",
    };
}
