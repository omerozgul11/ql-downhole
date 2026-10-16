using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DownholeStudio.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoldIf = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter IsEqual = new EqualsConverter();

    public static readonly IValueConverter Percent = new FuncValueConverter<double, string>(v => v.ToString("P0", CultureInfo.CurrentCulture));

    private sealed class EqualsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
