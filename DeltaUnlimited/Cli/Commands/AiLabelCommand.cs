using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DeltaUnlimited.Data;
using DeltaUnlimited.Gui;
using OpenCvSharp;

namespace DeltaUnlimited.Cli.Commands;

/// <summary>ailabel：录制素材事后标注器（图形界面）。与 airecord 配对，采集/标注分离：
/// 逐张显示 airecord 存的全帧（绿框标出可通行性 ROI），按 1/2/0/b 打标 → 裁 ROI 存入
/// passability/images/<标签>/ 并写回训练 manifest（source=record，与 aicollect 同格式）；
/// X 丢弃坏帧。每次打标即时落盘，无需保存；支持回翻复标（自动搬移旧 ROI 文件并改写清单）。</summary>
public static class AiLabelCommand
{
    public static void Run(DataStore data, string repoRoot, string[] cmdArgs)
    {
        var runtime = data.LoadRuntime();
        var cfg = data.LoadAiVision();
        using var store = AiLabelStore.Load(repoRoot, cfg, runtime.DesignWidth, runtime.DesignHeight);
        Console.WriteLine($"AI 标注器：共 {store.RowCount} 帧，未标 {store.UnlabeledCount}（{store.RecordDir}）");
        Console.WriteLine("窗口内按键：1=可通行 2=不可通行 0=无地面 B=背景 X=丢弃 ←→=翻张 N=下一个未标 Esc=退出");

        // 控制台入口线程是 MTA，WinForms 必须在专用 STA 线程上跑消息循环（同 GuiApp 模式）
        var ui = new Thread(() =>
        {
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
            catch { /* 已被其它组件设置则忽略 */ }
            Application.Run(new AiLabelForm(store));
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();

        Console.WriteLine($"🏁 标注会话结束：未标剩余 {store.UnlabeledCount} / 共 {store.RowCount}");
        Console.WriteLine("   训练清单分布: " + string.Join(" ", store.TrainCounts.Select(kv => $"{kv.Key}:{kv.Value}")));
        Console.WriteLine("   下一步: python src/train.py --data datasets/passability --epochs 30（ai-training/ 下）");
    }
}

/// <summary>图像辅助：加载/画 ROI 框/裁 ROI/Mat→Bitmap。集中在此，窗体只接触 Bitmap。</summary>
internal static class AiLabelImaging
{
    /// <summary>ImDecode 走 .NET IO 读字节，规避 imread 对非 ASCII 路径的编码问题。</summary>
    public static Mat? LoadFrame(string absPath)
        => File.Exists(absPath) ? Cv2.ImDecode(File.ReadAllBytes(absPath), ImreadModes.Color) : null;

    public static Rect RoiRect(AiVisionConfig cfg, int designW, int designH)
    {
        var (x, y, w, h) = AiCollectCommand.ClampRoi(cfg.Roi, designW, designH);
        return new Rect(x, y, w, h);
    }

    public static Mat DrawRoiBox(Mat frame, Rect roi)
    {
        var m = frame.Clone();
        Cv2.Rectangle(m, roi, new Scalar(80, 255, 80), 2);
        return m;
    }

    public static Bitmap ToBitmap(Mat bgr)
    {
        Cv2.ImEncode(".jpg", bgr, out byte[] jpeg, new[] { (int)ImwriteFlags.JpegQuality, 92 });
        return new Bitmap(new MemoryStream(jpeg));
    }
}

/// <summary>标注会话：录制清单（record_manifest.csv）+ 训练清单（passability/manifest.csv）+
/// 录制↔训练行映射（label_map.csv，复标时据此删旧行删旧图）+ 当前帧缓存。
/// 所有修改即时全量落盘（清单都是小文本）；窗体只接触索引与 Bitmap，不碰 OpenCvSharp。</summary>
internal sealed class AiLabelStore : IDisposable
{
    /// <summary>会写入训练清单的标签（background 也入清单，dataset.py 按非三分类自动跳过，与 aicollect 一致）。</summary>
    internal static readonly HashSet<string> TrainLabels = new() { "passable", "blocked", "no_ground", "background" };

    public sealed class RecordRow
    {
        public string RelPath = "";
        public string Label = "";   // ""=未标；passable/blocked/no_ground/background/discard
        public string Map = "";
        public string Season = "";
        public string Created = "";
    }

    private readonly string _recordDir;        // ai-training/datasets/record
    private readonly string _passabilityDir;   // ai-training/datasets/passability
    private readonly AiVisionConfig _cfg;
    private readonly Rect _roi;
    private readonly List<RecordRow> _rows = new();
    private readonly List<string> _trainLines = new();     // 训练 manifest 原始行（含表头）
    private readonly Dictionary<string, string> _trainByRecord = new(); // record relpath → 训练 ROI relpath
    private Mat? _frame;                                   // 当前打开的全帧（窗体一次只看一张）

    private AiLabelStore(string recordDir, string passabilityDir, AiVisionConfig cfg, Rect roi)
    {
        _recordDir = recordDir;
        _passabilityDir = passabilityDir;
        _cfg = cfg;
        _roi = roi;
    }

    public string RecordDir => _recordDir;
    public IReadOnlyList<RecordRow> Rows => _rows;
    public int RowCount => _rows.Count;
    public int UnlabeledCount => _rows.Count(r => r.Label.Length == 0);
    public bool HasFrame => _frame is not null;
    public IReadOnlyDictionary<string, int> TrainCounts { get; private set; } = new Dictionary<string, int>();

    public static AiLabelStore Load(string repoRoot, AiVisionConfig cfg, int designW, int designH)
    {
        string recordDir = Path.Combine(repoRoot, "ai-training", "datasets", "record");
        string passabilityDir = Path.Combine(repoRoot, "ai-training", "datasets", "passability");
        Directory.CreateDirectory(recordDir);
        Directory.CreateDirectory(passabilityDir);

        var store = new AiLabelStore(recordDir, passabilityDir, cfg, AiLabelImaging.RoiRect(cfg, designW, designH));

        // 录制清单：表头 relpath,label,map,season,created
        string recManifest = Path.Combine(recordDir, "record_manifest.csv");
        if (File.Exists(recManifest))
        {
            foreach (var line in File.ReadAllLines(recManifest).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length < 5) continue; // 空行/残行跳过
                store._rows.Add(new RecordRow { RelPath = p[0], Label = p[1], Map = p[2], Season = p[3], Created = p[4] });
            }
        }

        // 训练清单：表头 relpath,label,map,season,source,created（与 aicollect 一致）
        string trainManifest = Path.Combine(passabilityDir, "manifest.csv");
        if (!File.Exists(trainManifest))
            File.WriteAllText(trainManifest, "relpath,label,map,season,source,created\n");
        store._trainLines.AddRange(File.ReadAllLines(trainManifest).Where(l => l.Length > 0));

        // 映射表：record relpath → 训练 ROI relpath（复标时删旧行旧图用）
        string mapPath = Path.Combine(recordDir, "label_map.csv");
        if (File.Exists(mapPath))
        {
            foreach (var line in File.ReadAllLines(mapPath).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length >= 2) store._trainByRecord[p[0]] = p[1];
            }
        }

        store.Recount();
        return store;
    }

    /// <summary>打开某索引的全帧（先关旧帧）。返回文件是否存在。</summary>
    public bool Open(int index)
    {
        _frame?.Dispose();
        _frame = null;
        if (index < 0 || index >= _rows.Count) return false;
        _frame = AiLabelImaging.LoadFrame(Path.Combine(_recordDir, _rows[index].RelPath));
        return _frame is not null;
    }

    /// <summary>当前帧整图 + ROI 绿框（展示用）。</summary>
    public Bitmap? RenderFull()
        => _frame is null ? null : AiLabelImaging.ToBitmap(AiLabelImaging.DrawRoiBox(_frame, _roi));

    /// <summary>当前帧的 ROI 裁剪（模型实际视野）。</summary>
    public Bitmap? RenderRoi()
    {
        if (_frame is null) return null;
        using var crop = new Mat(_frame, _roi).Clone();
        return AiLabelImaging.ToBitmap(crop);
    }

    /// <summary>打标/复标（使用当前打开的帧裁 ROI）。返回是否发生了修改。</summary>
    public bool SetLabel(int index, string label)
    {
        if (index < 0 || index >= _rows.Count) return false;
        var row = _rows[index];
        if (row.Label == label) return false;
        bool oldIsTrain = TrainLabels.Contains(row.Label);
        bool newIsTrain = TrainLabels.Contains(label);
        if (newIsTrain && _frame is null) return false; // 全帧缺失无法裁 ROI，先于清旧判断，保证失败不留半截状态

        // 1) 清旧：复标/改丢弃时，删训练清单旧行 + 旧 ROI 文件
        if (oldIsTrain && _trainByRecord.TryGetValue(row.RelPath, out var oldTrainRel))
        {
            _trainLines.RemoveAll(l => l.StartsWith(oldTrainRel + ",", StringComparison.Ordinal));
            File.Delete(Path.Combine(_passabilityDir, oldTrainRel));
            _trainByRecord.Remove(row.RelPath);
        }

        // 2) 写新：裁 ROI 存盘 + 追加训练清单 + 记映射（discard 不进训练清单）
        if (newIsTrain)
        {
            string relDir = Path.Combine("images", label);
            Directory.CreateDirectory(Path.Combine(_passabilityDir, relDir));
            string file = $"roi_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
            while (File.Exists(Path.Combine(_passabilityDir, relDir, file)))
                file = $"roi_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Random.Shared.Next(100, 999)}.jpg";
            string trainRel = Path.Combine(relDir, file);
            using var crop = new Mat(_frame!, _roi).Clone();
            if (!crop.ImWrite(Path.Combine(_passabilityDir, trainRel),
                    new[] { (int)ImwriteFlags.JpegQuality, _cfg.Log.RoiJpegQuality }))
                throw new IOException($"ROI 保存失败: {trainRel}");
            _trainLines.Add($"{trainRel},{label},{row.Map},{row.Season},record,{row.Created}");
            _trainByRecord[row.RelPath] = trainRel;
        }

        row.Label = label;
        Flush();
        Recount();
        return true;
    }

    private void Recount()
    {
        TrainCounts = _trainLines.Skip(1)
            .Select(l => l.Split(','))
            .Where(p => p.Length >= 2)
            .GroupBy(p => p[1])
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private void Flush()
    {
        var sb = new StringBuilder("relpath,label,map,season,created\n");
        foreach (var r in _rows)
            sb.Append(r.RelPath).Append(',').Append(r.Label).Append(',')
              .Append(r.Map).Append(',').Append(r.Season).Append(',').Append(r.Created).Append('\n');
        File.WriteAllText(Path.Combine(_recordDir, "record_manifest.csv"), sb.ToString());

        File.WriteAllLines(Path.Combine(_passabilityDir, "manifest.csv"), _trainLines);

        var mb = new StringBuilder("record_relpath,train_relpath\n");
        foreach (var kv in _trainByRecord)
            mb.Append(kv.Key).Append(',').Append(kv.Value).Append('\n');
        File.WriteAllText(Path.Combine(_recordDir, "label_map.csv"), mb.ToString());
    }

    public void Dispose() => _frame?.Dispose();
}
