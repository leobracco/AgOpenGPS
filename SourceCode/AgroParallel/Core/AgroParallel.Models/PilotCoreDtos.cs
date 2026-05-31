// ============================================================================
// PilotCoreDtos.cs
// DTOs para las 4 categorías de "Core PilotX" que el view consume vía REST:
//   · Coverage  → triángulos pintados por sección
//   · Section   → estado actual de sectionOnRequest (decisión)
//   · QuantiX   → runtime y techo de dosis por motor
//   · Guidance  → XTE, heading error, steer-angle, modo activo
// El motor abajo sigue siendo PilotX (adaptadores FormGps* lo leen). Cuando
// migremos a un pipeline propio, solo cambia la impl detrás de la interfaz.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    // ---------------------------------------------------------------------
    // Coverage (paint PilotX-style)
    // ---------------------------------------------------------------------

    /// <summary>Vértice 2D en coords mundo (metros locales, easting/northing).</summary>
    public sealed class CoverageVertex
    {
        public double E { get; set; }
        public double N { get; set; }
        public CoverageVertex() { }
        public CoverageVertex(double e, double n) { E = e; N = n; }
    }

    /// <summary>Triangle strip de OpenGL: vértices intercalados que forman triángulos
    /// adyacentes. PilotX los emite así desde CPatches.</summary>
    public sealed class CoverageStrip
    {
        /// <summary>Vértices del strip (≥ 3 para formar al menos 1 triángulo).</summary>
        public List<CoverageVertex> Vertices { get; set; }
    }

    /// <summary>Cobertura agrupada por sección. Cada sección tiene N strips.</summary>
    public sealed class CoverageSection
    {
        public int Index { get; set; }
        /// <summary>True si la sección está habilitada en el implemento.</summary>
        public bool Enabled { get; set; }
        public List<CoverageStrip> Strips { get; set; }
    }

    public sealed class CoverageSnapshot
    {
        /// <summary>Lote actualmente abierto (path). Vacío si no hay job.</summary>
        public string FieldDirectory { get; set; }
        /// <summary>Revisión: incrementa cada vez que se agrega/borra cobertura.
        /// El cliente puede saltar el render si no cambió.</summary>
        public long Revision { get; set; }
        /// <summary>Color RGBA por defecto (0..255) para todas las strips.</summary>
        public int R { get; set; } = 75;
        public int G { get; set; } = 166;
        public int B { get; set; } = 63;
        public int A { get; set; } = 140;
        public List<CoverageSection> Sections { get; set; }
    }

    // ---------------------------------------------------------------------
    // Section control (decisión ON/OFF)
    // ---------------------------------------------------------------------

    public sealed class SectionControlSnapshot
    {
        public int NumSections { get; set; }
        /// <summary>sectionOnRequest por índice. La decisión de fondo (boundary,
        /// headland, anti-overlap, look-ahead) está enterrada en PilotX hoy; este DTO
        /// expone el resultado. Cuando movamos la lógica al Core, esta interfaz
        /// ya está en su lugar.</summary>
        public bool[] OnRequest { get; set; }
        /// <summary>True si el master de secciones está en modo automático.</summary>
        public bool IsAuto { get; set; }
        /// <summary>True si todas las secciones están forzadas ON por master manual.</summary>
        public bool IsManualOn { get; set; }
    }

    // ---------------------------------------------------------------------
    // QuantiX runtime (techo de dosis + estado en vivo por motor)
    // ---------------------------------------------------------------------

    /// <summary>Runtime de un motor QuantiX. Cubre la pregunta "qué dosis máxima
    /// puede aplicar hoy" + "qué pps está pidiendo el bridge en este instante".</summary>
    public sealed class QuantiXMotorRuntime
    {
        public string NodoUid { get; set; }
        public int MotorIndex { get; set; }
        public string Nombre { get; set; }
        public bool Habilitado { get; set; }

        /// <summary>kg/ha o seeds/ha objetivo en este instante. Viene de DosisFija,
        /// CampoDosis o shape global según el orden de prioridad del bridge.</summary>
        public double DosisObjetivo { get; set; }

        /// <summary>Pulsos por segundo objetivo (lo que el bridge publica a MQTT).</summary>
        public double TargetPps { get; set; }
        /// <summary>RPM equivalente al targetPps con DientesEngranaje.</summary>
        public double TargetRpm { get; set; }

        // ---- Techo operativo ----
        /// <summary>Hz máximos del motor a PWM máx (medidos en calibración).</summary>
        public double MaxHz { get; set; }
        /// <summary>RPM máxima derivada: MaxHz × 60 / DientesEngranaje.</summary>
        public double MaxRpm { get; set; }
        /// <summary>Gramos o semillas por segundo a MaxHz. = MaxHz × MeterCal.</summary>
        public double MaxOutputPerSec { get; set; }
        /// <summary>Techo de dosis (kg/ha o seeds/ha) a la velocidad actual y
        /// ancho activo. Si v=0 devuelve infinito conceptual (lo emitimos como -1).</summary>
        public double MaxDoseAtCurrentSpeed { get; set; }
        /// <summary>Tabla de dosis máx a velocidades típicas (5/7/10/12 km/h).</summary>
        public List<QuantiXMaxDosePoint> MaxDoseCurve { get; set; }
    }

    public sealed class QuantiXMaxDosePoint
    {
        public double SpeedKmh { get; set; }
        public double MaxDose { get; set; }
    }

    public sealed class QuantiXRuntimeSnapshot
    {
        public List<QuantiXMotorRuntime> Motores { get; set; }
        public double CurrentSpeedKmh { get; set; }
        public double CurrentToolWidthM { get; set; }
    }

    // ---------------------------------------------------------------------
    // Guidance (lo que hoy calcula CABLine + CABCurve + CYouTurn + CGuidance)
    // ---------------------------------------------------------------------

    public sealed class GuidanceSnapshot
    {
        /// <summary>"None" | "AB" | "Curve" | "Pivot" | "Headland" | "YouTurn"</summary>
        public string Mode { get; set; }
        /// <summary>Hay alguna referencia de guía configurada (A-B definidos, curva trazada).</summary>
        public bool IsLineSet { get; set; }
        /// <summary>Autopilot activo (engagement).</summary>
        public bool IsAutoSteerOn { get; set; }

        /// <summary>XTE: distancia perpendicular del pivote a la línea, en metros (con signo).</summary>
        public double XteMeters { get; set; }
        /// <summary>Error de heading respecto a la línea, en radianes.</summary>
        public double HeadingErrorRad { get; set; }
        /// <summary>Comando de steer-angle al actuador, en grados (deg).</summary>
        public double SteerAngleCommandDeg { get; set; }
        /// <summary>Distancia al próximo waypoint / al final de la pasada, en metros.</summary>
        public double DistanceToEndM { get; set; }

        /// <summary>Punto LookAhead actual (donde está mirando la lógica de seguimiento). null si no aplica.</summary>
        public FieldPoint LookAhead { get; set; }
    }
}
