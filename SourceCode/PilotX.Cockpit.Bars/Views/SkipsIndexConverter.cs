using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PilotX.Cockpit.Bars.Views;

/// <summary>
/// El VM expone SkipsValue en 1..10 (RowSkipsWidth, ver BarraAbajoViewModel);
/// el ComboBox de skips trabaja en SelectedIndex 0..9. Este converter hace
/// el offset -1/+1 para que el binding de SelectedIndex quede coherente sin
/// meter índices en el ViewModel (que debe seguir siendo UI-free).
/// </summary>
public sealed class SkipsIndexConverter : IValueConverter
{
    public static readonly SkipsIndexConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i - 1 : 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i + 1 : 1;
}
