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

    // ---- estado GL (creado en OnOpenGlInit, render thread) -------------
    private GL? _gl;
    private uint _program;
    private int _uMvp;
    private int _uColor;
    private uint _vao;
    private uint _vbo;
    private int _vboCapacityFloats;

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

    // ---- tool / sections (Stage 4a) ------------------------------------
    // Cada seccion = un segmento Left↔Right en coords mundo. Coloreamos
    // segun estado: gris (off), verde (auto + mapping), rojo (auto + NO
    // mapping = sectionOnRequest negado por boundary/headland), amarillo
    // (manual ON). El tractor + boundary se renderean encima.
    // Sin revision-cache porque los puntos cambian cada frame; el batch
    // es chico (max ~16 secciones × 2 puntos × 2 floats = 64 floats).
    private uint _toolVbo;
    private int _toolVboCapacityFloats;
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

    // Colores cockpit (RGBA 0..1). Identicos al Skia para paridad.
    private static readonly float[] ColBg            = { 0.055f, 0.078f, 0.063f, 1f }; // #0E1410
    private static readonly float[] ColGrid          = { 0.325f, 0.369f, 0.329f, 0.157f };
    private static readonly float[] ColBoundary      = { 0.357f, 0.784f, 0.314f, 1f }; // #5BC850
    private static readonly float[] ColIslandStroke  = { 0.561f, 0.627f, 0.573f, 1f }; // #8FA092
    private static readonly float[] ColTractor       = { 0.290f, 0.729f, 0.243f, 1f }; // #4ABA3E
    private static readonly float[] ColTractorEdge   = { 0.063f, 0.086f, 0.071f, 1f }; // #101612
    // Guidance line: cian brillante para contrastar con boundary (#5BC850
    // verde) y coverage (verde semitransp). #4DD8FF — visible sobre fondo
    // oscuro y sobre la capa pintada.
    private static readonly float[] ColGuidance      = { 0.302f, 0.847f, 1.000f, 1f }; // #4DD8FF
    // Tool / sections (Stage 4a). Las secciones se pintan como segmentos
    // gruesos (LineWidth lo controla el driver — algunos drivers lo
    // capean en 1.0, pero la barra es solo indicativa, no cubre area).
    private static readonly float[] ColToolOff       = { 0.561f, 0.627f, 0.573f, 1f }; // #8FA092 gris (off)
    private static readonly float[] ColToolAutoOn    = { 0.290f, 0.729f, 0.243f, 1f }; // #4ABA3E verde (auto + mapping)
    private static readonly float[] ColToolAutoOff   = { 0.929f, 0.282f, 0.282f, 1f }; // #ED4848 rojo (auto + NO mapping)
    private static readonly float[] ColToolManual    = { 0.973f, 0.808f, 0.247f, 1f }; // #F8CE3F amarillo (manual on)
    // Tram lines (Stage 4b). Tono claro semitransparente para no competir
    // con guidance cian ni con boundary verde. Coincide con el render
    // legacy (light pink/cream sobre fondo oscuro).
    private static readonly float[] ColTram          = { 0.930f, 0.720f, 0.735f, 0.65f }; // #EDB8BC alpha 0.65

    // Shaders GLSL 3.30 core (compat con GL ES 3.00 cambiando solo el
    // preludio). MVP en uniform; vertex pos en location 0; color en
    // uniform (un draw call por capa de color).
    private const string VertSrc =
        "#version 330 core\n" +
        "layout (location = 0) in vec2 aPos;\n" +
        "uniform mat4 uMvp;\n" +
        "void main(){ gl_Position = uMvp * vec4(aPos, 0.0, 1.0); }\n";

    private const string FragSrc =
        "#version 330 core\n" +
        "uniform vec4 uColor;\n" +
        "out vec4 FragColor;\n" +
        "void main(){ FragColor = uColor; }\n";

    /// <summary>
    /// Push de snapshot desde UI thread (HudPoller). Marca dirty y pide
    /// un frame nuevo; el render real ocurre en thread GL.
    /// </summary>
    public void OnSnapshot(HudSnapshot snap)
    {
        _snap = snap;
        InvalidateBboxIfChanged(snap);
        // RequestNextFrameRendering: API de OpenGlControlBase para
        // forzar un repaint sin tick continuo. No quemamos GPU en
        // idle: render solo cuando hay snapshot nuevo.
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
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
    /// Push de geometria de guidance desde UI thread (GuidanceGeometryPoller).
    /// El poller ya filtra por revision; aca solo guardamos pendiente y
    /// disparamos un frame nuevo. Si el modo viene "Off" igual aplicamos
    /// el snapshot — limpia la linea anterior.
    /// </summary>
    public void OnGuidance(GuidanceGeometrySnapshot snap)
    {
        if (snap == null) return;
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

        _program = CompileProgram(_gl, VertSrc, FragSrc);
        _uMvp   = _gl.GetUniformLocation(_program, "uMvp");
        _uColor = _gl.GetUniformLocation(_program, "uColor");

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

        // VBO de tool (Stage 4a). DYNAMIC_DRAW: se reescribe cada frame
        // que llega snapshot nuevo (4 Hz), con todos los segmentos
        // empacados. El draw real va en 3-4 batches por color.
        _toolVbo = _gl.GenBuffer();
        _toolVboCapacityFloats = 0;

        // VBO de tram (Stage 4b). STATIC_DRAW: solo cambia al regenerar
        // tram (cambio passes/ancho/displayMode). Concatena lineas + outer
        // + inner; cada uno con su rango en vertices.
        _tramVbo = _gl.GenBuffer();
        _tramVboCapacityFloats = 0;

        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] MapGlSurface: GL init OK");
    }

    protected override void OnOpenGlDeinit(GlInterface glInterface)
    {
        if (_gl == null) return;
        try
        {
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteBuffer(_coverageVbo);
            _gl.DeleteBuffer(_guidanceVbo);
            _gl.DeleteBuffer(_toolVbo);
            _gl.DeleteBuffer(_tramVbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] MapGlSurface deinit: " + ex.Message);
        }
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl == null) return;
        var sz = Bounds.Size;
        int wPx = (int)Math.Max(1, sz.Width);
        int hPx = (int)Math.Max(1, sz.Height);

        _gl.Viewport(0, 0, (uint)wPx, (uint)hPx);
        _gl.ClearColor(ColBg[0], ColBg[1], ColBg[2], ColBg[3]);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        // MVP: ortho 2D mapeando el bbox del lote (o un default centrado)
        // al rect del control con padding. Y-up para que North apunte
        // arriba (a diferencia del DrawingContext Skia que es Y-down).
        ComputeProjection(wPx, hPx, out double cxBbox, out double cyBbox, out double scale);

        // Matriz column-major 4x4 (ortho con offset/scale precomputado).
        // pos_clip.x = (pos_world.x - cx) * scale * 2 / w
        // pos_clip.y = (pos_world.y - cy) * scale * 2 / h
        float sx = (float)(scale * 2.0 / wPx);
        float sy = (float)(scale * 2.0 / hPx);
        float tx = (float)(-cxBbox * sx);
        float ty = (float)(-cyBbox * sy);
        Span<float> mvp = stackalloc float[16]
        {
            sx, 0,  0, 0,
            0,  sy, 0, 0,
            0,  0,  1, 0,
            tx, ty, 0, 1
        };
        unsafe
        {
            fixed (float* p = mvp)
                _gl.UniformMatrix4(_uMvp, 1, false, p);
        }

        // --- Capa 1: world grid ----------------------------------------
        // Lineas cada 10m alrededor del centro de bbox. Si no hay bbox
        // (idle / sin lote), usar un grid arbitrario alrededor del 0,0
        // mundo (el tractor aparecera ahi cuando llegue snapshot con
        // PivotEasting/PivotNorthing).
        DrawGrid(cxBbox, cyBbox, wPx, hPx, scale, ColGrid);

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
            DrawGuidance();

        // --- Capa 3b: tool / sections (Stage 4a) -----------------------
        // Va despues de guidance y antes del boundary: las secciones son
        // referencia del operario (donde aplica producto AHORA) y deben
        // quedar visibles sobre coverage/guidance. El boundary sigue por
        // arriba para que el contorno del lote no se pierda.
        if (_pendingTool != null)
        {
            _toolSnap = _pendingTool;
            _pendingTool = null;
            UploadTool(_toolSnap);
        }
        if (_toolSnap != null && _toolSnap.IsValid && _toolSnap.Sections != null && _toolSnap.Sections.Count > 0)
            DrawTool(_toolSnap);

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

            // --- Capa 4: tractor ---------------------------------------
            DrawTractor(snap.PivotEasting, snap.PivotNorthing, snap.Heading, scale);
        }

        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    // ---- helpers de render --------------------------------------------

    private void ComputeProjection(int wPx, int hPx, out double cx, out double cy, out double scale)
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

    private void UploadTool(ToolGeometrySnapshot snap)
    {
        if (_gl == null) return;
        if (snap.Sections == null || snap.Sections.Count == 0 || !snap.IsValid) return;

        int n = snap.Sections.Count;
        // 2 vertices por seccion (Left, Right) * 2 floats por vertice.
        int needFloats = n * 4;
        EnsureScratch(needFloats);
        for (int i = 0; i < n; i++)
        {
            var s = snap.Sections[i];
            int o = i * 4;
            _scratch[o    ] = (float)s.LeftE;
            _scratch[o + 1] = (float)s.LeftN;
            _scratch[o + 2] = (float)s.RightE;
            _scratch[o + 3] = (float)s.RightN;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _toolVbo);
        if (_toolVboCapacityFloats < needFloats)
        {
            int cap = Math.Max(_toolVboCapacityFloats, 64);
            while (cap < needFloats) cap *= 2;
            _toolVboCapacityFloats = cap;
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
                    (nuint)(needFloats * sizeof(float)),
                    p);
            }
        }
        // Volver al VBO dinamico para que el resto del render siga.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
    }

    private void DrawTool(ToolGeometrySnapshot snap)
    {
        if (_gl == null) return;
        // El layout en _toolVbo es: seccion i -> (Left.E, Left.N, Right.E, Right.N)
        // en offset i*4 floats = i*2 vertices. DrawArrays(Lines, start=i*2, count=2)
        // dibuja un segmento por call. Batch por color para minimizar
        // cambios de uniform: agrupamos las secciones por estado y emitimos
        // un draw por color que use DrawArraysIndirect... no, mantengamoslo
        // simple: 1 segmento = 1 DrawArrays. N max es ~16 sections; 16
        // draws/frame es nada.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _toolVbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
        }

        // Lineas gruesas para que se vean a varios zooms. Algunos drivers
        // (especialmente core profile) capean line width en 1.0 — no es
        // critico: el segmento sigue ahi, solo mas finito. En Stage 4
        // refinado podemos cambiar a quads triangulados con ancho real.
        _gl.LineWidth(3.0f);

        var sections = snap.Sections;
        for (int i = 0; i < sections!.Count; i++)
        {
            var s = sections[i];
            float[] col = SectionColor(s);
            _gl.Uniform4(_uColor, col[0], col[1], col[2], col[3]);
            _gl.DrawArrays(PrimitiveType.Lines, i * 2, 2);
        }

        _gl.LineWidth(1.0f);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0);
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

    private void DrawTractor(double e, double n, double headingRad, double scale)
    {
        if (_gl == null) return;
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
