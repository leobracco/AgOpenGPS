using System;

namespace AgOpenGPS.AvaloniaApp.Services.Branding
{
    public interface IBrandingService
    {
        BrandingDefinition Current { get; }
        event EventHandler<BrandingDefinition>? BrandingChanged;
        void Reload();
    }
}
