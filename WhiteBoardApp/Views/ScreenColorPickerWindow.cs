using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WhiteBoard.Services;

namespace WhiteBoard.Views;

/// <summary>
/// 屏幕取色器（滴管）：先把整个屏幕抓成一张图铺满窗口，
/// 鼠标移动时在光标旁边显示一个放大镜预览，点击即可取到该像素的颜色。
/// 这与白板的“批注 / 讲解”场景很搭：可以直接吸取课件、PPT 里的配色。
/// </summary>
public sealed class ScreenColorPickerWindow : Window
{
    private readonly CapturedFrame _frame;
    private readonly double _scale;
    private readonly Image _image;
    private readonly Canvas _magnifier;
    private readonly Border _previewBox;
    private readonly TextBlock _hexText;
    private readonly TextBlock _posText;
    private readonly Canvas _root;

    private Point _cursor;

    public ScreenColorPickerWindow(CapturedFrame frame)
    {
        _frame = frame;
        _scale = frame.Scale <= 0 ? 1.0 : frame.Scale;

        Title = "屏幕取色";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Cross);

        // 用截图的逻辑尺寸铺满窗口
        var logical = frame.ToLogicalBounds();
        Width = logical.Width;
        Height = logical.Height;
        Position = new PixelPoint(frame.ScreenLeft, frame.ScreenTop);

        _image = new Image
        {
            Source = frame.ToBitmap(),
            Stretch = Stretch.Fill,
            Width = logical.Width,
            Height = logical.Height
        };

        _root = new Canvas
        {
            Width = logical.Width,
            Height = logical.Height,
            Background = Brushes.Transparent,
            Children = { _image }
        };

        // 底部提示
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "点击取色", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                    new TextBlock { Text = "Esc / 右键 取消", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xC1, 0xD1)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                }
            }
        };

        Canvas.SetLeft(hint, 24);
        Canvas.SetTop(hint, 24);
        _root.Children.Add(hint);

        // 放大镜
        _hexText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        };

        _posText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xC1, 0xD1))
        };

        _previewBox = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.White,
            Background = Brushes.Black
        };

        var info = new StackPanel
        {
            Spacing = 2,
            Children = { _hexText, _posText }
        };

        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Children = { _previewBox, info }
            }
        };

        _magnifier = new Canvas
        {
            IsHitTestVisible = false,
            IsVisible = false,
            Children = { panel }
        };

        _root.Children.Add(_magnifier);

        Content = _root;

        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
        Opened += (_, _) => Focus();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _cursor = e.GetPosition(_root);

        var color = SampleColor(_cursor);
        if (color is null)
            return;

        _previewBox.Background = new SolidColorBrush(color.Value);
        _hexText.Text = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";

        var px = (int)Math.Round(_cursor.X * _scale);
        var py = (int)Math.Round(_cursor.Y * _scale);
        _posText.Text = $"({_frame.ScreenLeft + px}, {_frame.ScreenTop + py})";

        // 放大镜跟随光标，靠右/下边缘时自动翻到另一侧
        _magnifier.IsVisible = true;

        var offsetX = _cursor.X + 26;
        var offsetY = _cursor.Y + 22;

        if (offsetX + 210 > Width)
            offsetX = _cursor.X - 210;

        if (offsetY + 90 > Height)
            offsetY = _cursor.Y - 90;

        Canvas.SetLeft(_magnifier, offsetX);
        Canvas.SetTop(_magnifier, offsetY);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_root).Properties;

        if (props.IsRightButtonPressed)
        {
            Close();
            return;
        }

        if (!props.IsLeftButtonPressed)
            return;

        var color = SampleColor(e.GetPosition(_root));
        if (color is not null)
            SelectedColor = color;

        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>用户选中的颜色；取消时为 null。</summary>
    public Color? SelectedColor { get; private set; }

    private Color? SampleColor(Point logicalPoint)
    {
        var x = (int)Math.Round(logicalPoint.X * _scale);
        var y = (int)Math.Round(logicalPoint.Y * _scale);

        if (x < 0 || y < 0 || x >= _frame.Width || y >= _frame.Height)
            return null;

        var index = (y * _frame.Width + x) * 4;
        if (index + 2 >= _frame.Bgra.Length)
            return null;

        // GDI 抓下来是 BGRA
        return Color.FromRgb(_frame.Bgra[index + 2], _frame.Bgra[index + 1], _frame.Bgra[index]);
    }

    /// <summary>弹出取色器；用户取消时返回 null。</summary>
    public static async Task<Color?> PickAsync(Window owner)
    {
        try
        {
            // 抓屏前先把自己藏起来，避免把白板窗口本身拍进去
            var wasVisible = owner.IsVisible;
            if (wasVisible)
                owner.Hide();

            await Task.Delay(120).ConfigureAwait(true);

            var frame = ScreenCaptureService.CaptureVirtualScreen();

            if (wasVisible)
            {
                owner.Show();
                owner.Activate();
            }

            var picker = new ScreenColorPickerWindow(frame);
            await picker.ShowDialog(owner);
            return picker.SelectedColor;
        }
        catch
        {
            if (!owner.IsVisible)
                owner.Show();

            return null;
        }
    }
}
