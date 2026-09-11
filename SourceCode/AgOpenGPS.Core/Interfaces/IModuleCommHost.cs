namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CModuleComm necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// </summary>
    public interface IModuleCommHost
    {
        /// <summary>ahrs.isAutoSteerAuto — el switch remoto habilita autosteer.</summary>
        bool IsAutoSteerAuto { get; }

        /// <summary>Estado actual del botón de autosteer en pantalla.</summary>
        bool IsBtnAutoSteerOn { get; }

        /// <summary>Estado del botón master de secciones automático.</summary>
        btnStates AutoBtnState { get; }

        /// <summary>Estado del botón master de secciones manual.</summary>
        btnStates ManualBtnState { get; }
    }
}
