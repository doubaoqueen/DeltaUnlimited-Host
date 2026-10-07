using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DeltaUnlimited.Vision.Ai;

/// <summary>ONNX 推理会话封装：GPU 优先（DirectML EP，Windows 任意 DX12 GPU 零依赖）、失败自动回退 CPU。
/// 零拷贝缓冲区 API（FixedBufferOnnxValue）+ 预热由调用方完成；线程数必须显式指定（实测小模型默认全核反而慢且 p99 抖动大）。</summary>
public sealed class OnnxRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly long[] _inputShape;
    private readonly float[] _inputBuffer;
    private readonly float[] _outputBuffer;

    /// <summary>实际生效的执行提供者（"DirectML" / "CPU"）。</summary>
    public string Provider { get; }

    public int OutputCount => _outputBuffer.Length;

    /// <param name="ep">auto = 先试 DirectML 失败回退 CPU；dml = 仅 DirectML（失败抛异常）；cpu = 仅 CPU。</param>
    public OnnxRunner(string modelPath, string ep, int threads, int inputH, int inputW)
    {
        bool auto = ep.Equals("auto", StringComparison.OrdinalIgnoreCase);
        bool wantDml = auto || ep.Equals("dml", StringComparison.OrdinalIgnoreCase);
        string provider = "CPU";
        var so = BuildOptions(threads, wantDml, auto, out bool dmlApplied);
        if (dmlApplied) provider = "DirectML";

        try
        {
            _session = new InferenceSession(modelPath, so);
        }
        catch (Exception ex) when (auto && dmlApplied)
        {
            // auto 模式：DML 会话创建也可能失败（算子不被 DML 支持/驱动异常）——回退 CPU 重建，
            // 而不是把异常抛给调用方（与"低配笔记本自动回退 CPU"的承诺一致）
            Console.WriteLine($"  ⚠️ DirectML 会话创建失败，回退 CPU：{ex.Message}");
            so = BuildOptions(threads, wantDml: false, autoFallback: true, out _);
            provider = "CPU";
            _session = new InferenceSession(modelPath, so);
        }

        Provider = provider;
        _inputNames = new[] { _session.InputNames[0] };
        _outputNames = new[] { _session.OutputNames[0] };

        _inputShape = new long[] { 1, 3, inputH, inputW };
        _inputBuffer = new float[_inputShape.Aggregate(1L, (a, b) => a * b)];
        int outCount = 3;
        var outDims = _session.OutputMetadata[_outputNames[0]].Dimensions;
        if (outDims is { Length: 2 } && outDims[1] > 0) outCount = outDims[1];
        _outputBuffer = new float[outCount];

        // 模型文件只读一次，张量包装共享输入缓冲区（零拷贝路径，与推理基准工程一致）
        InputTensor = new DenseTensor<float>(_inputBuffer, new[] { 1, 3, inputH, inputW });
        OutputTensor = new DenseTensor<float>(_outputBuffer, new[] { 1, outCount });
        InputValue = FixedBufferOnnxValue.CreateFromTensor(InputTensor);
        OutputValue = FixedBufferOnnxValue.CreateFromTensor(OutputTensor);
    }

    /// <summary>构造会话选项：wantDml=false 走纯 CPU；AppendExecutionProvider_DML 失败时
    /// autoFallback=true 降级 CPU（auto 语义），false 直接抛出（dml 严格语义）。</summary>
    private static SessionOptions BuildOptions(int threads, bool wantDml, bool autoFallback, out bool dmlApplied)
    {
        var so = new SessionOptions();
        so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        if (threads > 0) so.IntraOpNumThreads = threads;
        dmlApplied = false;
        if (!wantDml) return so;
        try
        {
            so.AppendExecutionProvider_DML(0);
            dmlApplied = true;
        }
        catch (Exception ex)
        {
            if (!autoFallback) throw; // ep=dml：严格模式，保持原语义（失败即抛）
            Console.WriteLine($"  ⚠️ DirectML(GPU) 初始化失败，回退 CPU：{ex.Message}");
        }
        return so;
    }

    private DenseTensor<float> InputTensor { get; }
    private DenseTensor<float> OutputTensor { get; }
    private FixedBufferOnnxValue InputValue { get; }
    private FixedBufferOnnxValue OutputValue { get; }

    /// <summary>填充输入缓冲（CHW float），调用 Run，返回 softmax 概率与耗时。outputs 为新数组（缓冲区复用安全）。</summary>
    public (float[] Probs, double Ms) Run(Action<float[]> fillInput)
    {
        fillInput(_inputBuffer);
        var sw = Stopwatch.StartNew();
        _session.Run(_inputNames, new[] { InputValue }, _outputNames, new[] { OutputValue });
        sw.Stop();
        return (Softmax(_outputBuffer), sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>预热（DML 首跑含着色器编译，可达数百 ms；会话创建后调用一次）。</summary>
    public void Warmup()
    {
        var noise = new Random(42);
        Run(buf =>
        {
            for (int i = 0; i < buf.Length; i++) buf[i] = noise.NextSingle();
        });
    }

    internal static float[] Softmax(float[] logits)
    {
        float max = logits[0];
        for (int i = 1; i < logits.Length; i++)
            if (logits[i] > max) max = logits[i];
        double sum = 0;
        var exps = new double[logits.Length];
        for (int i = 0; i < logits.Length; i++)
        {
            exps[i] = Math.Exp(logits[i] - max);
            sum += exps[i];
        }
        var probs = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
            probs[i] = (float)(exps[i] / sum);
        return probs;
    }

    public void Dispose()
    {
        InputValue.Dispose();
        OutputValue.Dispose();
        _session.Dispose();
    }
}
