namespace DeltaUnlimited.Overlay;

/// <summary>
/// 统一日志：同时输出控制台与按日滚动的日志文件（logs/status_yyyyMMdd.log）。
/// 悬浮面板读取最新的日志文件（LatestLogPath）。
/// </summary>
public static class Logger
{
    private static string _dir = "";

    /// <summary>初始化日志目录（启动时调用一次）。</summary>
    public static void Init(string repoRoot)
    {
        _dir = Path.Combine(repoRoot, "logs");
        try { Directory.CreateDirectory(_dir); } catch { /* 日志目录创建失败不致命 */ }
    }

    /// <summary>最新日志文件路径（无日志时返回空串）。</summary>
    public static string LatestLogPath()
    {
        if (_dir == "" || !Directory.Exists(_dir)) return "";
        var newest = Directory.GetFiles(_dir, "status_*.log")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .FirstOrDefault();
        return newest ?? "";
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        Console.WriteLine(line);
        try
        {
            if (_dir != "")
                File.AppendAllText(Path.Combine(_dir, $"status_{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch
        {
            // 写文件失败不影响主流程
        }
    }
}
