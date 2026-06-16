// AogStateSnapshot — DTO read-only del estado de PilotX que los servicios
// AgroParallel consumen. Producido por IAogStateProvider.

using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>
    /// Geometría lateral de una sección, en metros relativos al centro de la
    /// herramienta. Negativo = izquierda del centro, positivo = derecha.
    /// </summary>
    public sealed class SectionExtent
    {
        public int Index { get; set; }
        public double Left { get; set; }
        public double Right { get; set; }
        public SectionExtent() { }
        public SectionExtent(int index, double left, double right)
        {
            Index = index; Left = left; Right = right;
        }
    }

    /// <summary>Punto 2D en metros locales (Easting/Northing).</summary>
    public sealed class FieldPoint
    {
        public double E { get; set; }
        public double N { get; set; }
        public FieldPoint() { }
        public FieldPoint(double e, double n) { E = e; N = n; }
    }

    /// <summary>Track activo (AB line / curve / pivote). null si no hay.</summary>
    public sealed class TrackInfo
    {
        /// <summary>Nombre puesto en PilotX.</summary>
        public string Name { get; set; }

        /// <summary>"AB" | "Curve" | "Pivot" | "None" | otro string.</summary>
        public string Mode { get; set; }

        /// <summary>Heading en rad (solo válido para modo AB).</summary>
        public double Heading { get; set; }

        /// <summary>Punto A (para AB lines). null si no aplica.</summary>
        public FieldPoint A { get; set; }
        /// <summary>Punto B (para AB lines). null si no aplica.</summary>
        public FieldPoint B { get; set; }

        /// <summary>Curva discretizada (para Curve / Pivot). null o vacío si no aplica.</summary>
        public List<FieldPoint> CurvePts { get; set; }
    }

    /// <summary>
    /// Snapshot inmutable del estado relevante de PilotX en un instante dado.
    /// Lo que necesitan los bridges (QuantiX/SectionX/OrbitX) para operar
    /// sin tener una referencia directa a FormGPS.
    /// </summary>
    public sealed class AogStateSnapshot
    {
        public bool IsJobStarted { get; set; }
        public string CurrentFieldDirectory { get; set; }

        /// <summary>
        /// Ruta absoluta del directorio raíz "Fields/" de PilotX. Producida por el
        /// host (en PilotX: <c>RegistrySettings.fieldsDirectory</c>). Permite a los
        /// servicios portables (OrbitXSync, etc.) localizar archivos de lotes
        /// sin acoplarse a <c>AgOpenGPS</c>.
        /// </summary>
        public string FieldsDirectory { get; set; }

        public double AvgSpeed { get; set; }      // km/h
        public double Heading { get; set; }       // rad

        public double PivotEasting { get; set; }
        public double PivotNorthing { get; set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }

        public int NumSections { get; set; }
        // sectionOnRequest por índice [0..NumSections-1].
        public bool[] SectionOnRequest { get; set; }

        /// <summary>Ancho de la herramienta en metros (tool.width).</summary>
        public double ToolWidth { get; set; }

        /// <summary>Offset lateral de la herramienta respecto al centro (tool.offset, metros).</summary>
        public double ToolOffset { get; set; }

        /// <summary>Posición del pivote de la herramienta (toolPos.easting), metros locales.</summary>
        public double ToolEasting { get; set; }
        /// <summary>Posición del pivote de la herramienta (toolPos.northing), metros locales.</summary>
        public double ToolNorthing { get; set; }
        /// <summary>Heading del pivote de la herramienta (toolPos.heading), radianes.</summary>
        public double ToolHeading { get; set; }

        /// <summary>
        /// Geometría lateral de cada sección, en metros relativos al centro
        /// de la herramienta (negativo = izquierda, positivo = derecha).
        /// Misma longitud que SectionOnRequest. Permite al renderer del Piloto
        /// dibujar un swath independiente por sección activa.
        /// </summary>
        public List<SectionExtent> SectionPositions { get; set; }

        /// <summary>
        /// Velocidad de cada sección en km/h (signo conservado: negativo = la
        /// sección va para atrás respecto al heading del implemento, ej.
        /// headland turn). Captura el efecto de rotación del implemento en
        /// curvas — la sección externa va más rápido que el pivote GPS y la
        /// interna más lento (o reversa). Misma longitud y orden que
        /// SectionOnRequest. Origen: <c>CSection.speedPixels * 0.36</c>
        /// (10 px/m → m/s → km/h), filtrado exponencial 70/30 en PilotX.
        /// Consumidores típicos: QuantiX (dosis por motor), FlowX, VistaX
        /// (SPM esperado por surco), publisher MQTT.
        /// </summary>
        public double[] SectionSpeedsKmh { get; set; }

        /// <summary>Velocidad del extremo izquierdo del implemento en km/h
        /// (<c>tool.farLeftSpeed * 3.6</c>, filtrado 70/30).</summary>
        public double ToolFarLeftSpeedKmh { get; set; }

        /// <summary>Velocidad del extremo derecho del implemento en km/h
        /// (<c>tool.farRightSpeed * 3.6</c>, filtrado 70/30).</summary>
        public double ToolFarRightSpeedKmh { get; set; }

        /// <summary>Dosis actual leída del shapefile (kg/ha) en la posición
        /// del tractor. 0 si no hay shapefile o si IsInsideShape == false.</summary>
        public double ShapeCurrentDose { get; set; }

        /// <summary>Tractor está dentro de un polígono del shapefile activo.</summary>
        public bool ShapeIsInside { get; set; }

        /// <summary>"Tractor" | "Harvester" | "Articulated". Lo que viene de Settings.</summary>
        public string VehicleType { get; set; }

        /// <summary>Marca del vehículo: "AGOpenGPS","JohnDeere","Fendt", etc.
        /// Se mapea a un sprite en /img/{tractors|harvesters}/.</summary>
        public string VehicleBrand { get; set; }

        // ---- Geometría del lote (todo en metros locales, mismo frame que PivotEasting/Northing) ----

        /// <summary>Boundaries del lote. El primero es el contorno externo,
        /// los siguientes (si hay) son drive-thru islands.</summary>
        public List<List<FieldPoint>> Boundaries { get; set; }

        /// <summary>Cabeceras (headland offset). Una por boundary; puede estar vacía.</summary>
        public List<List<FieldPoint>> Headlands { get; set; }

        /// <summary>Track de guía actualmente activo (AB line / curve / pivot). null si no hay.</summary>
        public TrackInfo ActiveTrack { get; set; }

        // ---- Áreas trabajadas (m²) ------------------------------------------
        // Provenientes de CFieldData. Diferencia entre las dos:
        //   - WorkedAreaTotalM2 cuenta cada paso por sección como área trabajada
        //     (incluye solapamiento si pasaste 2 veces por el mismo lugar).
        //   - ActualAreaCoveredM2 deduce el solapamiento — área "neta" cubierta.
        // Diferencia (Worked - Actual) = "área repintada" = base para calcular
        // el insumo ahorrado por el corte automático de secciones (SectionX/FlowX).

        /// <summary>Área total trabajada en m² (incluye solapamiento).</summary>
        public double WorkedAreaTotalM2 { get; set; }

        /// <summary>Área neta cubierta en m² (sin contar solapamiento).</summary>
        public double ActualAreaCoveredM2 { get; set; }

        /// <summary>
        /// Área del lote según el lindero (boundary exterior menos boundaries
        /// internos/exclusiones), en m². 0 si no hay lindero cargado. Equivale a
        /// CFieldData.areaBoundaryOuterLessInner. Para hectáreas: × 0.0001.
        /// </summary>
        public double BoundaryAreaM2 { get; set; }
    }

    /// <summary>
    /// Polígono de la capa shapefile activa (prescripción / dosis variable).
    /// Cada ring es un array intercalado [e0,n0, e1,n1, ...] en coords locales
    /// (las mismas que <see cref="AogStateSnapshot.PivotEasting"/>). El primer ring
    /// es el contorno exterior; los siguientes (si hay) son agujeros.
    /// </summary>
    public sealed class ShapePolygon
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }
        public List<double[]> Rings { get; set; }
    }

    /// <summary>
    /// Snapshot de la capa shapefile cargada en PilotX. La UI Piloto la pinta
    /// como overlay con dosis por color. Cadencia recomendada: 1 Hz (cambia
    /// solo al abrir/cerrar un .shp).
    /// </summary>
    public sealed class ShapeSnapshot
    {
        /// <summary>Identidad del shapefile cargado; el cliente dropea cache cuando cambia.</summary>
        public string SourceToken { get; set; }
        /// <summary>Cantidad de polígonos.</summary>
        public int Count { get; set; }
        /// <summary>Nombre del campo DBF usado para colorear (null = uniforme).</summary>
        public string StyleField { get; set; }
        public double StyleMin { get; set; }
        public double StyleMax { get; set; }
        public List<ShapePolygon> Polygons { get; set; }
    }

    /// <summary>
    /// Metadata de un campo DBF del shapefile activo. La UI lo usa para poblar
    /// dropdowns (ej. CampoDosis en QuantiX) y descartar columnas no numéricas.
    /// </summary>
    public sealed class ShapeFieldInfo
    {
        public string Name { get; set; }
        /// <summary>True si más del 50% de los polígonos tienen valor parseable a double.</summary>
        public bool Numeric { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        /// <summary>Cantidad de polígonos con valor numérico en este campo.</summary>
        public int Count { get; set; }
    }

    /// <summary>
    /// Respuesta del endpoint /api/aog/shape-fields. Incluye identidad del shape
    /// activo (SourceToken) para que la UI invalide cache al cambiar de capa.
    /// </summary>
    public sealed class ShapeFieldsSnapshot
    {
        public string SourceToken { get; set; }
        public List<ShapeFieldInfo> Fields { get; set; } = new List<ShapeFieldInfo>();
    }
}
