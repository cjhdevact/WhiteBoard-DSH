using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WhiteBoard.Services;

/// <summary>
/// 位图的延迟释放队列。
///
/// 背景：缩略图 / 缓存位图会被 Avalonia 的 Image 控件直接绑定到界面上。
/// 如果换缩略图时立刻 <c>Dispose()</c> 旧位图，而渲染线程还在用它，
/// Skia 侧就会访问已释放的原生内存 —— 表现为「切换底纹 / 换背景时突然崩溃」，
/// 这类崩溃没有托管异常，无法用 try/catch 兜住。
///
/// 做法：所有位图释放都排队，等若干帧（默认 3 帧）之后再真正释放，
/// 确保渲染管线已经不再引用它。程序退出时统一清空队列。
/// </summary>
public static class BitmapReleaser
{
    private static readonly List<(IDisposable Bitmap, int FramesLeft)> Queue = new();
    private static readonly object Gate = new();
    private static bool _hooked;

    /// <summary>延迟帧数：越大越安全，代价是内存回收稍慢。</summary>
    public static int DelayFrames { get; set; } = 3;

    /// <summary>把位图排入延迟释放队列。</summary>
    public static void Release(IDisposable? bitmap)
    {
        if (bitmap is null)
            return;

        EnsureHooked();

        lock (Gate)
        {
            Queue.Add((bitmap, DelayFrames));
        }

        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    /// <summary>推进一帧，释放到期的位图。</summary>
    public static void Flush()
    {
        List<IDisposable>? ready = null;

        lock (Gate)
        {
            for (var i = Queue.Count - 1; i >= 0; i--)
            {
                var (bitmap, frames) = Queue[i];
                frames--;

                if (frames <= 0)
                {
                    ready ??= new List<IDisposable>();
                    ready.Add(bitmap);
                    Queue.RemoveAt(i);
                }
                else
                {
                    Queue[i] = (bitmap, frames);
                }
            }
        }

        if (ready is null)
            return;

        foreach (var bitmap in ready)
        {
            try
            {
                bitmap.Dispose();
            }
            catch
            {
                // 释放失败不影响功能
            }
        }
    }

    /// <summary>立刻释放队列中的全部位图（退出时调用）。</summary>
    public static void FlushAll()
    {
        List<IDisposable> pending;

        lock (Gate)
        {
            pending = Queue.Select(x => x.Bitmap).ToList();
            Queue.Clear();
        }

        foreach (var bitmap in pending)
        {
            try
            {
                bitmap.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void EnsureHooked()
    {
        if (_hooked)
            return;

        _hooked = true;

        try
        {
            // 跟随渲染节奏推进队列
            if (Application.Current is not null)
            {
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background,
                    (_, _) => Flush());
                timer.Start();
            }
        }
        catch
        {
            // 没有 UI 线程（例如测试）时，Release 里的 Post 依然能推进
        }
    }
}
