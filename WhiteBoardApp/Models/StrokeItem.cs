using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace WhiteBoard.Models;

/// <summary>自由手写笔迹（笔 / 荧光笔 / 激光笔产生的轨迹）。</summary>
public sealed partial class StrokeItem : WhiteboardItem
{
    public List<StrokePoint> Points { get; set; } = new();

    /// <summary>是否为橡皮擦擦除后保留的“残段”。</summary>
    public bool IsEraserFragment { get; set; }

    [JsonIgnore]
    public override WhiteboardItemKind Kind => WhiteboardItemKind.Stroke;

    [JsonIgnore]
    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0)
                return default;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var pt in Points)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }

            var pad = Thickness * 0.5 + 1;
            return new Rect(minX - pad, minY - pad,
                            Math.Max(0.1, maxX - minX + pad * 2),
                            Math.Max(0.1, maxY - minY + pad * 2));
        }
    }

    public override bool HitTest(Point p, double tolerance)
    {
        if (Points.Count == 0)
            return false;

        var reach = Thickness * 0.5 + tolerance;
        var reachSq = reach * reach;

        // 单点（点一下）也算命中
        if (Points.Count == 1)
            return DistanceSquared(Points[0].ToPoint(), p) <= reachSq;

        for (var i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegmentSquared(p, Points[i - 1].ToPoint(), Points[i].ToPoint()) <= reachSq)
                return true;
        }

        return false;
    }

    public override WhiteboardItem Clone()
    {
        var copy = new StrokeItem
        {
            Points = Points.Select(x => x.Clone()).ToList(),
            IsEraserFragment = IsEraserFragment
        };
        CopyBaseTo(copy);
        return copy;
    }

    public override void Translate(Vector delta)
    {
        foreach (var pt in Points)
        {
            pt.X += delta.X;
            pt.Y += delta.Y;
        }

        // 位置变了，缓存的轮廓几何随之失效
        InvalidateGeometry();
    }

    /// <summary>
    /// 已缓存的轮廓几何。
    ///
    /// 白板每帧都要重画所有对象，如果每次都重新做样条细分 + 法线偏移，
    /// 一页几百条笔迹的渲染成本会非常高（实测 600 条要 80ms 以上，必然掉帧）。
    /// 已提交的笔迹是不变的，几何只算一次即可。
    /// </summary>
    [JsonIgnore]
    public Geometry? CachedGeometry
    {
        get
        {
            if (_geometryVersion == Points.Count && _geometry is not null)
                return _geometry;

            _geometry = Services.StrokeGeometryBuilder.Build(this);
            _geometryVersion = Points.Count;
            return _geometry;
        }
    }

    [JsonIgnore] private Geometry? _geometry;
    [JsonIgnore] private int _geometryVersion = -1;

    /// <summary>让缓存几何失效（点数变化或位置变化后必须调用）。</summary>
    public void InvalidateGeometry()
    {
        _geometry = null;
        _geometryVersion = -1;
    }

    /// <summary>把一条轨迹按 <paramref name="from"/> 起切成若干子轨迹（橡皮擦用）。</summary>
    public List<StrokeItem> SplitAt(int from)
    {
        var result = new List<StrokeItem>();
        if (from >= Points.Count)
            return result;

        var tail = new List<StrokePoint>(Points.Count - from);
        for (var i = from; i < Points.Count; i++)
            tail.Add(Points[i].Clone());

        var fragment = (StrokeItem)Clone();
        fragment.Points = tail;
        result.Add(fragment);
        return result;
    }

    internal static double DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    internal static double DistanceToSegmentSquared(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lenSq = dx * dx + dy * dy;

        if (lenSq < 1e-9)
            return DistanceSquared(p, a);

        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);

        var projX = a.X + t * dx;
        var projY = a.Y + t * dy;
        var ddx = p.X - projX;
        var ddy = p.Y - projY;
        return ddx * ddx + ddy * ddy;
    }
}
