namespace DeltaUnlimited.Overlay;

/// <summary>状态日志：所有命令把"做了什么/识别到什么"追加到 logs/status.log（悬浮面板的数据源）。</summary>
public static class StatusLog
{
    public static void Append(string repoRoot, string message)
    {
        try
        {
            string dir = Path.Combine(repoRoot, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "status.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
