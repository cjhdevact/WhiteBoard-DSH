using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace WhiteBoard.Views;

/// <summary>
/// 应用内的对话框统一走 Fluent 主题。
///
/// 说明：这里用「带主题资源的 Window」而不是 FluentAvalonia 的 ContentDialog。
/// ContentDialog 需要挂到某个 TopLevel 的 XamlRoot 上，且在同一进程里同时开多个
/// 顶层窗口时（主窗口 + 批注工具条 + 批注画布）行为不稳定；
/// 而 Window + DynamicResource 既拿到了同一套 Fluent 外观，
/// 又能在任意窗口上弹出、自动跟随深色 / 浅色主题。
/// </summary>
internal static class FluentDialog
{
    /// <summary>创建一个已套用 Fluent 配色的对话框窗口。</summary>
    public static Window Create(string title, Control body, out StackPanel buttons)
    {
        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0)
        };

        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 14),
                    Foreground = ThemeBrush("TextFillColorPrimaryBrush")
                },
                body,
                buttons
            }
        };

        return new Window
        {
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = ThemeBrush("SolidBackgroundFillColorBaseBrush"),
            MinWidth = 360,
            MaxWidth = 760
        };
    }

    /// <summary>按主题资源取画刷，取不到时给一个安全的回退值。</summary>
    public static IBrush ThemeBrush(string key, IBrush? fallback = null)
    {
        try
        {
            if (Application.Current is not null
                && Application.Current.TryFindResource(key, Application.Current.ActualThemeVariant, out var res)
                && res is IBrush brush)
            {
                return brush;
            }
        }
        catch
        {
            // 资源系统还没就绪时走回退值
        }

        return fallback ?? Brushes.Transparent;
    }

    /// <summary>主按钮（强调样式）。</summary>
    public static Button PrimaryButton(string text, Action onClick, double minWidth = 92)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = minWidth,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = ThemeBrush("AccentFillColorDefaultBrush"),
            Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush", Brushes.White)
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>次按钮（普通样式）。</summary>
    public static Button SecondaryButton(string text, Action onClick, double minWidth = 92)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = minWidth,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        button.Click += (_, _) => onClick();
        return button;
    }
}

/// <summary>统一风格的消息框。</summary>
public static class MessageBox
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 22,
            MaxWidth = 620,
            Foreground = FluentDialog.ThemeBrush("TextFillColorSecondaryBrush")
        };

        var window = FluentDialog.Create(title, text, out var row);
        row.Children.Add(FluentDialog.PrimaryButton("知道了",
            () => window.Close()));

        await window.ShowDialog(owner);
    }
}

/// <summary>确认对话框，返回用户是否点了确认。</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message,
        string confirmText = "确定", string cancelText = "取消")
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 22,
            MaxWidth = 620,
            Foreground = FluentDialog.ThemeBrush("TextFillColorSecondaryBrush")
        };

        var window = FluentDialog.Create(title, text, out var row);
        var result = false;

        row.Children.Add(FluentDialog.SecondaryButton(cancelText, () => window.Close()));
        row.Children.Add(FluentDialog.PrimaryButton(confirmText, () =>
        {
            result = true;
            window.Close();
        }));

        await window.ShowDialog(owner);
        return result;
    }
}

/// <summary>单行 / 多行文本输入对话框。取消时返回 null。</summary>
public static class TextInputDialog
{
    public static async Task<string?> ShowAsync(Window owner, string title, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 440,
            MinHeight = 96,
            FontSize = 14,
            Watermark = "输入文字，Ctrl+Enter 确认，Esc 取消"
        };

        var window = FluentDialog.Create(title, box, out var row);
        string? result = null;

        row.Children.Add(FluentDialog.SecondaryButton("取消", () => window.Close()));
        row.Children.Add(FluentDialog.PrimaryButton("确定", () =>
        {
            result = box.Text ?? string.Empty;
            window.Close();
        }));

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                result = box.Text ?? string.Empty;
                window.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                window.Close();
                e.Handled = true;
            }
        };

        window.Opened += (_, _) =>
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        };

        await window.ShowDialog(owner);
        return result;
    }
}
