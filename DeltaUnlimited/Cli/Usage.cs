namespace DeltaUnlimited.Cli;

/// <summary>CLI 用法说明。</summary>
public static class Usage
{
    public static void Print()
    {
        Console.WriteLine("""
            DeltaUnlimited 开发期
              (无参数 | gui)                 图形控制面板（托盘+启动/急停+日志窗）
              smoke                           Phase 0 数据层冒烟
              annotate <元素名> [截图路径]       静态识别冒烟 → screenshots/annotated/
              capture  [标题关键字] [文件名]     截屏冒烟 → screenshots/captured/
              click <元素名> [窗口关键字]        真实点击（点前/点后自动截图，默认 3 秒倒计时）
              hold <键名> <毫秒> [窗口关键字]     长按测试（帧差验证，如: hold move_forward 1500）
              turn <dx> [dy] [窗口关键字]         视角转动测试（帧差验证，如: turn 800 0）
              drill [圈数] [窗口关键字]           靶场自主行进循环（帧差确认+自动重试+急停）
              patrol [秒数] [阈值] [连续] [90°px] [窗口]   反应式巡逻 v2：贴墙停+横移低效双守卫，自动转向
              chain <workflow文件> [--auto]        按 JSON 步骤执行链路（detect/if_screen 分支/jump）
              crop <x> <y> <w> <h> <源图> [输出] [缩放]   从截图裁模板小图（可缩放换算设计分辨率）
              ocr <截图路径> [关键词...]      OCR 诊断：识别全部词 + 关键词严格/候选匹配
              overlay                           悬浮状态面板（游戏画面上实时显示识别/操作日志）
              windows                           列出可见窗口
            """);
    }
}
