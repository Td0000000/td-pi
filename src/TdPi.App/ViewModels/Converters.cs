using System.Globalization;
using Avalonia.Data.Converters;

namespace TdPi.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoolToOnOff = new BoolToOnOffConverter();

    /// <summary>true → 可见(正常),false → 折叠。</summary>
    public static readonly IValueConverter BoolToVisible = new BoolToVisibleConverter(inverse: false);

    /// <summary>false → 可见,true → 折叠(用于“无xx”提示等反向显示)。</summary>
    public static readonly IValueConverter BoolToInverseVisible = new BoolToVisibleConverter(inverse: true);

    private sealed class BoolToVisibleConverter(bool inverse) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var b = value is true;
            return inverse ? !b : b;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class BoolToOnOffConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? "启用" : "停用";

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is "启用";
    }
}
