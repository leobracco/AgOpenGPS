// ============================================================================
// FormGps.SectionsHost.cs
// Implementación de ISectionsHost sobre FormGPS. CSectionCalculator (los
// cuatro métodos de cálculo puro de secciones, extraídos de
// Sections.Designer.cs) vive en AgOpenGPS.Core y consume el host a través
// de esta interfaz (inversión de dependencia — traspaso de portabilidad
// 2026-07-19).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : ISectionsHost
    {
        CTool ISectionsHost.Tool => tool;
        CSection[] ISectionsHost.Section => section;
        int ISectionsHost.MaxSections => MAXSECTIONS;
        double ISectionsHost.AvgSpeed => avgSpeed;
        CTram ISectionsHost.Tram => tram;

        CPGN_FE ISectionsHost.P254 => p_254;
        CPGN_EF ISectionsHost.P239 => p_239;
        CPGN_E5 ISectionsHost.P229 => p_229;
    }
}
