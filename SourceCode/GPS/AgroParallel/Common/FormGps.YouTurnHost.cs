// ============================================================================
// FormGps.YouTurnHost.cs
// Implementación de IYouTurnHost sobre FormGPS. CYouTurn vive en
// AgOpenGPS.Core y consume el host a través de esta interfaz (hereda de
// IGuidanceHost — inversión de dependencia, traspaso de portabilidad
// 2026-07-17).
// ============================================================================

namespace AgOpenGPS
{
    public partial class FormGPS : IYouTurnHost
    {
        btnStates IYouTurnHost.AutoBtnState => autoBtnState;
        btnStates IYouTurnHost.ManualBtnState => manualBtnState;

        double IYouTurnHost.DistancePivotToTurnLine
        {
            get => distancePivotToTurnLine;
            set => distancePivotToTurnLine = value;
        }

        int IYouTurnHost.MakeUTurnCounter
        {
            get => makeUTurnCounter;
            set => makeUTurnCounter = value;
        }

        vec3 IYouTurnHost.PivotAxlePos => pivotAxlePos;
        vec3 IYouTurnHost.SteerAxlePos => steerAxlePos;

        bool IYouTurnHost.IsBoundAlarming { set => sounds.isBoundAlarming = value; }

        void IYouTurnHost.ClearUTurnPgn() => p_239.pgn[p_239.uturn] = 0;
    }
}
