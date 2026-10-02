using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhiteBoard.Models;

/// <summary>手写轨迹上的一个采样点。</summary>
public sealed class StrokePoint
{
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>笔压（0.1 ~ 1.0），鼠标输入固定为 1.0。</summary>
    public double Pressure { get; set; } = 1.0;

    /// <summary>采样时间（毫秒），用于激光笔淡出。</summary>
    public double T { get; set; }

    public StrokePoint() { }

    public StrokePoint(double x, double y, double pressure = 1.0, double t = 0)
    {
        X = x;
        Y = y;
        Pressure = pressure;
        T = t;
    }

    public Point ToPoint() => new(X, Y);

    public StrokePoint Clone() => new(X, Y, Pressure, T);
}

/// <summary>
/// 白板上的一个可绘制对象。派生类型通过 JSON 的 "kind" 字段区分，
/// 便于 .wbd 文件向后兼容。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StrokeItem), "stroke")]
[JsonDerivedType(typeof(ShapeItem), "shape")]
[JsonDerivedType(typeof(TextItem), "text")]
public abstract partial class WhiteboardItem : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private WhiteboardTool _tool = WhiteboardTool.Pen;

    [ObservableProperty]
    private Color _color = Colors.Black;

    [ObservableProperty]
    private double _thickness = 3;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private double _opacity = 1.0;

    [JsonIgnore]
    public abstract WhiteboardItemKind Kind { get; }

    /// <summary>对象的外接矩形（文档坐标）。</summary>
    [JsonIgnore]
    public abstract Rect Bounds { get; }

    /// <summary>该对象在 <paramref name="area"/> 内是否可能可见（用于视口裁剪）。</summary>
    public bool IsVisibleIn(Rect area)
    {
        var b = Bounds;
        return b.Width > 0 && b.Height > 0 && area.Intersects(b);
    }

    /// <summary>命中测试。</summary>
    public abstract bool HitTest(Point p, double tolerance);

    public abstract WhiteboardItem Clone();

    /// <summary>整体平移。</summary>
    public abstract void Translate(Vector delta);

    protected void CopyBaseTo(WhiteboardItem target)
    {
        target.Id = Guid.NewGuid().ToString("N");
        target.Tool = Tool;
        target.Color = Color;
        target.Thickness = Thickness;
        target.Opacity = Opacity;
    }
}
