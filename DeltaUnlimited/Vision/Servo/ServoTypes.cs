namespace DeltaUnlimited.Vision.Servo;

/// <summary>伺服循环结束原因（chain 侧据此走 JSON 分支）。</summary>
public enum ServoOutcome
{
    /// <summary>达成目标（如抵达撤离点/漫游时长到）。</summary>
    Success,

    /// <summary>超时（未达成，但过程安全）。</summary>
    Timeout,

    /// <summary>急停/人工中止。</summary>
    Stopped,

    /// <summary>贴墙/横移等守卫反复触发——判定卡死，交人工。</summary>
    Stuck,

    /// <summary>检测到危险（受击红屏等）——按红线立即交人工，不做自动逃跑。</summary>
    Danger,

    /// <summary>标记持续丢失（曾可见后消失 ≥ MarkerLostGiveUpCycles 周期）——疑似到达点上方/越过错点，
    /// 距离不可读时的辅助到达判据；chain 侧应走"确认到达"分支（如识别撤离倒计时条）而非当失败。</summary>
    MarkerLost,

    /// <summary>感知/输入异常。</summary>
    Error,
}

/// <summary>伺服循环结果。</summary>
public sealed record ServoResult(ServoOutcome Outcome, string Detail, double ElapsedMs)
{
    public bool Ok => Outcome == ServoOutcome.Success;
}

/// <summary>伺服每周期看到的画面状态（由感知层填充，纯数据）。
/// 注意：这里只描述"看见什么"，不含任何决策。</summary>
public sealed record ServoFrame(
    bool MarkerVisible,       // HUD 导航标记是否在画面内
    double MarkerOffsetPx,    // 标记相对屏幕中心的水平偏移（正=偏右）
    double DistanceMeters,    // 距离（-1 = 不可读/未知）
    double FrameDiff,         // 本周期帧差（卡墙守卫用）
    bool HitBlocked,          // AI 可通行性预警（observe-only 时恒 false）
    bool ArrivalPrompt = false,  // 到达判据权威：到达确认 UI 出现（撤离=顶部倒计时条，仅在撤离区内绘制）——游戏引擎说到了才算到
    bool HitFlash = false)       // 受击红屏信号（HitFlashMonitor 防抖确认后置 true）——触发即 Danger 交人工
{
    public static readonly ServoFrame None = new(false, 0, -1, 0, false);
}

/// <summary>伺服每周期要做的动作（**只允许视角与移动**；点击/开箱/撤离 F 一律回 chain 走点击安全网）。</summary>
public sealed record ServoAction(int TurnDx, bool HoldForward, bool Sprint, string Note)
{
    public static readonly ServoAction Idle = new(0, false, false, "待机");
}
