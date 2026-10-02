using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace WhiteBoard.Services;

/// <summary>
/// Win32 窗口辅助：鼠标穿透（点击穿透到下层应用）、窗口置顶、
/// 以及让无边框窗口可以被拖动。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowInteropService
{
    private const int GWL_EXSTYLE = -20;

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int HWND_TOPMOST = -1;
    private const int HWND_NOTOPMOST = -2;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>取到窗口的原生句柄；非 Windows 或句柄不可用时返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr GetHandle(Window window)
    {
        try
        {
            return window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 设置 / 取消鼠标穿透。穿透开启时窗口不再接收鼠标事件，
    /// 用户可以在批注层覆盖屏幕的同时操作下方的真实应用。
    /// </summary>
    public static bool SetClickThrough(Window window, bool enabled)
    {
        var handle = GetHandle(window);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            var exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
            var target = enabled
                ? exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED
                : exStyle & ~(long)WS_EX_TRANSPARENT;

            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(target));

            // 让新样式立即生效
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把窗口标记为工具窗口（不出现在 Alt+Tab 与任务栏）。</summary>
    public static bool SetToolWindow(Window window, bool enabled)
    {
        var handle = GetHandle(window);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            var exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
            var target = enabled
                ? exStyle | WS_EX_TOOLWINDOW
                : exStyle & ~(long)WS_EX_TOOLWINDOW;

            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(target));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>强制置顶 / 取消置顶（Topmost 属性失效时的兜底）。</summary>
    public static bool ForceTopmost(Window window, bool topmost)
    {
        var handle = GetHandle(window);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            SetWindowPos(handle, new IntPtr(topmost ? HWND_TOPMOST : HWND_NOTOPMOST),
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前是否有窗口拥有前台焦点（用于判断“别的应用正在被操作”）。</summary>
    public static IntPtr GetForegroundWindow() => GetForegroundWindowNative();

    /// <summary>当前前台窗口的标题。</summary>
    public static string GetForegroundWindowTitle()
    {
        try
        {
            var hwnd = GetForegroundWindowNative();
            if (hwnd == IntPtr.Zero)
                return string.Empty;

            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return string.Empty;

            var buffer = new char[length + 1];
            var copied = GetWindowText(hwnd, buffer, buffer.Length);
            return new string(buffer, 0, Math.Max(0, copied));
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));
}
