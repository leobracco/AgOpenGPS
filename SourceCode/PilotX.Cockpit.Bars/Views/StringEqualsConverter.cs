using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PilotX.Cockpit.Bars.Views;

/// <summary>
/// Compara el string bindeado (OpenSubmenu) contra el ConverterParameter para
/// mostrar/ocultar el panel del submenú correspondiente, sin meter lógica de
/// UI (índices, enums de layout) en el ViewModel — ver MenuIzquierdaViewModel.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && parameter is string p && s == p;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
