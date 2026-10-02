using System.Text.Json.Serialization;
using Avalonia;

namespace WhiteBoard.Models;

/// <summary>几何图形：直线、箭头、矩形、椭圆。</summary>
public sealed partial class ShapeItem : WhiteboardItem
{
    public Point Start { get; set; }

    public Point End { get; set; }

    public ShapeFill Fill { get; set; } = ShapeFill.None;

    [JsonIgnore]
    public override WhiteboardItemKind Kind => WhiteboardItemKind.Shape;

    [JsonIgnore]
    public override Rect Bounds
    {
        get
        {
            var pad = Thickness * 0.5 + 1;
            var r = new Rect(Start, End).Inflate(pad);
            return r.Width < 0.1 || r.Height < 0.1
                ? new Rect(r.X, r.Y, Math.Max(0.1, r.Width), Math.Max(0.1, r.Height))
                : r;
        }
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var reach = Thickness * 0.5 + tolerance;

        switch (Tool)
        {
            case WhiteboardTool.Line:
            case WhiteboardTool.Arrow:
                return StrokeItem.DistanceToSegmentSquared(p, Start, End) <= reach * reach;

            case WhiteboardTool.Ellipse:
            {
                var r = new Rect(Start, End);
                if (r.Width < 1 || r.Height < 1)
                    return StrokeItem.DistanceSquared(p, Start) <= reach * reach;

                if (Fill != ShapeFill.None && r.Contains(p))
                    return true;

                // 椭圆环命中：把点归一化到单位圆后判断半径差
                var cx = r.Center.X;
                var cy = r.Center.Y;
                var rx = r.Width / 2;
                var ry = r.Height / 2;
                var nx = (p.X - cx) / rx;
                var ny = (p.Y - cy) / ry;
                var norm = Math.Sqrt(nx * nx + ny * ny);
                var band = reach / Math.Min(rx, ry);
                return Math.Abs(norm - 1.0) <= Math.Max(band, 0.02);
            }

            default:
            {
                var r = new Rect(Start, End);
                if (r.Width < 1 || r.Height < 1)
                    return StrokeItem.DistanceSquared(p, Start) <= reach * reach;

                if (Fill != ShapeFill.None && r.Contains(p))
                    return true;

                var outer = r.Inflate(reach);
                var inner = r.Deflate(reach);
                return outer.Contains(p) && !inner.Contains(p);
            }
        }
    }

    public override WhiteboardItem Clone()
    {
        var copy = new ShapeItem { Start = Start, End = End, Fill = Fill };
        CopyBaseTo(copy);
        return copy;
    }

    public override void Translate(Vector delta)
    {
        Start = Start + delta;
        End = End + delta;
    }
}
