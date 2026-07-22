using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PilotX.Cockpit.Bars.Views;

/// <summary>
/// Convierte una ruta relativa de asset (ej. "barra-derecha/AutoSteerOn.png")
/// en un <see cref="Bitmap"/> cargado desde los recursos embebidos de la
/// librería (avares://PilotX.Cockpit.Bars/Assets/...). Cachea por ruta para no
/// recargar el mismo ícono en cada tick de estado (500 ms).
/// </summary>
public sealed class AssetImageConverter : IValueConverter
{
    public static readonly AssetImageConverter Instance = new();

    private static readonly Dictionary<string, Bitmap> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string rel || string.IsNullOrEmpty(rel)) return null;
        if (_cache.TryGetValue(rel, out var cached)) return cached;
        try
        {
            var uri = new Uri("avares://PilotX.Cockpit.Bars/Assets/" + rel);
            using var stream = AssetLoader.Open(uri);
            var bmp = new Bitmap(stream);
            _cache[rel] = bmp;
            return bmp;
        }
        catch
        {
            return null; // ícono faltante: no rompe la barra
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
