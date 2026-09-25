using System.Collections.Concurrent;
using System.Text;

namespace DeltaUnlimited.Gui;

/// <summary>控制台分流器：接管 Console.Out/Error 的 Tee——原样写回原输出，同时把每一行推给订阅者（GUI 日志窗）。
/// 订阅前的输出进入环形缓冲（最近 N 行），首个订阅者接入时补发（不漏掉启动阶段日志）。线程安全。</summary>
public static class ConsoleRelay
{
    /// <summary>订阅前缓冲的最大行数（启动日志兜底）。</summary>
    public const int BufferLines = 200;

    private static readonly object Gate = new();
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;
    private static readonly ConcurrentQueue<string> EarlyLines = new();
    private static int _earlyCount;
    private static event Action<string>? LineWritten;

    /// <summary>接管 Console.Out/Error（幂等，可重复调用）。</summary>
    public static void Attach()
    {
        lock (Gate)
        {
            if (_originalOut is not null) return;
            _originalOut = Console.Out;
            _originalError = Console.Error;
            Console.SetOut(new TeeWriter(_originalOut, Raise));
            Console.SetError(new TeeWriter(_originalError, Raise));
        }
    }

    /// <summary>订阅日志行；接入时补发启动缓冲。</summary>
    public static void Subscribe(Action<string> handler)
    {
        lock (Gate)
        {
            while (EarlyLines.TryDequeue(out var line)) handler(line);
            _earlyCount = 0;
            LineWritten += handler;
        }
    }

    public static void Unsubscribe(Action<string> handler)
    {
        lock (Gate) LineWritten -= handler;
    }

    private static void Raise(string line)
    {
        lock (Gate)
        {
            if (LineWritten is null)
            {
                EarlyLines.Enqueue(line);
                if (++_earlyCount > BufferLines)
                {
                    EarlyLines.TryDequeue(out _);
                    _earlyCount--;
                }
                return;
            }
            LineWritten(line);
        }
    }

    /// <summary>原输出照写 + 行事件；只对 WriteLine 推事件（整行日志），零散 Write 不回灌 GUI。</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly Action<string> _raise;

        public TeeWriter(TextWriter inner, Action<string> raise)
        {
            _inner = inner;
            _raise = raise;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void WriteLine(string? value)
        {
            try { _inner.WriteLine(value); } catch { }
            if (value is not null) _raise(value);
        }

        public override void WriteLine() { try { _inner.WriteLine(); } catch { } }

        public override void Write(string? value) { try { _inner.Write(value); } catch { } }

        public override void Write(char value) { try { _inner.Write(value); } catch { } }
    }
}
