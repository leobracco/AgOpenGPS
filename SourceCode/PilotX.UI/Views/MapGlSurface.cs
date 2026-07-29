// MapGlSurface.cs
//
// Stage 1 de la migracion OpenGL del mapa de guiado. Reemplaza el render
// Skia 2D de MapSkiaSurface por un pipeline GL real basado en Avalonia
// OpenGlControlBase + bindings Silk.NET.OpenGL.
//
// Capas renderiadas en Stage 1:
//   1. clear color (fondo cabina, #0E1410)
//   2. world grid en coordenadas reales (NO una grilla decorativa
//      hardcoded como tenia el Skia placeholder). El grid se escala
//      con el zoom calculado del bbox del lote.
//   3. boundary del lote (ring 0) en verde + islands (rings 1..n) en
//      gris atenuado.
//   4. sprite del tractor: triangulo orientado segun heading.
//
// Capas que NO entran en Stage 1 (van en stages 2..6):
//   - sections (worked area triangulado)
//   - guidance lines (AB / curves / contour)
//   - tool + sections del implemento + tram lines
//   - YouTurn paths + recorded paths
//   - camera control (zoom/pan/rotate mouse)
//
// El surface consume el mismo HudSnapshot que MapSkiaSurface — no hay
// nueva API HTTP en Stage 1. El cambio de zoom/pan se hace fit-to-bbox
// con padding fijo, identico al Skia (para poder validar paridad en
// cabina lado a lado con --gl=on/off).
//
// MVP simple: ortho (no perspective), zoom = fit-to-bbox, pan = bbox
// centrado. Las stages siguientes introducen un camera matrix con
// rotate/translate/zoom controlado por mouse.

using System;
using System.Collections.Generic;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Silk.NET.OpenGL;

namespace PilotX.Desktop.Views;

/// <summary>
/// Surface GL del mapa (Stage 1). Hosteada por <see cref="MapPanel"/>
/// cuando <c>App.UseGl</c> es true.
/// </summary>
public sealed class MapGlSurface : OpenGlControlBase
{
    // ---- estado del snapshot (UI thread) -------------------------------
    private HudSnapshot? _snap;
    private double _minE, _maxE, _minN, _maxN;
    private bool _hasBbox;
    private double _cacheKeyE, _cacheKeyN;
    private int _cacheKeyCount;

    // ---- cámara controlada por usuario (Stage 6) -----------------------
    // Zoom y pan aplicados ENCIMA del encuadre base (fit-to-bbox o tractor).
    // Doble-click resetea al encuadre automático.
    private double _userZoom = 1.0;          // multiplicador de escala (rueda)
    private double _userPanX, _userPanY;      // offset en metros mundo (arrastre)
    private double _lastEffectiveScale = 1.0; // px físicos por metro (para convertir el pan)

    // ---- estado GL (creado en OnOpenGlInit, render thread) -------------
    private GL? _gl;
    private bool _initFailed;
    private uint _program;
    private int _uMvp;
    private int _uColor;
    private uint _vao;
    private uint _vbo;
    private int _vboCapacityFloats;

    // ---- sprite del vehículo (reemplaza al triángulo) -------------------
    // El triángulo es el fallback: si no hay sprite elegido, no se pudo bajar
    // o falla la subida a GPU, se sigue dibujando el triángulo. Nunca se queda
    // el mapa sin marcador de posición.
    private uint _texProgram;
    private int _uTexMvp;
    private uint _texVbo;
    private uint _vehicleTex;
    private bool _vehicleTexReady;
    private double _vehicleAspect = 1.0;   // alto/ancho de la imagen
    // Pixeles pendientes de subir a GPU (se cargan desde el hilo de UI y se
    // suben en el próximo frame, que es donde hay contexto GL válido).
    private readonly float[] _mvpCache = new float[16];
    private byte[]? _pendingTexRgba;
    private int _pendingTexW, _pendingTexH;

    private uint _wheelTex;
    private bool _wheelTexReady;
    private byte[]? _pendingWheelRgba;
    private int _pendingWheelW, _pendingWheelH;

    private uint _implementoTex;
    private bool _implementoTexReady;
    private double _implementoAspect = 1.0;
    private byte[]? _pendingImplRgba;
    private int _pendingImplW, _pendingImplH;

    // Buffer CPU reutilizable. Se vuelca a _vbo en cada render que vea
    // un snapshot nuevo. No se aloca por frame.
    private float[] _scratch = new float[1024];

    // ---- coverage (worked area, Stage 2) -------------------------------
    // El servidor entrega N sections × M strips cada una. Para no pagar
    // un draw call por triangulo, concatenamos TODOS los strips en un
    // unico VBO y guardamos (offsetVerts, vertexCount) por strip; en
    // render emitimos un GL_TRIANGLE_STRIP por entrada.
    private uint _coverageVbo;
    private int _coverageVboCapacityFloats;
    // Lista (start, count) en vertices, donde start es el indice del
    // primer vertice del strip dentro del VBO (no en floats).
    private readonly List<(int Start, int Count)> _coverageRanges = new();
    private float _coverageR = 0.294f, _coverageG = 0.651f, _coverageB = 0.247f, _coverageA = 0.549f;
    private long _coverageRevisionUploaded = -1;
    private CoverageSnapshot? _pendingCoverage; // recibido desde UI thread, aplicado en render thread

    // ---- tram lines (Stage 4b) -----------------------------------------
    // Tramlines (wheel tracks) + outer/inner boundary tracks. Cambia rara
    // vez (regenerar passes/ancho/displayMode) — VBO STATIC_DRAW con
    // revision-cache. Layout en _tramVbo (todo concatenado):
    //   [lineas internas...] [outer boundary] [inner boundary]
    // Cada line tiene su (Start, Count) en vertices; outer/inner igual.
    // DisplayMode controla que se rendera:
    //   None             -> nada
    //   All              -> lines + outer + inner
    //   FillTracks       -> solo lines
    //   BoundaryTracks   -> solo outer + inner
    private uint _tramVbo;
    private int _tramVboCapacityFloats;
    private readonly List<(int Start, int Count)> _tramLineRanges = new();
    private int _tramOuterStart, _tramOuterCount;
    private int _tramInnerStart, _tramInnerCount;
    private long _tramRevisionUploaded = -1;
    private string _tramDisplayMode = "None";
    private TramGeometrySnapshot? _pendingTram;

    // ---- paths: youturn + recorded (Stage 5) ---------------------------
    // Dos polilineas simples ("caminos"): el giro de cabecera (youturn) y el
    // camino grabado (recorded). Cambian rara vez (nuevo giro / grabacion) —
    // VBO STATIC_DRAW con revision-cache. Layout en _pathsVbo (concatenado):
    //   [youturn...] [recorded...]
    // Cada uno con su (Start, Count) en vertices. Se rendean como
    // GL_LINE_STRIP con colores distintos.
    private uint _pathsVbo;
    private int _pathsVboCapacityFloats;
    private int _pathsYouTurnStart, _pathsYouTurnCount;
    private int _pathsRecordedStart, _pathsRecordedCount;
    private long _pathsRevisionUploaded = -1;
    private PathsGeometrySnapshot? _pendingPaths;

    // ---- tool / sections (Stage 4a) ------------------------------------
    // Cada seccion = un segmento Left↔Right en coords mundo. Coloreamos
    // segun estado: gris (off), verde (auto + mapping), rojo (auto + NO
    // mapping = sectionOnRequest negado por boundary/headland), amarillo
    // (manual ON). El tractor + boundary se renderean encima.
    // Sin revision-cache porque los puntos cambian cada frame; el batch
    // es chico (max ~16 secciones × 2 puntos × 2 floats = 64 floats).
    // (Sin VBO propio: las secciones se arman como quads en el scratch por
    // frame. Son ~16 y el batch es de 8 floats cada una.)
    private ToolGeometrySnapshot? _pendingTool;
    private ToolGeometrySnapshot? _toolSnap;

    // ---- guidance line (Stage 3) ---------------------------------------
    // Polyline de la linea/curva activa (AB/Curve/Contour). Se renderiza
    // como GL_LINE_STRIP con un unico draw call (el set es chico: 2 pts
    // para AB extendida, ~unos cientos para curves). VBO dedicado con
    // revision tracking — el GuidanceGeometryPoller corre a 1 Hz pero la
    // geometria casi nunca cambia, asi que en steady-state esto es solo
    // un DrawArrays por frame.
    private uint _guidanceVbo;
    private int _guidanceVboCapacityFloats;
    private int _guidanceVertexCount;
    private long _guidanceRevisionUploaded = -1;
    private string _guidanceMode = "Off";
    private GuidanceGeometrySnapshot? _pendingGuidance; // UI thread → render thread
    // Copia CPU de los puntos de la línea activa: la necesitamos para calcular
    // las guías paralelas vecinas (offset perpendicular por ancho de herramienta),
    // como hace AgOpenGPS al llenar el lote de líneas de pasada.
    private readonly List<GuidancePoint> _guidancePts = new List<GuidancePoint>();

    // ---- guías paralelas: VBO cacheado ---------------------------------
    // Las paralelas son función de (puntos de la línea, ancho de herramienta,
    // extensión del lote). Nada de eso cambia por frame: la geometría llega a
    // 1 Hz y el ancho solo al reconfigurar el implemento. Antes se recalculaban
    // enteras en CPU y se resubían a GPU en CADA frame — en modo curva son hasta
    // 81 offsets × N puntos con una raíz por punto y 81 BufferSubData, 60 veces
    // por segundo. Ahora se construyen una vez en este VBO y el frame solo emite
    // los draws. _parDirty se marca cuando cambia alguna de las tres entradas.
    private uint _parVbo;
    private int _parVboCapacityFloats;
    private readonly List<(int Start, int Count)> _parRanges = new();
    private bool _parIsLines;       // AB -> un unico GL_LINES; curva -> N LINE_STRIP
    private bool _parDirty = true;
    private double _parWidthUsed = -1;
    private double _parSpanUsed = -1;

    // Modo seguimiento heading-up: mapa centrado en el tractor y rotado por el
    // rumbo (tractor siempre apuntando arriba). Default on (lo que pidió el
    // operario). North-up = false.
    private bool _headingUp = true;

    // Cross-track error (m) para el lightbar. NaN = sin guía activa (no dibuja).
    private double _xte = double.NaN;

    // Creación de AB en el mapa (toco A, manejo, toco B): mientras _abCreating,
    // se dibuja el marcador del punto A y una línea pendiente A→tractor, para
    // que el operario vea la guía formándose de A hasta B.
    private bool _abCreating;
    private bool _abHasA;
    private double _abAe, _abAn;

    // Colores cockpit (RGBA 0..1). Identicos al Skia para paridad.
    private static readonly float[] ColBg            = { 0f, 0f, 0f, 1f };            // negro puro
    // Grilla apenas perceptible (gris muy oscuro, alpha bajo) para no confundirse
    // con las guías cian; el fondo queda esencialmente negro.
    private static readonly float[] ColGrid          = { 0.11f, 0.12f, 0.11f, 0.09f };
    private static readonly float[] ColBoundary      = { 0.357f, 0.784f, 0.314f, 1f }; // #5BC850
    private static readonly float[] ColIslandStroke  = { 0.561f, 0.627f, 0.573f, 1f }; // #8FA092
    private static readonly float[] ColTractor       = { 0.290f, 0.729f, 0.243f, 1f }; // #4ABA3E
    private static readonly float[] ColTractorEdge   = { 0.063f, 0.086f, 0.071f, 1f }; // #101612
    // Guidance line: cian brillante para contrastar con boundary (#5BC850
    // verde) y coverage (verde semitransp). #4DD8FF — visible sobre fondo
    // oscuro y sobre la capa pintada.
    private static readonly float[] ColGuidance      = { 0.302f, 0.847f, 1.000f, 1f }; // #4DD8FF
    // Guías paralelas vecinas: mismo cian pero tenue (alpha bajo) para que la
    // línea activa resalte por encima. Se dibujan por debajo de la activa.
    private static readonly float[] ColGuidancePar   = { 0.302f, 0.847f, 1.000f, 0.33f };
    // Tool / sections (Stage 4a). Las secciones se pintan como segmentos
    // gruesos (LineWidth lo controla el driver — algunos drivers lo
    // capean en 1.0, pero la barra es solo indicativa, no cubre area).
    // Sección cortada (apagada por el operario). Antes era #8FA092, un verde
    // grisáceo desaturado: sobre el fondo oscuro y al lado del verde de
    // "aplicando" no se leía como apagada — el operario lo describía como
    // "verde/marrón clarito". Cortada tiene que gritar, y el color de cortar es
    // el rojo.
    private static readonly float[] ColToolOff       = { 0.929f, 0.282f, 0.282f, 1f }; // #ED4848 rojo (cortada)
    private static readonly float[] ColToolAutoOn    = { 0.290f, 0.729f, 0.243f, 1f }; // #4ABA3E verde (auto + mapping)
    // Auto pero denegada (lindero/cabecera). Pasa a naranja porque el rojo se lo
    // quedó "cortada por el operario": si las dos fueran rojas, el operario no
    // podría distinguir lo que apagó él de lo que le apagó el sistema, que es
    // justo lo que necesita saber cuando algo no siembra y no entiende por qué.
    private static readonly float[] ColToolAutoOff   = { 0.910f, 0.447f, 0.110f, 1f }; // #E8721C naranja (auto + NO mapping)
    private static readonly float[] ColToolManual    = { 0.973f, 0.808f, 0.247f, 1f }; // #F8CE3F amarillo (manual on)
    // Tram lines (Stage 4b). Tono claro semitransparente para no competir
    // con guidance cian ni con boundary verde. Coincide con el render
    // legacy (light pink/cream sobre fondo oscuro).
    private static readonly float[] ColTram          = { 0.930f, 0.720f, 0.735f, 0.65f }; // #EDB8BC alpha 0.65
    // Paths (Stage 5). Dos colores fuertes y distinguibles entre si y del
    // resto de las capas: youturn naranja (#FF9E1B), recorded violeta/lila
    // (#B478FF).
    private static readonly float[] ColPathsYouTurn  = { 1.000f, 0.620f, 0.106f, 1f }; // #FF9E1B naranja
    private static readonly float[] ColPathsRecorded = { 0.706f, 0.470f, 1.000f, 1f }; // #B478FF violeta

    // Shaders: el MISMO cuerpo sirve para desktop GL 3.30 core y GL ES 3.00;
    // solo cambia el preludio (#version + precision). Avalonia en Windows
    // suele negociar un contexto GL ES (ANGLE), donde "#version 330 core"
    // NO compila -> el programa quedaba inválido y el mapa se veía NEGRO.
    // Elegimos el preludio según GlVersion.Type en OnOpenGlInit.
    private static string BuildVertSrc(bool es) =>
        (es ? "#version 300 es\n" : "#version 330 core\n") +
        "layout (location = 0) in vec2 aPos;\n" +
        "uniform mat4 uMvp;\n" +
        "void main(){ gl_Position = uMvp * vec4(aPos, 0.0, 1.0); }\n";

    private static string BuildFragSrc(bool es) =>
        (es ? "#version 300 es\nprecision mediump float;\n" : "#version 330 core\n") +
        "uniform vec4 uColor;\n" +
        "out vec4 FragColor;\n" +
        "void main(){ FragColor = uColor; }\n";

    // Shader con textura, para el sprite del vehículo. Va aparte del de color
    // plano: mezclarlos obligaría a un branch por fragmento en TODO lo que se
    // dibuja (cobertura, guías, lindero), que es la parte cara del frame.
    private static string BuildTexVertSrc(bool es) =>
        (es ? "#version 300 es\n" : "#version 330 core\n") +
        "layout (location = 0) in vec2 aPos;\n" +
        "layout (location = 1) in vec2 aUv;\n" +
        "uniform mat4 uMvp;\n" +
        "out vec2 vUv;\n" +
        "void main(){ vUv = aUv; gl_Position = uMvp * vec4(aPos, 0.0, 1.0); }\n";

    private static string BuildTexFragSrc(bool es) =>
        (es ? "#version 300 es\nprecision mediump float;\n" : "#version 330 core\n") +
        "in vec2 vUv;\n" +
        "uniform sampler2D uTex;\n" +
        "out vec4 FragColor;\n" +
        "void main(){ vec4 c = texture(uTex, vUv); if (c.a < 0.02) discard; FragColor = c; }\n";

    /// <summary>
    /// Carga el sprite del vehículo (RGBA sin premultiplicar). Se llama desde
    /// el hilo de UI; la subida real a GPU ocurre en el próximo frame, que es
    /// donde hay contexto GL. Pasar null vuelve al triángulo.
    /// </summary>
    public void SetVehicleSprite(byte[]? rgba, int width, int height)
    {
        if (rgba == null || width <= 0 || height <= 0)
        {
            _pendingTexRgba = null;
            _vehicleTexReady = false;
        }
        else
        {
            _pendingTexRgba = rgba;
            _pendingTexW = width;
            _pendingTexH = height;
            _vehicleAspect = (double)height / width;
        }
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    /// <summary>
    /// Textura de la rueda delantera. Se dibuja DOS veces (una por lado), cada
    /// una girada por su ángulo de Ackermann: por eso el arte del tractor no
    /// trae ruedas delanteras dibujadas.
    /// </summary>
    /// <summary>
    /// Sprite del implemento (sembradora, pulverizadora…). Se dibuja en la
    /// posición y rumbo de la HERRAMIENTA —que no es la del tractor: el
    /// implemento va rezagado y en curva apunta distinto— y a lo ancho real del
    /// implemento configurado.
    /// </summary>
    public void SetImplementSprite(byte[]? rgba, int width, int height)
    {
        if (rgba == null || width <= 0 || height <= 0)
        {
            _pendingImplRgba = null;
            _implementoTexReady = false;
        }
        else
        {
            _pendingImplRgba = rgba;
            _pendingImplW = width;
            _pendingImplH = height;
            _implementoAspect = (double)height / width;
        }
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    public void SetWheelSprite(byte[]? rgba, int width, int height)
    {
        if (rgba == null || width <= 0 || height <= 0)
        {
            _pendingWheelRgba = null;
            _wheelTexReady = false;
        }
        else
        {
            _pendingWheelRgba = rgba;
            _pendingWheelW = width;
            _pendingWheelH = height;
        }
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    /// <summary>
    /// Push de snapshot desde UI thread (HudPoller). Marca dirty y pide
    /// un frame nuevo; el render real ocurre en thread GL.
    /// </summary>
    public void OnSnapshot(HudSnapshot snap)
    {
        _snap = snap;
        _desdeFix.Restart();          // reloj para interpolar entre fixes
        InvalidateBboxIfChanged(snap);
        AjustarTickSuavizado(snap);
        // Con el mapa tapado por una pantalla, el HUD sigue llegando (lo consume
        // la barra de arriba) pero no hay nada que redibujar acá.
        if (_pausado) return;
        // RequestNextFrameRendering: API de OpenGlControlBase para
        // forzar un repaint sin tick continuo. No quemamos GPU en
        // idle: render solo cuando hay snapshot nuevo.
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    // ---- suavizado del movimiento --------------------------------------
    // El GPS entrega ~10 fixes/s. Si el mapa dibuja SOLO cuando llega un fix,
    // se ve a 10 fps: entrecortado, sobre todo en modo heading-up donde gira
    // el mundo entero. Entre fix y fix se avanza el tractor por estima
    // (velocidad × tiempo sobre el rumbo actual) y se pide frame a ~60 Hz.
    //
    // El tick SOLO corre con el tractor en movimiento: parado no hay nada que
    // interpolar y no tiene sentido quemar GPU en la cabina.
    private readonly System.Diagnostics.Stopwatch _desdeFix = System.Diagnostics.Stopwatch.StartNew();
    private DispatcherTimer? _tickSuave;

    /// <summary>Tope de extrapolación: más allá de esto se prefiere quedarse
    /// quieto antes que inventar posición. Si el GPS se corta, el tractor se
    /// frena en pantalla en vez de seguir viajando solo.</summary>
    private const double MaxExtrapolacionSeg = 0.30;

    // Pausa del render. Cuando una pantalla (Configuración, Hub, cualquier
    // producto X-*) tapa el mapa, seguir pidiendo frames a 30 Hz de un control
    // invisible es trabajo tirado — y es justo el momento en que WebView2 está
    // levantando su árbol de procesos Chromium y necesita la CPU.
    private bool _pausado;

    /// <summary>Frena el tick de suavizado. Idempotente.</summary>
    public void Pausar()
    {
        _pausado = true;
        if (_tickSuave != null && _tickSuave.IsEnabled) _tickSuave.Stop();
    }

    /// <summary>Reanuda el render. El tick vuelve solo con el próximo snapshot
    /// si el tractor está en movimiento.</summary>
    public void Reanudar()
    {
        if (!_pausado) return;
        _pausado = false;
        // Redibujar YA: al volver del menú el mapa tiene que estar al día, no
        // esperar al próximo fix.
        _renderPosInit = false;   // sin esto el filtro "viaja" desde la posición vieja
        RequestNextFrameRendering();
    }

    private void AjustarTickSuavizado(HudSnapshot snap)
    {
        if (_pausado)
        {
            if (_tickSuave != null && _tickSuave.IsEnabled) _tickSuave.Stop();
            return;
        }
        bool enMovimiento = snap != null && snap.AvgSpeed > 0.2;
        if (enMovimiento)
        {
            if (_tickSuave == null)
            {
                // 30 fps, NO 60. A velocidad de trabajo el tractor avanza ~6 cm
                // por frame a 30 fps: pedir 60 no aporta nada visible y duplica
                // el trabajo de render, que en la Intel UHD de la cabina es
                // headroom que se necesita para otra cosa.
                //
                // 30 y no 25: la pantalla es de 60 Hz. 60/30 = 2 exacto, así que
                // cada frame vive exactamente 2 refrescos y la cadencia queda
                // pareja. A 25 fps la división da 2,4 y los frames alternan
                // 2-2-3-2-2-3 refrescos — ese patrón irregular se ve peor que la
                // diferencia de 5 fps. Lo que molesta al ojo es la varianza, no
                // el promedio.
                //
                // El suavizado de posición (TauSuavizadoSeg) es independiente del
                // framerate, así que bajar la cadencia no lo afecta.
                _tickSuave = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(33)   // ~30 fps
                };
                _tickSuave.Tick += (_, _) => RequestNextFrameRendering();
            }
            if (!_tickSuave.IsEnabled) _tickSuave.Start();
        }
        else if (_tickSuave != null && _tickSuave.IsEnabled)
        {
            _tickSuave.Stop();
        }
    }

    /// <summary>
    /// Posición OBJETIVO del tractor: el último fix llevado al instante actual por
    /// estima. Devuelve el fix tal cual si está parado o si pasó demasiado desde el
    /// último dato.
    /// </summary>
    private (double e, double n) PosicionObjetivo(HudSnapshot snap)
    {
        double e = snap.PivotEasting, n = snap.PivotNorthing;
        double vms = snap.AvgSpeed / 3.6;                 // km/h → m/s
        if (vms <= 0.05) return (e, n);

        double dt = _desdeFix.Elapsed.TotalSeconds;
        if (dt <= 0 || dt > MaxExtrapolacionSeg) return (e, n);

        // Mismo criterio de ejes que el resto del mapa: rumbo 0 = Norte.
        double d = vms * dt;
        return (e + Math.Sin(snap.Heading) * d, n + Math.Cos(snap.Heading) * d);
    }

    // ---- posición de render suavizada ----------------------------------
    // La estima sola producía el efecto "goma": entre fixes el tractor avanzaba
    // extrapolado, y al llegar el fix siguiente la posición SALTABA al valor real
    // (adelante o atrás según cuánto se pasó la estima). Con un fix cada ~100 ms
    // eso es un escalón 10 veces por segundo, y en heading-up el escalón lo da el
    // mundo entero, que es donde más se nota.
    //
    // Ahora la posición dibujada persigue al objetivo con un filtro exponencial:
    // el escalón se reparte en unas decenas de ms en vez de aplicarse de golpe.
    private double _renderE, _renderN;
    private double _renderToolE, _renderToolN;
    private bool _renderPosInit;
    private readonly System.Diagnostics.Stopwatch _relojFrame = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Constante de tiempo del suavizado. Corta a propósito: alcanza para
    /// tapar el escalón del fix sin que el tractor se sienta "arrastrado".</summary>
    private const double TauSuavizadoSeg = 0.08;

    /// <summary>Salto que NO se interpola (cambio de lote, GPS recuperado,
    /// teleport del simulador): ahí conviene ir directo y no viajar por el mapa.</summary>
    private const double SaltoDirectoM = 25.0;

    /// <summary>
    /// Avanza la posición suavizada hacia el objetivo. Se llama UNA vez por frame
    /// (la cámara y el tractor leen el mismo resultado; llamarla dos veces haría
    /// avanzar el filtro doble y el suavizado quedaría dependiente del framerate).
    /// </summary>
    private void ActualizarPosicionRender(HudSnapshot snap)
    {
        var (te, tn) = PosicionObjetivo(snap);

        // Herramienta: mismo tratamiento, con su propia posición y rumbo. Si el
        // tractor va suave y el implemento a saltos, se ve como si se
        // desenganchara y volviera.
        double toE = snap.ToolEasting, toN = snap.ToolNorthing;
        double vms = snap.AvgSpeed / 3.6;
        double dtFix = _desdeFix.Elapsed.TotalSeconds;
        if (vms > 0.05 && dtFix > 0 && dtFix <= MaxExtrapolacionSeg)
        {
            double d = vms * dtFix;
            toE += Math.Sin(snap.ToolHeading) * d;
            toN += Math.Cos(snap.ToolHeading) * d;
        }

        double dtFrame = _relojFrame.Elapsed.TotalSeconds;
        _relojFrame.Restart();

        if (!_renderPosInit)
        {
            _renderE = te; _renderN = tn;
            _renderToolE = toE; _renderToolN = toN;
            _renderPosInit = true;
            return;
        }

        double dx = te - _renderE, dy = tn - _renderN;
        if (dx * dx + dy * dy > SaltoDirectoM * SaltoDirectoM)
        {
            _renderE = te; _renderN = tn;
            _renderToolE = toE; _renderToolN = toN;
            return;
        }

        // Exponencial independiente del framerate: con dt chico el paso es chico,
        // con dt grande el paso es grande, y el resultado no cambia si el mapa
        // corre a 30 o a 60 fps.
        double a = 1.0 - Math.Exp(-Math.Max(dtFrame, 0.0) / TauSuavizadoSeg);
        _renderE += dx * a;
        _renderN += dy * a;
        _renderToolE += (toE - _renderToolE) * a;
        _renderToolN += (toN - _renderToolN) * a;
    }

    /// <summary>
    /// Push de coverage snapshot desde UI thread (CoveragePoller). Solo
    /// se aplica si la revision cambio respecto a la ya cargada en VBO.
    /// </summary>
    public void OnCoverage(CoverageSnapshot snap)
    {
        if (snap == null) return;
        if (snap.Revision == _coverageRevisionUploaded) return;
        _pendingCoverage = snap;
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    /// <summary>
    /// Push de geometria del implemento (Stage 4a, ToolGeometryPoller).
    /// Sin filtrado por revision — los puntos cambian cada frame que el
    /// tractor se mueve.
    /// </summary>
    public void OnTool(ToolGeometrySnapshot snap)
    {
        if (snap == null) return;
        _pendingTool = snap;
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    /// <summary>
    /// Push de geometria de tram desde UI thread (TramGeometryPoller).
    /// El poller ya filtra por revision; aca solo guardamos pendiente y
    /// disparamos un frame nuevo.
    /// </summary>
    public void OnTram(TramGeometrySnapshot snap)
    {
        if (snap == null) return;
        if (snap.Revision == _tramRevisionUploaded) return;
        _pendingTram = snap;
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    /// <summary>
    /// Push de geometria de caminos (Stage 5, PathsGeometryPoller): youturn
    /// (giro de cabecera) + recorded path. El poller ya filtra por revision;
    /// aca solo guardamos pendiente y disparamos un frame nuevo.
    /// </summary>
    public void OnPaths(PathsGeometrySnapshot snap)
    {
        if (snap == null) return;
        if (snap.Revision == _pathsRevisionUploaded) return;
        _pendingPaths = snap;
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    /// <summary>
    /// Push de geometria de guidance desde UI thread (GuidanceGeometryPoller).
    /// El poller ya filtra por revision; aca solo guardamos pendiente y
    /// disparamos un frame nuevo. Si el modo viene "Off" igual aplicamos
    /// el snapshot — limpia la linea anterior.
    /// </summary>
    public void OnGuidance(GuidanceGeometrySnapshot snap)
    {
        if (snap == null) return;
        // El XTE cambia en cada poll aunque la geometría (revision) no —
        // se guarda siempre para que el lightbar refleje el desvío en vivo.
        _xte = snap.XteMeters;
        if (snap.Revision == _guidanceRevisionUploaded) return;
        _pendingGuidance = snap;
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    private void InvalidateBboxIfChanged(HudSnapshot snap)
    {
        var b = (snap.Boundaries != null && snap.Boundaries.Count > 0) ? snap.Boundaries[0] : null;
        if (b == null || b.Count < 3) { _hasBbox = false; return; }
        var first = b[0];
        if (_hasBbox && _cacheKeyE == first.E && _cacheKeyN == first.N && _cacheKeyCount == b.Count)
            return;
        _minE = double.MaxValue; _maxE = double.MinValue;
        _minN = double.MaxValue; _maxN = double.MinValue;
        foreach (var p in b)
        {
            if (p.E < _minE) _minE = p.E;
            if (p.E > _maxE) _maxE = p.E;
            if (p.N < _minN) _minN = p.N;
            if (p.N > _maxN) _maxN = p.N;
        }
        _cacheKeyE = first.E; _cacheKeyN = first.N; _cacheKeyCount = b.Count;
        _hasBbox = true;
    }

    // ---- ciclo GL ------------------------------------------------------

    protected override void OnOpenGlInit(GlInterface glInterface)
    {
        // Silk.NET.OpenGL: bindings via los procAddress que expone
        // Avalonia. Le pasamos un delegado que se resuelve por
        // ImportedFunctionDelegate; el binding lazy-resuelve cada GL
        // call la primera vez que se usa.
        _gl = GL.GetApi(name => glInterface.GetProcAddress(name));

        // Contexto ES (ANGLE en Windows, GLES nativo en Android) vs desktop
        // GL -> preludio de shader distinto. Sin esto, en un contexto ES el
        // shader 330 core no compila y el mapa queda negro.
        bool es = GlVersion.Type == Avalonia.OpenGL.GlProfileType.OpenGLES;
        // Console.Error (no Debug.WriteLine): en Android sin listener de
        // Trace registrado, Debug.WriteLine no llega a ningún lado incluso
        // en builds Debug — Console SÍ se redirige a logcat.
        Console.Error.WriteLine("[MapGlSurface] GL context: "
            + (es ? "OpenGL ES" : "desktop GL") + " " + GlVersion.Major + "." + GlVersion.Minor);
        try
        {
            _program = CompileProgram(_gl, BuildVertSrc(es), BuildFragSrc(es));
        }
        catch (Exception ex)
        {
            // Sin esto: _gl ya quedó no-null (asignado arriba) pero _program
            // queda en 0 -> OnOpenGlRender seguía intentando dibujar con un
            // programa inválido, sin excepción visible, mapa negro para
            // siempre. _initFailed hace que OnOpenGlRender corte apenas esto
            // pase, y el error queda en logcat (adb logcat, tag ".NET"/stderr).
            Console.Error.WriteLine("[MapGlSurface] FALLÓ compilar/linkear shaders: " + ex);
            _initFailed = true;
            return;
        }
        _uMvp   = _gl.GetUniformLocation(_program, "uMvp");
        _uColor = _gl.GetUniformLocation(_program, "uColor");

        // Programa con textura para el sprite del vehículo. Si falla, NO se
        // aborta el init: el mapa sigue andando con el triángulo de siempre.
        try
        {
            _texProgram = CompileProgram(_gl, BuildTexVertSrc(es), BuildTexFragSrc(es));
            _uTexMvp = _gl.GetUniformLocation(_texProgram, "uMvp");
            _texVbo = _gl.GenBuffer();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MapGlSurface] sin sprite de vehículo (shader): " + ex.Message);
            _texProgram = 0;
        }

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
        _gl.EnableVertexAttribArray(0);

        // VBO separado para coverage. Cambia rara vez (~1 Hz max), por
        // eso STATIC_DRAW; reasignamos con BufferData cuando llega un
        // snapshot con Revision distinta. El layout de atributos del
        // VAO sirve para ambos VBOs porque hacemos BindBuffer antes de
        // cada draw.
        _coverageVbo = _gl.GenBuffer();
        _coverageVboCapacityFloats = 0;

        // VBO de guidance (Stage 3). Mismo patron que coverage: STATIC_DRAW
        // porque cambia rara vez (revision del poller); el VAO comparte el
        // layout de atributos via BindBuffer pre-draw.
        _guidanceVbo = _gl.GenBuffer();
        _guidanceVboCapacityFloats = 0;

        // VBO de guías paralelas. STATIC_DRAW: se reconstruye solo cuando
        // cambia la línea, el ancho de herramienta o la extensión del lote.
        _parVbo = _gl.GenBuffer();
        _parVboCapacityFloats = 0;
        _parDirty = true;

        // (Las secciones ya no tienen VBO propio: DrawTool arma los quads en el
        // scratch por frame, que es lo que permite darles grosor real.)

        // VBO de tram (Stage 4b). STATIC_DRAW: solo cambia al regenerar
        // tram (cambio passes/ancho/displayMode). Concatena lineas + outer
        // + inner; cada uno con su rango en vertices.
        _tramVbo = _gl.GenBuffer();
        _tramVboCapacityFloats = 0;

        // VBO de paths (Stage 5). STATIC_DRAW: solo cambia al generar un giro
        // o grabar/cargar un camino. Concatena youturn + recorded; cada uno
        // con su rango en vertices.
        _pathsVbo = _gl.GenBuffer();
        _pathsVboCapacityFloats = 0;

        var glErr = _gl.GetError();
        Console.Error.WriteLine("[MapGlSurface] GL init OK (glGetError=" + glErr + ")");

        Dispatcher.UIThread.Post(ArrancarLatido, DispatcherPriority.Background);
    }

    protected override void OnOpenGlDeinit(GlInterface glInterface)
    {
        if (_gl == null) return;
        try
        {
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteBuffer(_coverageVbo);
            _gl.DeleteBuffer(_guidanceVbo);
            _gl.DeleteBuffer(_parVbo);
            _gl.DeleteBuffer(_tramVbo);
            _gl.DeleteBuffer(_pathsVbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MapGlSurface] deinit: " + ex.Message);
        }
        _gl = null;
    }

    private bool _loggedFirstRender;

    protected override void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl == null || _initFailed) return;
        var sz = Bounds.Size;
        int wPx = (int)Math.Max(1, sz.Width);
        int hPx = (int)Math.Max(1, sz.Height);

        _gl.Viewport(0, 0, (uint)wPx, (uint)hPx);
        _gl.ClearColor(ColBg[0], ColBg[1], ColBg[2], ColBg[3]);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        _framesDesdeLatido++;
        bool diag = _diagFrames < 3;
        if (diag) LogPixel("post-clear", wPx / 2, hPx / 2);

        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        if (!_loggedFirstRender)
        {
            _loggedFirstRender = true;
            Console.Error.WriteLine("[MapGlSurface] primer render: bounds=" + wPx + "x" + hPx
                + " program=" + _program + " glGetError=" + _gl.GetError());
        }

        // MVP: ortho 2D mapeando el bbox del lote (o un default centrado)
        // al rect del control con padding. Y-up para que North apunte
        // arriba (a diferencia del DrawingContext Skia que es Y-down).
        ComputeProjection(wPx, hPx, out double cxBbox, out double cyBbox, out double scale);

        // Modo heading-up (seguimiento): el mapa se centra en el tractor y rota
        // por su rumbo, así el tractor SIEMPRE apunta hacia arriba de la pantalla
        // (aunque doble), como los monitores de guiado. El triángulo del tractor
        // se dibuja con su rumbo real y la rotación del mundo lo deja apuntando
        // arriba (no hay que tocar DrawTractor). North-up (rotación 0) queda si
        // se apaga el modo o si todavía no hay posición.
        double alpha = 0.0;
        var followSnap = _snap;
        // Una sola actualización del filtro por frame, antes de cualquier uso.
        if (followSnap != null) ActualizarPosicionRender(followSnap);
        if (_headingUp && followSnap != null &&
            (followSnap.PivotEasting != 0 || followSnap.PivotNorthing != 0))
        {
            // Posición suavizada, no la del último fix: si la cámara salta de
            // fix en fix, TODO el mundo salta con ella y es lo que más se nota.
            cxBbox = _renderE;
            cyBbox = _renderN;
            alpha = followSnap.Heading;   // rad; rotar el mundo por el rumbo
        }

        // Matriz column-major 4x4. La rotación se aplica en espacio-pantalla
        // uniforme (sx/sy sólo corrigen aspecto), así un círculo del mundo se ve
        // como círculo en pantalla, sólo rotado. Con alpha=0 queda igual que antes.
        //   clip.x = sx[cosα(x-cx) - sinα(y-cy)]
        //   clip.y = sy[sinα(x-cx) + cosα(y-cy)]
        float sx = (float)(scale * 2.0 / wPx);
        float sy = (float)(scale * 2.0 / hPx);
        float ca = (float)Math.Cos(alpha);
        float sa = (float)Math.Sin(alpha);
        float m00 = sx * ca,  m10 = sy * sa;      // columna 0 (x)
        float m01 = -sx * sa, m11 = sy * ca;      // columna 1 (y)
        float tx = (float)(-(sx * (ca * cxBbox - sa * cyBbox)));
        float ty = (float)(-(sy * (sa * cxBbox + ca * cyBbox)));
        Span<float> mvp = stackalloc float[16]
        {
            m00, m10, 0, 0,
            m01, m11, 0, 0,
            0,   0,   1, 0,
            tx,  ty,  0, 1
        };
        unsafe
        {
            fixed (float* p = mvp)
                _gl.UniformMatrix4(_uMvp, 1, false, p);
        }
        // Copia para el shader del sprite del vehículo, que usa su propio
        // programa y necesita la MISMA transformación.
        for (int i = 0; i < 16; i++) _mvpCache[i] = mvp[i];

        // --- Capa 1: world grid — DESACTIVADA (2026-07-28, pedido usuario) --
        // El cuadriculado de fondo no le decía nada al operario: no marca
        // referencias del lote ni distancias que se usen manejando, y se
        // redibujaba entero en CADA frame (a 60 fps con el tractor en
        // movimiento son cientos de líneas por segundo para nada).
        // El método DrawGrid queda para poder volver a colgarlo de un toggle
        // si algún día hace falta.
        //DrawGrid(cxBbox, cyBbox, wPx, hPx, scale, ColGrid);

        // --- Capa 2: coverage (worked area) ----------------------------
        // Si hay snapshot pendiente con revision nueva, reuploadeamos el
        // VBO de coverage. El draw siempre va, aunque sea con los rangos
        // ya cargados de la pasada anterior — eso mantiene la capa
        // visible entre polls.
        if (_pendingCoverage != null)
        {
            UploadCoverage(_pendingCoverage);
            _pendingCoverage = null;
        }
        if (_coverageRanges.Count > 0)
            DrawCoverage();

        // --- Capa 2b: tram lines (wheel tracks + outer/inner boundary) -
        // Va entre coverage y guidance: marcas de navegacion que deben
        // quedar visibles sobre lo pintado pero por debajo de la linea
        // activa de guidance (que es la referencia primaria del operario).
        if (_pendingTram != null)
        {
            UploadTram(_pendingTram);
            _pendingTram = null;
        }
        if (_tramDisplayMode != "None" &&
            (_tramLineRanges.Count > 0 || _tramOuterCount > 0 || _tramInnerCount > 0))
            DrawTram();

        // --- Capa 3a: guidance line (AB/Curve/Contour) -----------------
        // Va despues de coverage y antes del boundary: queda visible sobre
        // el area pintada pero por debajo del contorno del lote (que es
        // referencia geometrica primaria). El boundary mantiene su color
        // fuerte para no perderse contra la linea cian.
        if (_pendingGuidance != null)
        {
            UploadGuidance(_pendingGuidance);
            _pendingGuidance = null;
        }
        if (_guidanceVertexCount >= 2 && _guidanceMode != "Off")
        {
            DrawGuidanceParallel();  // vecinas tenues, por debajo
            DrawGuidance();          // línea activa brillante, por encima
        }

        // --- Capa 3b: paths (youturn + recorded) -----------------------
        // Va despues de guidance: el giro de cabecera y el camino grabado
        // son marcas de navegacion que deben quedar visibles sobre la linea
        // activa. Colores fuertes distinguibles (youturn naranja, recorded
        // violeta). Revision-cache igual que tram.
        if (_pendingPaths != null)
        {
            UploadPaths(_pendingPaths);
            _pendingPaths = null;
        }
        if (_pathsYouTurnCount > 0 || _pathsRecordedCount > 0)
            DrawPaths();

        // --- Capa 3b: tool / sections (Stage 4a) -----------------------
        // Va despues de guidance y antes del boundary: las secciones son
        // referencia del operario (donde aplica producto AHORA) y deben
        // quedar visibles sobre coverage/guidance. El boundary sigue por
        // arriba para que el contorno del lote no se pierda.
        if (_pendingTool != null)
        {
            _toolSnap = _pendingTool;
            _pendingTool = null;
            // Ya no se sube a un VBO propio: DrawTool arma los quads por frame
            // con el scratch. Son ~16 secciones, nada.
        }
        // La barra de secciones va SIEMPRE. Antes se ocultaba cuando había sprite
        // de implemento cargado (`!_implementoTexReady`); como el sprite quedó
        // desactivado (ver Capa 4), esa condición dejaría el mapa sin barra y sin
        // máquina.
        if (_toolSnap != null && _toolSnap.IsValid && _toolSnap.Sections != null && _toolSnap.Sections.Count > 0)
            DrawTool(_toolSnap, scale);

        var snap = _snap;
        if (snap != null)
        {
            // --- Capa 3: boundaries ------------------------------------
            if (snap.Boundaries != null)
            {
                for (int i = 0; i < snap.Boundaries.Count; i++)
                {
                    var ring = snap.Boundaries[i];
                    if (ring == null || ring.Count < 2) continue;
                    var col = (i == 0) ? ColBoundary : ColIslandStroke;
                    DrawRing(ring, col);
                }
            }

            // --- Capa 4: implemento y tractor ---------------------------
            // El implemento va PRIMERO: el tractor lo tapa parcialmente en la
            // zona del enganche, que es lo correcto visualmente.
            SubirSpritePendiente();
            // --- sprite del implemento — DESACTIVADO (2026-07-28, pedido usuario) ---
            // Dibujado al ancho real (4,16 m) quedaba feo: el tractor tiene piso de
            // 26 px (ver minPx en DrawTractorSprite) y el implemento no, así que al
            // alejar el zoom el tractor se plantaba en su tamaño mínimo mientras la
            // sembradora se seguía achicando, y los dos quedaban descalzados.
            // Vuelve la barra de secciones de colores, que además dice más (estado
            // por sección) que el dibujo de la máquina.
            // El método queda por si se lo quiere recolgar con el piso de píxeles
            // corregido.
            //DrawImplementoSprite();
            DrawTractor(_renderE, _renderN, snap.Heading, scale);
        }

        // --- Capa 4b: creación de AB (marcador A + línea pendiente A→tractor) ---
        // Usa la posición suavizada, la misma con la que se dibujó el tractor: con
        // el fix crudo la línea pendiente no terminaba donde se ve la máquina.
        if (_abCreating && _abHasA && snap != null)
            DrawAbCreation(_renderE, _renderN, scale);

        // --- Capa 5: lightbar (XTE) en espacio-pantalla, sobre todo ----
        DrawLightbar();

        if (diag)
        {
            LogPixel("post-draw", wPx / 2, hPx / 2);
            Console.Error.WriteLine("[MapGlSurface] diag frame " + _diagFrames + ": hasBbox=" + _hasBbox
                + " snap=" + (_snap != null) + " pivotE=" + (_snap?.PivotEasting ?? 0)
                + " pivotN=" + (_snap?.PivotNorthing ?? 0) + " headingUp=" + _headingUp
                + " guidanceVerts=" + _guidanceVertexCount);
            _diagFrames++;
        }

        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    private int _diagFrames;

    // ---- latido de diagnóstico -----------------------------------------
    // El mapa se congeló dos veces (2026-07-29) sin dejar rastro: el log de
    // arranque se corta a los 3 frames y despues no se sabe mas nada, asi que
    // cuando pasa no hay con que distinguir "dejo de renderizar" de "renderiza
    // pero con datos viejos" ni de "se trabo el hilo de UI".
    //
    // Esto emite una linea cada 5 s con lo minimo para separar esos casos. Si
    // las lineas DEJAN de salir, el que se trabo es el hilo de UI (el timer
    // corre ahi). Si salen con fps=0, se dejo de pedir frames. Si salen con
    // fps>0 pero el fix no envejece, el que murio es el poller del HUD.
    private int _framesDesdeLatido;
    private DispatcherTimer? _latido;
    private readonly System.Diagnostics.Stopwatch _relojLatido = System.Diagnostics.Stopwatch.StartNew();

    private void ArrancarLatido()
    {
        if (_latido != null) return;
        _latido = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _latido.Tick += (_, _) =>
        {
            double seg = _relojLatido.Elapsed.TotalSeconds;
            if (seg <= 0) return;
            double fps = _framesDesdeLatido / seg;
            _framesDesdeLatido = 0;
            _relojLatido.Restart();

            var s = _snap;
            Console.Error.WriteLine(string.Format(
                "[MapGlSurface] latido fps={0:F1} pausado={1} tick={2} edadFix={3:F1}s vel={4:F1}",
                fps,
                _pausado,
                _tickSuave != null && _tickSuave.IsEnabled,
                _desdeFix.Elapsed.TotalSeconds,
                s != null ? s.AvgSpeed : -1));
        };
        _latido.Start();
    }

    private unsafe void LogPixel(string tag, int px, int py)
    {
        if (_gl == null) return;
        var buf = stackalloc byte[4];
        _gl.ReadPixels(px, py, 1, 1, GLEnum.Rgba, GLEnum.UnsignedByte, buf);
        Console.Error.WriteLine("[MapGlSurface] pixel(" + tag + ") @(" + px + "," + py + ") = "
            + buf[0] + "," + buf[1] + "," + buf[2] + "," + buf[3]);
    }

    // ---- Creación de AB en el mapa (API pública, la llama MainWindow) ----
    public void BeginAbCreation() { _abCreating = true; _abHasA = false; RequestNextFrameRendering(); }
    public void SetAbPointA(double e, double n) { _abAe = e; _abAn = n; _abHasA = true; RequestNextFrameRendering(); }
    public void EndAbCreation() { _abCreating = false; _abHasA = false; RequestNextFrameRendering(); }

    // Dibuja el marcador del punto A (rombo verde) + la línea pendiente A→tractor.
    private void DrawAbCreation(double pe, double pn, double scale)
    {
        if (_gl == null) return;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0); }

        // Línea pendiente A → tractor (amarilla brillante) — la guía "en
        // construcción". Se ve crecer desde A a medida que el tractor avanza.
        float[] colPend = { 1.0f, 0.92f, 0.20f, 1f }; // amarillo brillante
        EnsureScratch(4);
        _scratch[0] = (float)_abAe; _scratch[1] = (float)_abAn;
        _scratch[2] = (float)pe;    _scratch[3] = (float)pn;
        UploadAndDraw(PrimitiveType.Lines, 2, colPend);

        // Marcador A: rombo MAGENTA grande (distinto del tractor verde) en el
        // punto A, para que se vea claro dónde arrancó la guía.
        double r = 16.0 / scale; // ~16px en mundo
        float[] colA = { 1.0f, 0.15f, 0.75f, 1f }; // magenta
        EnsureScratch(8);
        _scratch[0] = (float)_abAe;       _scratch[1] = (float)(_abAn + r);
        _scratch[2] = (float)(_abAe - r); _scratch[3] = (float)_abAn;
        _scratch[4] = (float)(_abAe + r); _scratch[5] = (float)_abAn;
        _scratch[6] = (float)_abAe;       _scratch[7] = (float)(_abAn - r);
        UploadAndDraw(PrimitiveType.TriangleStrip, 4, colA);
    }

    // Lightbar: barra de desvío arriba-centro del mapa. Un indicador que se
    // mueve al lado OPUESTO al error (te dice hacia dónde corregir): si estás a
    // la derecha de la línea, el marcador va a la izquierda. Verde centrado,
    // amarillo y rojo según crece el desvío. Se dibuja en NDC (MVP identidad),
    // como overlay fijo (no rota con el mapa).
    private void DrawLightbar()
    {
        if (_gl == null || double.IsNaN(_xte)) return;

        // MVP identidad → vértices en NDC [-1,1].
        Span<float> idm = stackalloc float[16] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
        unsafe { fixed (float* p = idm) _gl.UniformMatrix4(_uMvp, 1, false, p); }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0); }
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        const double yc = 0.86;      // altura del lightbar (NDC)
        const double halfW = 0.42;   // medio ancho de la pista
        const double barH = 0.045;   // alto de las cajas
        const double fullScale = 0.5; // XTE (m) que llega al borde

        // Pista de fondo (línea tenue) + ticks cada 1/5.
        float[] colTrack = { 0.561f, 0.627f, 0.573f, 0.35f };
        EnsureScratch(4);
        _scratch[0] = (float)-halfW; _scratch[1] = (float)yc;
        _scratch[2] = (float) halfW; _scratch[3] = (float)yc;
        UploadAndDraw(PrimitiveType.Lines, 2, colTrack);
        for (int i = -5; i <= 5; i++)
        {
            double tx = i / 5.0 * halfW;
            double th = (i == 0) ? barH * 1.4 : barH * 0.7; // el central más alto
            EnsureScratch(4);
            _scratch[0] = (float)tx; _scratch[1] = (float)(yc - th);
            _scratch[2] = (float)tx; _scratch[3] = (float)(yc + th);
            UploadAndDraw(PrimitiveType.Lines, 2, colTrack);
        }

        // Indicador: lado opuesto al error. Clamp a la pista.
        double x = Math.Clamp(-_xte / fullScale, -1.0, 1.0) * halfW;
        double a = Math.Abs(_xte);
        float[] col = a < 0.05 ? new float[] { 0.290f, 0.729f, 0.243f, 0.95f }   // verde (centrado)
                    : a < 0.20 ? new float[] { 0.973f, 0.808f, 0.247f, 0.95f }   // amarillo
                               : new float[] { 0.929f, 0.282f, 0.282f, 0.95f };  // rojo
        double bw = 0.028; // medio ancho del marcador
        // Quad (TriangleStrip): (x-bw, yc-barH),(x-bw, yc+barH),(x+bw, yc-barH),(x+bw, yc+barH)
        EnsureScratch(8);
        _scratch[0] = (float)(x - bw); _scratch[1] = (float)(yc - barH);
        _scratch[2] = (float)(x - bw); _scratch[3] = (float)(yc + barH);
        _scratch[4] = (float)(x + bw); _scratch[5] = (float)(yc - barH);
        _scratch[6] = (float)(x + bw); _scratch[7] = (float)(yc + barH);
        UploadAndDraw(PrimitiveType.TriangleStrip, 4, col);

        _gl.Disable(EnableCap.Blend);
    }

    // ---- cámara: API pública (la maneja MapPanel, que sí recibe el mouse;
    //      OpenGlControlBase no es hit-testable de forma confiable) ---------

    /// <summary>Zoom multiplicativo (rueda). factor>1 acerca.</summary>
    public void ZoomBy(double factor)
    {
        _userZoom = Math.Clamp(_userZoom * factor, 0.05, 60.0);
        RequestNextFrameRendering();
    }

    /// <summary>Pan por delta de píxeles lógicos del arrastre. renderScaling
    /// convierte a físicos; scale es px físicos/metro. Pantalla Y-down vs
    /// mundo Y-up -> +dyPx.</summary>
    public void PanByPixels(double dxPx, double dyPx, double renderScaling)
    {
        double sc = _lastEffectiveScale > 1e-6 ? _lastEffectiveScale : 1.0;
        _userPanX -= dxPx * renderScaling / sc;
        _userPanY += dyPx * renderScaling / sc;
        RequestNextFrameRendering();
    }

    /// <summary>Vuelve al encuadre automático (fit-to-bbox).</summary>
    public void ResetCamera()
    {
        _userZoom = 1.0; _userPanX = 0; _userPanY = 0;
        RequestNextFrameRendering();
    }

    // ---- helpers de render --------------------------------------------

    // Proyección final = encuadre base (fit-to-bbox o tractor) + cámara de
    // usuario (zoom multiplicativo + pan aditivo, Stage 6).
    private void ComputeProjection(int wPx, int hPx, out double cx, out double cy, out double scale)
    {
        ComputeBaseProjection(wPx, hPx, out double bcx, out double bcy, out double bscale);
        scale = bscale * _userZoom;
        cx = bcx + _userPanX;
        cy = bcy + _userPanY;
        _lastEffectiveScale = scale;
    }

    private void ComputeBaseProjection(int wPx, int hPx, out double cx, out double cy, out double scale)
    {
        // Padding fijo (40px) idem MapSkiaSurface para que el toggle
        // GL on/off no cambie composicion visual.
        const double pad = 40.0;
        if (_hasBbox)
        {
            double bboxW = Math.Max(_maxE - _minE, 0.001);
            double bboxH = Math.Max(_maxN - _minN, 0.001);
            double w = Math.Max(wPx - 2 * pad, 1);
            double h = Math.Max(hPx - 2 * pad, 1);
            scale = Math.Min(w / bboxW, h / bboxH);
            cx = (_minE + _maxE) * 0.5;
            cy = (_minN + _maxN) * 0.5;
            return;
        }
        // Sin bbox: si hay snapshot con posicion, centramos en el
        // tractor con una escala generica (1 px = 0.1m).
        var s = _snap;
        if (s != null && (s.PivotEasting != 0 || s.PivotNorthing != 0))
        {
            cx = s.PivotEasting;
            cy = s.PivotNorthing;
            scale = 8.0;
            return;
        }
        cx = 0; cy = 0; scale = 8.0;
    }

    private void DrawGrid(double cx, double cy, int wPx, int hPx, double scale, float[] color)
    {
        if (_gl == null) return;
        // Step adaptivo: que las lineas queden razonablemente espaciadas
        // (apuntamos a ~80px en pantalla). Asi al alejar la camara el
        // grid se vuelve mas grueso (sin perder densidad visual).
        double targetPxStep = 80.0;
        double worldStep = targetPxStep / scale;
        // Redondear a 1, 2, 5, 10, 20, 50, 100... idem AOG/FormGPS.
        double pow10 = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(worldStep, 0.001))));
        double n = worldStep / pow10;
        double step = (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * pow10;

        // Calcular extents en coords mundo segun viewport.
        double halfW = wPx * 0.5 / scale;
        double halfH = hPx * 0.5 / scale;
        double x0 = Math.Floor((cx - halfW) / step) * step;
        double x1 = Math.Ceiling((cx + halfW) / step) * step;
        double y0 = Math.Floor((cy - halfH) / step) * step;
        double y1 = Math.Ceiling((cy + halfH) / step) * step;

        // Generar vertex array: dos vertices por linea, lineas verticales
        // primero, despues horizontales.
        int needFloats = 0;
        for (double x = x0; x <= x1; x += step) needFloats += 4;
        for (double y = y0; y <= y1; y += step) needFloats += 4;
        EnsureScratch(needFloats);
        int idx = 0;
        for (double x = x0; x <= x1; x += step)
        {
            _scratch[idx++] = (float)x; _scratch[idx++] = (float)y0;
            _scratch[idx++] = (float)x; _scratch[idx++] = (float)y1;
        }
        for (double y = y0; y <= y1; y += step)
        {
            _scratch[idx++] = (float)x0; _scratch[idx++] = (float)y;
            _scratch[idx++] = (float)x1; _scratch[idx++] = (float)y;
        }

        UploadAndDraw(PrimitiveType.Lines, idx / 2, color);
    }

    private void UploadCoverage(CoverageSnapshot snap)
    {
        if (_gl == null) return;
        _coverageRanges.Clear();
        // Color del snapshot a 0..1.
        _coverageR = snap.R / 255f;
        _coverageG = snap.G / 255f;
        _coverageB = snap.B / 255f;
        _coverageA = snap.A / 255f;

        // Primera pasada: total de vertices.
        int totalVerts = 0;
        if (snap.Sections != null)
        {
            foreach (var sec in snap.Sections)
            {
                if (sec?.Strips == null) continue;
                foreach (var st in sec.Strips)
                {
                    if (st?.Vertices == null || st.Vertices.Count < 3) continue;
                    totalVerts += st.Vertices.Count;
                }
            }
        }
        if (totalVerts == 0)
        {
            _coverageRevisionUploaded = snap.Revision;
            return;
        }

        int needFloats = totalVerts * 2;
        if (_scratch.Length < needFloats)
        {
            int cap = _scratch.Length;
            while (cap < needFloats) cap *= 2;
            _scratch = new float[cap];
        }

        // Segunda pasada: empaquetar floats + registrar rangos.
        int writeIdx = 0;
        int vertexCursor = 0;
        if (snap.Sections != null)
        {
            foreach (var sec in snap.Sections)
            {
                if (sec?.Strips == null) continue;
                foreach (var st in sec.Strips)
                {
                    if (st?.Vertices == null || st.Vertices.Count < 3) continue;
                    int n = st.Vertices.Count;
                    for (int i = 0; i < n; i++)
                    {
                        _scratch[writeIdx++] = (float)st.Vertices[i].E;
                        _scratch[writeIdx++] = (float)st.Vertices[i].N;
                    }
                    _coverageRanges.Add((vertexCursor, n));
                    vertexCursor += n;
                }
            }
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _coverageVbo);
        // BufferData (no BufferSubData) porque la revision implica
        // reemplazo completo del contenido. STATIC_DRAW: el driver
        // puede ubicar la memoria con menos overhead que DYNAMIC.
        unsafe
        {
            fixed (float* p = _scratch)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(needFloats * sizeof(float)),
                    p,
                    BufferUsageARB.StaticDraw);
            }
        }
        _coverageVboCapacityFloats = needFloats;
        _coverageRevisionUploaded = snap.Revision;

        // Restaurar VBO dinamico como activo (el resto del render lo usa).
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
    }

    private void DrawCoverage()
    {
        if (_gl == null || _coverageRanges.Count == 0) return;

        // Alpha blending para que la capa de coverage sea semitransparente
        // sobre el grid del fondo. Se desactiva despues para que las
        // siguientes capas (boundary, tractor) queden con su alpha=1.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _coverageVbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
        _gl.Uniform4(_uColor, _coverageR, _coverageG, _coverageB, _coverageA);

        // Un draw call por strip. Con el VBO ya cargado, el overhead por
        // strip es solo el call de DrawArrays — aceptable hasta ~10k
        // strips. Si en cabina vemos cuellos, agregamos MultiDrawArrays
        // o un index buffer con primitive restart (GL 3.1+).
        for (int i = 0; i < _coverageRanges.Count; i++)
        {
            var r = _coverageRanges[i];
            _gl.DrawArrays(PrimitiveType.TriangleStrip, r.Start, (uint)r.Count);
        }

        _gl.Disable(EnableCap.Blend);
        // Volver al VBO dinamico para que el resto del render siga.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
    }

    private void UploadGuidance(GuidanceGeometrySnapshot snap)
    {
        if (_gl == null) return;
        _guidanceMode = snap.Mode ?? "Off";
        var pts = snap.Points;
        int n = (pts != null) ? pts.Count : 0;
        _guidanceVertexCount = n;
        _guidanceRevisionUploaded = snap.Revision;

        // Copia CPU para las paralelas. Cambió la línea -> hay que reconstruir
        // el VBO de paralelas (que si no se reusa tal cual entre frames).
        _guidancePts.Clear();
        if (pts != null) _guidancePts.AddRange(pts);
        _parDirty = true;

        if (n < 2 || _guidanceMode == "Off")
        {
            // Nada que rendear — dejamos _guidanceVertexCount en 0 para
            // que el DrawGuidance no se llame este frame.
            return;
        }

        int needFloats = n * 2;
        EnsureScratch(needFloats);
        for (int i = 0; i < n; i++)
        {
            _scratch[i * 2]     = (float)pts![i].E;
            _scratch[i * 2 + 1] = (float)pts[i].N;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _guidanceVbo);
        // Grow + upload completo. Como cambia rara vez, BufferData esta bien.
        if (_guidanceVboCapacityFloats < needFloats)
        {
            int cap = Math.Max(_guidanceVboCapacityFloats, 64);
            while (cap < needFloats) cap *= 2;
            _guidanceVboCapacityFloats = cap;
            unsafe
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(cap * sizeof(float)),
                    (void*)0,
                    BufferUsageARB.StaticDraw);
            }
        }
        unsafe
        {
            fixed (float* p = _scratch)
            {
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer,
                    0,
                    (nuint)(needFloats * sizeof(float)),
                    p);
            }
        }
        // Restaurar VBO dinamico (los draws siguientes lo asumen).
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
    }

    private void DrawGuidance()
    {
        if (_gl == null) return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _guidanceVbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
        _gl.Uniform4(_uColor, ColGuidance[0], ColGuidance[1], ColGuidance[2], ColGuidance[3]);
        // AB son dos puntos -> LINE_STRIP de 2 = una linea. Curve/Contour
        // mantienen el mismo primitive (open polyline). Si en el futuro
        // queremos cerrar el contour visualmente, cambiamos a LineLoop solo
        // para ese modo.
        _gl.DrawArrays(PrimitiveType.LineStrip, 0, (uint)_guidanceVertexCount);

        // Volver al VBO dinamico para las capas siguientes.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
    }

    // Guías paralelas vecinas: replican la línea activa desplazada ±k·ancho
    // perpendicular, llenando el lote como hace AgOpenGPS. Se calculan en CPU
    // desde _guidancePts + ToolWidth y se dibujan tenues por debajo de la activa.
    // AB (2 puntos) → offset rígido perpendicular. Curva (polilínea) → cada
    // vértice se desplaza por su normal local.
    /// <summary>
    /// Reconstruye el VBO de guías paralelas. Solo corre cuando cambió la línea,
    /// el ancho de herramienta o la extensión del lote — NO por frame.
    /// </summary>
    private void RebuildGuidanceParallel(double width, double span)
    {
        if (_gl == null) return;
        _parRanges.Clear();
        _parIsLines = false;

        int n = _guidancePts.Count;
        // Cap defensivo (evita miles de líneas si el ancho es chico).
        int half = (int)Math.Ceiling(span / width) + 1;
        half = Math.Clamp(half, 1, 40);

        bool isAb = (_guidanceMode == "AB") || n == 2;
        int writeIdx = 0;
        int vertexCursor = 0;

        if (isAb)
        {
            var a = _guidancePts[0];
            var b = _guidancePts[n - 1];
            double dx = b.E - a.E, dy = b.N - a.N;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;
            double px = -dy / len, py = dx / len;   // perpendicular unitaria

            // Todas las paralelas (menos k=0, la activa) en un solo GL_LINES:
            // 2 vértices por línea.
            EnsureScratch(2 * half * 4);
            for (int k = -half; k <= half; k++)
            {
                if (k == 0) continue;
                double ox = px * width * k, oy = py * width * k;
                _scratch[writeIdx++] = (float)(a.E + ox); _scratch[writeIdx++] = (float)(a.N + oy);
                _scratch[writeIdx++] = (float)(b.E + ox); _scratch[writeIdx++] = (float)(b.N + oy);
            }
            _parIsLines = true;
            _parRanges.Add((0, writeIdx / 2));
        }
        else
        {
            // Curva: normal local por vértice (perpendicular a la tangente).
            // Todas las paralelas van concatenadas; cada una con su rango.
            EnsureScratch(2 * half * n * 2);
            for (int k = -half; k <= half; k++)
            {
                if (k == 0) continue;
                for (int i = 0; i < n; i++)
                {
                    var p0 = _guidancePts[Math.Max(0, i - 1)];
                    var p1 = _guidancePts[Math.Min(n - 1, i + 1)];
                    double dx = p1.E - p0.E, dy = p1.N - p0.N;
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
                    double px = -dy / len, py = dx / len;
                    var pi = _guidancePts[i];
                    _scratch[writeIdx++] = (float)(pi.E + px * width * k);
                    _scratch[writeIdx++] = (float)(pi.N + py * width * k);
                }
                _parRanges.Add((vertexCursor, n));
                vertexCursor += n;
            }
        }

        if (writeIdx == 0) return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _parVbo);
        unsafe
        {
            fixed (float* p = _scratch)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(writeIdx * sizeof(float)), p, BufferUsageARB.StaticDraw);
            }
        }
        _parVboCapacityFloats = writeIdx;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
    }

    private void DrawGuidanceParallel()
    {
        if (_gl == null) return;
        // Se dibuja cuando hay guía activa (lo garantiza el caller: mode!=Off y
        // >=2 puntos). La cantidad de paralelas cubre el lote si hay lindero, o
        // un rango razonable alrededor del tractor si el lote es nuevo/sin
        // lindero — así las paralelas aparecen apenas se crea la guía.
        var snap = _snap;
        double width = snap != null ? snap.ToolWidth : 0;
        if (width < 0.05) return;                     // sin ancho no hay paso
        if (_guidancePts.Count < 2) return;

        // Extensión: el lote si hay lindero, si no ~300 m alrededor.
        double span = _hasBbox ? Math.Max(_maxE - _minE, _maxN - _minN) : 300.0;

        // Reconstruir SOLO si cambió alguna entrada. En steady-state esto no
        // corre nunca y el frame se va en los DrawArrays de abajo.
        if (_parDirty || width != _parWidthUsed || span != _parSpanUsed)
        {
            RebuildGuidanceParallel(width, span);
            _parWidthUsed = width;
            _parSpanUsed = span;
            _parDirty = false;
        }
        if (_parRanges.Count == 0) return;

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _parVbo);
        unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0); }
        _gl.Uniform4(_uColor, ColGuidancePar[0], ColGuidancePar[1], ColGuidancePar[2], ColGuidancePar[3]);

        if (_parIsLines)
        {
            var r = _parRanges[0];
            _gl.DrawArrays(PrimitiveType.Lines, r.Start, (uint)r.Count);
        }
        else
        {
            for (int i = 0; i < _parRanges.Count; i++)
            {
                var r = _parRanges[i];
                _gl.DrawArrays(PrimitiveType.LineStrip, r.Start, (uint)r.Count);
            }
        }

        _gl.Disable(EnableCap.Blend);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0); }
    }

    /// <summary>
    /// Grosor de la barra de secciones, en píxeles de pantalla. Va en píxeles y
    /// no en metros a propósito: es un indicador de estado, no una medida del
    /// terreno, así que tiene que verse igual de bien con el zoom cerca o lejos.
    /// </summary>
    private const double GrosorBarraSeccionesPx = 7.0;

    private void DrawTool(ToolGeometrySnapshot snap, double scale)
    {
        if (_gl == null) return;

        // Se dibuja con QUADS y no con GL_LINES. LineWidth lo capea el driver:
        // en core profile y en GL ES —que es lo que Avalonia negocia en Windows,
        // y lo que corre la placa de la cabina— el máximo suele ser 1.0, así que
        // pedir 3 px daba una línea de 1 px igual y el grosor no se podía tocar.
        // Con dos triángulos por sección el ancho es nuestro y se ve de verdad.
        double grosorMundo = GrosorBarraSeccionesPx / Math.Max(scale, 1e-6);
        double medio = grosorMundo * 0.5;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }

        // Un draw por sección: N max ~16, y así cada una conserva su color de
        // estado sin tener que reordenar nada.
        var sections = snap.Sections;
        for (int i = 0; i < sections!.Count; i++)
        {
            var s = sections[i];

            double dx = s.RightE - s.LeftE;
            double dy = s.RightN - s.LeftN;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;      // sección de ancho cero

            // Perpendicular unitaria × medio grosor: engrosa la barra hacia
            // adelante y hacia atrás de la herramienta.
            double px = -dy / len * medio;
            double py = dx / len * medio;

            EnsureScratch(8);
            _scratch[0] = (float)(s.LeftE  + px); _scratch[1] = (float)(s.LeftN  + py);
            _scratch[2] = (float)(s.LeftE  - px); _scratch[3] = (float)(s.LeftN  - py);
            _scratch[4] = (float)(s.RightE + px); _scratch[5] = (float)(s.RightN + py);
            _scratch[6] = (float)(s.RightE - px); _scratch[7] = (float)(s.RightN - py);

            UploadAndDraw(PrimitiveType.TriangleStrip, 4, SectionColor(s));
        }
    }

    private void UploadTram(TramGeometrySnapshot snap)
    {
        if (_gl == null) return;
        _tramDisplayMode = snap.DisplayMode ?? "None";
        _tramRevisionUploaded = snap.Revision;
        _tramLineRanges.Clear();
        _tramOuterStart = 0; _tramOuterCount = 0;
        _tramInnerStart = 0; _tramInnerCount = 0;

        if (_tramDisplayMode == "None")
        {
            return; // nada que renderear, dejamos VBO como estaba
        }

        // Contar vertices total para reservar de una vez.
        int totalVerts = 0;
        if (snap.Lines != null)
        {
            foreach (var l in snap.Lines)
            {
                if (l?.Points == null || l.Points.Count < 2) continue;
                totalVerts += l.Points.Count;
            }
        }
        int outerN = (snap.OuterBoundary != null && snap.OuterBoundary.Count >= 2) ? snap.OuterBoundary.Count : 0;
        int innerN = (snap.InnerBoundary != null && snap.InnerBoundary.Count >= 2) ? snap.InnerBoundary.Count : 0;
        totalVerts += outerN + innerN;
        if (totalVerts == 0) return;

        int needFloats = totalVerts * 2;
        EnsureScratch(needFloats);

        int writeIdx = 0;
        int vertexCursor = 0;

        // Lineas internas
        if (snap.Lines != null)
        {
            foreach (var l in snap.Lines)
            {
                if (l?.Points == null || l.Points.Count < 2) continue;
                int n = l.Points.Count;
                for (int i = 0; i < n; i++)
                {
                    _scratch[writeIdx++] = (float)l.Points[i].E;
                    _scratch[writeIdx++] = (float)l.Points[i].N;
                }
                _tramLineRanges.Add((vertexCursor, n));
                vertexCursor += n;
            }
        }

        // Outer boundary
        if (outerN > 0)
        {
            _tramOuterStart = vertexCursor;
            _tramOuterCount = outerN;
            for (int i = 0; i < outerN; i++)
            {
                _scratch[writeIdx++] = (float)snap.OuterBoundary![i].E;
                _scratch[writeIdx++] = (float)snap.OuterBoundary[i].N;
            }
            vertexCursor += outerN;
        }

        // Inner boundary
        if (innerN > 0)
        {
            _tramInnerStart = vertexCursor;
            _tramInnerCount = innerN;
            for (int i = 0; i < innerN; i++)
            {
                _scratch[writeIdx++] = (float)snap.InnerBoundary![i].E;
                _scratch[writeIdx++] = (float)snap.InnerBoundary[i].N;
            }
            vertexCursor += innerN;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _tramVbo);
        // BufferData (no SubData) porque la revision implica reemplazo
        // completo. STATIC_DRAW: rara vez cambia.
        unsafe
        {
            fixed (float* p = _scratch)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(needFloats * sizeof(float)),
                    p,
                    BufferUsageARB.StaticDraw);
            }
        }
        _tramVboCapacityFloats = needFloats;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
    }

    private void DrawTram()
    {
        if (_gl == null) return;

        // Tono claro semitransparente -> alpha blending on, off al salir.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _tramVbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
        _gl.Uniform4(_uColor, ColTram[0], ColTram[1], ColTram[2], ColTram[3]);
        _gl.LineWidth(2.0f);

        bool drawLines = (_tramDisplayMode == "All" || _tramDisplayMode == "FillTracks");
        bool drawBnd   = (_tramDisplayMode == "All" || _tramDisplayMode == "BoundaryTracks");

        if (drawLines)
        {
            for (int i = 0; i < _tramLineRanges.Count; i++)
            {
                var r = _tramLineRanges[i];
                _gl.DrawArrays(PrimitiveType.LineStrip, r.Start, (uint)r.Count);
            }
        }

        if (drawBnd)
        {
            if (_tramOuterCount > 0)
                _gl.DrawArrays(PrimitiveType.LineLoop, _tramOuterStart, (uint)_tramOuterCount);
            if (_tramInnerCount > 0)
                _gl.DrawArrays(PrimitiveType.LineLoop, _tramInnerStart, (uint)_tramInnerCount);
        }

        _gl.LineWidth(1.0f);
        _gl.Disable(EnableCap.Blend);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
    }

    private void UploadPaths(PathsGeometrySnapshot snap)
    {
        if (_gl == null) return;
        _pathsRevisionUploaded = snap.Revision;
        _pathsYouTurnStart = 0; _pathsYouTurnCount = 0;
        _pathsRecordedStart = 0; _pathsRecordedCount = 0;

        int ytN = (snap.YouTurn != null && snap.YouTurn.Count >= 2) ? snap.YouTurn.Count : 0;
        int recN = (snap.Recorded != null && snap.Recorded.Count >= 2) ? snap.Recorded.Count : 0;
        int totalVerts = ytN + recN;
        if (totalVerts == 0) return; // nada que renderear, dejamos VBO como estaba

        int needFloats = totalVerts * 2;
        EnsureScratch(needFloats);

        int writeIdx = 0;
        int vertexCursor = 0;

        // YouTurn
        if (ytN > 0)
        {
            _pathsYouTurnStart = vertexCursor;
            _pathsYouTurnCount = ytN;
            for (int i = 0; i < ytN; i++)
            {
                _scratch[writeIdx++] = (float)snap.YouTurn![i].E;
                _scratch[writeIdx++] = (float)snap.YouTurn[i].N;
            }
            vertexCursor += ytN;
        }

        // Recorded
        if (recN > 0)
        {
            _pathsRecordedStart = vertexCursor;
            _pathsRecordedCount = recN;
            for (int i = 0; i < recN; i++)
            {
                _scratch[writeIdx++] = (float)snap.Recorded![i].E;
                _scratch[writeIdx++] = (float)snap.Recorded[i].N;
            }
            vertexCursor += recN;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _pathsVbo);
        // BufferData (no SubData) porque la revision implica reemplazo
        // completo. STATIC_DRAW: rara vez cambia.
        unsafe
        {
            fixed (float* p = _scratch)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(needFloats * sizeof(float)),
                    p,
                    BufferUsageARB.StaticDraw);
            }
        }
        _pathsVboCapacityFloats = needFloats;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
    }

    private void DrawPaths()
    {
        if (_gl == null) return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _pathsVbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
        _gl.LineWidth(2.0f);

        // YouTurn (naranja)
        if (_pathsYouTurnCount > 0)
        {
            _gl.Uniform4(_uColor, ColPathsYouTurn[0], ColPathsYouTurn[1], ColPathsYouTurn[2], ColPathsYouTurn[3]);
            _gl.DrawArrays(PrimitiveType.LineStrip, _pathsYouTurnStart, (uint)_pathsYouTurnCount);
        }

        // Recorded (violeta)
        if (_pathsRecordedCount > 0)
        {
            _gl.Uniform4(_uColor, ColPathsRecorded[0], ColPathsRecorded[1], ColPathsRecorded[2], ColPathsRecorded[3]);
            _gl.DrawArrays(PrimitiveType.LineStrip, _pathsRecordedStart, (uint)_pathsRecordedCount);
        }

        _gl.LineWidth(1.0f);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }
    }

    private static float[] SectionColor(ToolSectionGeometry s)
    {
        // Reglas (heredadas del render legacy FormGPS):
        //   btn=Off (0)  → gris
        //   btn=Manual (2) + isOn → amarillo
        //   btn=Auto (1) + isMapping → verde (esta aplicando)
        //   btn=Auto (1) + !isMapping → rojo (boundary/headland/anti-overlap denegada)
        if (s.BtnState == 0) return ColToolOff;
        if (s.BtnState == 2) return s.IsOn ? ColToolManual : ColToolOff;
        // btn=Auto
        return s.IsMapping ? ColToolAutoOn : ColToolAutoOff;
    }

    private void DrawRing(List<FieldPoint> ring, float[] color)
    {
        if (_gl == null) return;
        int n = ring.Count;
        int needFloats = n * 2;
        EnsureScratch(needFloats);
        for (int i = 0; i < n; i++)
        {
            _scratch[i * 2]     = (float)ring[i].E;
            _scratch[i * 2 + 1] = (float)ring[i].N;
        }
        UploadAndDraw(PrimitiveType.LineLoop, n, color);
    }

    /// <summary>Sube a GPU los sprites pendientes. Se llama desde el hilo GL.</summary>
    private void SubirSpritePendiente()
    {
        if (_gl == null || _texProgram == 0) return;

        if (_pendingTexRgba != null)
        {
            var px = _pendingTexRgba;
            _pendingTexRgba = null;
            if (_vehicleTex == 0) _vehicleTex = _gl.GenTexture();
            _vehicleTexReady = SubirTextura(_vehicleTex, px, _pendingTexW, _pendingTexH, "vehículo");
        }

        if (_pendingWheelRgba != null)
        {
            var px = _pendingWheelRgba;
            _pendingWheelRgba = null;
            if (_wheelTex == 0) _wheelTex = _gl.GenTexture();
            _wheelTexReady = SubirTextura(_wheelTex, px, _pendingWheelW, _pendingWheelH, "rueda");
        }

        if (_pendingImplRgba != null)
        {
            var px = _pendingImplRgba;
            _pendingImplRgba = null;
            if (_implementoTex == 0) _implementoTex = _gl.GenTexture();
            _implementoTexReady = SubirTextura(_implementoTex, px, _pendingImplW, _pendingImplH, "implemento");
        }
    }

    /// <summary>
    /// Dibuja el implemento en la posición/rumbo de la herramienta, a lo ancho
    /// real configurado. Se ubica de modo que la BARRA (el borde trasero de la
    /// imagen, donde van los cuerpos de siembra) quede sobre el punto de la
    /// herramienta, y la lanza salga hacia adelante, buscando al tractor.
    /// Devuelve false si no hay textura: entonces el mapa sigue con las barras
    /// de sección de siempre.
    /// </summary>
    private bool DrawImplementoSprite()
    {
        if (_gl == null || _texProgram == 0 || !_implementoTexReady) return false;
        var snap = _snap;
        if (snap == null) return false;

        double ancho = snap.ToolWidth;
        if (ancho <= 0.2) return false;          // sin implemento configurado
        double largo = ancho * _implementoAspect;

        try
        {
            _gl.UseProgram(_texProgram);
            unsafe
            {
                fixed (float* m = _mvpCache) _gl.UniformMatrix4(_uTexMvp, 1, false, m);
            }
            // Posición suavizada de la herramienta (la calcula
            // ActualizarPosicionRender junto con la del tractor, con el mismo
            // filtro): si el implemento avanza a saltos mientras el tractor va
            // suave, se ve como si se desenganchara y volviera.
            double te = _renderToolE, tn = _renderToolN;

            // Centro medio largo adelante del punto de herramienta.
            DrawQuadTex(_implementoTex, te, tn, snap.ToolHeading,
                        0, largo * 0.5, ancho * 0.5, largo * 0.5, 0);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MapGlSurface] fallo dibujando el implemento: " + ex.Message);
            _implementoTexReady = false;
            return false;
        }
    }

    private bool SubirTextura(uint tex, byte[] px, int w, int h, string que)
    {
        try
        {
            _gl!.BindTexture(TextureTarget.Texture2D, tex);
            unsafe
            {
                fixed (byte* p = px)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba,
                        (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }
            // CLAMP: sin esto, el filtrado del borde repite el lado opuesto y
            // aparece una franja del otro extremo del sprite.
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MapGlSurface] no se pudo subir el sprite de " + que + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Ángulos de Ackermann: la rueda interna gira más que la externa. Copia
    /// exacta de AckermannAngles (AgOpenGPS.Core/DrawLib), para que el vehículo
    /// se vea igual que en el renderer nativo.
    /// </summary>
    private static void Ackermann(double anguloRueda, out double izq, out double der)
    {
        izq = anguloRueda;
        der = anguloRueda;
        if (anguloRueda > 0.0) izq *= 1.25;
        else der *= 1.25;
    }

    /// <summary>
    /// Dibuja un quad texturado en coords de vehículo (X = derecha, Y = avance),
    /// rotado por rumbo y opcionalmente por un giro propio (ruedas).
    /// cx/cy son el centro en coords locales; hx/hy las MEDIAS medidas — mismo
    /// criterio que el centerToU1V1 del renderer nativo.
    /// </summary>
    private void DrawQuadTex(uint tex, double e, double n, double headingRad,
                             double cx, double cy, double hx, double hy, double giroRad)
    {
        if (_gl == null) return;

        double sg = Math.Sin(giroRad), cg = Math.Cos(giroRad);
        double sh = Math.Sin(headingRad), ch = Math.Cos(headingRad);

        // local -> (giro propio) -> (rumbo) -> mundo
        (double X, double Y) A(double lx, double ly)
        {
            double rx = lx * cg - ly * sg;
            double ry = lx * sg + ly * cg;
            rx += cx; ry += cy;
            return (e + (rx * ch + ry * sh), n + (rx * (-sh) + ry * ch));
        }

        var p0 = A(-hx, -hy);
        var p1 = A( hx, -hy);
        var p2 = A( hx,  hy);
        var p3 = A(-hx,  hy);

        float[] v =
        {
            (float)p0.X, (float)p0.Y, 0f, 1f,
            (float)p1.X, (float)p1.Y, 1f, 1f,
            (float)p2.X, (float)p2.Y, 1f, 0f,
            (float)p0.X, (float)p0.Y, 0f, 1f,
            (float)p2.X, (float)p2.Y, 1f, 0f,
            (float)p3.X, (float)p3.Y, 0f, 0f,
        };

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _texVbo);
        unsafe
        {
            fixed (float* p = v)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(v.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            }
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 4, (void*)0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, sizeof(float) * 4, (void*)(sizeof(float) * 2));
        }
        _gl.EnableVertexAttribArray(0);
        _gl.EnableVertexAttribArray(1);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    /// <summary>
    /// Dibuja el sprite del vehículo orientado por rumbo. Devuelve false si no
    /// hay textura lista, para que el llamador caiga al triángulo.
    /// </summary>
    private bool DrawTractorSprite(double e, double n, double headingRad, double scale)
    {
        if (_gl == null || _texProgram == 0 || !_vehicleTexReady) return false;

        // Geometría IDÉNTICA al renderer nativo (GuidanceDrawExtensions):
        // el origen es el eje TRASERO (pivote), el cuerpo va centrado medio
        // wheelbase adelante y las ruedas delanteras en el eje delantero.
        // Las medidas son medias-medidas (centerToU1V1 del original).
        var snap = _snap;
        double wb = snap?.Wheelbase ?? 0;
        double tw = snap?.TrackWidth ?? 0;
        if (wb <= 0.1) wb = 3.3;    // perfil sin cargar: valores típicos
        if (tw <= 0.1) tw = 1.9;

        // Piso en píxeles: sin esto el vehículo desaparece al alejar el zoom.
        double minPx = 26.0;
        double anchoPx = (2 * tw) * scale;
        double k = anchoPx < minPx ? minPx / anchoPx : 1.0;
        wb *= k; tw *= k;

        try
        {
            _gl.UseProgram(_texProgram);
            unsafe
            {
                fixed (float* m = _mvpCache) _gl.UniformMatrix4(_uTexMvp, 1, false, m);
            }

            // Cuerpo: centro (0, wb/2), medias medidas (tw, wb).
            DrawQuadTex(_vehicleTex, e, n, headingRad, 0, wb * 0.5, tw, wb, 0);

            // Ruedas delanteras: una por lado sobre el eje delantero (y = wb),
            // cada una girada por SU ángulo de Ackermann. El ángulo va negado
            // igual que en el nativo (ahí entra como -steerAngle).
            if (_wheelTexReady)
            {
                Ackermann(-(snap?.SteerAngleDeg ?? 0), out double izqDeg, out double derDeg);
                double izq = izqDeg * Math.PI / 180.0;
                double der = derDeg * Math.PI / 180.0;
                double whx = tw * 0.5, why = wb * 0.75;
                DrawQuadTex(_wheelTex, e, n, headingRad,  tw * 0.5, wb, whx, why, der);
                DrawQuadTex(_wheelTex, e, n, headingRad, -tw * 0.5, wb, whx, why, izq);
            }

            // Dejar el estado como lo espera el resto del frame: atributo 1
            // apagado y el VBO/programa de color plano de vuelta.
            _gl.DisableVertexAttribArray(1);
            _gl.UseProgram(_program);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            unsafe
            {
                _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MapGlSurface] fallo dibujando el sprite: " + ex.Message);
            _vehicleTexReady = false;   // no reintentar cada frame
            return false;
        }
    }

    private void DrawTractor(double e, double n, double headingRad, double scale)
    {
        if (_gl == null) return;

        SubirSpritePendiente();
        // Con sprite cargado se dibuja el vehículo; si no, el triángulo.
        if (DrawTractorSprite(e, n, headingRad, scale)) return;
        // Tamano del triangulo en pixeles -> convertir a coords mundo
        // dividiendo por scale (px / (px/m) = m).
        double sizePx = 12 * 1.8; // idem Skia (scaleFactor 1.8)
        double sizeWorld = sizePx / scale;
        // Heading: en AOG, heading 0 = North, sentido horario. En coords
        // World Y-up: tip apunta a +N cuando heading=0 -> (0, +size).
        // Rotacion CW por heading: x' = sin(h)*y0 + cos(h)*x0; pero el
        // codigo Skia usaba x' = x*cos - y*sin con Y invertido. Aca,
        // adaptado a Y-up: tip = (sin(h)*size, cos(h)*size).
        double s = Math.Sin(headingRad), c = Math.Cos(headingRad);
        double tipX = e + ( 0 * c +  sizeWorld * s);
        double tipY = n + ( 0 * (-s) + sizeWorld * c);
        double blX  = e + (-sizeWorld * 0.65 * c + (-sizeWorld * 0.55) * s);
        double blY  = n + (-sizeWorld * 0.65 * (-s) + (-sizeWorld * 0.55) * c);
        double brX  = e + ( sizeWorld * 0.65 * c + (-sizeWorld * 0.55) * s);
        double brY  = n + ( sizeWorld * 0.65 * (-s) + (-sizeWorld * 0.55) * c);

        // Triangulo relleno.
        EnsureScratch(6);
        _scratch[0] = (float)tipX; _scratch[1] = (float)tipY;
        _scratch[2] = (float)blX;  _scratch[3] = (float)blY;
        _scratch[4] = (float)brX;  _scratch[5] = (float)brY;
        UploadAndDraw(PrimitiveType.Triangles, 3, ColTractor);

        // Borde oscuro (line loop) por encima.
        UploadAndDraw(PrimitiveType.LineLoop, 3, ColTractorEdge);
    }

    private void EnsureScratch(int neededFloats)
    {
        if (_scratch.Length < neededFloats)
        {
            int cap = _scratch.Length;
            while (cap < neededFloats) cap *= 2;
            _scratch = new float[cap];
        }
    }

    private void UploadAndDraw(PrimitiveType prim, int vertexCount, float[] color)
    {
        if (_gl == null) return;
        // Grow VBO si el batch es mas grande de lo que cabe; mas eficiente
        // que reallocar cada draw call.
        int floatsNeeded = vertexCount * 2;
        if (_vboCapacityFloats < floatsNeeded)
        {
            int cap = Math.Max(_vboCapacityFloats, 256);
            while (cap < floatsNeeded) cap *= 2;
            _vboCapacityFloats = cap;
            unsafe
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(cap * sizeof(float)),
                    (void*)0,
                    BufferUsageARB.DynamicDraw);
            }
        }
        unsafe
        {
            fixed (float* p = _scratch)
            {
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer,
                    0,
                    (nuint)(floatsNeeded * sizeof(float)),
                    p);
            }
        }
        _gl.Uniform4(_uColor, color[0], color[1], color[2], color[3]);
        _gl.DrawArrays(prim, 0, (uint)vertexCount);
    }

    // ---- shader util ---------------------------------------------------

    private static uint CompileProgram(GL gl, string vs, string fs)
    {
        uint v = CompileShader(gl, ShaderType.VertexShader, vs);
        uint f = CompileShader(gl, ShaderType.FragmentShader, fs);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, v);
        gl.AttachShader(p, f);
        gl.BindAttribLocation(p, 0, "aPos");
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            var log = gl.GetProgramInfoLog(p);
            gl.DeleteShader(v); gl.DeleteShader(f); gl.DeleteProgram(p);
            throw new InvalidOperationException("MapGlSurface: link failed: " + log);
        }
        gl.DetachShader(p, v);
        gl.DetachShader(p, f);
        gl.DeleteShader(v);
        gl.DeleteShader(f);
        return p;
    }

    private static uint CompileShader(GL gl, ShaderType type, string src)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            var log = gl.GetShaderInfoLog(s);
            gl.DeleteShader(s);
            throw new InvalidOperationException("MapGlSurface: compile " + type + " failed: " + log);
        }
        return s;
    }
}
