namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CYouTurn necesita del host (FormGPS en WinForms). Hereda de
    /// IGuidanceHost (ABLine/Curve/Vehicle/Bnd/Mc/escalares) y agrega lo
    /// propio del U-turn. Inversión de dependencia para el traspaso de
    /// portabilidad (2026-07-17).
    /// </summary>
    public interface IYouTurnHost : IGuidanceHost
    {
        /// <summary>autoBtnState — estado del master de secciones auto.</summary>
        btnStates AutoBtnState { get; }

        /// <summary>manualBtnState — estado del master de secciones manual.</summary>
        btnStates ManualBtnState { get; }

        /// <summary>distancePivotToTurnLine — distancia pivot→línea de giro (m).</summary>
        double DistancePivotToTurnLine { get; set; }

        /// <summary>makeUTurnCounter — debounce de armado del U-turn.</summary>
        int MakeUTurnCounter { get; set; }

        /// <summary>pivotAxlePos — posición del eje pivote.</summary>
        vec3 PivotAxlePos { get; }

        /// <summary>steerAxlePos — posición del eje directriz.</summary>
        vec3 SteerAxlePos { get; }

        /// <summary>sounds.isBoundAlarming — apagar la alarma de límite.</summary>
        bool IsBoundAlarming { set; }

        /// <summary>p_239.pgn[uturn] = 0 — limpiar el byte de U-turn del PGN 239.</summary>
        void ClearUTurnPgn();
    }
}
