using Avalonia.Media.Imaging;

namespace WhiteBoard.Services;

/// <summary>
/// 桌面截图（Base64）的共享缓存。
/// 缩略图、导出、桌面批注层都会用到同一张背景图，缓存可以避免反复解码。
/// </summary>
public static class BackgroundImageCache
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Order = new();

    private const int MaxEntries = 12;

    public static Bitmap? Get(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(base64, out var cached))
            {
                Order.Remove(base64);
                Order.AddFirst(base64);
                return cached;
            }

            try
            {
                var bytes = Convert.FromBase64String(base64);
                using var ms = new MemoryStream(bytes);
                var bitmap = new Bitmap(ms);

                Cache[base64] = bitmap;
                Order.AddFirst(base64);

                while (Order.Count > MaxEntries)
                {
                    var last = Order.Last!.Value;
                    Order.RemoveLast();

                    // 同样延迟释放：位图可能正被界面上的 Image 使用
                    if (Cache.Remove(last, out var evicted))
                        BitmapReleaser.Release(evicted);
                }

                return bitmap;
            }
            catch
            {
                // 数据损坏时静默降级为纯色背景
                return null;
            }
        }
    }

    public static void Clear()
    {
        lock (Cache)
        {
            foreach (var bmp in Cache.Values)
                BitmapReleaser.Release(bmp);

            Cache.Clear();
            Order.Clear();
        }
    }
}
