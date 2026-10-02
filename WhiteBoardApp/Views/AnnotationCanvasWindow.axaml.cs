using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.Services;
using WhiteBoard.ViewModels;

namespace WhiteBoard.Views;

/// <summary>
/// 批注画布窗口：覆盖整块虚拟屏幕的完全透明窗口。
///
///  • 透明覆盖模式：窗口全透明，直接对着真实桌面书写；
///  • 冻结画面模式：把截图作为临时底图绘制（<see cref="WhiteboardCanvas.OverlayBackground"/>），
///    不写入文档，因此不会破坏页面原有的批注内容；
///  • 鼠标穿透：<c>WS_EX_TRANSPARENT</c> 只作用于本窗口，工具条窗口不受影响。
/// </summary>
public partial class AnnotationCanvasWindow : Window
{
    private readonly AnnotationOverlayViewModel? _vm;
    private Bitmap? _frozenBitmap;

    public AnnotationCanvasWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public AnnotationCanvasWindow(AnnotationOverlayViewModel viewModel, PixelRect screenBounds, double scaling)
        : this()
    {
        _vm = viewModel;
        DataContext = viewModel;

        Position = new PixelPoint(screenBounds.X, screenBounds.Y);
        Width = screenBounds.Width / Math.Max(0.1, scaling);
        Height = screenBounds.Height / Math.Max(0.1, scaling);

        InitializeCanvas();
        HookViewModel();
        ApplyFrozenFrame();

        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnCanvasKeyDown, RoutingStrategies.Tunnel);
    }

    private WhiteboardCanvas DrawCanvas => this.FindControl<WhiteboardCanvas>("Canvas")!;

    private void InitializeCanvas()
    {
        var canvas = DrawCanvas;

        canvas.ItemCompleted += (_, e) =>
        {
            _vm?.HandleItemCompleted(e.Item);
        };

        canvas.ItemsErased += (_, e) => _vm?.HandleItemsErased(e.Removed, e.Added);

        canvas.SelectionMoved += (_, e) => _vm?.HandleSelectionMoved(e);

        canvas.TextEditRequested += async (_, item) =>
        {
            var text = await TextInputDialog.ShowAsync(this, "编辑标注文本", item.Text);
            if (text is null)
                return;

            item.Text = text;
            canvas.InvalidateVisual();
        };
    }

    private void HookViewModel()
    {
        if (_vm is null)
            return;

        _vm.InvalidateRequested += (_, _) => DrawCanvas.InvalidateVisual();
        _vm.ClickThroughChanged += (_, value) => ApplyClickThrough(value);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AnnotationOverlayViewModel.FrozenFrame)
                or nameof(AnnotationOverlayViewModel.ShowFrozen))
            {
                ApplyFrozenFrame();
            }
        };
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        FitCanvasWorld();

        // 透明置顶窗口很容易「不激活」：触屏 / 笔的第一下只会激活窗口、
        // 不产生 PointerPressed，用户就会觉得批注层点不动。这里显式激活并聚焦。
        try
        {
            Activate();
            Focus();
        }
        catch
        {
            // 某些窗口管理器不允许抢焦点，忽略
        }

        DrawCanvas.Focus();
        DrawCanvas.InvalidateVisual();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            // 关闭前务必取消穿透，否则样式会留在窗口上
            WindowInteropService.SetClickThrough(this, false);
        }

        // 冻结底图只属于本次批注
        DrawCanvas.OverlayBackground = null;
        BitmapReleaser.Release(_frozenBitmap);
        _frozenBitmap = null;

        _vm?.Detach();
    }

    /// <summary>把画布世界坐标与屏幕逻辑坐标对齐成 1:1，保证笔迹与截图精确重合。</summary>
    private void FitCanvasWorld()
    {
        var canvas = DrawCanvas;
        if (canvas.Bounds.Width < 2 || canvas.Bounds.Height < 2)
            return;

        canvas.FitWorldRectToViewport(new Rect(0, 0, canvas.Bounds.Width, canvas.Bounds.Height));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FitCanvasWorld();
    }

    /// <summary>把冻结截图挂到画布的临时覆盖层上。</summary>
    private void ApplyFrozenFrame()
    {
        var canvas = DrawCanvas;
        var frame = _vm?.FrozenFrame;

        if (frame is null)
        {
            canvas.OverlayBackground = null;
            canvas.OverlayBackgroundRect = null;

            BitmapReleaser.Release(_frozenBitmap);
            _frozenBitmap = null;

            canvas.InvalidateVisual();
            return;
        }

        if (_frozenBitmap is null)
            _frozenBitmap = frame.ToBitmap();

        var logical = frame.ToLogicalBounds();

        canvas.OverlayBackground = _frozenBitmap;
        canvas.OverlayBackgroundRect = new Rect(0, 0, logical.Width, logical.Height);
        canvas.InvalidateVisual();
    }

    private void ApplyTopmost()
    {
        if (!OperatingSystem.IsWindows())
            return;

        WindowInteropService.ForceTopmost(this, true);

        // 注意：这里不再设置 WS_EX_TOOLWINDOW。
        // 工具窗口样式叠加在透明层叠窗口上时，会让部分触控 / 笔输入被系统吞掉，
        // 表现为批注层完全点不动。任务栏隐藏交给 ShowInTaskbar=False 就足够了。
    }

    private void ApplyClickThrough(bool value)
    {
        if (!OperatingSystem.IsWindows())
            return;

        WindowInteropService.SetClickThrough(this, value);

        if (!value)
        {
            ApplyTopmost();
            Activate();
            DrawCanvas.Focus();
        }
    }

    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                _vm.CloseCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Delete:
            {
                var selected = _vm.ActivePage.Items.Where(i => i.IsSelected).ToList();
                if (selected.Count > 0)
                {
                    foreach (var item in selected)
                        _vm.ActivePage.Items.Remove(item);

                    _vm.Session.History.Push(new RemoveItemsAction(_vm.ActivePage.Items, selected, "删除标注"));
                    DrawCanvas.InvalidateVisual();
                }

                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.Z:
                    _vm.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Y:
                    _vm.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.D0:
                    FitCanvasWorld();
                    e.Handled = true;
                    return;
            }

            return;
        }

        // 工具快捷键
        var tool = e.Key switch
        {
            Key.P => (WhiteboardTool?)WhiteboardTool.Pen,
            Key.H => WhiteboardTool.Highlighter,
            Key.L => WhiteboardTool.Laser,
            Key.E => WhiteboardTool.Eraser,
            Key.V => WhiteboardTool.Select,
            Key.T => WhiteboardTool.Text,
            _ => null
        };

        if (tool is not null)
        {
            _vm.ActiveTool = tool.Value;
            e.Handled = true;
            return;
        }

        // 数字键 1..9 快速换色
        var index = e.Key switch
        {
            Key.D1 => 0, Key.D2 => 1, Key.D3 => 2, Key.D4 => 3, Key.D5 => 4,
            Key.D6 => 5, Key.D7 => 6, Key.D8 => 7, Key.D9 => 8,
            _ => -1
        };

        if (index >= 0 && index < _vm.Palette.Count)
        {
            _vm.PenColor = _vm.Palette[index].Color;
            e.Handled = true;
        }
    }
}
