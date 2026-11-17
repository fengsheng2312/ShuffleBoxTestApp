using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ShuffleBoxTestApp.Converters;

/// <summary>布尔取反后转可见性：true → 折叠，false → 可见。用于"不可用时才提示"的场景。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>
/// 连接状态文本 → 状态胶囊背景色。
/// 直接在转换器里给出画刷，避免为三个状态再建一套资源字典与触发器。
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Freeze("#E8F5E9");
    private static readonly Brush Warn = Freeze("#FFF3E0");
    private static readonly Brush Off = Freeze("#F5F5F5");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "已连接" => Ok,
            "已连接·未握手" => Warn,
            _ => Off,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>连接状态文本 → 状态胶囊文字色。</summary>
public sealed class StatusToForegroundConverter : IValueConverter
{
    private static readonly Brush Ok = Freeze("#2E7D32");
    private static readonly Brush Warn = Freeze("#E65100");
    private static readonly Brush Off = Freeze("#9E9E9E");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "已连接" => Ok,
            "已连接·未握手" => Warn,
            _ => Off,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 判定结果 → 文字色（F-CHK-04 异常分类在表格里的直观呈现）。
/// </summary>
public sealed class VerdictToBrushConverter : IValueConverter
{
    private static readonly Brush Pass = Freeze("#2E7D32");
    private static readonly Brush WrongOrder = Freeze("#E65100");
    private static readonly Brush Open = Freeze("#C62828");
    private static readonly Brush Unknown = Freeze("#616161");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "Pass" => Pass,
            "错序" => WrongOrder,
            "错插(应断路)" => WrongOrder,
            "断路" => Open,
            _ => Unknown,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>日志级别 → 日志行文字色。</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly Brush Info = Freeze("#D4D4D4");
    private static readonly Brush Warn = Freeze("#DCDCAA");
    private static readonly Brush Error = Freeze("#F48771");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "Warn" => Warn,
            "Error" => Error,
            _ => Info,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
