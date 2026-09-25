namespace DeltaUnlimited.Cli;

/// <summary>链路因急停/人工中止而停止（不是错误）：命令层捕获后优雅收尾（释放按键+打印停止提示）。</summary>
public sealed class ChainStoppedException : Exception
{
    public ChainStoppedException() : base("链路已停止（急停/人工中止）") { }
}
