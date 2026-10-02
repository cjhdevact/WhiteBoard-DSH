using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WhiteBoard.Models;

namespace WhiteBoard.Services;

/// <summary>
/// 白板渲染器：把 <see cref="WhiteboardPage"/> 的内容画到任意 <see cref="DrawingContext"/> 上。
/// 所有绘制都走这里，从而保证「屏幕显示」「缩略图」「导出 PNG」三处效果完全一致。
///
/// 性能要点：
///  1. 画刷 / 画笔通过 <see cref="RenderCache"/> 复用，避免每帧产生大量临时对象；
///  2. 底纹（方格、点阵、五线谱、田字格）只绘制可视区域，不会铺满整张虚拟纸面。
/// </summary>
public static class WhiteboardRenderer
{
    /// <summary>荧光笔的不透明度。</summary>
    public const double HighlighterAlpha = 0.42;

    /// <summary>荧光笔的宽度放大倍数。</summary>
    public const double HighlighterWidthScale = 6.0;

    public static readonly Color DefaultLaserColor = Color.FromRgb(0xFF, 0x2D, 0x55);

    /// <summary>该笔画实际使用的颜色（含透明度）。</summary>
    public static Color ResolveColor(WhiteboardItem item)
    {
        var c = item.Color;
        var alpha = item.Tool switch
        {
            WhiteboardTool.Highlighter => HighlighterAlpha,
            WhiteboardTool.Laser => 0.95,
            _ => 1.0
        };

        alpha *= Math.Clamp(item.Opacity, 0.0, 1.0);
        return Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);
    }

    /// <summary>该笔画实际使用的粗细。</summary>
    public static double ResolveThickness(WhiteboardItem item)
        => item.Tool == WhiteboardTool.Highlighter
            ? item.Thickness * HighlighterWidthScale
            : item.Thickness;

    // ─────────────────────────────────────────────────────────────
    //  页面背景
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 绘制页面背景。
    /// <paramref name="visible"/> 是当前视口在文档坐标里的范围，
    /// 底纹只在这个范围内生成，避免为不可见区域做无用功。
    /// </summary>
    public static void DrawPageBackground(
        DrawingContext ctx,
        WhiteboardPage page,
        Rect area,
        IImage? backgroundImage = null,
        RenderCache? cache = null,
        Rect? visible = null)
    {
        cache ??= new RenderCache();
        var paintArea = visible.HasValue ? visible.Value.Intersect(area) : area;

        if (paintArea.Width <= 0 || paintArea.Height <= 0)
            paintArea = area;

        ctx.FillRectangle(cache.Brush(page.Background), area);

        if (backgroundImage is not null)
            ctx.DrawImage(backgroundImage, area);

        switch (page.BackgroundStyle)
        {
            case PageBackgroundStyle.Grid:
                DrawGrid(ctx, paintArea, 40, Color.FromArgb(90, 90, 120, 180), cache);
                break;
            case PageBackgroundStyle.Lines:
                DrawHorizontalLines(ctx, paintArea, 40, Color.FromArgb(90, 90, 120, 180), cache);
                break;
            case PageBackgroundStyle.Dots:
                DrawDots(ctx, paintArea, 40, Color.FromArgb(110, 90, 120, 180), cache);
                break;
            case PageBackgroundStyle.Music:
                DrawMusicStaff(ctx, paintArea, Color.FromArgb(120, 60, 60, 60), cache);
                break;
            case PageBackgroundStyle.TianZiGe:
                DrawTianZiGe(ctx, paintArea, 96, Color.FromArgb(95, 200, 80, 80), cache);
                break;
        }
    }

    /// <summary>限制底纹单元数量，避免极端缩放时生成海量图元。</summary>
    private const int MaxPrimitives = 4000;

    private static void DrawGrid(DrawingContext ctx, Rect area, double step, Color color, RenderCache cache)
    {
        var minorPen = cache.Pen(WithAlpha(color, 0.45), 1);
        var majorPen = cache.Pen(color, 1.2);

        // 单元过小时先放大步距，保证图元数量可控
        step = EnsureDensity(area, step, out var majorEvery, out var indexX, out var indexY);

        for (var x = indexX * step; x <= area.Right; x += step, indexX++)
        {
            var pen = indexX % majorEvery == 0 ? majorPen : minorPen;
            ctx.DrawLine(pen, new Point(x, area.Y), new Point(x, area.Bottom));
        }

        for (var y = indexY * step; y <= area.Bottom; y += step, indexY++)
        {
            var pen = indexY % majorEvery == 0 ? majorPen : minorPen;
            ctx.DrawLine(pen, new Point(area.X, y), new Point(area.Right, y));
        }
    }

    private static void DrawHorizontalLines(DrawingContext ctx, Rect area, double step, Color color, RenderCache cache)
    {
        var pen = cache.Pen(color, 1);
        step = EnsureDensity(area, step, out _, out _, out var indexY);

        for (var y = indexY * step; y <= area.Bottom; y += step, indexY++)
        {
            // 每 5 条加粗一条，更像作业本
            var p = indexY % 10 == 0 ? cache.Pen(color, 1.6) : pen;
            ctx.DrawLine(p, new Point(area.X, y), new Point(area.Right, y));
        }
    }

    private static void DrawDots(DrawingContext ctx, Rect area, double step, Color color, RenderCache cache)
    {
        var brush = cache.Brush(color);
        step = EnsureDensity(area, step, out _, out var indexX, out var indexY);

        for (var x = indexX * step; x <= area.Right; x += step)
        {
            for (var y = indexY * step; y <= area.Bottom; y += step)
                ctx.FillRectangle(brush, new Rect(x - 1.2, y - 1.2, 2.4, 2.4));
        }
    }

    private static void DrawMusicStaff(DrawingContext ctx, Rect area, Color color, RenderCache cache)
    {
        var pen = cache.Pen(color, 1.2);
        const double staffGap = 14;
        const double groupGap = 96;

        var first = Math.Floor(area.Y / groupGap) * groupGap;
        var guard = 0;

        for (var top = first; top + staffGap * 4 <= area.Bottom && guard++ < 400; top += groupGap)
        {
            for (var i = 0; i < 5; i++)
            {
                var y = top + 40 + i * staffGap;
                if (y < area.Y - 1 || y > area.Bottom + 1)
                    continue;

                ctx.DrawLine(pen, new Point(area.X, y), new Point(area.Right, y));
            }
        }
    }

    private static void DrawTianZiGe(DrawingContext ctx, Rect area, double size, Color color, RenderCache cache)
    {
        var solid = cache.Pen(color, 1.4);
        var dashed = cache.DashedPen(WithAlpha(color, 0.7), 1, 4, 3);

        size = EnsureDensity(area, size, out _, out var startX, out var startY);

        var guard = 0;
        for (var x = startX * size; x + size <= area.Right && guard++ < 4000; x += size)
        {
            for (var y = startY * size; y + size <= area.Bottom && guard++ < 4000; y += size)
            {
                var cell = new Rect(x, y, size, size);
                ctx.DrawRectangle(null, solid, cell);
                ctx.DrawLine(dashed, new Point(cell.Center.X, cell.Y), new Point(cell.Center.X, cell.Bottom));
                ctx.DrawLine(dashed, new Point(cell.X, cell.Center.Y), new Point(cell.Right, cell.Center.Y));
            }
        }
    }

    /// <summary>
    /// 保证底纹在可视区域内的图元数量不超过 <see cref="MaxPrimitives"/>：
    /// 必要时按 2 的幂放大步距，并返回对齐后的起始序号。
    /// </summary>
    private static double EnsureDensity(Rect area, double step, out int majorEvery, out int startX, out int startY)
    {
        majorEvery = 5;

        if (step < 1)
            step = 1;

        // 水平、垂直方向的单元数上限各取一半
        var maxPerAxis = Math.Max(8, MaxPrimitives / 2);
        var scale = 1;

        while (area.Width / (step * scale) > maxPerAxis || area.Height / (step * scale) > maxPerAxis)
            scale *= 2;

        if (scale > 1)
        {
            step *= scale;
            majorEvery = 5;
        }

        startX = (int)Math.Floor(area.X / step);
        startY = (int)Math.Floor(area.Y / step);
        return step;
    }

    private static Color WithAlpha(Color c, double factor)
        => Color.FromArgb((byte)Math.Clamp(c.A * factor, 0, 255), c.R, c.G, c.B);

    // ─────────────────────────────────────────────────────────────
    //  绘制对象
    // ─────────────────────────────────────────────────────────────

    public static void DrawItems(DrawingContext ctx, IEnumerable<WhiteboardItem> items, RenderCache? cache = null)
    {
        cache ??= new RenderCache();

        foreach (var item in items)
            DrawItem(ctx, item, cache);
    }

    public static void DrawItem(DrawingContext ctx, WhiteboardItem item, RenderCache? cache = null)
    {
        cache ??= new RenderCache();

        switch (item)
        {
            case StrokeItem stroke:
                DrawStroke(ctx, stroke, cache);
                break;
            case ShapeItem shape:
                DrawShape(ctx, shape, cache);
                break;
            case TextItem text:
                DrawText(ctx, text, cache);
                break;
        }
    }

    public static void DrawStroke(DrawingContext ctx, StrokeItem stroke, RenderCache? cache = null)
    {
        if (stroke.Points.Count == 0)
            return;

        cache ??= new RenderCache();

        var color = ResolveColor(stroke);
        var thickness = Math.Max(0.6, ResolveThickness(stroke));

        // 荧光笔和激光笔使用等宽笔触（叠色效果靠半透明实现）
        if (stroke.Tool is WhiteboardTool.Highlighter or WhiteboardTool.Laser)
        {
            var pen = cache.Pen(color, Math.Max(0.6, thickness), PenLineCap.Round, PenLineJoin.Round);
            var brush = cache.Brush(color);

            if (stroke.Points.Count == 1)
            {
                var p = stroke.Points[0];
                ctx.DrawEllipse(brush, null, p.ToPoint(), thickness / 2, thickness / 2);
                return;
            }

            ctx.DrawGeometry(null, pen, BuildPolyline(stroke.Points));
            return;
        }

        // 普通笔：带压感的变宽轮廓（几何带缓存，只算一次）
        var geometry = stroke.CachedGeometry;
        if (geometry is not null)
            ctx.DrawGeometry(cache.Brush(color), null, geometry);
    }

    private static Geometry BuildPolyline(IReadOnlyList<StrokePoint> points)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(points[0].ToPoint(), false);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(points[i].ToPoint());
            ctx.EndFigure(false);
        }

        return geo;
    }

    public static void DrawShape(DrawingContext ctx, ShapeItem shape, RenderCache? cache = null)
    {
        cache ??= new RenderCache();

        var color = ResolveColor(shape);
        var thickness = Math.Max(0.5, shape.Thickness);
        var pen = cache.Pen(color, thickness, PenLineCap.Round, PenLineJoin.Round);

        IBrush? fill = shape.Fill switch
        {
            ShapeFill.Solid => cache.Brush(Color.FromArgb(255, color.R, color.G, color.B)),
            ShapeFill.Translucent => cache.Brush(Color.FromArgb(64, color.R, color.G, color.B)),
            _ => null
        };

        switch (shape.Tool)
        {
            case WhiteboardTool.Line:
                ctx.DrawLine(pen, shape.Start, shape.End);
                break;

            case WhiteboardTool.Arrow:
                DrawArrow(ctx, pen, color, shape.Start, shape.End, thickness, cache);
                break;

            case WhiteboardTool.Ellipse:
            {
                var r = new Rect(shape.Start, shape.End);
                ctx.DrawEllipse(fill, pen, r.Center, r.Width / 2, r.Height / 2);
                break;
            }

            default:
                ctx.DrawRectangle(fill, pen, new Rect(shape.Start, shape.End));
                break;
        }
    }

    private static void DrawArrow(
        DrawingContext ctx, IPen pen, Color color, Point start, Point end, double thickness, RenderCache cache)
    {
        ctx.DrawLine(pen, start, end);

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3)
            return;

        var ux = dx / len;
        var uy = dy / len;

        var head = Math.Clamp(thickness * 4.0, 8.0, 42.0);
        var spread = 0.46; // 弧度

        Point Rotate(double angle, double scale)
        {
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            return new Point(
                end.X - (ux * cos - uy * sin) * head * scale,
                end.Y - (ux * sin + uy * cos) * head * scale);
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(end, true);
            g.LineTo(Rotate(spread, 1));
            g.LineTo(Rotate(0, 0.72));
            g.LineTo(Rotate(-spread, 1));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(cache.Brush(color), null, geo);
    }

    public static void DrawText(DrawingContext ctx, TextItem item, RenderCache? cache = null)
    {
        if (string.IsNullOrEmpty(item.Text))
            return;

        cache ??= new RenderCache();

        var typeface = new Typeface(
            new FontFamily(item.FontFamily),
            item.ResolvedFontStyle,
            item.ResolvedFontWeight);

        var ft = new FormattedText(
            item.Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            item.FontSize,
            cache.Brush(ResolveColor(item)));

        ctx.DrawText(ft, item.Position);
    }

    // ─────────────────────────────────────────────────────────────
    //  离屏渲染（缩略图 / 导出）
    // ─────────────────────────────────────────────────────────────

    /// <summary>把一页离屏渲染成位图。</summary>
    public static RenderTargetBitmap RenderPage(
        WhiteboardPage page,
        PixelSize pixelSize,
        double logicalWidth,
        double logicalHeight,
        IImage? backgroundImage = null)
    {
        var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        var cache = new RenderCache();
        cache.BeginFrame();

        using var ctx = bitmap.CreateDrawingContext();
        var area = new Rect(0, 0, logicalWidth, logicalHeight);
        DrawPageBackground(ctx, page, area, backgroundImage, cache);
        DrawItems(ctx, page.Items, cache);

        return bitmap;
    }
}
