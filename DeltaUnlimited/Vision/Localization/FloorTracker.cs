namespace DeltaUnlimited.Vision.Localization;

/// <summary>楼层判别配置（债4 2026-10）：图层清单 + 邻接关系 + 滞回参数。
/// 邻接 = 允许直接切换的层对（楼梯/门）；未标定切换点坐标前，约束在"层对"粒度——
/// 楼层是离散事件不是连续量，只能沿着邻接边走（室外↔1F↔2F、1F↔B1），禁止隔层跳变。</summary>
public sealed class FloorConfig
{
    /// <summary>图层名 → 底图路径（相对项目根）。键序即展示序。</summary>
    public Dictionary<string, string> Layers { get; set; } = new()
    {
        ["outdoor"] = "screenshots/mapoverview/zero_dam_tiles_z4.png",
        ["1f"] = "screenshots/mapoverview/daba_1f_z4.png",
        ["2f"] = "screenshots/mapoverview/daba_2f_z4.png",
        ["b1"] = "screenshots/mapoverview/daba_0f_z4.png",
    };

    /// <summary>允许直接切换的层对（"a|b" 无向）。缺省全连通——标定切换点后收紧。</summary>
    public HashSet<string> Adjacency { get; set; } = new()
    {
        "outdoor|1f", "1f|2f", "1f|b1",
    };

    /// <summary>换层滞回：挑战层分数须 ≥ 当前层分数 + 该差值才计一票（防重复地形抖动）。</summary>
    public double SwitchMargin { get; set; } = 0.05;

    /// <summary>换层确认：连续 N 帧满足滞回条件才真正切换。</summary>
    public int SwitchConfirmFrames { get; set; } = 3;

    /// <summary>全层失配容忍：当前层连续 N 帧低于 MinScore → 层态置 Unknown（HUD 被盖/读图失败），
    /// 下帧起重新全域首锚。</summary>
    public int MaxMissFrames { get; set; } = 8;
}

/// <summary>楼层状态机（纯逻辑，无 IO 无图像——分数由 MultiLayerLocalizer 喂入，可单测）。
/// 规则（2026-10 债4 方案）：
/// ① Unknown → 首锚：任意层分数达标即锁定该层（首批确认帧同样要过 SwitchConfirmFrames）；
/// ② 已锁定 → 滞回：只有邻接层且分数高出 SwitchMargin，连续 SwitchConfirmFrames 帧才切换；
/// ③ 非邻接层分数再高也不切（楼层只能沿楼梯走）；
/// ④ 全层失配连续 MaxMissFrames 帧 → Unknown（宁失联不乱跳）。</summary>
public sealed class FloorTracker
{
    private readonly FloorConfig _cfg;
    private string? _challenger;
    private int _challengerRun;
    private int _missRun;

    public FloorTracker(FloorConfig cfg)
    {
        _cfg = cfg;
    }

    /// <summary>当前层（null = Unknown：尚未首锚或失联重锚中）。</summary>
    public string? Current { get; private set; }

    public static bool Adjacent(FloorConfig cfg, string a, string b)
        => a == b || cfg.Adjacency.Contains($"{a}|{b}") || cfg.Adjacency.Contains($"{b}|{a}");

    /// <summary>喂入本帧各层最佳匹配分（缺层=未参与搜索）。返回判定的当前层。</summary>
    public string? Update(IReadOnlyDictionary<string, double> layerScores, double minScore)
    {
        if (layerScores.Count == 0) return Current;

        var best = layerScores.Aggregate((kv1, kv2) => kv1.Value >= kv2.Value ? kv1 : kv2);

        // ④ 全层失配容忍
        if (best.Value < minScore)
        {
            _challenger = null;
            _challengerRun = 0;
            if (++_missRun >= _cfg.MaxMissFrames)
                Current = null;   // 失联：下帧重新首锚
            return Current;
        }
        _missRun = 0;

        // ① 首锚
        if (Current is null)
        {
            if (++_challengerRun >= Math.Max(1, _cfg.SwitchConfirmFrames))
            {
                Current = best.Key;
                _challenger = null;
                _challengerRun = 0;
            }
            else
            {
                _challenger = best.Key;
            }
            return Current;
        }

        // ② ③ 滞回换层：挑战者须邻接、须持续胜出
        if (best.Key != Current)
        {
            bool valid = Adjacent(_cfg, Current, best.Key)
                         && best.Value >= layerScores.GetValueOrDefault(Current, double.MinValue) + _cfg.SwitchMargin;
            if (valid && _challenger == best.Key)
            {
                if (++_challengerRun >= Math.Max(1, _cfg.SwitchConfirmFrames))
                {
                    Current = best.Key;
                    _challenger = null;
                    _challengerRun = 0;
                }
            }
            else if (valid)
            {
                _challenger = best.Key;
                _challengerRun = 1;
            }
            else
            {
                _challenger = null;
                _challengerRun = 0;
            }
        }
        else
        {
            _challenger = null;
            _challengerRun = 0;
        }
        return Current;
    }

    /// <summary>人工复位（换局/进新图）。</summary>
    public void Reset()
    {
        Current = null;
        _challenger = null;
        _challengerRun = 0;
        _missRun = 0;
    }
}
