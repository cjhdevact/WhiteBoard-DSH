using Avalonia;
using Avalonia.Media;
using WhiteBoard.Models;

namespace WhiteBoard.Services;

/// <summary>
/// 把笔迹采样点转换成有宽度的轮廓几何。
/// 使用 Catmull-Rom 曲线把折线细分成平滑曲线，再沿法线方向按笔压偏移，
/// 得到可以 Fill 的闭合多边形，从而支持带压感的粗细变化。
/// </summary>
public static class StrokeGeometryBuilder
{
    /// <summary>每个采样点之间插入的细分数。</summary>
    private const int Subdivisions = 4;

    /// <summary>
    /// 落笔 / 抬笔处额外插入的「收锋」长度（相对笔宽）。
    /// 参考 ui/note2.png：真实手写的起笔和收笔会自然变尖，而不是一刀切的圆头。
    /// </summary>
    private const double TaperLengthRatio = 0.45;

    /// <summary>收锋处的压力下限，保证尖而不至于细到看不见。</summary>
    private const double TaperMinPressure = 0.05;

    public static Geometry? Build(StrokeItem stroke)
    {
        var pts = stroke.Points;
        if (pts.Count == 0)
            return null;

        var baseHalf = Math.Max(0.35, stroke.Thickness * 0.5);

        // 单点：画一个圆点
        if (pts.Count == 1)
            return new EllipseGeometry(new Rect(
                pts[0].X - baseHalf, pts[0].Y - baseHalf, baseHalf * 2, baseHalf * 2));

        var smooth = Subdivide(pts);

        // 落笔 / 抬笔收锋：在两端各插一个压力极低的引导点
        TaperEnds(smooth, baseHalf);

        var left = new List<Point>(smooth.Count);
        var right = new List<Point>(smooth.Count);

        for (var i = 0; i < smooth.Count; i++)
        {
            var cur = smooth[i].ToPoint();

            // 用相邻点估算切线方向
            var prev = smooth[Math.Max(0, i - 1)].ToPoint();
            var next = smooth[Math.Min(smooth.Count - 1, i + 1)].ToPoint();

            var dx = next.X - prev.X;
            var dy = next.Y - prev.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);

            if (len < 1e-6)
            {
                dx = 1;
                dy = 0;
                len = 1;
            }

            var nx = -dy / len;
            var ny = dx / len;

            var p = Math.Clamp(smooth[i].Pressure, TaperMinPressure, 1.0);
            var half = baseHalf * (0.55 + 0.45 * p);

            left.Add(new Point(cur.X + nx * half, cur.Y + ny * half));
            right.Add(new Point(cur.X - nx * half, cur.Y - ny * half));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(left[0], true);

            // 左侧
            for (var i = 1; i < left.Count; i++)
                ctx.LineTo(left[i]);

            // 收笔端：压力已经收细，这里直接平滑连回右侧即可
            var tailHalf = baseHalf * (0.55 + 0.45 * Math.Clamp(smooth[^1].Pressure, TaperMinPressure, 1.0));
            DrawEndCap(ctx, left[^1], right[^1], smooth[^1].ToPoint(), tailHalf);

            // 右侧反向
            for (var i = right.Count - 1; i >= 0; i--)
                ctx.LineTo(right[i]);

            // 落笔端
            var headHalf = baseHalf * (0.55 + 0.45 * Math.Clamp(smooth[0].Pressure, TaperMinPressure, 1.0));
            DrawEndCap(ctx, right[0], left[0], smooth[0].ToPoint(), headHalf);

            ctx.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>
    /// 在笔迹两端插入收锋点：沿切线方向延伸一小段，压力降到最低，
    /// 于是轮廓在两端自然收窄成笔尖。
    /// </summary>
    private static void TaperEnds(List<StrokePoint> smooth, double baseHalf)
    {
        if (smooth.Count < 2)
            return;

        var taper = Math.Max(1.5, baseHalf * 2 * TaperLengthRatio);

        // 落笔端：在首个点「之前」延伸
        var head = smooth[0];
        var headNext = smooth[1];
        var dxHead = head.X - headNext.X;
        var dyHead = head.Y - headNext.Y;
        var lenHead = Math.Sqrt(dxHead * dxHead + dyHead * dyHead);

        if (lenHead > 1e-6)
        {
            var ux = dxHead / lenHead;
            var uy = dyHead / lenHead;

            smooth.Insert(0, new StrokePoint(
                head.X + ux * taper,
                head.Y + uy * taper,
                TaperMinPressure,
                head.T));

            // 原有的首点也压低一点，让收窄更平滑
            smooth[1].Pressure = Math.Min(smooth[1].Pressure, 0.45);
        }

        // 抬笔端：在末个点「之后」延伸
        var tail = smooth[^1];
        var tailPrev = smooth[^2];
        var dxTail = tail.X - tailPrev.X;
        var dyTail = tail.Y - tailPrev.Y;
        var lenTail = Math.Sqrt(dxTail * dxTail + dyTail * dyTail);

        if (lenTail > 1e-6)
        {
            var ux = dxTail / lenTail;
            var uy = dyTail / lenTail;

            smooth[^1].Pressure = Math.Min(smooth[^1].Pressure, 0.45);

            smooth.Add(new StrokePoint(
                tail.X + ux * taper,
                tail.Y + uy * taper,
                TaperMinPressure,
                tail.T));
        }
    }

    /// <summary>端头：收锋端直接连线，未收窄时补一个半圆头。</summary>
    private static void DrawEndCap(StreamGeometryContext ctx, Point from, Point to, Point center, double radius)
    {
        if (radius < 0.2)
        {
            ctx.LineTo(to);
            return;
        }

        ArcTo(ctx, from, to, center, radius);
    }

    /// <summary>半圆头：在 <paramref name="center"/> 处以半径 <paramref name="radius"/> 从左点绕到右点。</summary>
    private static void ArcTo(StreamGeometryContext ctx, Point from, Point to, Point center, double radius)
    {
        if (radius < 0.2)
        {
            ctx.LineTo(to);
            return;
        }

        // 判断绕行方向，保证端点是半圆而不是穿过图形的劣弧
        var cross = (from.X - center.X) * (to.Y - center.Y) - (from.Y - center.Y) * (to.X - center.X);
        ctx.ArcTo(to, new Size(radius, radius), 0, cross > 0, SweepDirection.CounterClockwise);
    }

    /// <summary>用 Catmull-Rom 样条细分折线，使手写线条平滑。</summary>
    private static List<StrokePoint> Subdivide(IReadOnlyList<StrokePoint> pts)
    {
        if (pts.Count < 3)
        {
            var simple = new List<StrokePoint>(pts.Count);
            foreach (var p in pts)
                simple.Add(p.Clone());
            return simple;
        }

        var result = new List<StrokePoint>((pts.Count - 1) * Subdivisions + 1);

        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];

            for (var s = 0; s < Subdivisions; s++)
            {
                var t = (double)s / Subdivisions;

                var t2 = t * t;
                var t3 = t2 * t;

                var x = 0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t +
                               (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                               (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);

                var y = 0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t +
                               (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                               (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);

                var pressure = p1.Pressure + (p2.Pressure - p1.Pressure) * t;
                var time = p1.T + (p2.T - p1.T) * t;

                result.Add(new StrokePoint(x, y, pressure, time));
            }
        }

        result.Add(pts[^1].Clone());
        return result;
    }

    /// <summary>
    /// 把长笔迹简化（Ramer–Douglas–Peucker），减少点数、加快渲染。
    /// </summary>
    public static List<StrokePoint> Simplify(IReadOnlyList<StrokePoint> points, double tolerance)
    {
        if (points.Count < 3 || tolerance <= 0)
            return points.Select(p => p.Clone()).ToList();

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        SimplifySegment(points, 0, points.Count - 1, tolerance * tolerance, keep);

        var result = new List<StrokePoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i].Clone());
        }

        return result;
    }

    private static void SimplifySegment(
        IReadOnlyList<StrokePoint> points, int first, int last, double tolSq, bool[] keep)
    {
        if (last <= first + 1)
            return;

        var a = points[first].ToPoint();
        var b = points[last].ToPoint();

        var maxDist = -1.0;
        var index = -1;

        for (var i = first + 1; i < last; i++)
        {
            var d = StrokeItem.DistanceToSegmentSquared(points[i].ToPoint(), a, b);
            if (d > maxDist)
            {
                maxDist = d;
                index = i;
            }
        }

        if (maxDist > tolSq && index > 0)
        {
            keep[index] = true;
            SimplifySegment(points, first, index, tolSq, keep);
            SimplifySegment(points, index, last, tolSq, keep);
        }
    }
}
