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

        /// <summary>Metros del pivote a la linea de giro (cabecera).
        /// -2222 = sin linea de giro / sin dato. La usan los overlays para
        /// auto-minimizarse cerca de la cabecera (ahi manda el mapa).</summary>
        public double DistanciaCabeceraM { get; set; } = -2222;
        public double Heading { get; set; }       // rad

        /// <summary>Calidad de fix GPS (pn.fixQuality): 4=RTK fijo, 5=RTK float, 2=DGPS, otro=sin fix.</summary>
        public int FixQuality { get; set; }

        /// <summary>Alimentación externa conectada (SystemInformation.PowerStatus), para la barra superior HTML.</summary>
        public bool PowerOnline { get; set; }

        // ---- Barra derecha HTML (espejo del panelRight nativo) ----------------
        // Estados de los botones de operación. La UI web replica con esto las
        // mismas imágenes/visibilidades que pinta GUI.Designer.cs.

        /// <summary>Piloto automático activado (isBtnAutoSteerOn).</summary>
        public bool IsAutoSteerOn { get; set; }

        /// <summary>Auto-centrar guía al pivote (trk.isAutoSnapToPivot) — cambia el ícono del piloto.</summary>
        public bool IsAutoSnapToPivot { get; set; }

        /// <summary>Giro automático en cabecera activado (yt.isYouTurnBtnOn).</summary>
        public bool IsYouTurnOn { get; set; }

        /// <summary>Secciones en automático (autoBtnState == Auto).</summary>
        public bool IsSectionAutoOn { get; set; }

        /// <summary>Secciones en manual (manualBtnState == On).</summary>
        public bool IsSectionManualOn { get; set; }

        /// <summary>Hay comunicación ISOBUS viva (isobus.IsAlive()) — muestra el botón.</summary>
        public bool IsobusAlive { get; set; }

        /// <summary>Control de secciones ISOBUS habilitado.</summary>
        public bool IsobusOn { get; set; }

        /// <summary>Cambio automático de guía (trk.isAutoTrack).</summary>
        public bool IsAutoTrackOn { get; set; }

        /// <summary>Guiado por contorno activado (ct.isContourBtnOn).</summary>
        public bool IsContourOn { get; set; }

        /// <summary>Contorno bloqueado a la pasada actual (ct.isLocked).</summary>
        public bool IsContourLocked { get; set; }

        /// <summary>Índice de la guía activa (trk.idx). -1 = sin guía.</summary>
        public int TrackIdx { get; set; } = -1;

        /// <summary>Cantidad de guías visibles (para mostrar los botones de ciclado).</summary>
        public int TracksVisible { get; set; }

        /// <summary>Cantidad total de guías del lote (trk.gArr.Count).</summary>
        public int TracksTotal { get; set; }

        /// <summary>Hay lindero cargado (bnd.bndList.Count > 0) — habilita U-turn.</summary>
        public bool HasBoundary { get; set; }

        // ---- Barra abajo HTML (espejo del panelBottom nativo) -----------------

        /// <summary>Color de la próxima bandera (flagColor): 0=roja, 1=verde, 2=amarilla.</summary>
        public int FlagColor { get; set; }

        /// <summary>Feature de nudge/ajuste de guía habilitada (isNudgeOn).</summary>
        public bool IsNudgeOn { get; set; }

        /// <summary>El lote tiene cabecera construida (bndList[0].hdLine.Count > 0).</summary>
        public bool HasHeadland { get; set; }

        /// <summary>Cabecera activada (bnd.isHeadlandOn).</summary>
        public bool IsHeadlandOn { get; set; }

        /// <summary>La cabecera controla las secciones (bnd.isSectionControlledByHeadland).</summary>
        public bool IsSectionControlledByHeadland { get; set; }

        /// <summary>Hidráulico habilitado por config ((setArdMac_setting0 &amp; 2) == 2).</summary>
        public bool HasHydLift { get; set; }

        /// <summary>Levante hidráulico activado (vehicle.isHydLiftOn).</summary>
        public bool IsHydLiftOn { get; set; }

        /// <summary>Hay tramlines en el lote (tramList + tramBndOuterArr > 0).</summary>
        public bool HasTram { get; set; }

        /// <summary>Modo de vista de tramlines (tram.displayMode): 0=off, 1=todo, 2=líneas, 3=lindero.</summary>
        public int TramDisplayMode { get; set; }

        /// <summary>Modo de salteo del U-turn (yt.skipMode): 0=normal, 1=alterno, 2=saltea trabajadas.</summary>
        public int YouSkipMode { get; set; }

        /// <summary>Cantidad de pasadas que saltea el U-turn (yt.rowSkipsWidth, 1..10).</summary>
        public int RowSkipsWidth { get; set; } = 1;

        public double PivotEasting { get; set; }
        public double PivotNorthing { get; set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }

        public int NumSections { get; set; }
        // sectionOnRequest por índice [0..NumSections-1].
        public bool[] SectionOnRequest { get; set; }

        /// <summary>Estado del BOTÓN de cada sección: 0=Off, 1=Auto, 2=On (mismo
        /// orden que btnStates). Distinto de SectionOnRequest, que es si la
        /// sección aplica AHORA: acá va lo que eligió el operario. La botonera
        /// necesita los 3 estados para pintar rojo/verde/ámbar como el nativo —
        /// con SectionOnRequest sola, Auto y On se ven idénticos.</summary>
        public int[] SectionStates { get; set; }

        /// <summary>True si el implemento está configurado por secciones
        /// individuales (≤16, cada una con su ancho); false si está por ZONAS
        /// (≤8 grupos de secciones iguales). Determina qué botonera mostrar y qué
        /// comando mandar: <c>seccion_&lt;n&gt;</c> vs <c>zona_&lt;n&gt;</c>.</summary>
        public bool IsSectionsNotZones { get; set; }

        /// <summary>Corte de cada zona (zoneRanges 1..8): hasta qué número de
        /// sección llega. 0 = zona inexistente. Vacío en modo secciones. La zona 1
        /// va de la sección 1 a ZoneRanges[0]; la zona N, de ZoneRanges[N-2]+1 a
        /// ZoneRanges[N-1].</summary>
        public int[] ZoneRanges { get; set; }

        /// <summary>Ancho de la herramienta en metros (tool.width).</summary>
        public double ToolWidth { get; set; }

        /// <summary>Distancia entre ejes (m). La necesita el mapa para dibujar el
        /// vehículo a escala real y ubicar el eje delantero.</summary>
        public double Wheelbase { get; set; }

        /// <summary>Trocha (m): ancho entre ruedas. Junto con Wheelbase define el
        /// tamaño del sprite del vehículo, igual que el renderer nativo.</summary>
        public double TrackWidth { get; set; }

        /// <summary>Ángulo real del sensor de dirección (grados, + = derecha).
        /// Con esto el mapa gira las ruedas delanteras: el arte del tractor NO
        /// las trae dibujadas justamente porque se dibujan aparte y giran.</summary>
        public double SteerAngleDeg { get; set; }

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

        /// <summary>
        /// Lindero que se está grabando MANEJANDO, punto por punto, todavía sin
        /// cerrar ni guardar. null cuando no hay grabación en curso.
        ///
        /// Va aparte de <see cref="Boundaries"/> a propósito: eso son linderos
        /// confirmados y el mapa los cierra (LineLoop). Este es una tira abierta
        /// que crece — mostrarlo cerrado dibujaría un lado que el operario
        /// todavía no recorrió, y sobre eso se decide dónde cortar.
        /// </summary>
        public List<FieldPoint> BoundaryBeingMade { get; set; }

        /// <summary>Track de guía actualmente activo (AB line / curve / pivot). null si no hay.</summary>
        public TrackInfo ActiveTrack { get; set; }

        // ---- Por qué el giro en cabecera no arranca ------------------------
        //
        // El U-turn se arma solo si el pivote está DENTRO del área de giro y el
        // error de track es chico. Cuando no gira, sin estos campos no hay forma
        // de saber si es porque el tractor está fuera del lote, porque el lote
        // tiene la geometría rota o porque va muy desviado — desde la UI se veía
        // igual que "no anda". Son diagnóstico, no control.

        /// <summary>El pivote quedó fuera del lindero (o fuera del área de giro
        /// con el U-turn activo). Con esto en true el giro NO se va a armar.</summary>
        public bool IsOutOfBounds { get; set; }

        /// <summary>Desvío respecto de la guía, en metros. Por encima de ~1 km
        /// el updater descarta el giro en curso y vuelve a empezar.</summary>
        public double CrossTrackErrorM { get; set; }

        /// <summary>Fase interna del U-turn (10 = camino ya construido).</summary>
        public int YouTurnPhase { get; set; }

        /// <summary>Hay un giro disparado ahora mismo. El botón de giro manual
        /// pasa a "cancelar" con esto en true.</summary>
        public bool IsYouTurnTriggered { get; set; }

        /// <summary>Cuántas guías saltea el giro. 1 = va a la de al lado.</summary>
        public int YouTurnSkipWidth { get; set; }

        /// <summary>Modo de salteo: "normal", "alternado" o "ignora_trabajadas".</summary>
        public string YouTurnSkipMode { get; set; }

        /// <summary>Área NETA del lote: exterior menos islas. Si sale negativa,
        /// hay linderos "internos" más grandes que el exterior y la geometría no
        /// cierra — el giro y el corte por lindero no pueden funcionar.</summary>
        public bool BoundaryGeometryOk { get; set; }

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
        /// <summary>
        /// Valor del campo de dosis (StyleField) de ESTE polígono, o null si la
        /// capa no tiene campo numérico. Sin esto el cliente solo recibía el
        /// color, y "tocar una zona para ver qué dosis trae" obligaba a
        /// deshacer el gradiente — con el valor viaja el dato, no la pintura.
        /// </summary>
        public double? V { get; set; }

        /// <summary>
        /// Índice del feature en el ARCHIVO geojson de la prescripción (atributo
        /// FI), o null si la capa no viene de una prescripción. Es la identidad
        /// que la edición de dosis por toque manda al server: el índice de
        /// DIBUJO no sirve porque los MultiPolygon se parten en piezas y corren
        /// la numeración (medido: 121 features → 217 polígonos dibujados).
        /// </summary>
        public int? Fi { get; set; }
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
        /// <summary>
        /// Revisión de la GEOMETRÍA proyectada. El SourceToken es el archivo, y
        /// no alcanza: al abrir otro lote la misma capa se reproyecta a otro
        /// origen y las coordenadas cambian enteras. Sin esto el server
        /// contestaba "sin cambios" y el mapa seguía dibujando las zonas
        /// corridas la diferencia entre los dos orígenes.
        /// </summary>
        public string GeomRev { get; set; }
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
