namespace DeltaUnlimited.Data;

/// <summary>经济决策纯函数（跑刀工程提案 §5）：白名单起步、不做价值函数。
/// 输入物品库条目 + 现场读数 → 输出 捡/丢/撤 三个布尔。纯函数：无 IO、无随机、无状态，全部可单测。
/// 红线：K1 跑通前这些函数不被任何搜刮代码调用——它们只是 S0 骨架 + 单测。</summary>
public static class EconomyAgent
{
    /// <summary>品质档位文字 → 数值（白1 绿2 蓝3 紫4 金5 红6；无法识别 = 0）。
    /// 品质色样本标定见 docs/K0采集清单.md §5.3。</summary>
    public static int TierValue(string? tier)
    {
        return (tier ?? "").Trim() switch
        {
            "白" => 1, "绿" => 2, "蓝" => 3, "紫" => 4, "金" => 5, "红" => 6, _ => 0,
        };
    }

    /// <summary>OCR 读数匹配物品库条目：读数包含条目名或任一关键词变体（不区分大小写）。
    /// 方向：OCR 读数是长文本（带噪声/后缀），条目名与关键词是短词——t.Contains(短词)。
    /// 命中多条时返回第一条（录入顺序即优先顺序）。</summary>
    public static ItemCatalogEntry? MatchItem(ItemCatalogTable catalog, string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return null;
        string t = ocrText.Trim();
        foreach (var e in catalog.Items)
        {
            if (Contains(t, e.Name)) return e;
            foreach (var k in e.OcrKeywords)
                if (Contains(t, k)) return e;
        }
        return null;

        static bool Contains(string text, string word) =>
            !string.IsNullOrEmpty(word) && text.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是否值得捡：白名单命中 + 品质达标 + 背包有空位。
    /// 物品库无条目命中 → 不捡（fail-open：跑刀宁可漏捡不可乱捡）。</summary>
    public static bool ShouldPick(ItemCatalogTable catalog, string ocrText, int tierValue, bool backpackFull)
    {
        var entry = MatchItem(catalog, ocrText);
        return entry is not null
               && entry.Pick == true
               && tierValue >= Math.Max(1, catalog.MinTierToPick)
               && !backpackFull;
    }

    /// <summary>背包满时是否丢：品质低于保留档就丢（白名单起步的简单规则，K3 再细化）。</summary>
    public static bool ShouldDiscard(int tierValue, int keepTier) => tierValue < keepTier;

    /// <summary>是否该撤离：剩余时间 ≤ 红线（红线启用时），或背包已满（简单规则，K3 再细化）。</summary>
    public static bool TimeToExtract(int remainingMinutes, bool backpackFull, int extractDeadlineMin)
        => backpackFull || (extractDeadlineMin > 0 && remainingMinutes <= extractDeadlineMin);
}
