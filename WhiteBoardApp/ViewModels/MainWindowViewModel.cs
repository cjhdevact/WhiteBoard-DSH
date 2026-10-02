using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.Services;

namespace WhiteBoard.ViewModels;

/// <summary>页面导航条里的一项。</summary>
public sealed partial class PageItemViewModel : ObservableObject
{
    public PageItemViewModel(WhiteboardPage page, int index)
    {
        Page = page;
        Index = index;
        Title = string.IsNullOrWhiteSpace(page.Title) ? $"第 {index + 1} 页" : page.Title;
    }

    public WhiteboardPage Page { get; }

    public int Index { get; }

    [ObservableProperty]
    private string _title;

    public int ItemCount => Page.Items.Count;

    public string ItemCountText => ItemCount == 0 ? "空白" : $"{ItemCount} 个对象";

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// 白板主窗口的视图模型。
/// 工具 / 颜色状态来自 <see cref="EditorState"/>，文档与历史来自 <see cref="WhiteboardSession"/>。
/// </summary>
public sealed partial class MainWindowViewModel : EditorState
{
    private bool _syncing;

    public MainWindowViewModel(WhiteboardSession session)
        : base(session.Settings)
    {
        Session = session;

        ShowGrid = session.Settings.ShowGrid;
        LaserEnabled = session.Settings.LaserEnabled;

        ToolBar = new ToolBarViewModel(this, ToolBarItems.MainToolBar());

        // 一体机默认：全屏白板模式（隐藏标题栏、页面栏收起）
        IsTitleBarHidden = session.Settings.HideTitleBar;
        IsPagePanelVisible = session.Settings.ShowPagePanel;

        Session.PropertyChanged += OnSessionPropertyChanged;

        // EditorState 的 partial 钩子已被 MVVM 生成器占用（OnActiveToolChanged 会与方法名冲突），
        // 这里改为监听自身的属性变化来同步工具条选中态与形状填充文案。
        PropertyChanged += OnSelfPropertyChanged;

        RebuildPageList();
    }

    private void OnSelfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ActiveTool):
                SyncToolBarState();
                OnPropertyChanged(nameof(StatusText));
                break;

            case nameof(ShapeFill):
                OnPropertyChanged(nameof(ShapeFillText));
                break;
        }
    }

    public WhiteboardSession Session { get; }

    public WhiteboardDocument Document => Session.Document;

    public WhiteboardPage ActivePage => Session.ActivePage;

    public ToolBarViewModel ToolBar { get; }

    public ObservableCollection<PageItemViewModel> PageList { get; } = new();

    // ─────────────────────────────────────────────────────────
    //  工具栏命令
    // ─────────────────────────────────────────────────────────

    /// <summary>切换工具（参数为工具 Id：Select / Pen / Highlighter / Laser / Eraser / Line / …）。</summary>
    [RelayCommand]
    private void PickToolById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (Enum.TryParse<WhiteboardTool>(id, true, out var tool))
        {
            ActiveTool = tool;
            Session.StatusMessage = $"当前工具：{ToolDisplayName}";
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>工具条上的动作按钮统一入口（参数为 __ 开头的 Id）。</summary>
    [RelayCommand]
    private void ToolAction(string? id)
    {
        switch (id)
        {
            case "__undo":
                UndoCommand.Execute(null);
                break;

            case "__redo":
                RedoCommand.Execute(null);
                break;

            case "__clear":
                ClearPageCommand.Execute(null);
                break;

            case "__save":
                SaveRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "__close":
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "__through":
            case "__freeze":
                // 主窗口工具条不包含这两项，仅在批注工具条里使用
                break;

            case "__whiteboard":
                ToggleWhiteboardRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>工具条 / 菜单里的下拉面板（参数为面板 Id）。</summary>
    [RelayCommand]
    private void OpenPanel(string? id)
    {
        ActivePanel = string.Equals(ActivePanel, id, StringComparison.Ordinal) ? null : id;
        OnPropertyChanged(nameof(IsBackgroundPanelOpen));
    }

    [ObservableProperty]
    private string? _activePanel;

    public bool IsBackgroundPanelOpen => string.Equals(ActivePanel, "Background", StringComparison.Ordinal);

    [RelayCommand]
    private void ClosePanel()
    {
        ActivePanel = null;
        OnPropertyChanged(nameof(IsBackgroundPanelOpen));
    }

    // ─────────────────────────────────────────────────────────
    //  批量状态同步到工具条按钮
    // ─────────────────────────────────────────────────────────

    /// <summary>同步工具条选中态与可用状态（通过自身的 PropertyChanged 钩子触发）。</summary>
    private void SyncToolBarState()
    {
        ToolBar.RefreshSelection();
        UpdateToolAvailability();
    }

    // ── 历史 ────────────────────────────────────────────────

    public bool CanUndo => Session.History.CanUndo;

    public bool CanRedo => Session.History.CanRedo;

    public string? UndoTooltip => Session.History.CanUndo ? $"撤销 {Session.History.NextUndoName}" : "没有可撤销的操作";

    public string? RedoTooltip => Session.History.CanRedo ? $"重做 {Session.History.NextRedoName}" : "没有可重做的操作";

    public void RefreshHistoryState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTooltip));
        OnPropertyChanged(nameof(RedoTooltip));
        UpdateToolAvailability();
    }

    /// <summary>按当前状态刷新工具条按钮的可用性。</summary>
    private void UpdateToolAvailability()
    {
        foreach (var item in ToolBar.Items)
        {
            item.IsEnabled = item.Id switch
            {
                "__undo" => CanUndo,
                "__redo" => CanRedo,
                "__clear" => ActivePage.Items.Count > 0,
                _ => true
            };
        }
    }

    // ─────────────────────────────────────────────────────────
    //  选项（颜色 / 粗细 / 填充 / 字号 / 橡皮）
    // ─────────────────────────────────────────────────────────

    // 可见性属性（IsColorOptionsVisible / IsEraserOptionsVisible /
    // IsShapeOptionsVisible / IsTextOptionsVisible）继承自 EditorState。

    public IReadOnlyList<string> BackgroundStyleNames { get; } = new[]
    {
        "空白", "方格", "横线", "点阵", "五线谱", "田字格"
    };

    public IReadOnlyList<PaletteColor> BackgroundColors { get; } = new[]
    {
        new PaletteColor("白", Colors.White),
        new PaletteColor("米黄", Color.FromRgb(0xFA, 0xF5, 0xE6)),
        new PaletteColor("淡绿", Color.FromRgb(0xE8, 0xF5, 0xE9)),
        new PaletteColor("淡蓝", Color.FromRgb(0xE7, 0xF0, 0xFA)),
        new PaletteColor("深灰", Color.FromRgb(0x2B, 0x2F, 0x36)),
        new PaletteColor("墨绿", Color.FromRgb(0x0F, 0x2B, 0x22)),
        new PaletteColor("黑板绿", Color.FromRgb(0x1E, 0x3B, 0x2E))
    };

    public IReadOnlyList<string> ExportSizes { get; } = new[] { "1920×1080", "2560×1440", "3840×2160" };

    [ObservableProperty]
    private string _exportSize = "1920×1080";

    /// <summary>当前图形填充方式的文案。</summary>
    public string ShapeFillText => ShapeFill switch
    {
        ShapeFill.Solid => "实心",
        ShapeFill.Translucent => "半透明",
        _ => "无填充"
    };

    // ── 标题 / 状态 ─────────────────────────────────────────

    public string WindowTitleText => Session.DisplayName;

    public string StatusText => $"{Session.StatusMessage}   ·   {Session.PageIndicator}   ·   {ToolDisplayName}";

    public int ZoomPercent => (int)Math.Round(CurrentZoom * 100);

    [ObservableProperty]
    private double _currentZoom = 1.0;

    partial void OnCurrentZoomChanged(double value) => OnPropertyChanged(nameof(ZoomPercent));

    /// <summary>当前主题（true = 深色）。</summary>
    [ObservableProperty]
    private bool _isDarkTheme;

    public string ThemeIcon => IsDarkTheme ? "WeatherMoon" : "WeatherSunny";

    public bool IsFullScreen { get; private set; }

    // ─────────────────────────────────────────────────────────
    //  视图交互回调（由 View 注入）
    // ─────────────────────────────────────────────────────────

    public event EventHandler? FitToViewportRequested;

    public event EventHandler? ResetZoomRequested;

    /// <summary>请求打开桌面批注层（透明覆盖）。</summary>
    public event EventHandler? DesktopAnnotateRequested;

    /// <summary>请求冻结屏幕并批注。</summary>
    public event EventHandler? FreezeScreenRequested;

    public event EventHandler? ToggleFullScreenRequested;

    public event EventHandler? CanvasInvalidated;

    public event EventHandler? ShowShortcutsRequested;

    public event EventHandler? ShowAboutRequested;

    public event EventHandler? InsertTextDialogRequested;

    /// <summary>请求保存（文件对话框由 View 提供）。</summary>
    public event EventHandler? SaveRequested;

    /// <summary>请求关闭窗口。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>请求显示 / 隐藏白板窗口。</summary>
    public event EventHandler? ToggleWhiteboardRequested;

    // ── 文件 / 帮助相关命令：由 View 注入实现（需要窗口的 StorageProvider） ──

    public ICommand? NewDocumentCommand { get; set; }

    public ICommand? OpenDocumentCommand { get; set; }

    public ICommand? SaveDocumentCommand { get; set; }

    public ICommand? SaveAsDocumentCommand { get; set; }

    public ICommand? ExportPngCommand { get; set; }

    public ICommand? ExportAllPngCommand { get; set; }

    public ICommand? ExitCommand { get; set; }

    public ICommand? PickScreenColorCommand2 { get; set; }

    [RelayCommand]
    private void InsertText() => InsertTextDialogRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowShortcuts() => ShowShortcutsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowAbout() => ShowAboutRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    /// <summary>
    /// 把「深色主题」状态真正应用到全局 ThemeVariant。
    /// FluentAvalonia 的 RequestedThemeVariant=Default 会跟随系统，
    /// 用户手动切换时需要覆盖它。
    /// </summary>
    partial void OnIsDarkThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeIcon));

        try
        {
            if (Application.Current is not null)
            {
                Application.Current.RequestedThemeVariant =
                    value ? ThemeVariant.Dark : ThemeVariant.Light;
            }
        }
        catch
        {
            // 主题切换失败不影响功能
        }
    }

    /// <summary>显示 / 隐藏标题栏（全屏白板模式下默认隐藏）。</summary>
    [RelayCommand]
    private void ToggleTitleBar()
    {
        IsTitleBarHidden = !IsTitleBarHidden;
        Session.Settings.HideTitleBar = IsTitleBarHidden;
        Session.Settings.Save();
    }

    /// <summary>标题栏是否隐藏。</summary>
    [ObservableProperty]
    private bool _isTitleBarHidden = true;

    /// <summary>
    /// 页面缩略图面板是否可见。默认隐藏，
    /// 只有点击左上角页码（x/x）才显示，再点一次收起。
    /// </summary>
    [RelayCommand]
    private void TogglePagePanel()
    {
        IsPagePanelVisible = !IsPagePanelVisible;
        Session.Settings.ShowPagePanel = IsPagePanelVisible;
        Session.Settings.Save();

        if (IsPagePanelVisible)
            RefreshThumbnails(force: true);
    }

    [ObservableProperty]
    private bool _isPagePanelVisible;

    public void InvalidateCanvas() => CanvasInvalidated?.Invoke(this, EventArgs.Empty);

    public void SetFullScreenState(bool value)
    {
        IsFullScreen = value;
        OnPropertyChanged(nameof(IsFullScreen));
    }

    public void NotifyStatusChanged() => OnPropertyChanged(nameof(StatusText));

    // ─────────────────────────────────────────────────────────
    //  页面管理
    // ─────────────────────────────────────────────────────────

    public void RebuildPageList()
    {
        foreach (var existing in PageList)
        {
            existing.Page.Items.CollectionChanged -= OnPageItemsChanged;
            existing.Page.PropertyChanged -= OnPagePropertyChanged;
        }

        PageList.Clear();

        for (var i = 0; i < Session.Pages.Count; i++)
        {
            var vm = new PageItemViewModel(Session.Pages[i], i)
            {
                IsActive = ReferenceEquals(Session.Pages[i], Session.ActivePage)
            };

            vm.Page.Items.CollectionChanged += OnPageItemsChanged;
            vm.Page.PropertyChanged += OnPagePropertyChanged;

            PageList.Add(vm);
        }

        OnPropertyChanged(nameof(ActivePage));
        OnPropertyChanged(nameof(ActivePageTitle));
        OnPropertyChanged(nameof(PageIndicators));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanClearPage));
        RefreshThumbnails(force: true);
    }

    public bool CanClearPage => ActivePage.Items.Count > 0;

    private void OnPageItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_syncing)
            return;

        Session.Thumbnails.Invalidate(sender as WhiteboardPage);
        OnPropertyChanged(nameof(CanClearPage));
        UpdateToolAvailability();
        RefreshThumbnails();
    }

    private void OnPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing)
            return;

        if (e.PropertyName is nameof(WhiteboardPage.Title)
            or nameof(WhiteboardPage.Background)
            or nameof(WhiteboardPage.BackgroundStyle)
            or nameof(WhiteboardPage.BackgroundImageBase64))
        {
            Session.Thumbnails.Invalidate(sender as WhiteboardPage);
            RefreshThumbnails();
        }
    }

    /// <summary>窗口关闭时解除订阅。</summary>
    public void Detach()
    {
        Session.PropertyChanged -= OnSessionPropertyChanged;

        foreach (var vm in PageList)
        {
            vm.Page.Items.CollectionChanged -= OnPageItemsChanged;
            vm.Page.PropertyChanged -= OnPagePropertyChanged;
        }
    }

    public void RefreshThumbnails(bool force = false)
    {
        if (PageList.Count == 0)
            return;

        var thumbs = Session.Thumbnails.Build(Session.Pages, force);

        for (var i = 0; i < PageList.Count && i < thumbs.Count; i++)
        {
            PageList[i].Thumbnail = thumbs[i].Bitmap;
            PageList[i].IsActive = ReferenceEquals(PageList[i].Page, Session.ActivePage);
        }
    }

    [RelayCommand]
    private void AddPage()
    {
        Session.AddPage();
        AfterPageChange("已新增一页");
    }

    [RelayCommand]
    private void DuplicatePage()
    {
        var source = ActivePage;
        var copy = source.Clone();
        copy.Title = $"第 {Session.Pages.Count + 1} 页";

        Session.Pages.Insert(Session.Pages.IndexOf(source) + 1, copy);
        Session.ActivePage = copy;
        AfterPageChange("已复制当前页");
    }

    [RelayCommand]
    private void DeletePage(PageItemViewModel? item)
    {
        var target = item?.Page ?? ActivePage;

        if (Session.Pages.Count <= 1)
        {
            Session.StatusMessage = "至少需要保留一页";
            return;
        }

        Session.RemovePage(target);
        AfterPageChange("已删除页面");
    }

    [RelayCommand]
    private void GoToPage(PageItemViewModel? item)
    {
        if (item is null || ReferenceEquals(item.Page, Session.ActivePage))
            return;

        Session.ActivePage = item.Page;
        AfterPageChange($"已切换到第 {Session.PageIndex + 1} 页");
    }

    [RelayCommand]
    private void NextPage()
    {
        var index = Session.Pages.IndexOf(Session.ActivePage);
        if (index < Session.Pages.Count - 1)
        {
            Session.ActivePage = Session.Pages[index + 1];
            AfterPageChange($"第 {index + 2} 页");
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        var index = Session.Pages.IndexOf(Session.ActivePage);
        if (index > 0)
        {
            Session.ActivePage = Session.Pages[index - 1];
            AfterPageChange($"第 {index} 页");
        }
    }

    private void AfterPageChange(string message)
    {
        Session.IsDirty = true;
        Session.StatusMessage = message;

        RebuildPageList();
        InvalidateCanvas();
    }

    public string PageIndicators => Session.PageIndicator;

    public string ActivePageTitle
    {
        get => ActivePage.Title;
        set
        {
            if (ActivePage.Title == value)
                return;

            ActivePage.Title = value ?? string.Empty;
            Session.IsDirty = true;

            for (var i = 0; i < PageList.Count; i++)
            {
                if (ReferenceEquals(PageList[i].Page, ActivePage))
                {
                    PageList[i].Title = string.IsNullOrWhiteSpace(value) ? $"第 {i + 1} 页" : value;
                    break;
                }
            }

            Session.Thumbnails.Invalidate(ActivePage);
            RefreshThumbnails();
        }
    }

    // ─────────────────────────────────────────────────────────
    //  页面背景
    // ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetBackgroundStyle(string name)
    {
        var style = Enum.TryParse<PageBackgroundStyle>(name, true, out var parsed)
            ? parsed
            : (PageBackgroundStyle)Math.Clamp(BackgroundStyleNames.ToList().IndexOf(name), 0, 5);

        ApplyBackgroundStyle(style);
    }

    [RelayCommand]
    private void SetBackgroundStyleIndex(int index) => ApplyBackgroundStyle((PageBackgroundStyle)Math.Clamp(index, 0, 5));

    private void ApplyBackgroundStyle(PageBackgroundStyle style)
    {
        ActivePage.BackgroundStyle = style;
        Session.Settings.PageBackgroundStyle = style.ToString();

        Session.IsDirty = true;
        Session.StatusMessage = $"背景样式：{BackgroundStyleNames[(int)style]}";

        InvalidateCanvas();
        Session.Thumbnails.Invalidate(ActivePage);
        RefreshThumbnails();
        OnPropertyChanged(nameof(ActiveBackgroundStyleIndex));
    }

    public int ActiveBackgroundStyleIndex => (int)ActivePage.BackgroundStyle;

    [RelayCommand]
    private void SetBackgroundColor(PaletteColor? color)
    {
        if (color is null)
            return;

        ActivePage.Background = color.Color;
        Session.Settings.PageBackground = color.Hex;

        Session.IsDirty = true;
        Session.StatusMessage = $"背景颜色：{color.Name}";

        InvalidateCanvas();
        Session.Thumbnails.Invalidate(ActivePage);
        RefreshThumbnails();
    }

    [RelayCommand]
    private void ClearBackgroundImage()
    {
        if (!ActivePage.HasBackgroundImage)
            return;

        ActivePage.BackgroundImageBase64 = null;
        Session.IsDirty = true;
        Session.StatusMessage = "已移除背景截图";

        InvalidateCanvas();
        Session.Thumbnails.Invalidate(ActivePage);
        RefreshThumbnails();
    }

    // ─────────────────────────────────────────────────────────
    //  历史操作
    // ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void Undo()
    {
        if (Session.History.Undo())
        {
            Session.StatusMessage = "已撤销";
            InvalidateCanvas();
            RefreshThumbnails();
            RefreshHistoryState();
            OnPropertyChanged(nameof(CanClearPage));
        }
    }

    [RelayCommand]
    private void Redo()
    {
        if (Session.History.Redo())
        {
            Session.StatusMessage = "已重做";
            InvalidateCanvas();
            RefreshThumbnails();
            RefreshHistoryState();
            OnPropertyChanged(nameof(CanClearPage));
        }
    }

    [RelayCommand]
    private void ClearPage()
    {
        var page = ActivePage;
        if (page.Items.Count == 0)
        {
            Session.StatusMessage = "当前页已经是空白";
            return;
        }

        var removed = page.Items.ToList();

        foreach (var item in removed)
            page.Items.Remove(item);

        Session.History.Push(new RemoveItemsAction(page.Items, removed, "清空页面"));

        Session.IsDirty = true;
        Session.StatusMessage = $"已清空第 {Session.PageIndex + 1} 页";
        InvalidateCanvas();
        Session.Thumbnails.Invalidate(page);
        RefreshThumbnails();
        RefreshHistoryState();
    }

    [RelayCommand]
    private void DeleteSelection()
    {
        var page = ActivePage;
        var selected = page.Items.Where(i => i.IsSelected).ToList();

        if (selected.Count == 0)
        {
            Session.StatusMessage = "请先用「选择」工具选中对象";
            return;
        }

        foreach (var item in selected)
            page.Items.Remove(item);

        Session.History.Push(new RemoveItemsAction(page.Items, selected, "删除对象"));
        Session.IsDirty = true;
        Session.StatusMessage = $"已删除 {selected.Count} 个对象";
        InvalidateCanvas();
        RefreshThumbnails();
        RefreshHistoryState();
    }

    // ─────────────────────────────────────────────────────────
    //  视图 / 桌面批注
    // ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleDesktopAnnotate() => DesktopAnnotateRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void FreezeScreen() => FreezeScreenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void FitToViewport() => FitToViewportRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ResetZoom() => ResetZoomRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleFullScreen() => ToggleFullScreenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>在指定文档坐标插入文本。</summary>
    public void InsertText(string text, double x, double y)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var item = new TextItem
        {
            Text = text,
            Position = new Point(x, y),
            Color = PenColor,
            FontSize = FontSize <= 0 ? 28 : FontSize,
            FontFamily = FontFamily
        };

        ActivePage.Items.Add(item);
        Session.History.Push(new AddItemsAction(ActivePage.Items, new[] { item }, "文本"));

        Session.IsDirty = true;
        Session.StatusMessage = "已添加文本";
        InvalidateCanvas();
        Session.Thumbnails.Invalidate(ActivePage);
        RefreshThumbnails();
        RefreshHistoryState();
    }

    // ─────────────────────────────────────────────────────────
    //  画布事件（由 View 转发）
    // ─────────────────────────────────────────────────────────

    public void HandleItemCompleted(WhiteboardItem item)
    {
        var page = ActivePage;
        page.Items.Add(item);

        var name = item switch
        {
            TextItem => "文本",
            ShapeItem => "图形",
            _ => item.Tool == WhiteboardTool.Highlighter ? "荧光笔" : "书写"
        };

        Session.History.Push(new AddItemsAction(page.Items, new[] { item }, name));
        Session.IsDirty = true;
        Session.StatusMessage = $"{name}已记录（本页 {page.Items.Count} 个对象）";

        RefreshHistoryState();
        RefreshThumbnails();
        OnPropertyChanged(nameof(CanClearPage));
        OnPropertyChanged(nameof(StatusText));
    }

    public void HandleItemsErased(IReadOnlyList<WhiteboardItem> removed, IReadOnlyList<WhiteboardItem> added)
    {
        var page = ActivePage;

        foreach (var item in removed)
            page.Items.Remove(item);

        foreach (var item in added)
            page.Items.Add(item);

        if (removed.Count == 0 && added.Count == 0)
            return;

        Session.History.Push(new ReplaceItemsAction(page.Items, removed, added, "擦除"));
        Session.IsDirty = true;

        RefreshHistoryState();
        RefreshThumbnails();
    }

    public void HandleSelectionMoved(SelectionMovedEventArgs e)
    {
        if (e.Entries.Count == 0)
            return;

        Session.History.Push(new MoveItemsAction(e.Entries));
        Session.IsDirty = true;
        RefreshHistoryState();
        RefreshThumbnails();
    }

    public void UpdateStatus(string message)
    {
        Session.StatusMessage = message;
        OnPropertyChanged(nameof(StatusText));
    }

    // ─────────────────────────────────────────────────────────
    //  会话同步
    // ─────────────────────────────────────────────────────────

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing)
            return;

        switch (e.PropertyName)
        {
            case nameof(WhiteboardSession.ActivePage):
                _syncing = true;
                try
                {
                    RebuildPageList();
                    OnPropertyChanged(nameof(ActivePage));
                    OnPropertyChanged(nameof(ActivePageTitle));
                    OnPropertyChanged(nameof(PageIndicators));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(WindowTitleText));
                    OnPropertyChanged(nameof(ActiveBackgroundStyleIndex));
                    RefreshHistoryState();
                    InvalidateCanvas();
                }
                finally
                {
                    _syncing = false;
                }

                break;

            case nameof(WhiteboardSession.IsDirty):
            case nameof(WhiteboardSession.CurrentFilePath):
                OnPropertyChanged(nameof(WindowTitleText));
                break;

            case nameof(WhiteboardSession.StatusMessage):
                OnPropertyChanged(nameof(StatusText));
                break;
        }
    }
}
