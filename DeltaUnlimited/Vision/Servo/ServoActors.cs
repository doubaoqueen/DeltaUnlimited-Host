using DeltaUnlimited.Input;

namespace DeltaUnlimited.Vision.Servo;

/// <summary>伺服感知接口：每周期返回一帧感知状态。
/// K0 标定前由测试假实现填充；K0 后由"导航标记模板匹配 + 距离 OCR"的真实实现填充。</summary>
public interface IServoPerceiver
{
    ServoFrame Observe();
}

/// <summary>伺服动作执行接口：只允许移动类动作（转向/前进/冲刺），禁止任何点击——
/// 点击类动作一律回 chain 层走 ClickAt 安全网（提案 §3）。</summary>
public interface IServoActor
{
    void Perform(ServoAction action);

    /// <summary>结束兜底：释放所有键（任何结局都必须调用，try/finally 保证）。</summary>
    void Release();
}

/// <summary>真实键鼠执行器（K1 真机验收时接上）：
/// 转向 = InputService.MoveRelative（自带缓动/抖动拟人化）；前进/冲刺 = 持续按住 W/Shift。
/// 状态变化时才重新按/放键；任何 Release 都走 ReleaseAllHeldKeys 全局兜底（巡逻同款）。
/// 键位不写死：构造时注入（默认 w/shift，真机从 data/game_ops.json 的按键映射取）。</summary>
public sealed class KeyboardMouseActor : IServoActor
{
    private readonly string _forwardKey;
    private readonly string _sprintKey;
    private bool _forwardDown;
    private bool _sprintDown;

    public KeyboardMouseActor(string forwardKey = "w", string sprintKey = "shift")
    {
        _forwardKey = forwardKey;
        _sprintKey = sprintKey;
    }

    public void Perform(ServoAction action)
    {
        if (action.TurnDx != 0)
            InputService.MoveRelative(action.TurnDx, 0);

        bool forward = action.HoldForward;
        bool sprint = action.HoldForward && action.Sprint;
        if (forward == _forwardDown && sprint == _sprintDown) return;  // 状态没变不动键

        InputService.ReleaseAllHeldKeys();                             // 状态切换先松手再按（防连按抖动）
        _forwardDown = forward;
        _sprintDown = sprint;
        if (!forward) return;

        InputService.PressKeys(sprint
            ? new[] { _forwardKey, _sprintKey }
            : new[] { _forwardKey });
    }

    public void Release()
    {
        InputService.ReleaseAllHeldKeys();
        _forwardDown = false;
        _sprintDown = false;
    }
}
