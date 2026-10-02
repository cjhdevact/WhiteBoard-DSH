using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WhiteBoard.Controls;
using WhiteBoard.Models;

namespace WhiteBoard.InputTests;

/// <summary>
/// 探测哪种「透明覆盖窗口」配置在 Windows 上能真正收到鼠标输入。
/// 结论直接决定 AnnotationCanvasWindow 的写法。
/// </summary>
public static class WindowInputProbe
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .WithInterFont()
            .SetupWithoutStarting();

        var results = new List<string>();

        // 依次尝试几种窗口配置，每种独立创建 / 显示 / 测试 / 关闭
        results.Add(Probe("A 当前实现（ExtendClientArea + TransparencyLevelHint）", cfg =>
        {
            cfg.Window.ExtendClientAreaToDecorationsHint = true;
            cfg.Window.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            cfg.Window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        }));

        results.Add(Probe("B 去掉 ExtendClientArea（保留 TransparencyLevelHint）", cfg =>
        {
            cfg.Window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        }));

        results.Add(Probe("C ExtendClientArea + 画布显式 Transparent 背景", cfg =>
        {
            cfg.Window.ExtendClientAreaToDecorationsHint = true;
            cfg.Window.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            cfg.Window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            cfg.HitLayer.Background = Brushes.Transparent;
        }));

        results.Add(Probe("D 全透明 + Maximized + 画布透明背景", cfg =>
        {
            cfg.Window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            cfg.HitLayer.Background = Brushes.Transparent;
            cfg.UseMaximized = true;
        }));

        results.Add(Probe("E 不透明窗口（对照：确认输入链路本身正常）", cfg =>
        {
            cfg.Window.Background = Brushes.DarkSlateBlue;
            cfg.HitLayer.Background = Brushes.Transparent;
        }));

        Console.WriteLine();
        Console.WriteLine("=== 探测汇总 ===");
        foreach (var r in results)
            Console.WriteLine("  " + r);

        return 0;
    }

    private sealed class Config
    {
        public Window Window { get; init; } = null!;
        public WhiteboardCanvas Canvas { get; init; } = null!;
        public Border HitLayer { get; init; } = null!;
        public bool UseMaximized { get; set; }
    }

    private static string Probe(string label, Action<Config> configure)
    {
        var page = new WhiteboardPage();

        var canvas = new WhiteboardCanvas
        {
            Page = page,
            Tool = WhiteboardTool.Pen,
            PenColor = Colors.White,
            PenThickness = 6
        };

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = Brushes.Transparent,
            Content = new Border { Child = canvas, Background = Brushes.Transparent },
            Width = 800,
            Height = 600,
            Position = new PixelPoint(40, 40)
        };

        var cfg = new Config { Window = window, Canvas = canvas, HitLayer = (Border)window.Content! };
        configure(cfg);

        var hit = 0;
        var moved = 0;

        canvas.PointerPressed += (_, _) => hit++;
        canvas.PointerMoved += (_, _) => moved++;

        window.Show();

        if (cfg.UseMaximized)
            window.WindowState = WindowState.Maximized;

        var done = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 用系统级鼠标事件模拟：移动 + 左键按下（需要窗口真的接收输入）
            Win32Input.SendMouseMove(window.Position.X + 300, window.Position.Y + 300);
            Win32Input.SendMouseDown();

            var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t2.Tick += (_, _) =>
            {
                t2.Stop();
                Win32Input.SendMouseUp();
                window.Close();
                done = true;
            };
            t2.Start();
        };
        timer.Start();

        // 泵消息直到完成
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!done && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        try { window.Close(); } catch { }

        var verdict = hit > 0 ? "收到输入 ✔" : "收不到输入 ✘";
        return $"{label,-46} PointerPressed={hit} Moved={moved}  → {verdict}";
    }
}

/// <summary>直接用 Win32 注入真实的鼠标事件（SendInput）。</summary>
internal static class Win32Input
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static void Send(uint flags, int dx = 0, int dy = 0)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
        };

        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    /// <summary>把屏幕像素坐标换算成 SendInput 需要的 0..65535 归一化坐标。</summary>
    private static (int X, int Y) Normalize(int screenX, int screenY)
    {
        var w = Math.Max(1, GetSystemMetrics(0));  // SM_CXSCREEN
        var h = Math.Max(1, GetSystemMetrics(1));  // SM_CYSCREEN

        return ((int)(screenX * 65535.0 / w), (int)(screenY * 65535.0 / h));
    }

    public static void SendMouseMove(int screenX, int screenY)
    {
        var (x, y) = Normalize(screenX, screenY);
        Send(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, x, y);
    }

    public static void SendMouseDown() => Send(MOUSEEVENTF_LEFTDOWN);

    public static void SendMouseUp() => Send(MOUSEEVENTF_LEFTUP);
}
