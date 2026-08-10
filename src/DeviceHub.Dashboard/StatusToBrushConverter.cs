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

public sealed class BytesToSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = System.Convert.ToInt64(value ?? 0L);

        if (bytes <= 0)
            return "-";

        // Los fabricantes venden en GB decimales; mostrarlo asi evita el
        // "mi disco de 500 GB dice 465".
        return bytes >= 1_000_000_000_000L
            ? $"{bytes / 1_000_000_000_000d:0.#} TB"
            : $"{bytes / 1_000_000_000d:0.#} GB";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Un solo converter para "hay algo que mostrar":
    ///
    ///   null            -> oculto
    ///   bool            -> el propio valor
    ///   numero (Count)  -> visible solo si es > 0
    ///   cualquier otro  -> visible
    ///
    /// Evita tener tres converters identicos (NullToVisibility, CountToVisibility,
    /// BoolToVisibility) que hacen la misma pregunta.
    ///
    /// Con parameter="invert" devuelve Visible en el caso contrario.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            null => false,
            bool boolean => boolean,
            int count => count > 0,
            string text => text.Length > 0,
            _ => true
        };

        if (parameter as string == "invert")
            flag = !flag;

        return flag ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
