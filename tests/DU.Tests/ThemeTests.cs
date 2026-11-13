using System.Drawing;
using DeltaUnlimited.Gui;
using Xunit;

namespace DU.Tests;

/// <summary>主题调色板测试：两套主题角色齐全、深浅互异、正文/底色可区分。
/// 可读性是硬约束——"黑字压深底"那种不可读配色不允许再出现。</summary>
public class ThemeTests
{
    public static TheoryData<Theme> AllThemes() => new() { Theme.Light, Theme.Dark };

    [Fact]
    public void LightAndDark_AreDistinct()
    {
        Assert.False(Theme.Light.IsDark);
        Assert.True(Theme.Dark.IsDark);
        Assert.NotEqual(Theme.Light.Window, Theme.Dark.Window);
        Assert.NotEqual(Theme.Light.Text, Theme.Dark.Text);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AllRolesFilled_NoEmptyColor(Theme t)
    {
        foreach (var prop in typeof(Theme).GetProperties())
        {
            if (prop.PropertyType != typeof(Color)) continue;
            var v = (Color)prop.GetValue(t)!;
            Assert.True(!v.IsEmpty, $"主题 {t.Name} 的角色 {prop.Name} 是空色");
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TextVsBackground_Readable(Theme t)
    {
        // 正文/底色必须互异（可读性的底线；黑白反转也算"互异"）
        Assert.NotEqual(t.Text, t.Window);
        Assert.NotEqual(t.Text, t.Card);
        Assert.NotEqual(t.LogText, t.LogBack);
        Assert.NotEqual(t.AccentText, t.Accent);
    }
}
