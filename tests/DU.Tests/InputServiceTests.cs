using DeltaUnlimited.Input;
using Xunit;

namespace DU.Tests;

/// <summary>输入层纯逻辑测试：按键名 → 虚拟键码映射（不涉及任何系统调用）。</summary>
public class InputServiceTests
{
    [Theory]
    [InlineData("w", 0x57)]
    [InlineData("W", 0x57)]
    [InlineData("3", 0x33)]
    [InlineData("space", 0x20)]
    [InlineData("shift", 0x10)]
    [InlineData("ctrl", 0x11)]
    [InlineData("alt", 0x12)]
    [InlineData("capslock", 0x14)]
    [InlineData("=", 0xBB)]
    [InlineData("[", 0xDB)]
    [InlineData("f1", 0x70)]
    [InlineData("f12", 0x7B)]
    [InlineData("enter", 0x0D)]
    [InlineData("esc", 0x1B)]
    [InlineData("tab", 0x09)]
    public void MapKeyName_MapsKnownKeys(string key, ushort expectedVk)
        => Assert.Equal(expectedVk, InputService.MapKeyName(key));

    [Fact]
    public void MapKeyName_UnknownKey_ReturnsZero()
        => Assert.Equal((ushort)0, InputService.MapKeyName("不存在的键名"));
}
