// ============================================================================
// FormGps.RecordedPathHost.cs
// Implementación de IRecordedPathHost sobre FormGPS. CRecordedPath vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (hereda de
// IYouTurnHost — inversión de dependencia, traspaso de portabilidad 2026-07-17).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : IRecordedPathHost
    {
        CYouTurn IRecordedPathHost.YouTurn => yt;

        double IRecordedPathHost.SimStepDistance
        {
            set => sim.stepDistance = value;
        }

        void IRecordedPathHost.ClickSectionMasterAuto() => btnSectionMasterAuto.PerformClick();

        void IRecordedPathHost.OnRecordedPathStopped()
        {
            btnPathGoStop.Image = Properties.Resources.boundaryPlay;
            btnPathRecordStop.Enabled = true;
            btnPickPath.Enabled = true;
            btnResumePath.Enabled = true;
        }
    }
}
