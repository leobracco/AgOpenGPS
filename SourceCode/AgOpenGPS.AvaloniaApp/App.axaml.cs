using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using AgOpenGPS.AvaloniaApp.Services.Branding;
using AgOpenGPS.AvaloniaApp.Services.Hosting;
using AgOpenGPS.AvaloniaApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenGPS.AvaloniaApp
{
    public partial class App : Application
    {
        public static IServiceProvider Services { get; private set; } = null!;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            Services = ServiceRegistration.Build();

            var branding = Services.GetRequiredService<IBrandingService>();
            ApplyBranding(branding.Current);
            branding.BrandingChanged += (_, def) =>
                Dispatcher.UIThread.Post(() => ApplyBranding(def));

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = Services.GetRequiredService<MainWindow>();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void ApplyBranding(BrandingDefinition def)
        {
            SetColor("AccentColor", def.AccentColorHex);
            SetColor("WarningColor", def.WarningColorHex);
            SetColor("ErrorColor", def.ErrorColorHex);
            SetColor("BackgroundColor", def.BackgroundColorHex);
            SetColor("SurfaceColor", def.SurfaceColorHex);
            SetColor("ForegroundColor", def.ForegroundColorHex);
        }

        private void SetColor(string key, string hex)
        {
            if (TryParseColor(hex, out var color))
            {
                Resources[key] = color;
                Resources[key + "Brush"] = new SolidColorBrush(color);
            }
        }

        private static bool TryParseColor(string hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            if (s.Length != 8) return false;
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            color = Color.FromArgb((byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            return true;
        }
    }
}
