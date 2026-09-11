namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CHeadingUpdater necesita del host (FormGPS en WinForms). Hereda
    /// de IAutoSteerHost (Pn/Fd/Vehicle/Ahrs/Mc/AvgSpeed/TimedMessageBox, ya
    /// existía) y agrega el estado propio del switch de heading (Fix/VTG/
    /// Dual), extraído de UpdateFixPosition en Position.designer.cs —
    /// traspaso de portabilidad (2026-07-20, bloque 9 matriz Android).
    /// GpsHeading/IsReverse/IsChangingDirection se amplían a get/set en
    /// IAutoSteerHost porque este método las escribe (antes solo las leía).
    /// </summary>
    public interface IHeadingHost : IAutoSteerHost
    {
        /// <summary>headingFromSource — qué caso del switch corre ("Fix"/"VTG"/"Dual").</summary>
        string HeadingFromSource { get; set; }

        /// <summary>fixHeading — heading corregido usado para dibujar/PGN.</summary>
        double FixHeading { get; set; }

        /// <summary>camHeading — heading de cámara (grados).</summary>
        double CamHeading { get; set; }

        /// <summary>smoothCamHeading — heading de cámara suavizado (radianes).</summary>
        double SmoothCamHeading { get; set; }

        /// <summary>prevFix — último fix válido usado para heading por distancia.</summary>
        vec2 PrevFix { get; set; }

        /// <summary>prevDistFix — último fix usado solo para el display de distancia.</summary>
        vec2 PrevDistFix { get; set; }

        /// <summary>lastReverseFix — último fix usado para detectar reversa en Dual.</summary>
        vec2 LastReverseFix { get; set; }

        /// <summary>lastGPS — se fija al setear el primer heading (no se lee en este método, pero se preserva el campo).</summary>
        vec2 LastGps { get; set; }

        /// <summary>stepFixPts — historial circular de fixes para heading por pasos.</summary>
        vecFix2Fix[] StepFixPts { get; }

        /// <summary>currentStepFix — índice del paso usado este frame.</summary>
        int CurrentStepFix { get; set; }

        double DistanceCurrentStepFix { get; set; }
        double DistanceCurrentStepFixDisplay { get; set; }
        double FixToFixHeadingDistance { get; set; }

        /// <summary>minHeadingStepDist — distancia mínima entre pasos para heading (m).</summary>
        double MinHeadingStepDist { get; }

        /// <summary>gpsMinimumStepDistance — distancia mínima de fix a fix para no descartar.</summary>
        double GpsMinimumStepDistance { get; }

        bool IsFirstHeadingSet { get; set; }
        bool HasBeenFirstHeadingSet { get; set; }
        bool IsReverseWithIMU { get; set; }

        /// <summary>delta — ángulo entre heading previo y nuevo (detección de reversa).</summary>
        double Delta { get; set; }

        /// <summary>filteredDelta — delta pasabajos, para no confundir ruido con cambio de dirección real.</summary>
        double FilteredDelta { get; set; }

        /// <summary>imuGPS_Offset — offset entre heading de IMU y de GPS.</summary>
        double ImuGpsOffset { get; set; }

        /// <summary>imuCorrected — heading de IMU ya corregido con el offset.</summary>
        double ImuCorrected { get; set; }

        double RollCorrectionDistance { get; set; }
        double CorrectionDistanceGraph { get; set; }
        double UncorrectedEastingGraph { get; set; }

        /// <summary>camSmoothFactor — cuánto se suaviza el heading de cámara (fijo, de Settings).</summary>
        double CamSmoothFactor { get; }

        /// <summary>dualReverseDetectionDistance — distancia mínima para chequear reversa en Dual.</summary>
        double DualReverseDetectionDistance { get; }

        /// <summary>lblSpeed.ForeColor — rojo cuando autoswitch Dual pasa a Fix, verde cuando vuelve a Dual.</summary>
        void SetSpeedLabelColor(bool isRed);

        /// <summary>Resto del cálculo de posición (hitch/pivote/secciones), ya en CPositionUpdater.</summary>
        void TheRest();
    }
}
