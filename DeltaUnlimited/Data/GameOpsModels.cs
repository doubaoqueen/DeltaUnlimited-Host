using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>游戏操作表 data/game_ops.json：键位映射 + 复合动作。</summary>
public sealed class GameOpsTable
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("分辨率")]
    public ResolutionInfo? Resolution { get; set; }

    [JsonPropertyName("说明")]
    public string? Note { get; set; }

    /// <summary>未映射字段兜底收纳（如"界面坐标点说明"“保留备查”段），不再被 System.Text.Json 静默丢弃（评审 P3-8）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>操作名 -> 实际按键，如 "interact" -> "f"。</summary>
    [JsonPropertyName("按键映射")]
    public Dictionary<string, string> KeyMap { get; set; } = new();

    [JsonPropertyName("复合动作")]
    public Dictionary<string, GameAction> Actions { get; set; } = new();
}

public sealed class ResolutionInfo
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

/// <summary>复合动作：一串步骤，如 pick_up（移动到目标 + 按交互键）。</summary>
public sealed class GameAction
{
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("steps")]
    public List<GameActionStep> Steps { get; set; } = new();
}

public sealed class GameActionStep
{
    /// <summary>move_to | key | key_combo | key_down | key_up | move_hold | wait</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    /// <summary>按键：操作名（如 "interact"，运行时经 KeyMap 解析）或具体键名。</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("keys")]
    public List<string>? Keys { get; set; }

    /// <summary>坐标（支持 "target.x" 这类引用）。</summary>
    [JsonPropertyName("x")]
    public string? X { get; set; }

    [JsonPropertyName("y")]
    public string? Y { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("seconds")]
    public double? Seconds { get; set; }
}
