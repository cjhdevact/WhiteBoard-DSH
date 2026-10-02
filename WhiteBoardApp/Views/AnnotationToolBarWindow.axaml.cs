using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WhiteBoard.Controls;
using WhiteBoard.ViewModels;

namespace WhiteBoard.Views;

/// <summary>
/// 可移动的批注工具条窗口（参考 ui/note.png 的布局，内部同样使用 FluentAvalonia 的 CommandBar）。
///
/// 之所以独立成一个窗口：鼠标穿透只能按窗口设置。工具条与画布分成两个窗口后，
/// 开启穿透时画布不再接收鼠标，而工具条依然可以点击（否则就退不出批注了）。
/// </summary>
public partial class AnnotationToolBarWindow : Window
{
    private readonly AnnotationOverlayViewModel? _vm;

    public AnnotationToolBarWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public AnnotationToolBarWindow(AnnotationOverlayViewModel viewModel, PixelRect screenBounds, double scaling)
        : this()
    {
        _vm = viewModel;
        DataContext = viewModel;

        // 用 FluentAvalonia 的 CommandBar 呈现批注工具（与主界面同一套按钮定义）
        var bar = this.FindControl<FluentAvalonia.UI.Controls.CommandBar>("ToolBar");
        if (bar is not null)
        {
            FluentToolBarBuilder.Attach(bar, viewModel.ToolBar,
                viewModel.PickToolByIdCommand, viewModel.ToolActionCommand);
        }

        // 默认停在屏幕右下角（按需求）
        Position = new PixelPoint(
            screenBounds.Right - (int)(420 * scaling),
            screenBounds.Bottom - (int)(260 * scaling));

        Opened += (_, _) => KeepOnScreen(screenBounds, scaling);
        SizeChanged += (_, _) => KeepOnScreen(screenBounds, scaling);

        _screenBounds = screenBounds;
        _scaling = scaling;
    }

    private PixelRect _screenBounds;
    private double _scaling = 1.0;

    /// <summary>
    /// 把工具条吸附在屏幕右下角，并保证它始终完整可见。
    /// 工具条尺寸会随主题字号变化，因此每次尺寸变化都重新贴合。
    /// </summary>
    private void KeepOnScreen(PixelRect screenBounds, double scaling)
    {
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);

        if (width <= 0 || height <= 0)
            return;

        const int margin = 16;

        var x = Math.Clamp(
            screenBounds.Right - width - margin,
            screenBounds.X + margin,
            Math.Max(screenBounds.X + margin, screenBounds.Right - width - margin));

        var y = Math.Clamp(
            screenBounds.Bottom - height - margin,
            screenBounds.Y + margin,
            Math.Max(screenBounds.Y + margin, screenBounds.Bottom - height - margin));

        Position = new PixelPoint(x, y);
    }

    /// <summary>拖动工具条（按住顶部手柄区域）。</summary>
    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        try
        {
            BeginMoveDrag(e);
        }
        catch
        {
            // 某些窗口管理器不支持，忽略
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm is not null)
        {
            _vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
