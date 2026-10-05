namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CRecordedPath necesita del host (FormGPS en WinForms). Hereda de
    /// IYouTurnHost (PivotAxlePos/AutoBtnState/Vehicle/etc.) y agrega el
    /// acceso al simulador, al YouTurn (ya en Core) y dos hooks de UI.
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// </summary>
    public interface IRecordedPathHost : IYouTurnHost
    {
        /// <summary>yt — CYouTurn ya vive en Core, se expone directo.</summary>
        CYouTurn YouTurn { get; }

        /// <summary>sim.stepDistance — velocidad del simulador (m/tick).</summary>
        double SimStepDistance { set; }

        /// <summary>btnSectionMasterAuto.PerformClick() — toggle de secciones auto.</summary>
        void ClickSectionMasterAuto();

        /// <summary>UI al frenar el path grabado: botón play + rehabilitar botonera.</summary>
        void OnRecordedPathStopped();
    }
}
