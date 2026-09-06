using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>顺序链路 workflows/*.json —— 最小顺序 runner 的输入格式（节点图引擎的前身）。</summary>
public sealed class Chain
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    [JsonPropertyName("steps")]
    public List<ChainStep> Steps { get; set; } = new();
}

/// <summary>单步操作：key 按键 / click_element 点元素 / wait 等待 / pause 人工确认点。</summary>
public sealed class ChainStep
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("element")]
    public string? Element { get; set; }

    [JsonPropertyName("wait_ms")]
    public int? WaitMs { get; set; }

    [JsonPropertyName("capture")]
    public string? Capture { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    [JsonPropertyName("jump_to")]
    public string? JumpTo { get; set; }

    [JsonPropertyName("screen")]
    public string? Screen { get; set; }

    /// <summary>click_element 专用：点击后必须进入的目标界面（配合 timeout_ms 轮询验证，防止"点了就当成功"）。</summary>
    [JsonPropertyName("expect_screen")]
    public string? ExpectScreen { get; set; }

    /// <summary>expect_screen 轮询超时（毫秒），默认 8000。</summary>
    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; set; }

    /// <summary>额外重试次数（总点击次数 = retries + 1），默认 0。</summary>
    [JsonPropertyName("retries")]
    public int? Retries { get; set; }
}
