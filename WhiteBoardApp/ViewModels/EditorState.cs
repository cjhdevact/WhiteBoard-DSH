using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.Services;

namespace WhiteBoard.ViewModels;

/// <summary>调色板 / 粗细 / 工具选项栏里用到的一个颜色项。</summary>
public sealed partial class PaletteColor : ObservableObject
{
    public PaletteColor(string name, Color color)
    {
        Name = name;
        Color = color;
        Hex = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        Brush = new SolidColorBrush(color);
    }

    public string Name { get; }

    public Color Color { get; }

    public string Hex { get; }

    public IBrush Brush { get; }
}

/// <summary>粗细选项。</summary>
public sealed class ThicknessOption
{
    public ThicknessOption(double value, string label)
    {
        Value = value;
        Label = label;
    }

    public double Value { get; }

    public string Label { get; }

    public double PreviewSize => Math.Clamp(Value * 1.6, 3, 22);
}

/// <summary>
/// 主窗口与桌面批注层共享的“当前笔状态”。
/// 两个窗口绑定同一个实例，因此在批注层换的颜色会同步到白板工具栏。
/// </summary>
public abstract partial class EditorState : ObservableObject
{
    protected EditorState(AppSettings settings)
    {
        PenColor = AppSettings.ParseColor(settings.LastPenColor, Colors.White);
        PenThickness = settings.PenThickness <= 0 ? 3 : settings.PenThickness;
        HighlighterThickness = settings.HighlighterThickness <= 0 ? 8 : settings.HighlighterThickness;
        EraserRadius = settings.EraserRadius <= 0 ? 14 : settings.EraserRadius;
        FontSize = settings.DefaultFontSize <= 0 ? 28 : settings.DefaultFontSize;
        FontFamily = string.IsNullOrWhiteSpace(settings.DefaultFontFamily) ? "Microsoft YaHei UI" : settings.DefaultFontFamily;

        EraserMode = Enum.TryParse<EraserMode>(settings.EraserMode, out var em) ? em : EraserMode.Pixel;
        ShapeFill = Enum.TryParse<ShapeFill>(settings.ShapeFill, out var sf) ? sf : ShapeFill.None;
    }

    // ── 工具 ────────────────────────────────────────────────

    [ObservableProperty]
    private WhiteboardTool _activeTool = WhiteboardTool.Pen;

    public bool IsSelectTool => ActiveTool == WhiteboardTool.Select;
    public bool IsPenTool => ActiveTool == WhiteboardTool.Pen;
    public bool IsHighlighterTool => ActiveTool == WhiteboardTool.Highlighter;
    public bool IsLaserTool => ActiveTool == WhiteboardTool.Laser;
    public bool IsEraserTool => ActiveTool == WhiteboardTool.Eraser;
    public bool IsShapeTool => ActiveTool is WhiteboardTool.Line or WhiteboardTool.Arrow
        or WhiteboardTool.Rectangle or WhiteboardTool.Ellipse;
    public bool IsTextTool => ActiveTool == WhiteboardTool.Text;

    /// <summary>当前是不是“画笔类”工具（决定颜色/粗细栏是否可用）。</summary>
    public bool IsInkTool => ActiveTool is WhiteboardTool.Pen or WhiteboardTool.Highlighter
        or WhiteboardTool.Line or WhiteboardTool.Arrow or WhiteboardTool.Rectangle
        or WhiteboardTool.Ellipse or WhiteboardTool.Text or WhiteboardTool.Laser;

    /// <summary>形状选项（填充方式）是否显示。</summary>
    public bool IsShapeOptionsVisible => ActiveTool is WhiteboardTool.Line or WhiteboardTool.Arrow
        or WhiteboardTool.Rectangle or WhiteboardTool.Ellipse;

    public bool IsEraserOptionsVisible => ActiveTool == WhiteboardTool.Eraser;

    public bool IsTextOptionsVisible => ActiveTool == WhiteboardTool.Text;

    public bool IsColorOptionsVisible => IsInkTool && ActiveTool != WhiteboardTool.Eraser;

    /// <summary>选项栏整体是否显示。</summary>
    public bool IsOptionsBarVisible => IsInkTool || ActiveTool == WhiteboardTool.Eraser;

    partial void OnActiveToolChanged(WhiteboardTool value)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsPenTool));
        OnPropertyChanged(nameof(IsHighlighterTool));
        OnPropertyChanged(nameof(IsLaserTool));
        OnPropertyChanged(nameof(IsEraserTool));
        OnPropertyChanged(nameof(IsShapeTool));
        OnPropertyChanged(nameof(IsTextTool));
        OnPropertyChanged(nameof(IsInkTool));
        OnPropertyChanged(nameof(IsShapeOptionsVisible));
        OnPropertyChanged(nameof(IsEraserOptionsVisible));
        OnPropertyChanged(nameof(IsTextOptionsVisible));
        OnPropertyChanged(nameof(IsColorOptionsVisible));
        OnPropertyChanged(nameof(IsOptionsBarVisible));
        OnPropertyChanged(nameof(ToolDisplayName));
    }

    public string ToolDisplayName => ActiveTool switch
    {
        WhiteboardTool.Select => "选择",
        WhiteboardTool.Pen => "硬笔",
        WhiteboardTool.Highlighter => "荧光笔",
        WhiteboardTool.Laser => "激光笔",
        WhiteboardTool.Eraser => "橡皮擦",
        WhiteboardTool.Line => "直线",
        WhiteboardTool.Arrow => "箭头",
        WhiteboardTool.Rectangle => "矩形",
        WhiteboardTool.Ellipse => "椭圆",
        WhiteboardTool.Text => "文本",
        WhiteboardTool.Lasso => "套索",
        _ => "硬笔"
    };

    [RelayCommand]
    private void SelectTool(WhiteboardTool tool) => ActiveTool = tool;

    [RelayCommand]
    private void SelectToolByName(string name)
    {
        if (Enum.TryParse<WhiteboardTool>(name, true, out var tool))
            ActiveTool = tool;
    }

    // ── 颜色 ────────────────────────────────────────────────

    [ObservableProperty]
    private Color _penColor;

    public IBrush PenBrush => new SolidColorBrush(PenColor);

    public string PenColorHex => $"#{PenColor.R:X2}{PenColor.G:X2}{PenColor.B:X2}";

    /// <summary>当前颜色在调色板里的位置（没有匹配项时为 -1）。</summary>
    public int PaletteIndex
    {
        get
        {
            for (var i = 0; i < Palette.Count; i++)
            {
                if (Palette[i].Color == PenColor)
                    return i;
            }

            return -1;
        }
    }

    partial void OnPenColorChanged(Color value)
    {
        OnPropertyChanged(nameof(PenBrush));
        OnPropertyChanged(nameof(PenColorHex));
        OnPropertyChanged(nameof(PaletteIndex));

        // 拆成 R/G/B 通知，自定义颜色的滑杆与预览才会跟着动
        OnPropertyChanged(nameof(ColorR));
        OnPropertyChanged(nameof(ColorG));
        OnPropertyChanged(nameof(ColorB));
        OnPropertyChanged(nameof(ColorPreviewBrush));
        OnPropertyChanged(nameof(IsCustomColor));
    }

    /// <summary>
    /// 自定义颜色的红通道。
    /// Avalonia 的 Color 是只读结构体，不能直接双向绑定 <c>PenColor.R</c>，
    /// 所以拆成三个可写属性，滑杆拖动时才真的会改到颜色。
    /// </summary>
    public byte ColorR
    {
        get => PenColor.R;
        set => PenColor = Color.FromArgb(PenColor.A, value, PenColor.G, PenColor.B);
    }

    public byte ColorG
    {
        get => PenColor.G;
        set => PenColor = Color.FromArgb(PenColor.A, PenColor.R, value, PenColor.B);
    }

    public byte ColorB
    {
        get => PenColor.B;
        set => PenColor = Color.FromArgb(PenColor.A, PenColor.R, PenColor.G, value);
    }

    /// <summary>自定义颜色的实时预览。</summary>
    public IBrush ColorPreviewBrush => new SolidColorBrush(PenColor);

    /// <summary>当前颜色是否不在预设调色板里。</summary>
    public bool IsCustomColor => PaletteIndex < 0;

    /// <summary>按 R/G/B 直接设置颜色（也支持 #RRGGBB 文本）。</summary>
    [RelayCommand]
    private void SetColorRgb(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        var parts = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 3
            && byte.TryParse(parts[0], out var r)
            && byte.TryParse(parts[1], out var g)
            && byte.TryParse(parts[2], out var b))
        {
            PenColor = Color.FromRgb(r, g, b);
        }
    }

    /// <summary>从十六进制文本设置颜色（支持 #RGB / #RRGGBB / #AARRGGBB）。</summary>
    [RelayCommand]
    private void SetColorHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;

        var text = hex.Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;

        try
        {
            PenColor = Color.Parse(text);
        }
        catch
        {
            // 输入非法时保持原色
        }
    }

    public IReadOnlyList<PaletteColor> Palette { get; } = new[]
    {
        new PaletteColor("黑", Color.FromRgb(0x1A, 0x1A, 0x1A)),
        new PaletteColor("红", Color.FromRgb(0xE8, 0x1F, 0x1F)),
        new PaletteColor("橙", Color.FromRgb(0xF2, 0x7A, 0x0D)),
        new PaletteColor("黄", Color.FromRgb(0xF5, 0xC2, 0x11)),
        new PaletteColor("绿", Color.FromRgb(0x1E, 0xA8, 0x55)),
        new PaletteColor("青", Color.FromRgb(0x14, 0xB8, 0xC4)),
        new PaletteColor("蓝", Color.FromRgb(0x1B, 0x6F, 0xE0)),
        new PaletteColor("紫", Color.FromRgb(0x7A, 0x3F, 0xC9)),
        new PaletteColor("粉", Color.FromRgb(0xE8, 0x4C, 0x9B)),
        new PaletteColor("棕", Color.FromRgb(0x8A, 0x5A, 0x2B)),
        new PaletteColor("灰", Color.FromRgb(0x77, 0x7F, 0x8A)),
        new PaletteColor("白", Colors.White)
    };

    [RelayCommand]
    private void PickColor(PaletteColor? color)
    {
        if (color is not null)
            PenColor = color.Color;
    }

    [RelayCommand]
    private void SwapBlackWhite()
        => PenColor = PenColor.R + PenColor.G + PenColor.B < 200 ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);

    /// <summary>请求宿主打开屏幕取色器（需要访问桌面像素，由 View 实现）。</summary>
    public event EventHandler? PickScreenColorRequested;

    [RelayCommand]
    private void PickScreenColor() => PickScreenColorRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>直接指定颜色（十六进制字符串）。</summary>
    [RelayCommand]
    private void SetPenColorHex(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
            PenColor = AppSettings.ParseColor(hex, PenColor);
    }

    // ── 粗细 ────────────────────────────────────────────────

    [ObservableProperty]
    private double _penThickness;

    [ObservableProperty]
    private double _highlighterThickness;

    /// <summary>当前笔实际生效的粗细（荧光笔有自己的默认值）。</summary>
    public double EffectiveThickness
    {
        get => ActiveTool == WhiteboardTool.Highlighter ? HighlighterThickness : PenThickness;
        set
        {
            if (ActiveTool == WhiteboardTool.Highlighter)
                HighlighterThickness = value;
            else
                PenThickness = value;
        }
    }

    partial void OnPenThicknessChanged(double value) => OnPropertyChanged(nameof(EffectiveThickness));

    partial void OnHighlighterThicknessChanged(double value) => OnPropertyChanged(nameof(EffectiveThickness));

    public IReadOnlyList<ThicknessOption> ThicknessOptions { get; } = new[]
    {
        new ThicknessOption(1.5, "细"),
        new ThicknessOption(3, "中"),
        new ThicknessOption(6, "粗"),
        new ThicknessOption(10, "特粗")
    };

    [RelayCommand]
    private void PickThickness(ThicknessOption? option)
    {
        if (option is not null)
            EffectiveThickness = option.Value;
    }

    [RelayCommand]
    private void PickThicknessValue(double value) => EffectiveThickness = value;

    // ── 橡皮 / 图形选项 ─────────────────────────────────────

    [ObservableProperty]
    private EraserMode _eraserMode = EraserMode.Pixel;

    [ObservableProperty]
    private double _eraserRadius;

    public bool IsPixelEraser => EraserMode == EraserMode.Pixel;

    public bool IsStrokeEraser => EraserMode == EraserMode.Stroke;

    partial void OnEraserModeChanged(EraserMode value)
    {
        OnPropertyChanged(nameof(IsPixelEraser));
        OnPropertyChanged(nameof(IsStrokeEraser));
    }

    /// <summary>切换橡皮模式。参数用字符串，便于 XAML 的 CommandParameter 直接传枚举名。</summary>
    [RelayCommand]
    private void SetEraserMode(string? name)
    {
        if (Enum.TryParse<EraserMode>(name, true, out var mode))
            EraserMode = mode;
    }

    /// <summary>切换橡皮模式（强类型重载，供 C# 调用）。</summary>
    public void SetEraserMode(EraserMode mode) => EraserMode = mode;

    [ObservableProperty]
    private ShapeFill _shapeFill = ShapeFill.None;

    public bool IsFillNone => ShapeFill == ShapeFill.None;

    public bool IsFillTranslucent => ShapeFill == ShapeFill.Translucent;

    public bool IsFillSolid => ShapeFill == ShapeFill.Solid;

    partial void OnShapeFillChanged(ShapeFill value)
    {
        OnPropertyChanged(nameof(IsFillNone));
        OnPropertyChanged(nameof(IsFillTranslucent));
        OnPropertyChanged(nameof(IsFillSolid));
    }

    /// <summary>切换图形填充方式。参数用字符串，便于 XAML 的 CommandParameter 直接传枚举名。</summary>
    [RelayCommand]
    private void SetShapeFill(string? name)
    {
        if (Enum.TryParse<ShapeFill>(name, true, out var fill))
            ShapeFill = fill;
    }

    /// <summary>切换图形填充方式（强类型重载，供 C# 调用）。</summary>
    public void SetShapeFill(ShapeFill fill) => ShapeFill = fill;

    // ── 文本 ────────────────────────────────────────────────

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private string _fontFamily = "Microsoft YaHei UI";

    public IReadOnlyList<string> FontFamilies { get; } = new[]
    {
        "Microsoft YaHei UI",
        "微软雅黑",
        "SimSun",
        "SimHei",
        "KaiTi",
        "Segoe UI",
        "Arial",
        "Consolas"
    };

    // ── 画面显示 ────────────────────────────────────────────

    [ObservableProperty]
    private bool _showGrid = true;

    [ObservableProperty]
    private bool _laserEnabled = true;

    /// <summary>把设置写回持久化对象。</summary>
    public void FlushToSettings(AppSettings settings)
    {
        settings.LastPenColor = $"#{PenColor.A:X2}{PenColor.R:X2}{PenColor.G:X2}{PenColor.B:X2}";
        settings.PenThickness = PenThickness;
        settings.HighlighterThickness = HighlighterThickness;
        settings.EraserRadius = EraserRadius;
        settings.EraserMode = EraserMode.ToString();
        settings.ShapeFill = ShapeFill.ToString();
        settings.LaserEnabled = LaserEnabled;
        settings.ShowGrid = ShowGrid;
        settings.DefaultFontSize = FontSize;
        settings.DefaultFontFamily = FontFamily;
    }
}
