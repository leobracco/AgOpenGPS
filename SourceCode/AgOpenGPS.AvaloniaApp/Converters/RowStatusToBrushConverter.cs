using System;
using System.Globalization;
using AgOpenGPS.Seeding;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AgOpenGPS.AvaloniaApp.Converters
{
    public sealed class RowStatusToBrushConverter : IValueConverter
    {
        public static readonly RowStatusToBrushConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is RowAlertSeverity sev)
            {
                string key = sev switch
                {
                    RowAlertSeverity.Critical => "ErrorColorBrush",
                    RowAlertSeverity.Warning => "WarningColorBrush",
                    _ => "AccentColorBrush",
                };
                if (Application.Current is { } app && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush brush)
                    return brush;
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
    }
}
