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
        var so = new SessionOptions();
        so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        if (threads > 0) so.IntraOpNumThreads = threads;

        string provider = "CPU";
        if (ep.Equals("auto", StringComparison.OrdinalIgnoreCase) || ep.Equals("dml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                so.AppendExecutionProvider_DML(0);
                provider = "DirectML";
            }
            catch (Exception ex) when (ep.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  ⚠️ DirectML(GPU) 初始化失败，回退 CPU：{ex.Message}");
            }
        }

        _session = new InferenceSession(modelPath, so);
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
