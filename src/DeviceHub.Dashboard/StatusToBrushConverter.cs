using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DeviceHub.Contracts;

namespace DeviceHub.Dashboard;

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Online = new(Color.FromRgb(0x1F, 0xA5, 0x4F));
    private static readonly SolidColorBrush Unreachable = new(Color.FromRgb(0xE0, 0x9B, 0x13));
    private static readonly SolidColorBrush Offline = new(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MachineStatus.Online => Online,
        MachineStatus.Unreachable => Unreachable,
        _ => Offline
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>Con parameter="invert" devuelve Visible cuando el valor es false.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (parameter as string == "invert")
            flag = !flag;

        return flag ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
