using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace WhiteBoard.Services;

/// <summary>一次桌面截图的原始像素数据（BGRA，自上而下）。</summary>
public sealed class CapturedFrame : IDisposable
{
    /// <summary>虚拟屏幕左上角在物理像素坐标系中的位置（多显示器时可能为负）。</summary>
    public int ScreenLeft { get; init; }

    public int ScreenTop { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>每个逻辑像素对应多少物理像素（DPI 缩放比）。</summary>
    public double Scale { get; init; } = 1.0;

    public byte[] Bgra { get; init; } = Array.Empty<byte>();

    /// <summary>物理像素 → 逻辑像素（DIP）的换算。</summary>
    public Rect ToLogicalBounds() => new(0, 0, Width / Scale, Height / Scale);

    public Bitmap ToBitmap()
    {
        var w = Math.Max(1, Width);
        var h = Math.Max(1, Height);

        var writeable = new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96 * Scale, 96 * Scale),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = writeable.Lock())
        {
            var stride = fb.RowBytes;
            var srcStride = w * 4;

            if (stride == srcStride)
            {
                Marshal.Copy(Bgra, 0, fb.Address, Math.Min(Bgra.Length, stride * h));
            }
            else
            {
                for (var y = 0; y < h; y++)
                {
                    var srcOffset = y * srcStride;
                    if (srcOffset + srcStride > Bgra.Length)
                        break;

                    Marshal.Copy(Bgra, srcOffset, fb.Address + y * stride, srcStride);
                }
            }
        }

        return writeable;
    }

    public string ToPngBase64()
    {
        using var bmp = ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    public void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>
/// Windows 桌面截屏。使用 GDI BitBlt 抓取整个虚拟屏幕，
/// 覆盖多显示器与高 DPI 场景。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenCaptureService
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;

    /// <summary>抓取整个虚拟桌面。</summary>
    public static CapturedFrame CaptureVirtualScreen()
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            // 极端情况下的退路：抓主屏
            x = 0;
            y = 0;
            width = Math.Max(1, GetSystemMetrics(0));
            height = Math.Max(1, GetSystemMetrics(1));
        }

        var scale = GetPrimaryScale();
        return CaptureRegion(x, y, width, height, scale);
    }

    /// <summary>抓取屏幕上的一个区域（物理像素坐标）。</summary>
    public static CapturedFrame CaptureRegion(int x, int y, int width, int height, double scale = 1.0)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var screenDc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("无法获取屏幕设备上下文（CreateDC 失败）。");

        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;

        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // 负值 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            };

            bitmap = CreateDIBSection(memDc, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("无法创建离屏位图（CreateDIBSection 失败）。");

            oldBitmap = SelectObject(memDc, bitmap);

            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("抓屏失败（BitBlt 失败）。");

            var stride = width * 4;
            var buffer = new byte[stride * height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            return new CapturedFrame
            {
                ScreenLeft = x,
                ScreenTop = y,
                Width = width,
                Height = height,
                Scale = scale,
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

            if (screenDc != IntPtr.Zero)
                DeleteDC(screenDc);
        }
    }

    /// <summary>主显示器的 DPI 缩放比（1.0 = 100%）。</summary>
    public static double GetPrimaryScale()
    {
        try
        {
            var dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
                return 1.0;

            try
            {
                var dpi = GetDeviceCaps(dc, 88); // LOGPIXELSX
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>把截图存为 PNG 文件。</summary>
    public static void SavePng(CapturedFrame frame, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var bmp = frame.ToBitmap();
        bmp.Save(path);
    }

    // ── P/Invoke ──────────────────────────────────────────────

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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string? pwszDriver, string? pwszDevice, string? pszPort, IntPtr pdm);

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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    // 便于诊断：确认进程处于 DPI 感知模式
    internal static void LogDpiMode()
    {
        try
        {
            Debug.WriteLine($"[ScreenCapture] primaryScale={GetPrimaryScale():0.##}");
        }
        catch
        {
            // ignore
        }
    }
}
