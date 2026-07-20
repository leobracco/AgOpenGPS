// ============================================================================
// FormGps.SettingsSenderHost.cs
// Implementación de ISettingsSenderHost sobre FormGPS. CSettingsSender
// (SendSettings/SendRelaySettingsToMachineModule, extraídos de FormGPS.cs)
// vive en AgOpenGPS.Core y consume el host a través de esta interfaz
// (inversión de dependencia — traspaso de portabilidad 2026-07-19).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : ISettingsSenderHost
    {
        CPGN_FC ISettingsSenderHost.P252 => p_252;
        CPGN_FB ISettingsSenderHost.P251 => p_251;
        CPGN_EE ISettingsSenderHost.P238 => p_238;
        CPGN_EC ISettingsSenderHost.P236 => p_236;
        CPGN_EB ISettingsSenderHost.P235 => p_235;

        CSection[] ISettingsSenderHost.Section => section;
        CTool ISettingsSenderHost.Tool => tool;

        void ISettingsSenderHost.SendPgnToLoop(byte[] data) => SendPgnToLoop(data);
    }
}
