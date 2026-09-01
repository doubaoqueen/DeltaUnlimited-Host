using Vanara.PInvoke;

Console.WriteLine("✅ DeltaUnlimited BotHost 启动");
var desktopHwnd = User32.GetDesktopWindow();
Console.WriteLine($"桌面句柄：{desktopHwnd}");

Console.WriteLine("\n按回车退出...");
_ = Console.ReadLine();
