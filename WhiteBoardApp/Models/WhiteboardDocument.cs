using System.Collections.ObjectModel;
using Avalonia.Media;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhiteBoard.Models;

/// <summary>整份白板文档（多页）。可直接序列化为 .wbd 文件。</summary>
public sealed partial class WhiteboardDocument : ObservableObject
{
    /// <summary>文件格式版本，便于以后升级。</summary>
    public int FormatVersion { get; set; } = 1;

    public string Title { get; set; } = "未命名白板";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>画布逻辑尺寸（文档坐标下的可视区域）。</summary>
    public double CanvasWidth { get; set; } = 1920;

    public double CanvasHeight { get; set; } = 1080;

    public ObservableCollection<WhiteboardPage> Pages { get; set; } = new();

    [JsonIgnore]
    public WhiteboardPage? ActivePage { get; set; }

    public static WhiteboardDocument CreateDefault()
    {
        var doc = new WhiteboardDocument();

        // 默认板面用希沃风格的深绿，配白色笔直接就能写
        doc.Pages.Add(new WhiteboardPage
        {
            Title = "第 1 页",
            Background = Color.FromRgb(0x0E, 0x3A, 0x2F)
        });

        doc.ActivePage = doc.Pages[0];
        return doc;
    }
}
