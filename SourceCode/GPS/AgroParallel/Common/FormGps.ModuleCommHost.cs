// ============================================================================
// FormGps.ModuleCommHost.cs
// Implementación de IModuleCommHost sobre FormGPS. CModuleComm vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (inversión de
// dependencia — traspaso de portabilidad 2026-07-17).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : IModuleCommHost
    {
        bool IModuleCommHost.IsAutoSteerAuto => ahrs.isAutoSteerAuto;
        bool IModuleCommHost.IsBtnAutoSteerOn => isBtnAutoSteerOn;
        btnStates IModuleCommHost.AutoBtnState => autoBtnState;
        btnStates IModuleCommHost.ManualBtnState => manualBtnState;
    }
}
