using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WhiteBoard.Services;

/// <summary>可注册的全局热键动作。</summary>
public enum GlobalHotKeyAction
{
    /// <summary>切换桌面批注（在“批注当前画面”与“退出批注”之间切换）。</summary>
    ToggleAnnotate = 1,

    /// <summary>直接进入桌面批注（冷冻当前屏幕画面）。</summary>
    FreezeAndAnnotate = 2,

    /// <summary>显示 / 隐藏白板主窗口。</summary>
    ToggleWhiteboard = 3,

    /// <summary>清空当前页。</summary>
    ClearPage = 4
}

/// <summary>
/// 系统级全局热键。使用 RegisterHotKey，因此即使焦点在其它程序里，
/// 也能用 Ctrl+Alt+A 之类的组合键随时调出批注。
/// 内部使用一个隐藏的 message-only 窗口接收 WM_HOTKEY。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLOSE = 0x0010;
    private const int WM_DESTROY = 0x0002;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private const uint VK_A = 0x41;
    private const uint VK_D = 0x44;
    private const uint VK_W = 0x57;
    private const uint VK_L = 0x4C;

    private readonly Dictionary<int, GlobalHotKeyAction> _registrations = new();
    private readonly List<int> _registeredIds = new();
    private readonly WndProcDelegate _wndProc; // 必须保持引用，否则会被 GC 回收
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private IntPtr _hwnd;
    private string? _className;
    private bool _disposed;

    public GlobalHotKeyService()
    {
        _wndProc = WindowProc;
    }

    /// <summary>热键被按下时触发（在后台线程上）。</summary>
    public event Action<GlobalHotKeyAction>? Triggered;

    /// <summary>注册是否成功（在非 Windows 平台或注册失败时为 false）。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>未能注册的热键说明，用于提示用户。</summary>
    public List<string> Failures { get; } = new();

    /// <summary>启动消息循环并注册默认热键。</summary>
    public void Start()
    {
        if (_disposed || _thread is not null)
            return;

        if (!OperatingSystem.IsWindows())
            return;

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "WhiteBoard.GlobalHotKeys"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void MessageLoop()
    {
        try
        {
            _className = "WhiteBoardHotKeySink_" + Guid.NewGuid().ToString("N");

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = _className
            };

            if (RegisterClassEx(ref wc) == 0)
            {
                _ready.Set();
                return;
            }

            // HWND_MESSAGE(-3)：仅消息窗口，不出现在界面上
            _hwnd = CreateWindowEx(0, _className, "WhiteBoardHotKeys", 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _ready.Set();
                return;
            }

            RegisterDefaults();
            IsRunning = true;
            _ready.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch
        {
            IsRunning = false;
            _ready.Set();
        }
        finally
        {
            UnregisterAll();

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_className is not null)
            {
                UnregisterClass(_className, GetModuleHandle(null));
                _className = null;
            }
        }
    }

    private void RegisterDefaults()
    {
        Register(1, MOD_CONTROL | MOD_ALT, VK_A, GlobalHotKeyAction.ToggleAnnotate, "Ctrl+Alt+A 切换桌面批注");
        Register(2, MOD_CONTROL | MOD_ALT, VK_D, GlobalHotKeyAction.FreezeAndAnnotate, "Ctrl+Alt+D 冻结屏幕并批注");
        Register(3, MOD_CONTROL | MOD_ALT, VK_W, GlobalHotKeyAction.ToggleWhiteboard, "Ctrl+Alt+W 显示/隐藏白板");
        Register(4, MOD_CONTROL | MOD_ALT, VK_L, GlobalHotKeyAction.ClearPage, "Ctrl+Alt+L 清空当前页");
    }

    private void Register(int id, uint modifiers, uint vk, GlobalHotKeyAction action, string display)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        // 加上 MOD_NOREPEAT，避免按住时反复触发
        const uint MOD_NOREPEAT = 0x4000;

        if (RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk))
        {
            _registrations[id] = action;
            _registeredIds.Add(id);
        }
        else
        {
            Failures.Add(display);
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _registeredIds)
        {
            if (_hwnd != IntPtr.Zero)
                UnregisterHotKey(_hwnd, id);
        }

        _registeredIds.Clear();
        _registrations.Clear();
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
            {
                var id = wParam.ToInt32();
                if (_registrations.TryGetValue(id, out var action))
                {
                    try
                    {
                        Triggered?.Invoke(action);
                    }
                    catch
                    {
                        // 订阅方异常不应影响消息循环
                    }
                }

                return IntPtr.Zero;
            }

            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
        IsRunning = false;
    }

    // ── P/Invoke ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
