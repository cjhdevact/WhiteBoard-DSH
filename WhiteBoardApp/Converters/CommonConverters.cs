using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using WhiteBoard.Models;
using WhiteBoard.Services;

namespace WhiteBoard.Converters;

/// <summary>把 #RRGGBB / #AARRGGBB 字符串转成画刷，用于模板里动态取色。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color)
            return new SolidColorBrush(color);

        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            return new SolidColorBrush(AppSettings.ParseColor(hex, Colors.Black));

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>判断当前工具是否为指定工具，用于左侧工具栏的选中态。</summary>
public sealed class ToolEqualsConverter : IValueConverter
{
    public static readonly ToolEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is WhiteboardTool tool && parameter is string name
            && Enum.TryParse<WhiteboardTool>(name, true, out var expected))
        {
            return tool == expected;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 只读绑定：由 RadioButton 的命令负责写回
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>数值比较，用于粗细 / 橡皮模式等选项的选中态。</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        if (parameter is string s && value is Enum)
            return string.Equals(value.ToString(), s, StringComparison.OrdinalIgnoreCase);

        if (parameter is string text && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
            && value is IConvertible)
        {
            try
            {
                return Math.Abs(System.Convert.ToDouble(value, CultureInfo.InvariantCulture) - number) < 0.001;
            }
            catch
            {
                return false;
            }
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>取反。</summary>
public sealed class NotConverter : IValueConverter
{
    public static readonly NotConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value is null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value is null;
}

/// <summary>非空即显示。</summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public static readonly NotNullToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
