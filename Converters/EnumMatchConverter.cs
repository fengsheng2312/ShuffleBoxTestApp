using System.Globalization;
using System.Windows.Data;

namespace ShuffleBoxTestApp.Converters;

/// <summary>
/// 枚举 ↔ bool 转换器，用于 RadioButton.IsChecked 绑定枚举属性。
/// EnumValue 指定该按钮代表的枚举值；ConvertBack 仅在选中时回写。
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object? EnumValue { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.Equals(EnumValue);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? EnumValue! : Binding.DoNothing;
}
