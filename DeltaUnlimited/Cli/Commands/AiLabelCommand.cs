using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DeltaUnlimited.Data;
using DeltaUnlimited.Gui;
using DeltaUnlimited.Overlay;
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
        int totalRows = store.RowCount;

        // 出帧策略（只影响标注顺序/可见集合，不改数据）
        bool onlyUsable = cmdArgs.Any(a => a.Equals("--only-usable", StringComparison.OrdinalIgnoreCase));
        bool goldFirst = cmdArgs.Any(a => a.Equals("--gold-first", StringComparison.OrdinalIgnoreCase));
        var (kept, filtered, goldMoved) = store.ApplyView(onlyUsable, goldFirst);

        int start = ResolveStart(store, cmdArgs);
        Console.WriteLine($"AI 标注器：共 {store.RowCount} 帧，未标 {store.UnlabeledCount}（{store.RecordDir}）");
        if (onlyUsable || goldFirst)
            Console.WriteLine($"  🎯 出帧策略：{(onlyUsable ? "--only-usable" : "")}{(onlyUsable && goldFirst ? " + " : "")}{(goldFirst ? "--gold-first" : "")}"
                              + $"（视图 {kept}/{totalRows} 帧"
                              + (onlyUsable ? $"，VLM 判定淘汰已过滤 {filtered} 帧" : "")
                              + (goldFirst ? $"，金帧前移 {goldMoved} 帧" : "") + "；数据未改动）");
        Console.WriteLine(store.PrescreenedCount > 0
            ? $"  🤖 已载入 VLM 初筛结果 {store.PrescreenedCount} 条——标注时右侧会显示该帧的 VLM 判断（参考，不是标签）"
            : "  ℹ 未找到 VLM 初筛结果（ai-training/datasets/record/prescreen.csv）——标注时该区显示\"无初筛记录\"");
        Console.WriteLine($"  启动位置：第 {start + 1} 帧（--start unlabeled=第一个未标（缺省）/ first=从头复览 / 行号=指定行，按窗口 [N/M] 的 1 基行号）");
        if (store.MissingCount > 0)
            Console.WriteLine($"⚠ {store.MissingCount} 帧清单有记录但全帧文件已不在（可能被手动清理）——窗口内按 M 一键丢弃，或 X 逐张丢弃");
        Console.WriteLine("窗口内按键：A/S/D/F=可通行/不可通行/无地面/背景 X=丢弃 M=清理缺失帧 ←→=翻张 N=下一个未标 Esc=退出（1/2/0/B 兼容）");

        // 控制台入口线程是 MTA，WinForms 必须在专用 STA 线程上跑消息循环（同 GuiApp 模式）
        var ui = new Thread(() =>
        {
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
            catch { /* 已被其它组件设置则忽略 */ }
            Application.Run(new AiLabelForm(store, start));
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();

        Console.WriteLine($"🏁 标注会话结束：未标剩余 {store.UnlabeledCount} / 共 {store.RowCount}");
        Console.WriteLine("   训练清单分布: " + string.Join(" ", store.TrainCounts.Select(kv => $"{kv.Key}:{kv.Value}")));
        Console.WriteLine("   下一步: python src/train.py --data datasets/passability --epochs 30（ai-training/ 下）");
    }

    /// <summary>解析 --start 起点：unlabeled（缺省，第一个未标；全标完则落回复览）/
    /// first（第一个文件完好的帧，从头过一遍）/ 数字（1 基行号，同窗口 [N/M] 显示）。</summary>
    internal static int ResolveStart(AiLabelStore store, string[] cmdArgs)
    {
        string mode = "unlabeled";
        for (int i = 0; i < cmdArgs.Length - 1; i++)
            if (cmdArgs[i] == "--start") mode = cmdArgs[i + 1].ToLowerInvariant();
        switch (mode)
        {
            case "unlabeled":
                int unl = store.FirstUnlabeled();
                return unl >= 0 ? unl : Math.Max(0, store.FirstViewable());
            case "first":
                return Math.Max(0, store.FirstViewable());
            default:
                if (int.TryParse(mode, out int n))
                    return Math.Clamp(n - 1, 0, Math.Max(0, store.RowCount - 1));
                throw new ArgumentException($"未知 --start 值: {mode}（可用 unlabeled / first / 行号数字）");
        }
    }
}

/// <summary>图像辅助：加载/画 ROI 框/裁 ROI/Mat→Bitmap。集中在此，窗体只接触 Bitmap。</summary>
internal static class AiLabelImaging
{
    /// <summary>ImDecode 走 .NET IO 读字节，规避 imread 对非 ASCII 路径的编码问题；解码失败/空图一律返回 null。</summary>
    public static Mat? LoadFrame(string absPath)
    {
        if (!File.Exists(absPath)) return null;
        var m = Cv2.ImDecode(File.ReadAllBytes(absPath), ImreadModes.Color);
        return m.Empty() ? null : m;
    }

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
    private readonly List<RecordRow> _allRows = new();    // 数据真身（写回清单/统计用，出帧策略不动它）
    private readonly List<RecordRow> _rows = new();        // 当前出帧视图（导航用；元素与 _allRows 是同一批对象）
    private readonly List<string> _trainLines = new();     // 训练 manifest 原始行（含表头）
    private readonly Dictionary<string, string> _trainByRecord = new(); // record relpath → 训练 ROI relpath
    private Dictionary<string, PrescreenRow> _prescreen = new();        // VLM 初筛结果（参考，非标签）
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

    /// <summary>已载入的 VLM 初筛结果条数（启动时打印，便于确认"标注时能看到 VLM 判断"是否生效）。</summary>
    public int PrescreenedCount => _prescreen.Count;

    /// <summary>该帧的 VLM 初筛结果（无记录返回 null）。**参考信息，不是标签**。</summary>
    public PrescreenRow? PrescreenFor(int index)
    {
        if (index < 0 || index >= _rows.Count) return null;
        return _prescreen.TryGetValue(PrescreenLookup.Normalize(_rows[index].RelPath), out var row) ? row : null;
    }
    public IReadOnlyDictionary<string, int> TrainCounts { get; private set; } = new Dictionary<string, int>();

    /// <summary>清单有记录但全帧文件已不在磁盘的数量（可能被手动清理；这类帧无法打标，只能丢弃或忽略）。</summary>
    public int MissingCount
    {
        get
        {
            int n = 0;
            foreach (var r in _allRows)
                if (!File.Exists(Path.Combine(_recordDir, r.RelPath))) n++;
            return n;
        }
    }

    public bool FileExists(int index)
        => index >= 0 && index < _rows.Count && File.Exists(Path.Combine(_recordDir, _rows[index].RelPath));

    /// <summary>第一个文件完好的行号；全缺失返回 -1。</summary>
    public int FirstViewable()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (FileExists(i)) return i;
        return -1;
    }

    /// <summary>第一个未标注且文件完好的行号（续标起点）；没有则第一个未标注行（哪怕文件缺失）；
    /// 全部标完返回 -1（调用方落到复览模式）。</summary>
    public int FirstUnlabeled()
    {
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Label.Length != 0) continue;
                if (pass == 0 ? FileExists(i) : !FileExists(i)) return i;
            }
        return -1;
    }

    /// <summary>一键丢弃所有"未标注且文件缺失"的帧（已标注的帧其 ROI 已在训练清单里，不动）。返回处理数量。
    /// 作用在数据真身（_allRows）上，与出帧视图无关。</summary>
    public int DiscardMissing()
    {
        int n = 0;
        for (int i = 0; i < _allRows.Count; i++)
        {
            var row = _allRows[i];
            if (row.Label.Length != 0) continue;
            if (File.Exists(Path.Combine(_recordDir, row.RelPath))) continue;
            if (SetLabel(row, "discard")) n++;
        }
        return n;
    }

    /// <summary>出帧视图（只影响标注顺序与可见集合，**不改任何数据**）：
    /// onlyUsable = 只保留"VLM 判定可用"或"无初筛记录"的帧（判定淘汰的帧不出）；
    /// goldFirst = 金帧（敌人/物资信号/交互提示）排到最前面。返回 (视图帧数, 被过滤掉, 前移的金帧数)。</summary>
    public (int Kept, int Filtered, int GoldMoved) ApplyView(bool onlyUsable, bool goldFirst)
    {
        int filtered = 0, goldMoved = 0;
        if (onlyUsable)
        {
            var kept = new List<RecordRow>();
            foreach (var r in _rows)
            {
                var ps = PrescreenOf(r);
                if (ps is { Judged: true } && !ps.RoiUsableTrue) { filtered++; continue; }
                kept.Add(r);
            }
            _rows.Clear();
            _rows.AddRange(kept);
        }
        if (goldFirst)
        {
            var gold = _rows.Where(r => PrescreenOf(r) is { Judged: true } ps && ps.IsGoldFrame).ToList();
            var rest = _rows.Where(r => !(PrescreenOf(r) is { Judged: true } ps && ps.IsGoldFrame)).ToList();
            goldMoved = gold.Count;
            _rows.Clear();
            _rows.AddRange(gold);
            _rows.AddRange(rest);
        }
        return (RowCount, filtered, goldMoved);
    }

    private PrescreenRow? PrescreenOf(RecordRow row)
        => _prescreen.TryGetValue(PrescreenLookup.Normalize(row.RelPath), out var v) ? v : null;

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
                store._allRows.Add(new RecordRow { RelPath = p[0], Label = p[1], Map = p[2], Season = p[3], Created = p[4] });
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

        // VLM 初筛结果（prescreen.csv）：标注时作为**参考**展示，绝不作为标签（见 docs/AI初筛操作手册.md §0）
        store._prescreen = PrescreenLookup.Load(repoRoot);

        // 出帧视图初始 = 全量（调用方按 --only-usable / --gold-first 再收窄，数据真身不受影响）
        store._rows.AddRange(store._allRows);

        store.Recount();
        return store;
    }

    /// <summary>打开某索引的全帧（先关旧帧）。返回文件是否存在且可解码。</summary>
    public bool Open(int index)
    {
        _frame?.Dispose();
        _frame = null;
        if (index < 0 || index >= _rows.Count) return false;
        try
        {
            _frame = AiLabelImaging.LoadFrame(Path.Combine(_recordDir, _rows[index].RelPath));
        }
        catch (Exception ex)
        {
            Logger.Warn($"AI 标注器：帧加载异常 {ex.Message}");
            _frame = null;
        }
        return _frame is not null;
    }

    /// <summary>当前帧整图 + ROI 绿框（展示用）。渲染异常返回 null，绝不抛到 UI 线程。</summary>
    public Bitmap? RenderFull()
    {
        if (_frame is null) return null;
        try { return AiLabelImaging.ToBitmap(AiLabelImaging.DrawRoiBox(_frame, _roi)); }
        catch (Exception ex) { Logger.Warn($"AI 标注器：整图渲染失败 {ex.Message}"); return null; }
    }

    /// <summary>当前帧的 ROI 裁剪（模型实际视野）。渲染异常返回 null。</summary>
    public Bitmap? RenderRoi()
    {
        if (_frame is null) return null;
        try
        {
            using var crop = new Mat(_frame, _roi).Clone();
            return AiLabelImaging.ToBitmap(crop);
        }
        catch (Exception ex) { Logger.Warn($"AI 标注器：ROI 渲染失败 {ex.Message}"); return null; }
    }

    /// <summary>打标/复标（使用当前打开的帧裁 ROI）。返回是否发生了修改。
    /// 顺序上先做可能失败的写盘（新 ROI 落盘），成功后才改清单——失败即无副作用，不留半截状态。</summary>
    public bool SetLabel(int index, string label)
    {
        if (index < 0 || index >= _rows.Count) return false;
        return SetLabel(_rows[index], label);
    }

    /// <summary>按行对象打标（出帧视图过滤后仍作用于同一批数据对象）。</summary>
    public bool SetLabel(RecordRow row, string label)
    {
        if (row is null) return false;
        if (row.Label == label) return false;
        bool oldIsTrain = TrainLabels.Contains(row.Label);
        bool newIsTrain = TrainLabels.Contains(label);
        if (newIsTrain && _frame is null) return false;

        // 1) 新 ROI 落盘（discard 不进训练清单，无需裁图）
        string? trainRel = null;
        if (newIsTrain)
        {
            try
            {
                string relDir = Path.Combine("images", label);
                Directory.CreateDirectory(Path.Combine(_passabilityDir, relDir));
                string file = $"roi_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                while (File.Exists(Path.Combine(_passabilityDir, relDir, file)))
                    file = $"roi_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Random.Shared.Next(100, 999)}.jpg";
                trainRel = Path.Combine(relDir, file);
                using var crop = new Mat(_frame!, _roi).Clone();
                if (!crop.ImWrite(Path.Combine(_passabilityDir, trainRel),
                        new[] { (int)ImwriteFlags.JpegQuality, _cfg.Log.RoiJpegQuality }))
                    throw new IOException($"ROI 保存失败: {trainRel}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"AI 标注器：打标写盘失败，标签未变更（{ex.Message}）");
                return false;
            }
        }

        // 2) 清旧：复标/改丢弃时，删训练清单旧行 + 旧 ROI 文件
        if (oldIsTrain && _trainByRecord.TryGetValue(row.RelPath, out var oldTrainRel))
        {
            _trainLines.RemoveAll(l => l.StartsWith(oldTrainRel + ",", StringComparison.Ordinal));
            File.Delete(Path.Combine(_passabilityDir, oldTrainRel));
            _trainByRecord.Remove(row.RelPath);
        }

        // 3) 记新：追加训练清单 + 映射 + 行标签 + 落盘
        if (trainRel is not null)
        {
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
        // ⚠️ 必须写"数据真身"（_allRows）：出帧视图可能被 --only-usable/--gold-first 过滤或重排，
        // 若按视图写回会把被过滤的帧从清单里删掉（数据丢失）。
        var sb = new StringBuilder("relpath,label,map,season,created\n");
        foreach (var r in _allRows)
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
