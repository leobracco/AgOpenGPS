using System;
using AgOpenGPS.AvaloniaApp.Services.Branding;
using AgOpenGPS.AvaloniaApp.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public partial class HeaderBarViewModel : ViewModelBase
    {
        private readonly IBrandingService _branding;

        [ObservableProperty]
        private string _pilotName = "CentriX";

        [ObservableProperty]
        private string _tagline = string.Empty;

        public KpiTileViewModel SpeedTile { get; } = new("VELOCIDAD", "km/h");
        public KpiTileViewModel RpmTile { get; } = new("RPM", "rpm");
        public KpiTileViewModel GpsTile { get; } = new("GPS", "");
        public KpiTileViewModel HectaresTile { get; } = new("HECTAREAS", "ha");

        public HeaderBarViewModel(IBrandingService branding)
        {
            _branding = branding;
            ApplyBranding(branding.Current);
            branding.BrandingChanged += OnBrandingChanged;

            GpsTile.Value = "—";
            GpsTile.Badge = "NO FIX";
            SpeedTile.Value = "0.0";
            RpmTile.Value = "0";
            HectaresTile.Value = "0.0";
        }

        private void OnBrandingChanged(object? sender, BrandingDefinition def) => ApplyBranding(def);

        private void ApplyBranding(BrandingDefinition def)
        {
            PilotName = def.PilotName;
            Tagline = def.Tagline;
        }
    }
}
