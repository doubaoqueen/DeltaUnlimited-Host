using System.Drawing;

namespace DeltaUnlimited.Gui;

/// <summary>主题调色板：按"角色"命名颜色（窗底/面板/卡片/正文/弱文字/强调/危险/日志底/日志字/边框/输入底），
/// 深浅两套。MainForm.ApplyTheme 按角色给控件上色——切主题 = 换调色板再刷一遍，控件结构不动。
/// 可读性是硬约束：两种主题下正文与底色都要高对比（浅色 #1A1A1A on #F3F3F3 / 深色 #F2F2F2 on #202020）。</summary>
public sealed record Theme(
    string Name,
    bool IsDark,
    Color Window,
    Color Panel,
    Color Card,
    Color Text,
    Color TextMuted,
    Color Accent,
    Color AccentText,
    Color Danger,
    Color LogBack,
    Color LogText,
    Color Border,
    Color FieldBack)
{
    /// <summary>浅色 · Win11 风：浅灰窗底 + 白卡片 + Win11 蓝强调；日志窗保留深色终端风。</summary>
    public static Theme Light { get; } = new(
        Name: "浅色 · Win11",
        IsDark: false,
        Window: Color.FromArgb(243, 243, 243),
        Panel: Color.FromArgb(249, 250, 251),
        Card: Color.FromArgb(255, 255, 255),
        Text: Color.FromArgb(26, 26, 26),
        TextMuted: Color.FromArgb(96, 96, 96),
        Accent: Color.FromArgb(0, 103, 192),
        AccentText: Color.FromArgb(255, 255, 255),
        Danger: Color.FromArgb(196, 43, 28),
        LogBack: Color.FromArgb(30, 30, 34),
        LogText: Color.FromArgb(232, 232, 236),
        Border: Color.FromArgb(210, 212, 216),
        FieldBack: Color.FromArgb(255, 255, 255));

    /// <summary>深色：全窗深灰 + 亮字，日志窗更深一档做区分；强调色换亮蓝保证在深底上醒目。</summary>
    public static Theme Dark { get; } = new(
        Name: "深色",
        IsDark: true,
        Window: Color.FromArgb(32, 32, 32),
        Panel: Color.FromArgb(43, 43, 43),
        Card: Color.FromArgb(51, 51, 53),
        Text: Color.FromArgb(242, 242, 242),
        TextMuted: Color.FromArgb(165, 165, 165),
        Accent: Color.FromArgb(76, 194, 255),
        AccentText: Color.FromArgb(10, 32, 44),
        Danger: Color.FromArgb(255, 107, 99),
        LogBack: Color.FromArgb(22, 22, 25),
        LogText: Color.FromArgb(232, 232, 236),
        Border: Color.FromArgb(72, 72, 74),
        FieldBack: Color.FromArgb(38, 38, 41));
}
