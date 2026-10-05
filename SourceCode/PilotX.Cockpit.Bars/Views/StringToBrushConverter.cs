using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PilotX.Cockpit.Bars.Views;

/// <summary>
/// Convierte un string hex ("#4ABA3E") a IBrush para bindear directo
/// contra propiedades tipo Fill/Background sin exponer IBrush desde el
/// ViewModel (que solo conoce strings, ver BarraSuperiorViewModel.GpsDotColor).
/// Reusado por las otras barras (BarraInferior, etc.) que también pintan
/// semáforos de estado con color en hex.
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                return Brush.Parse(s);
            }
            catch (Exception)
            {
                return Brushes.Transparent;
            }
        }
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
