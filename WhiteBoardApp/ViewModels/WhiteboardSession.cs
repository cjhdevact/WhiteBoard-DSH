using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhiteBoard.Models;
using WhiteBoard.Services;

namespace WhiteBoard.ViewModels;

/// <summary>
/// 一次白板会话：主窗口与桌面批注层共用同一份文档、同一份历史、同一份设置。
/// 两个窗口都绑定到它，因此在批注层上写的内容会立即出现在白板里，撤销也能互通。
/// </summary>
public sealed partial class WhiteboardSession : ObservableObject
{
    public WhiteboardSession(WhiteboardDocument document, AppSettings settings)
    {
        Document = document;
        Settings = settings;

        Pages = document.Pages;
        ActivePage = document.ActivePage ?? Pages.FirstOrDefault() ?? new WhiteboardPage { Title = "第 1 页" };

        if (Pages.Count == 0)
            Pages.Add(ActivePage);

        document.ActivePage = ActivePage;

        History = new UndoRedoService();
        Thumbnails = new ThumbnailService();

        History.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        };
    }

    /// <summary>当前打开的文档。</summary>
    public WhiteboardDocument Document { get; }

    /// <summary>用户偏好。</summary>
    public AppSettings Settings { get; }

    /// <summary>页面集合（与 <see cref="Document"/>.Pages 为同一个集合）。</summary>
    public ObservableCollection<WhiteboardPage> Pages { get; }

    public UndoRedoService History { get; }

    public ThumbnailService Thumbnails { get; }

    [ObservableProperty]
    private WhiteboardPage _activePage;

    /// <summary>当前文件路径（未保存过则为 null）。</summary>
    [ObservableProperty]
    private string? _currentFilePath;

    /// <summary>是否需要保存（用于标题栏的 * 标记）。</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>状态栏提示文本。</summary>
    [ObservableProperty]
    private string _statusMessage = "就绪";

    public bool CanUndo => History.CanUndo;

    public bool CanRedo => History.CanRedo;

    public int PageIndex => Pages.IndexOf(ActivePage);

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Document.Title) ? "未命名白板" : Document.Title;
            return IsDirty ? name + " *" : name;
        }
    }

    partial void OnActivePageChanged(WhiteboardPage value)
    {
        Document.ActivePage = value;
        OnPropertyChanged(nameof(PageIndex));
        OnPropertyChanged(nameof(PageIndicator));
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DisplayName));

    public string PageIndicator => Pages.Count == 0 ? "0 / 0" : $"{Pages.IndexOf(ActivePage) + 1} / {Pages.Count}";

    /// <summary>新增一页并切换过去。</summary>
    public WhiteboardPage AddPage()
    {
        var template = ActivePage;
        var page = new WhiteboardPage
        {
            Title = $"第 {Pages.Count + 1} 页",
            Background = template?.Background ?? DefaultBoardColor,
            BackgroundStyle = template?.BackgroundStyle ?? PageBackgroundStyle.Blank
        };

        Pages.Add(page);
        ActivePage = page;
        IsDirty = true;
        OnPropertyChanged(nameof(PageIndicator));
        return page;
    }

    /// <summary>默认板面颜色（希沃风格的深绿）。</summary>
    public static Avalonia.Media.Color DefaultBoardColor
        => AppSettings.ParseColor(AppSettings.DefaultBoardColor,
            Avalonia.Media.Color.FromRgb(0x0E, 0x3A, 0x2F));

    /// <summary>
    /// 新建一页，并把一张桌面截图作为它的背景（“冻结屏幕并批注”用）。
    /// 页面的逻辑尺寸与截图一致，这样批注坐标在不同分辨率下都能对齐。
    /// </summary>
    public WhiteboardPage AddPageWithScreenshot(CapturedFrame frame)
    {
        var logical = frame.ToLogicalBounds();

        var page = new WhiteboardPage
        {
            Title = $"批注 {Pages.Count + 1}",
            Background = Avalonia.Media.Colors.White,
            BackgroundStyle = PageBackgroundStyle.Blank,
            BackgroundImageBase64 = frame.ToPngBase64(),
            BackgroundImageRect = new Avalonia.Rect(0, 0, logical.Width, logical.Height)
        };

        Pages.Add(page);
        ActivePage = page;
        IsDirty = true;
        OnPropertyChanged(nameof(PageIndicator));
        return page;
    }

    public void RemovePage(WhiteboardPage page)
    {
        if (Pages.Count <= 1)
            return;

        var index = Pages.IndexOf(page);
        if (index < 0)
            return;

        Pages.Remove(page);
        ActivePage = Pages[Math.Clamp(index, 0, Pages.Count - 1)];
        IsDirty = true;
        OnPropertyChanged(nameof(PageIndicator));
    }

    /// <summary>把页面移动到新的位置。</summary>
    public void MovePage(WhiteboardPage page, int newIndex)
    {
        var oldIndex = Pages.IndexOf(page);
        if (oldIndex < 0)
            return;

        newIndex = Math.Clamp(newIndex, 0, Pages.Count - 1);
        if (newIndex == oldIndex)
            return;

        Pages.Move(oldIndex, newIndex);
        IsDirty = true;
        OnPropertyChanged(nameof(PageIndicator));
    }

    /// <summary>加载一份文档并切换会话状态。</summary>
    public void LoadDocument(WhiteboardDocument document, string? path)
    {
        Document.Title = document.Title;
        Document.CreatedAt = document.CreatedAt;
        Document.FormatVersion = document.FormatVersion;
        Document.CanvasWidth = document.CanvasWidth;
        Document.CanvasHeight = document.CanvasHeight;

        Pages.Clear();
        foreach (var page in document.Pages)
            Pages.Add(page);

        if (Pages.Count == 0)
            Pages.Add(new WhiteboardPage { Title = "第 1 页" });

        ActivePage = Pages[0];
        Document.ActivePage = ActivePage;

        History.Clear();
        Thumbnails.Dispose();

        CurrentFilePath = path;
        IsDirty = false;
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>重置为一个空白文档。</summary>
    public void ResetDocument()
    {
        Pages.Clear();

        var page = new WhiteboardPage { Title = "第 1 页", Background = DefaultBoardColor };
        Pages.Add(page);
        ActivePage = page;

        Document.Title = "未命名白板";
        Document.ActivePage = page;

        History.Clear();
        Thumbnails.Dispose();

        CurrentFilePath = null;
        IsDirty = false;
        OnPropertyChanged(nameof(PageIndicator));
    }
}
