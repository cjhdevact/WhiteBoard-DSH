using System.Text.Json.Serialization;

namespace WhiteBoard.Models;

/// <summary>白板可用的书写/绘制工具。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WhiteboardTool
{
    /// <summary>选择 / 移动（框选、拖动笔画）。</summary>
    Select = 0,

    /// <summary>硬笔（普通书写笔）。</summary>
    Pen = 1,

    /// <summary>荧光笔（半透明、粗线条、叠加变深）。</summary>
    Highlighter = 2,

    /// <summary>激光笔（临时轨迹，自动淡出，不写入文档）。</summary>
    Laser = 3,

    /// <summary>橡皮擦（按笔画擦除）。</summary>
    Eraser = 4,

    /// <summary>直线。</summary>
    Line = 5,

    /// <summary>箭头。</summary>
    Arrow = 6,

    /// <summary>矩形。</summary>
    Rectangle = 7,

    /// <summary>椭圆。</summary>
    Ellipse = 8,

    /// <summary>文本标注。</summary>
    Text = 9,

    /// <summary>套索 / 自由选区。</summary>
    Lasso = 10
}

/// <summary>形状的填充方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShapeFill
{
    /// <summary>只有边框。</summary>
    None = 0,

    /// <summary>半透明填充。</summary>
    Translucent = 1,

    /// <summary>不透明填充。</summary>
    Solid = 2
}

/// <summary>对象类型，用于序列化与渲染分派。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WhiteboardItemKind
{
    Stroke = 0,
    Shape = 1,
    Text = 2,
    Image = 3
}
