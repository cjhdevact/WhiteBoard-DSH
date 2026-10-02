using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace WhiteBoard.Controls;

/// <summary>
/// 把 ui 目录里给的参考图（clean.png）做成可用的工具栏图标。
///
/// 背景：参考图是**固定浅灰色**的位图，直接当图标贴在深色主题的工具栏上
/// 几乎看不见（这也是「橡皮没用我给的图片」看起来没生效的原因）。
///
/// 做法：直接读写像素，把位图变成「单色 + alpha」的图标：
///   • 透明像素 → 全透明
///   • 不透明像素 → 用当前主题的前景色，alpha 沿用原像素的 alpha
///
/// 刻意不用 RenderTargetBitmap + PushOpacityMask —— 那条路依赖 Skia 的合成行为，
/// 遇到某些位图会静默失败（图标变空白，最后落到兜底字形，看起来就是「没用我给的图」）。
/// </summary>
public static class ImageIconFactory
{
    private const int DefaultIconPixels = 28;

    /// <summary>着色结果缓存：资源地址 + 颜色 + 尺寸 → 图标。</summary>
    private static readonly Dictionary<(string Uri, uint Color, int Size), Bitmap> Cache = new();

    /// <summary>生成图标；失败时返回 null（调用方会退回 Symbol 字形）。</summary>
    public static IImage? Create(string? uri, bool tint, int size = DefaultIconPixels)
    {
        if (string.IsNullOrEmpty(uri))
            return null;

        var color = tint ? ResolveForeground() : Colors.White;

        lock (Cache)
        {
            var key = (uri, ToKey(color), size);
            if (Cache.TryGetValue(key, out var cached))
                return cached;
        }

        try
        {
            using var source = LoadRaw(uri);
            if (source is null)
                return null;

            var result = Tint(source, color, size);

            lock (Cache)
                Cache[(uri, ToKey(color), size)] = result;

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? LoadRaw(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把源位图缩放到目标尺寸后逐像素着色。</summary>
    private static Bitmap Tint(Bitmap source, Color color, int size)
    {
        var srcW = source.PixelSize.Width;
        var srcH = source.PixelSize.Height;

        if (srcW <= 0 || srcH <= 0)
            throw new InvalidOperationException("源图尺寸无效");

        var scale = Math.Min((double)size / srcW, (double)size / srcH);
        var w = Math.Max(1, (int)Math.Round(srcW * scale));
        var h = Math.Max(1, (int)Math.Round(srcH * scale));

        // 第一步：等比缩放，得到规整的像素数据
        using var scaled = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));

        using (var ctx = scaled.CreateDrawingContext())
        {
            ctx.DrawImage(source, new Rect(0, 0, srcW, srcH), new Rect(0, 0, w, h));
        }

        // 第二步：读回像素
        var stride = w * 4;
        var buffer = new byte[stride * h];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            scaled.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        // 第三步：按主题色重新着色，写进 WriteableBitmap（Bgra8888 + 预乘 alpha）
        var target = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);

        var offsetX = (size - w) / 2;
        var offsetY = (size - h) / 2;

        using (var fb = target.Lock())
        {
            unsafe
            {
                var dstBase = (byte*)fb.Address;
                var dstStride = fb.RowBytes;

                for (var y = 0; y < size; y++)
                {
                    var row = dstBase + y * dstStride;

                    for (var x = 0; x < size * 4; x++)
                        row[x] = 0;
                }

                for (var y = 0; y < h; y++)
                {
                    var ty = y + offsetY;
                    if (ty < 0 || ty >= size)
                        continue;

                    var srcRow = y * stride;
                    var dstRow = dstBase + ty * dstStride;

                    for (var x = 0; x < w; x++)
                    {
                        var tx = x + offsetX;
                        if (tx < 0 || tx >= size)
                            continue;

                        // Bgra8888：B,G,R,A
                        var a = buffer[srcRow + x * 4 + 3];
                        if (a == 0)
                            continue;

                        var o = tx * 4;
                        dstRow[o] = (byte)(color.B * a / 255);      // B
                        dstRow[o + 1] = (byte)(color.G * a / 255);  // G
                        dstRow[o + 2] = (byte)(color.R * a / 255);  // R
                        dstRow[o + 3] = a;                          // A
                    }
                }
            }
        }

        return target;
    }

    /// <summary>取当前主题的前景色（FluentAvalonia 的资源里有）。</summary>
    private static Color ResolveForeground()
    {
        try
        {
            var app = Application.Current;
            if (app is not null)
            {
                var variant = app.ActualThemeVariant;
                const string key = "TextFillColorPrimaryBrush";

                // 应用资源 → 再退回样式资源（FluentAvalonia 的主题资源挂在 Styles 上）
                if (app.Resources.TryGetResource(key, variant, out var res) && res is ISolidColorBrush brush)
                    return brush.Color;

                foreach (var style in app.Styles)
                {
                    if (style.TryGetResource(key, variant, out var res2) && res2 is ISolidColorBrush brush2)
                        return brush2.Color;
                }
            }
        }
        catch
        {
            // 取不到就用兜底色
        }

        return Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            ? Colors.White
            : Color.FromRgb(0x1A, 0x1A, 0x1A);
    }

    private static uint ToKey(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
