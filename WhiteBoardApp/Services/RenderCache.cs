using Avalonia;
using Avalonia.Media;

namespace WhiteBoard.Services;

/// <summary>
/// 单帧渲染资源缓存。
///
/// 白板每帧都会重画所有对象，如果每次都 new 一个 SolidColorBrush / Pen，
/// 一页几百个对象就会产生大量临时对象，触发 GC 造成掉帧——这正是
/// 「橡皮擦不跟手」的主要原因之一。
///
/// 缓存在每一帧开始时清空，因此不会长期持有对象，也不会因为颜色变化而失效。
/// </summary>
public sealed class RenderCache
{
    private readonly Dictionary<uint, IBrush> _brushes = new(64);
    private readonly Dictionary<(uint Color, double Thickness, byte Cap, byte Join), IPen> _pens = new(64);
    private readonly Dictionary<(double Radius, uint Color, bool Filled), IBrush> _dots = new(16);

    /// <summary>开始新的一帧。</summary>
    public void BeginFrame()
    {
        _brushes.Clear();
        _pens.Clear();
        _dots.Clear();
    }

    public IBrush Brush(Color color)
    {
        var key = Key(color);
        if (_brushes.TryGetValue(key, out var cached))
            return cached;

        var brush = new SolidColorBrush(color).ToImmutable();
        _brushes[key] = brush;
        return brush;
    }

    public IPen Pen(Color color, double thickness, PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
    {
        var key = (Key(color), Math.Round(thickness, 3), (byte)cap, (byte)join);
        if (_pens.TryGetValue(key, out var cached))
            return cached;

        var pen = new Pen(Brush(color), thickness)
        {
            LineCap = cap,
            LineJoin = join
        }.ToImmutable();

        _pens[key] = pen;
        return pen;
    }

    /// <summary>虚线画笔（方格纸的田字格会用到）。</summary>
    public IPen DashedPen(Color color, double thickness, double dashLength, double gapLength)
    {
        // 虚线的 dash 数组参与 key，这里按 (color, thickness, dash, gap) 做二级缓存
        var key = (Key(color), Math.Round(thickness, 3));
        var dashKey = HashCode.Combine(key, Math.Round(dashLength, 2), Math.Round(gapLength, 2));

        if (_dashPens.TryGetValue(dashKey, out var cached))
            return cached;

        var pen = new Pen(Brush(color), thickness, new DashStyle(new[] { dashLength, gapLength }, 0))
        {
            LineCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Miter
        }.ToImmutable();

        _dashPens[dashKey] = pen;
        return pen;
    }

    private readonly Dictionary<int, IPen> _dashPens = new(8);

    private static uint Key(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
