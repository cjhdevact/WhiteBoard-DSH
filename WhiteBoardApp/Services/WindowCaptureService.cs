using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace WhiteBoard.Services;

/// <summary>
/// 按窗口句柄截取某个窗口的画面（含窗口自身内容）。
/// 用于自动化验证：即使窗口被遮挡或未激活，也能拿到它渲染出来的像素。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowCaptureService
{
    private const int PW_RENDERFULLCONTENT = 0x00000002;
    private const int DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;

    /// <summary>按窗口标题查找顶层窗口句柄（支持通配）。</summary>
    public static IntPtr FindWindowByTitle(string titlePart)
    {
        var result = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return true;

            var buffer = new char[length + 1];
            var copied = GetWindowText(hwnd, buffer, buffer.Length);
            if (copied <= 0)
                return true;

            var title = new string(buffer, 0, copied);
            if (!title.Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                return true;

            result = hwnd;
            return false; // 找到就停
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>截取指定窗口的客户区 + 边框。</summary>
    public static CapturedFrame CaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            throw new ArgumentException("窗口句柄无效。", nameof(hwnd));

        if (!GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("GetWindowRect 失败。");

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        var windowDc = GetWindowDC(hwnd);
        if (windowDc == IntPtr.Zero)
            throw new InvalidOperationException("GetWindowDC 失败。");

        var memDc = CreateCompatibleDC(windowDc);
        var bitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;

        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            };

            bitmap = CreateDIBSection(memDc, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败。");

            oldBitmap = SelectObject(memDc, bitmap);

            if (!PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT))
                throw new InvalidOperationException("PrintWindow 失败（窗口可能尚未完成首帧渲染）。");

            var stride = width * 4;
            var buffer = new byte[stride * height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            return new CapturedFrame
            {
                ScreenLeft = rect.Left,
                ScreenTop = rect.Top,
                Width = width,
                Height = height,
                Scale = ScreenCaptureService.GetPrimaryScale(),
                Bgra = buffer
            };
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);

            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);

            if (memDc != IntPtr.Zero)
                DeleteDC(memDc);

            if (windowDc != IntPtr.Zero)
                ReleaseDC(hwnd, windowDc);
        }
    }

    /// <summary>把窗口带到前台（截屏前的可选步骤）。</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            ShowWindow(hwnd, 5 /* SW_SHOW */);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // 前台切换可能被系统拒绝，忽略
        }
    }

    /// <summary>保存窗口截图。</summary>
    public static void SaveToPng(CapturedFrame frame, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var bmp = frame.ToBitmap();
        bmp.Save(path);
    }

    // ── P/Invoke ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER pbmi, int usage, out IntPtr ppvBits, IntPtr hSection, int offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);
}
