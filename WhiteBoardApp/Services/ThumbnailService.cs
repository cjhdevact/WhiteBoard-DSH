using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WhiteBoard.Models;

namespace WhiteBoard.Services;

/// <summary>页缩略图。</summary>
public sealed class PageThumbnail : IDisposable
{
    public PageThumbnail(WhiteboardPage page, RenderTargetBitmap bitmap)
    {
        Page = page;
        Bitmap = bitmap;
    }

    public WhiteboardPage Page { get; }

    public RenderTargetBitmap Bitmap { get; }

    public int ItemCount => Page.Items.Count;

    public string Title => string.IsNullOrWhiteSpace(Page.Title) ? "未命名" : Page.Title;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// 把所有页面渲染成小图，用于页面导航条。
///
/// 重要：位图通过 <see cref="BitmapReleaser"/> 延迟释放。
/// 这些位图会直接绑定到界面的 Image 控件上，若在换图时立即 Dispose，
/// 渲染线程可能仍在引用已释放的原生内存，导致切换底纹 / 背景时直接崩溃。
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int ThumbWidth = 168;
    private const int ThumbHeight = 95;

    private readonly Dictionary<WhiteboardPage, PageThumbnail> _cache = new();
    private long _lastRebuild;

    /// <summary>缩略图尺寸（逻辑单位，按 16:9 渲染）。</summary>
    public static PixelSize ThumbnailSize => new(ThumbWidth, ThumbHeight);

    /// <summary>缩略图之间的最小重建间隔（毫秒），避免连续书写时卡顿。</summary>
    public int MinRebuildIntervalMs { get; set; } = 600;

    public IReadOnlyList<PageThumbnail> Build(IEnumerable<WhiteboardPage> pages, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastRebuild < MinRebuildIntervalMs)
            return _cache.Values.ToList();

        _lastRebuild = now;

        var list = new List<PageThumbnail>();
        var live = new HashSet<WhiteboardPage>();

        foreach (var page in pages)
        {
            live.Add(page);

            if (!force && _cache.TryGetValue(page, out var existing) && existing.ItemCount == page.Items.Count)
            {
                list.Add(existing);
                continue;
            }

            if (_cache.Remove(page, out var stale))
                BitmapReleaser.Release(stale.Bitmap);

            var thumb = Render(page);
            _cache[page] = thumb;
            list.Add(thumb);
        }

        // 清理已删除页的缓存
        foreach (var key in _cache.Keys.Where(k => !live.Contains(k)).ToList())
        {
            if (_cache.Remove(key, out var dead))
                BitmapReleaser.Release(dead.Bitmap);
        }

        return list;
    }

    private static PageThumbnail Render(WhiteboardPage page)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(ThumbWidth, ThumbHeight), new Vector(96, 96));

        // 页面逻辑尺寸：固定为 1920×1080 与背景截图尺寸的较大者
        var logicalW = Math.Max(1920, page.BackgroundImageRect.Right);
        var logicalH = Math.Max(1080, page.BackgroundImageRect.Bottom);

        using (var ctx = bitmap.CreateDrawingContext())
        {
            var scale = Math.Min(ThumbWidth / logicalW, ThumbHeight / logicalH);

            // 先铺白，避免透明底在深色界面上看不清
            ctx.FillRectangle(Avalonia.Media.Brushes.White, new Rect(0, 0, ThumbWidth, ThumbHeight));

            using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
            {
                var area = new Rect(0, 0, logicalW, logicalH);
                var bg = BackgroundImageCache.Get(page.BackgroundImageBase64);
                WhiteboardRenderer.DrawPageBackground(ctx, page, area, bg);
                WhiteboardRenderer.DrawItems(ctx, page.Items);
            }
        }

        return new PageThumbnail(page, bitmap);
    }

    public void Invalidate(WhiteboardPage? page)
    {
        if (page is null)
            return;

        if (_cache.Remove(page, out var thumb))
            BitmapReleaser.Release(thumb.Bitmap);
    }

    public void Dispose()
    {
        foreach (var thumb in _cache.Values)
            BitmapReleaser.Release(thumb.Bitmap);

        _cache.Clear();
    }
}
