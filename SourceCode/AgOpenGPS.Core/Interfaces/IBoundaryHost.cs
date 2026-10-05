namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CBoundary (partials CBoundary/CFence/CTurn/CHead)
    /// necesita del host (FormGPS en WinForms). Inversión de dependencia para
    /// el traspaso de portabilidad (2026-07-17). CModuleComm/CFieldData/
    /// CSection ya viven en Core y se exponen directos; sonido y PGN de
    /// hidráulico quedan detrás de métodos.
    /// </summary>
    public interface IBoundaryHost
    {
        CModuleComm Mc { get; }
        CFieldData Fd { get; }
        CSection[] Section { get; }

        /// <summary>yt.uturnDistanceFromBoundary — ancho del espacio de giro.</summary>
        double UturnDistanceFromBoundary { get; }

        /// <summary>ABLine.lineWidth — grosor de línea de guiado (px GL).</summary>
        int ABLineWidth { get; }

        vec3 PivotAxlePos { get; }
        vec3 ToolPivotPos { get; }

        double AvgSpeed { get; }
        bool IsReverse { get; }

        /// <summary>vehicle.isHydLiftOn.</summary>
        bool IsHydLiftOn { get; }

        /// <summary>Aviso de proximidad de cabecera habilitado.</summary>
        bool IsHeadlandDistanceOn { get; }

        // --- tool ---
        int ToolNumOfSections { get; }
        int ToolRpWidth { get; }
        double ToolLookAheadOnPixelsLeft { get; }
        double ToolLookAheadOnPixelsRight { get; }
        bool ToolIsLeftSideInHeadland { get; set; }
        bool ToolIsRightSideInHeadland { get; set; }

        /// <summary>p_239.pgn[p_239.hydLift] = value (1 = bajar, 2 = subir).</summary>
        void SetHydLiftPgn(byte value);

        // --- sonido (CSound queda en GPS por los Resources) ---
        bool IsHydLiftChange { get; set; }
        bool IsHydLiftSoundOn { get; }
        void PlayHydLiftUp();
        void PlayHydLiftDn();
        bool IsBoundAlarming { get; set; }
        void PlayHeadlandSound();
    }
}
