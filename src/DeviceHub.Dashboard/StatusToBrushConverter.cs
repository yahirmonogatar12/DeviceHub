using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DeviceHub.Contracts;

namespace DeviceHub.Dashboard;

public sealed class StatusToBrushConverter : IValueConverter
{
    // Mismos tonos que Styles/Theme.xaml: el color de estado aparece en la lista
    // (converter) y en las tarjetas de KPI (XAML), y tienen que coincidir.
    private static readonly SolidColorBrush Online = new(Color.FromRgb(0x3F, 0xBF, 0x7F));
    private static readonly SolidColorBrush Unreachable = new(Color.FromRgb(0xE8, 0xA1, 0x3A));
    private static readonly SolidColorBrush Offline = new(Color.FromRgb(0x7A, 0x7A, 0x7A));

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

/// <summary>
/// Pinta lo seleccionado: devuelve <see cref="Igual"/> cuando el valor coincide
/// con el parametro y <see cref="Distinto"/> cuando no.
///
/// Existe porque un Style compartido no puede comparar contra un valor distinto
/// por instancia. La primera version usaba RadioButton con IsChecked enlazado en
/// dos sentidos y salio mal: el control escribe de vuelta al enlace tanto al
/// marcarse como al desmarcarse, y el menu lateral saltaba solo entre Equipos y
/// Auditoria varias veces por segundo. Asi el resaltado se calcula en un solo
/// sentido y no queda estado que se pueda desincronizar.
/// </summary>
public sealed class MatchBrushConverter : IValueConverter
{
    public Brush? Igual { get; set; }
    public Brush? Distinto { get; set; }

    /// <summary>Sin parametro equivale a cadena vacia: en XAML no hay forma
    /// directa de escribir ConverterParameter="" (queda como null).</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string ?? string.Empty, parameter as string ?? string.Empty, StringComparison.Ordinal)
            ? Igual
            : Distinto;

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
