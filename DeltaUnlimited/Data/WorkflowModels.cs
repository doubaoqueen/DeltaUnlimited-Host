using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaUnlimited.Data;

/// <summary>节点图 workflows/*.json —— 节点图引擎输入格式（引擎待实现，当前由顺序链路 chain 替代）。</summary>
public sealed class Workflow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>节点 id -> 定义。</summary>
    [JsonPropertyName("nodes")]
    public Dictionary<string, WorkflowNode> Nodes { get; set; } = new();

    [JsonPropertyName("edges")]
    public List<WorkflowEdge> Edges { get; set; } = new();
}

public sealed class WorkflowNode
{
    /// <summary>节点类型名，如 Log / IfNode / DetectElement。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>节点参数（运行时按类型解释，保留 JsonElement 保持灵活）。</summary>
    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement> Params { get; set; } = new();
}

public sealed class WorkflowEdge
{
    /// <summary>"n1"（默认 done 端口）或 "n5.true"（指定端口）。</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";
}
