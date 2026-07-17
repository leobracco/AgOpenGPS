// ============================================================================
// FormGps.IsobusHost.cs
// Implementación de IIsobusHost sobre FormGPS. CISOBUS vive en AgOpenGPS.Core
// y consume el host a través de esta interfaz (inversión de dependencia,
// traspaso de portabilidad 2026-07-17). SendPgnToLoop ya existe en FormGPS
// (implementación implícita).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : IIsobusHost
    {
        bool IIsobusHost.IsobusSectionControlImageOn
        {
            set => btnIsobusSectionControl.Image = value
                ? Properties.Resources.IsobusSectionControlOn
                : Properties.Resources.IsobusSectionControlOff;
        }

        bool IIsobusHost.IsobusButtonVisible
        {
            set => btnIsobusSectionControl.Visible = value;
        }
    }
}
