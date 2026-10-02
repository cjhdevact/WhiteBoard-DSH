using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhiteBoard.Models;

/// <summary>一页白板。每页独立保存背景与所有绘制对象。</summary>
public sealed partial class WhiteboardPage : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private Color _background = Color.FromRgb(0x0E, 0x3A, 0x2F);

    /// <summary>背景样式：纯色 / 方格 / 横线 / 点阵。</summary>
    [ObservableProperty]
    private PageBackgroundStyle _backgroundStyle = PageBackgroundStyle.Blank;

    /// <summary>桌面批注页所使用的原始截图（PNG 的 Base64）。为空表示纯色背景页。</summary>
    [ObservableProperty]
    private string? _backgroundImageBase64;

    /// <summary>
    /// 背景截图在文档坐标里占据的矩形。
    /// 桌面批注层按当前屏幕尺寸抓图，因此这里记录的是“物理像素 / DPI 缩放”
    /// 得到的逻辑尺寸，而不是固定的 1920×1080。
    /// </summary>
    public Rect BackgroundImageRect { get; set; } = new(0, 0, 1920, 1080);

    public ObservableCollection<WhiteboardItem> Items { get; set; } = new();

    [JsonIgnore]
    public bool HasBackgroundImage => !string.IsNullOrEmpty(BackgroundImageBase64);

    partial void OnBackgroundImageBase64Changed(string? value) => OnPropertyChanged(nameof(HasBackgroundImage));

    public WhiteboardPage Clone()
    {
        var copy = new WhiteboardPage
        {
            Title = Title,
            Background = Background,
            BackgroundStyle = BackgroundStyle,
            BackgroundImageBase64 = BackgroundImageBase64,
            BackgroundImageRect = BackgroundImageRect
        };

        foreach (var item in Items)
            copy.Items.Add(item.Clone());

        return copy;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PageBackgroundStyle
{
    /// <summary>纯色。</summary>
    Blank = 0,

    /// <summary>方格纸。</summary>
    Grid = 1,

    /// <summary>横线纸。</summary>
    Lines = 2,

    /// <summary>点阵纸。</summary>
    Dots = 3,

    /// <summary>五线谱（音乐课用）。</summary>
    Music = 4,

    /// <summary>田字格（语文/写字课用）。</summary>
    TianZiGe = 5
}
