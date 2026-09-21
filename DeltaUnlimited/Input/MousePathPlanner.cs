namespace DeltaUnlimited.Input;

/// <summary>屏幕坐标点（轻量结构，供路径规划与测试使用，无外部依赖）。</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// 拟人鼠标路径规划（纯逻辑，可测试）：从当前点到目标点生成一条自然弧线路径。
/// 要素：随机贝塞尔弧线（不直）、smoothstep 缓动（起步慢→中途快→收尾慢）、随机过冲再收回、途中逐点抖动；
/// 末点严格等于目标（落点精确，且全程绝对坐标不受系统“提高指针精确度”加速影响）。
/// 注意：拟人化降低机械感，不等于免疫风控；实战频率与行为仍需自行控制。
/// </summary>
public static class MousePathPlanner
{
    /// <summary>生成从 start 到 end 的路径点列（末点=end；距离过近时只返回 end 一点）。</summary>
    public static IReadOnlyList<ScreenPoint> Plan(ScreenPoint start, ScreenPoint end, MousePathConfig cfg, Random rng)
    {
        int dx = end.X - start.X, dy = end.Y - start.Y;
        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var pts = new List<ScreenPoint>();

        if (dist <= 6) // 太近：直接到位（人也经常只微移一次）
        {
            pts.Add(end);
            return pts;
        }

        // 贝塞尔控制点 = 中点 + 法向随机偏移（弧线方向/幅度每次随机，不重样）
        double mx = (start.X + end.X) / 2.0, my = (start.Y + end.Y) / 2.0;
        double len = Math.Sqrt((double)dx * dx + (double)dy * dy);
        double nx = -dy / len, ny = dx / len;
        double curve = (rng.NextDouble() * 2 - 1) * Math.Min(cfg.CurvatureMaxPx, dist * 0.35);
        double cx = mx + nx * curve, cy = my + ny * curve;

        // 时长随距离增长 + 随机，按步间隔折算步数（步数多=轨迹细腻）
        int duration = (int)Math.Clamp(
            (cfg.MinDurationMs + cfg.MaxDurationMs) / 2.0 * (0.85 + 0.3 * rng.NextDouble()) + dist * cfg.PerPixelMs,
            cfg.MinDurationMs, cfg.MaxDurationMs);
        int steps = Math.Max(6, duration / Math.Max(1, cfg.StepIntervalMs));

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double e = t * t * (3 - 2 * t); // smoothstep：起步慢→中途快→收尾慢
            double x = (1 - e) * (1 - e) * start.X + 2 * (1 - e) * e * cx + e * e * end.X;
            double y = (1 - e) * (1 - e) * start.Y + 2 * (1 - e) * e * cy + e * e * end.Y;
            if (i < steps) // 抖动只加在途中，末点必须精确
            {
                x += (rng.NextDouble() * 2 - 1) * cfg.JitterPx;
                y += (rng.NextDouble() * 2 - 1) * cfg.JitterPx;
            }
            pts.Add(new ScreenPoint((int)Math.Round(x), (int)Math.Round(y)));
        }

        // 过冲再收回：沿末段方向冲出一点，再回到目标（人甩鼠标常见的“冲过头”微调）
        if (rng.NextDouble() < cfg.OvershootProbability && pts.Count >= 2)
        {
            var prev = pts[^2];
            double ldx = end.X - prev.X, ldy = end.Y - prev.Y;
            double ld = Math.Sqrt(ldx * ldx + ldy * ldy);
            if (ld > 1)
            {
                int over = rng.Next(cfg.OvershootPxMin, cfg.OvershootPxMax + 1);
                pts.Add(new ScreenPoint(
                    end.X + (int)Math.Round(ldx / ld * over),
                    end.Y + (int)Math.Round(ldy / ld * over)));
                pts.Add(end);
            }
        }

        return pts;
    }
}
