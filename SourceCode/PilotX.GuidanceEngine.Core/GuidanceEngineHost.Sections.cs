// ============================================================================
// GuidanceEngineHost.Sections.cs — ISectionsHost + ISettingsSenderHost.
// Mismo patrón que FormGps.SectionsHost.cs/FormGps.SettingsSenderHost.cs.
// ============================================================================

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost : ISectionsHost, ISettingsSenderHost
    {
        CTool ISectionsHost.Tool => Tool;
        CSection[] ISectionsHost.Section => Sections;
        int ISectionsHost.MaxSections => MaxSectionsConst;
        double ISectionsHost.AvgSpeed => avgSpeed;
        CTram ISectionsHost.Tram => Tram;
        CPGN_FE ISectionsHost.P254 => P254Field;
        CPGN_EF ISectionsHost.P239 => P239Field;
        CPGN_E5 ISectionsHost.P229 => P229Field;

        CPGN_FC ISettingsSenderHost.P252 => P252Field;
        CPGN_FB ISettingsSenderHost.P251 => P251Field;
        CPGN_EE ISettingsSenderHost.P238 => P238Field;
        CPGN_EC ISettingsSenderHost.P236 => P236Field;
        CPGN_EB ISettingsSenderHost.P235 => P235Field;
        CSection[] ISettingsSenderHost.Section => Sections;
        CTool ISettingsSenderHost.Tool => Tool;
        void ISettingsSenderHost.SendPgnToLoop(byte[] data) => SendPgnToLoop(data);
    }
}
