using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.Services;

namespace WhiteBoard.ViewModels;

/// <summary>
/// 桌面批注会话的视图模型（工具条窗口与画布窗口共用同一个实例）。
///
/// 与主窗口共用同一份 <see cref="WhiteboardSession"/> 和同一份
/// <see cref="EditorState"/>，因此在批注层写的字会立刻出现在白板对应页上，
/// 撤销也能互通。
/// </summary>
public sealed partial class AnnotationOverlayViewModel : EditorState
{
    private readonly MainWindowViewModel _main;

    public AnnotationOverlayViewModel(MainWindowViewModel main, WhiteboardSession session, WhiteboardPage page)
        : base(session.Settings)
    {
        _main = main;
        Session = session;
        ActivePage = page;

        // 继承主窗口当前的笔状态
        ActiveTool = main.ActiveTool;
        PenColor = main.PenColor;
        PenThickness = main.PenThickness;
        HighlighterThickness = main.HighlighterThickness;
        EraserRadius = main.EraserRadius;
        EraserMode = main.EraserMode;
        ShapeFill = main.ShapeFill;
        FontSize = main.FontSize;
        FontFamily = main.FontFamily;
        LaserEnabled = main.LaserEnabled;

        ToolBar = new ToolBarViewModel(this, ToolBarItems.AnnotationToolBar());

        // 双向同步画笔状态
        main.PropertyChanged += OnSharedPropertyChanged;
        PropertyChanged += OnSelfPropertyChanged;
    }

    public WhiteboardSession Session { get; }

    public ToolBarViewModel ToolBar { get; }

    /// <summary>
    /// 批注挂在哪一页上。
    /// 必须是可观察属性：XAML 里 <c>Page="{Binding ActivePage}"</c> 依赖它来更新画布，
    /// 只读属性不会发出变更通知，会导致批注层画布始终指向旧页面（写不上字）。
    /// </summary>
    [ObservableProperty]
    private WhiteboardPage _activePage;

    public ObservableCollection<WhiteboardPage> Pages => Session.Pages;

    // ── 模式 ────────────────────────────────────────────────

    /// <summary>冻结屏幕后的底图（仅本次批注有效，不写入文档）。</summary>
    [ObservableProperty]
    private CapturedFrame? _frozenFrame;

    /// <summary>是否显示冻结底图（false = 直接覆盖在真实桌面上）。</summary>
    [ObservableProperty]
    private bool _showFrozen = true;

    /// <summary>鼠标穿透（只作用于画布窗口）。</summary>
    [ObservableProperty]
    private bool _clickThrough;

    public bool HasFrozenFrame => FrozenFrame is not null;

    partial void OnFrozenFrameChanged(CapturedFrame? value)
    {
        OnPropertyChanged(nameof(HasFrozenFrame));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(IsTransparent));
    }

    partial void OnShowFrozenChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(IsTransparent));
        RequestInvalidate();
    }

    partial void OnClickThroughChanged(bool value)
    {
        ClickThroughChanged?.Invoke(this, value);
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(CanDraw));
    }

    /// <summary>画布是否需要透明背景（透明 = 让真实桌面透出来）。</summary>
    public bool IsTransparent => !ShowFrozen || !HasFrozenFrame;

    /// <summary>穿透时不再接收鼠标，主窗口视图模型据此提示。</summary>
    public bool CanDraw => !ClickThrough;

    public string ModeText => (HasFrozenFrame, ShowFrozen, ClickThrough) switch
    {
        (_, _, true) => "鼠标穿透中",
        (true, true, _) => "冻结画面",
        (true, false, _) => "透明覆盖",
        _ => "透明覆盖"
    };

    public string HintText => ClickThrough
        ? "鼠标穿透已开启：可以正常操作下层程序，点工具条「鼠标穿透」按钮返回批注"
        : "左键书写 · 右键拖动批注内容 · 1~9 换色 · Esc 退出批注";

    // 可见性属性继承自 EditorState

    public bool CanUndo => Session.History.CanUndo;

    public bool CanRedo => Session.History.CanRedo;

    // ── 事件 ────────────────────────────────────────────────

    public event EventHandler<bool>? ClickThroughChanged;

    public event EventHandler? CloseRequested;

    public event EventHandler? InvalidateRequested;

    /// <summary>请求把冻结底图换成 / 去掉。</summary>
    public event EventHandler? ToggleFrozenRequested;

    private void RequestInvalidate()
    {
        InvalidateRequested?.Invoke(this, EventArgs.Empty);
        _main.InvalidateCanvas();
    }

    // ── 命令 ────────────────────────────────────────────────

    [RelayCommand]
    private void PickToolById(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && Enum.TryParse<WhiteboardTool>(id, true, out var tool))
        {
            ActiveTool = tool;
            Session.StatusMessage = $"批注工具：{ToolDisplayName}";
        }
    }

    /// <summary>批注工具条上的动作按钮。</summary>
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
                ClickThrough = !ClickThrough;
                break;

            case "__freeze":
                ToggleFrozenRequested?.Invoke(this, EventArgs.Empty);
                break;

            case "__whiteboard":
                SwitchToWhiteboardRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public event EventHandler? SaveRequested;

    public event EventHandler? SwitchToWhiteboardRequested;

    /// <summary>批注菜单：保存白板。</summary>
    [RelayCommand]
    private void Save() => SaveRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>批注菜单：退出批注并回到白板主界面。</summary>
    [RelayCommand]
    private void SwitchToWhiteboard() => SwitchToWhiteboardRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleClickThrough() => ClickThrough = !ClickThrough;

    [RelayCommand]
    private void ToggleFrozen() => ToggleFrozenRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Undo()
    {
        if (Session.History.Undo())
        {
            Session.StatusMessage = "批注层：已撤销";
            RequestInvalidate();
        }

        RaiseHistory();
    }

    [RelayCommand]
    private void Redo()
    {
        if (Session.History.Redo())
        {
            Session.StatusMessage = "批注层：已重做";
            RequestInvalidate();
        }

        RaiseHistory();
    }

    [RelayCommand]
    private void ClearPage()
    {
        var page = ActivePage;
        if (page.Items.Count == 0)
            return;

        var removed = page.Items.ToList();
        foreach (var item in removed)
            page.Items.Remove(item);

        Session.History.Push(new RemoveItemsAction(page.Items, removed, "清空批注"));
        RequestInvalidate();
        RaiseHistory();
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        _main.RefreshHistoryState();
    }

    // ── 画布事件 ────────────────────────────────────────────

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
        Session.StatusMessage = $"批注层：{name}已记录";

        RaiseHistory();
        _main.RefreshThumbnails();
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
        RaiseHistory();
        _main.RefreshThumbnails();
    }

    public void HandleSelectionMoved(SelectionMovedEventArgs e)
    {
        if (e.Entries.Count == 0)
            return;

        Session.History.Push(new MoveItemsAction(e.Entries));
        RaiseHistory();
        _main.RefreshThumbnails();
    }

    // ── 与主窗口状态同步 ────────────────────────────────────

    private bool _syncing;

    private void OnSharedPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing)
            return;

        _syncing = true;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(ActiveTool): ActiveTool = _main.ActiveTool; break;
                case nameof(PenColor): PenColor = _main.PenColor; break;
                case nameof(PenThickness): PenThickness = _main.PenThickness; break;
                case nameof(HighlighterThickness): HighlighterThickness = _main.HighlighterThickness; break;
                case nameof(EraserMode): EraserMode = _main.EraserMode; break;
                case nameof(EraserRadius): EraserRadius = _main.EraserRadius; break;
                case nameof(ShapeFill): ShapeFill = _main.ShapeFill; break;
                case nameof(LaserEnabled): LaserEnabled = _main.LaserEnabled; break;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSelfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing)
            return;

        _syncing = true;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(ActiveTool):
                    _main.ActiveTool = ActiveTool;
                    ToolBar.RefreshSelection();
                    break;
                case nameof(PenColor): _main.PenColor = PenColor; break;
                case nameof(PenThickness): _main.PenThickness = PenThickness; break;
                case nameof(HighlighterThickness): _main.HighlighterThickness = HighlighterThickness; break;
                case nameof(EraserMode): _main.EraserMode = EraserMode; break;
                case nameof(EraserRadius): _main.EraserRadius = EraserRadius; break;
                case nameof(ShapeFill): _main.ShapeFill = ShapeFill; break;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>会话结束时的清理。</summary>
    public void Detach()
    {
        _main.PropertyChanged -= OnSharedPropertyChanged;
        FrozenFrame = null;
    }
}
