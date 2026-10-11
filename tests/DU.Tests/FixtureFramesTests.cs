using OpenCvSharp;
using Xunit;

namespace DU.Tests;

/// <summary>fixtures 管线自检：写→读→断言→清理。用测试现场生成的临时图验证整条链路，
/// 不依赖任何已入库素材（真实素材由用户 K0 采集后放入）。</summary>
public class FixtureFramesTests
{
    [Fact]
    public void RoundTrip_WriteLoadAssert_Cleanup()
    {
        string name = "__fixture_selftest__";
        try
        {
            Directory.CreateDirectory(FixtureFrames.Dir);
            using (var img = new Mat(120, 200, MatType.CV_8UC3, new Scalar(0, 0, 255)))
            {
                Assert.True(Cv2.ImWrite(Path.Combine(FixtureFrames.Dir, name + ".png"), img));
            }

            Assert.True(FixtureFrames.Has(name));
            using var loaded = FixtureFrames.Load(name);
            Assert.NotNull(loaded);
            Assert.Equal(120, loaded.Rows);
            Assert.Equal(200, loaded.Cols);
        }
        finally
        {
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
            {
                var p = Path.Combine(FixtureFrames.Dir, name + ext);
                if (File.Exists(p)) File.Delete(p);
            }
        }
    }

    [Fact]
    public void MissingFixture_ReturnsNull_NeverThrows()
    {
        Assert.False(FixtureFrames.Has("k0_不存在的素材_xyz"));
        Assert.Null(FixtureFrames.Load("k0_不存在的素材_xyz"));
    }

    [Fact]
    public void RunIfAvailable_SkipsWhenAbsent_RunsWhenPresent()
    {
        // 素材缺席 → 返回 false（测试体不执行，调用方自行 return = 跳过语义）
        Assert.False(FixtureFrames.RunIfAvailable("k0_不存在的素材_xyz", _ => throw new Exception("不该执行")));

        // 临时写一张 → 应执行并传入同尺寸 Mat
        string name = "__fixture_selftest_run__";
        try
        {
            Directory.CreateDirectory(FixtureFrames.Dir);
            using (var img = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(0)))
                Cv2.ImWrite(Path.Combine(FixtureFrames.Dir, name + ".jpg"), img);

            bool ran = false;
            Assert.True(FixtureFrames.RunIfAvailable(name, m => { ran = m.Rows == 64; }));
            Assert.True(ran);
        }
        finally
        {
            var p = Path.Combine(FixtureFrames.Dir, name + ".jpg");
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
