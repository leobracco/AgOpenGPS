namespace AgOpenGPS.AvaloniaApp.Services.Branding
{
    public sealed class BrandingDefinition
    {
        public int SchemaVersion { get; set; } = 1;
        public string PilotName { get; set; } = "CentriX";
        public string Tagline { get; set; } = "Precision Seeding Co-Pilot";
        public string AccentColorHex { get; set; } = "#4CD964";
        public string WarningColorHex { get; set; } = "#FF9500";
        public string ErrorColorHex { get; set; } = "#FF3B30";
        public string BackgroundColorHex { get; set; } = "#0E0E0E";
        public string SurfaceColorHex { get; set; } = "#1A1A1A";
        public string ForegroundColorHex { get; set; } = "#FFFFFF";
        public string FontFamily { get; set; } = "Inter";
        public bool ShowVersionInHeader { get; set; } = true;
    }
}
