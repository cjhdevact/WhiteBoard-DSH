using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace WhiteBoard.Models;

/// <summary>文本标注。字号记录在 <see cref="WhiteboardItem.Thickness"/> 的 3 倍上，便于复用画笔粗细滑杆。</summary>
public sealed partial class TextItem : WhiteboardItem
{
    public string Text { get; set; } = string.Empty;

    public Point Position { get; set; }

    /// <summary>字号（像素）。</summary>
    public double FontSize { get; set; } = 24;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    [JsonIgnore]
    public override WhiteboardItemKind Kind => WhiteboardItemKind.Text;

    [JsonIgnore]
    public override Rect Bounds
    {
        get
        {
            var size = Measure();
            return new Rect(Position.X, Position.Y, Math.Max(8, size.Width), Math.Max(8, size.Height));
        }
    }

    public override bool HitTest(Point p, double tolerance) => Bounds.Inflate(tolerance).Contains(p);

    public override WhiteboardItem Clone()
    {
        var copy = new TextItem
        {
            Text = Text,
            Position = Position,
            FontSize = FontSize,
            Bold = Bold,
            Italic = Italic,
            FontFamily = FontFamily
        };
        CopyBaseTo(copy);
        return copy;
    }

    public override void Translate(Vector delta) => Position += delta;

    /// <summary>
    /// 文本尺寸的近似估算。真实排版由控件测量完成，这里按
    /// 「中日韩字符占 1 个字宽、其他占 0.55」估算，足够用于命中测试与选区。
    /// </summary>
    public Size Measure()
    {
        var lines = Text.Split('\n');
        double maxWidth = 0;
        var lineHeight = FontSize * 1.35;

        foreach (var line in lines)
        {
            var w = 0.0;
            foreach (var ch in line)
                w += ch > 0x2E80 ? FontSize : FontSize * 0.55;
            if (w > maxWidth) maxWidth = w;
        }

        return new Size(maxWidth, Math.Max(lineHeight, lines.Length * lineHeight));
    }

    public FontStyle ResolvedFontStyle => Italic ? FontStyle.Italic : FontStyle.Normal;

    public FontWeight ResolvedFontWeight => Bold ? FontWeight.Bold : FontWeight.Normal;
}
