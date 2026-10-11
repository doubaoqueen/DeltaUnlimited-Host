using OpenCvSharp;

namespace DU.Tests;

/// <summary>fixtures 帧回归管线（跑刀工程提案 §6）：从 tests/fixtures/ 读取本地截图喂感知函数。
/// 素材由用户 K0 采集后人工放入（tests/fixtures/ 已在 gitignore，不进库；见 tests/fixtures/README.md）；
/// 无素材时相关测试应显式跳过而不是失败——本类提供 Has/Load 供测试自行判断。</summary>
public static class FixtureFrames
{
    /// <summary>fixtures 目录（bin 输出下随构建复制；源目录 tests/fixtures/）。</summary>
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg" };

    /// <summary>按名字（不带扩展名）找 fixture 文件，不存在返回 null。</summary>
    public static string? Find(string name)
    {
        foreach (var ext in Exts)
        {
            var p = Path.Combine(Dir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>该名字的 fixture 是否存在。</summary>
    public static bool Has(string name) => Find(name) is not null;

    /// <summary>读入 fixture 帧（Mat，BGR）；不存在返回 null。</summary>
    public static Mat? Load(string name)
    {
        var p = Find(name);
        return p is null ? null : Cv2.ImRead(p);
    }

    /// <summary>fixtures 帧回归的标准测试体：素材在 → 喂感知函数并断言期望输出；素材不在 → 跳过。
    /// 用法：if (!FixtureFrames.RunIfAvailable("k0_hit_flash", AssertHitFlash)) return;</summary>
    public static bool RunIfAvailable(string name, Action<Mat> assertion)
    {
        var frame = Load(name);
        if (frame is null) return false;   // 素材未入库 → 测试自行 return（跳过）
        try { assertion(frame); }
        finally { frame.Dispose(); }
        return true;
    }
}
