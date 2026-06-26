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

    /// <summary>Geometria de la linea/curva activa para el render del mapa
    /// (Stage 3 migracion OpenGL PilotX.Desktop). Separada de GuidanceSnapshot
    /// porque cambia de cadencia: control state se polea ~4 Hz, geometria
    /// solo cuando se cambia de linea o se redefine — 1 Hz alcanza.</summary>
    public sealed class GuidanceGeometrySnapshot
    {
        /// <summary>"Off" | "AB" | "Curve" | "Contour" — corresponde al modo
        /// activo en FormGPS cuando se tomo el snapshot.</summary>
        public string Mode { get; set; } = "Off";
        /// <summary>Polyline en coords mundo (easting/northing locales) que
        /// representa la linea actual. Para AB se entrega como dos puntos
        /// extendidos lejos (~500m) a ambos lados del segmento original; el
        /// cliente puede asumir que es una recta. Para Curve y Contour es la
        /// polilinea real ya cargada en memoria.</summary>
        public List<FieldPoint> Points { get; set; }
        /// <summary>Revision: incrementa cuando cambia el set de puntos (mode
        /// toggle, AB redefinida, curva nueva). El cliente usa este numero
        /// para evitar re-upload del VBO cuando no cambio nada.</summary>
        public long Revision { get; set; }
    }

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

    // ---------------------------------------------------------------------
    // Tool geometry (Stage 4a — barra del implemento + estado por seccion)
    // ---------------------------------------------------------------------

    /// <summary>Una seccion del implemento en coords mundo + estado en vivo.
    /// El render dibuja un quad entre (Left, Right) en este frame y el
    /// (Left, Right) del frame anterior — eso lo hace FormGPS legacy con un
    /// TRIANGLE_STRIP cada 4 puntos. Aca solo entregamos los puntos
    /// actuales; el cliente arma el strip si quiere efecto de "estela", o
    /// dibuja la barra como linea simple Left↔Right por seccion (Stage 4a
    /// arranca con el segundo enfoque).</summary>
    public sealed class ToolSectionGeometry
    {
        /// <summary>Indice de la seccion (0..NumSections-1).</summary>
        public int Index { get; set; }
        public double LeftE { get; set; }
        public double LeftN { get; set; }
        public double RightE { get; set; }
        public double RightN { get; set; }
        /// <summary>Esta seccion esta efectivamente ON (aplica producto en este
        /// instante). Distinto de sectionOnRequest — este es el state final
        /// post-decision.</summary>
        public bool IsOn { get; set; }
        /// <summary>Esta seccion esta dibujando coverage (paint area).</summary>
        public bool IsMapping { get; set; }
        /// <summary>Estado del boton del operario: 0=Off, 1=Auto, 2=On (manual).</summary>
        public int BtnState { get; set; }
    }

    /// <summary>Geometria del implemento (Stage 4a). Cambia cada frame que
    /// el tractor se mueve — sin revision-cache, el cliente re-uploadea el
    /// VBO en cada poll. Cadencia esperada: 4 Hz (igual que el HUD).
    /// Payload chico (~16 secciones × 6 doubles = trivial).</summary>
    public sealed class ToolGeometrySnapshot
    {
        /// <summary>Cuantas secciones tiene el implemento configurado.</summary>
        public int NumSections { get; set; }
        /// <summary>True si la geometria es valida (job started + tool inicializado).
        /// Si es false, el cliente debe esconder la capa.</summary>
        public bool IsValid { get; set; }
        public List<ToolSectionGeometry> Sections { get; set; }
    }

    // ---------------------------------------------------------------------
    // Tram geometry (Stage 4b — lineas de tramline / wheel tracks)
    // ---------------------------------------------------------------------

    /// <summary>Una polyline de tramline en coords mundo. Cada tramline marca
    /// donde tienen que pasar las ruedas en pasadas siguientes.</summary>
    public sealed class TramLine
    {
        public List<FieldPoint> Points { get; set; }
    }

    /// <summary>Modo de display del tram (eco del enum interno de PilotX
    /// TramMode): "None" | "All" | "FillTracks" | "BoundaryTracks". El
    /// render usa este flag para decidir que renderear:
    ///   · None             → nada
    ///   · All              → todo: lines + outer + inner
    ///   · FillTracks       → solo lines (las generadas adentro del lote)
    ///   · BoundaryTracks   → solo outer + inner (tracks pegados al borde)</summary>
    public sealed class TramGeometrySnapshot
    {
        public string DisplayMode { get; set; } = "None";
        public List<TramLine> Lines { get; set; }
        /// <summary>Tram outer boundary (uno solo). Lista de puntos cerrada.</summary>
        public List<FieldPoint> OuterBoundary { get; set; }
        /// <summary>Tram inner boundary (idem, cerrada).</summary>
        public List<FieldPoint> InnerBoundary { get; set; }
        /// <summary>Revision: incrementa cuando se regenera el tram (cambio de
        /// passes, ancho, alpha, displayMode). Permite al cliente saltar el
        /// re-upload del VBO.</summary>
        public long Revision { get; set; }
    }
}
