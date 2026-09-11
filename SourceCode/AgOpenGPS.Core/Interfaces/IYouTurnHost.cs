namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CYouTurn necesita del host (FormGPS en WinForms). Hereda de
    /// IGuidanceHost (ABLine/Curve/Vehicle/Bnd/Mc/escalares) y agrega lo
    /// propio del U-turn. Inversión de dependencia para el traspaso de
    /// portabilidad (2026-07-17). Ampliada 2026-07-20 con lo que necesita
    /// CYouTurnUpdater (stop crítico por boundary + creación/disparo del
    /// youturn, extraído de UpdateFixPosition en Position.designer.cs) —
    /// mismo host, así se evita una interfaz nueva redundante.
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

        /// <summary>sounds.isBoundAlarming.</summary>
        bool IsBoundAlarming { get; set; }

        /// <summary>p_239.pgn[uturn] = 0 — limpiar el byte de U-turn del PGN 239.</summary>
        void ClearUTurnPgn();

        /// <summary>yt (CYouTurn) — la instancia misma, para orquestar creación/disparo desde fuera.</summary>
        CYouTurn Yt { get; }

        /// <summary>crossTrackError — promedio de error de cruce, ya calculado por CAutoSteerUpdater.</summary>
        int CrossTrackError { get; }

        /// <summary>sounds.isTurnSoundOn.</summary>
        bool IsTurnSoundOn { get; }

        /// <summary>Reproduce el sonido de "creación de youturn demasiado cerca".</summary>
        void PlayTurnTooCloseSound();

        /// <summary>Reproduce el sonido de alarma de boundary.</summary>
        void PlayBoundaryAlarmSound();
    }
}
