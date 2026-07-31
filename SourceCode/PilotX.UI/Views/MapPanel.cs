// MapPanel.cs
//
// Wrapper Grid del mapa principal. Stage 1 de la migracion OpenGL: hostea
// internamente una surface Skia (legacy MapSkiaSurface) o una surface GL
// (nueva MapGlSurface) segun `App.UseGl`.
//
// La API publica del control se mantiene IDENTICA a la version previa
// (`OnSnapshot(HudSnapshot)`) para no obligar a tocar MainWindow ni el
// XAML. Lo que cambia es solamente quien renderea adentro: cuando
// PilotX arranca con `--gl=on` se usa MapGlSurface (OpenGlControlBase
// de Avalonia + bindings Silk.NET.OpenGL); sin el flag, sigue el render
// Skia 2D que ya validamos en cabina.
//
// Toggle CLI (default OFF mientras estabilizamos GL):
//   PilotX.Desktop.exe --gl=on        -> usa MapGlSurface
//   PilotX.Desktop.exe                -> sigue Skia (sin riesgo)
//
// Cuando MapGlSurface llegue a paridad visual con el render OpenGL de
// FormGPS legacy (stages 2..6), el toggle desaparece y MapSkiaSurface
// se retira.

using System;
using Avalonia.Controls;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

/// <summary>
/// Vista principal del mapa de guiado. Wrapper que delega el render a
/// una surface interna (Skia o GL).
/// </summary>
public sealed class MapPanel : Grid
{
    private readonly MapSkiaSurface? _skia;
    private MapGlSurface? _gl;

    // ---- watchdog del contexto GL --------------------------------------
    //
    // Observado en cabina (2026-07-29): el mapa se queda congelado y NO vuelve
    // nunca. El latido de MapGlSurface lo dejó claro — fps=0 con pausado=False,
    // el tick corriendo, los fixes llegando frescos y el hilo de UI vivo: o sea
    // RequestNextFrameRendering se llama y Avalonia jamás invoca OnOpenGlRender.
    // Traer la ventana al frente tampoco lo revive.
    //
    // Es pérdida del contexto GL (ANGLE/D3D pierde el device por suspensión,
    // reset de driver o cambio de pantalla) que OpenGlControlBase de Avalonia
    // 11.2.3 no recupera solo. AgOpenWeb llegó a la misma conclusión por otro
    // camino y por eso mantiene su render 2D como baseline indefinido.
    //
    // Acá se detecta y se REHACE la surface: un control nuevo fuerza un
    // OnOpenGlInit nuevo y con eso un contexto nuevo. La cobertura, las guías y
    // el resto vuelven solos con el próximo poll; los sprites hay que
    // reaplicarlos, por eso se guardan.
    private DispatcherTimer? _watchdog;
    private int _ultimoFrameVisto = -1;
    private int _strikes;
    private int _resurrecciones;

    /// <summary>Ciclos sin un solo frame antes de dar el contexto por muerto.
    /// Tres a 4 s da ~12 s de gracia: suficiente para no confundirlo con una
    /// pausa legítima o un hipo del compositor.</summary>
    private const int StrikesParaRehacer = 3;

    /// <summary>Tope de intentos. Si con esto no revive, el problema es otro y
    /// seguir recreando controles solo agrega ruido.</summary>
    private const int MaxResurrecciones = 5;

    // Últimos sprites empujados, para poder reaplicarlos a la surface nueva.
    private byte[]? _spVeh; private int _spVehW, _spVehH;
    private byte[]? _spRueda; private int _spRuedaW, _spRuedaH;
    private byte[]? _spImpl; private int _spImplW, _spImplH;
    private byte[]? _spPiso; private int _spPisoW, _spPisoH;
    private HudSnapshot? _ultimoSnap;

    // Estado del pan (arrastre). El input del mapa se maneja ACÁ (en el Grid
    // contenedor) porque OpenGlControlBase no recibe eventos de puntero de
    // forma confiable; con Background=Transparent el Grid sí los recibe.
    private bool _isPanning;
    private Avalonia.Point _lastPointer;

    /// <summary>Se dispara cuando el mapa se tapa (false) o vuelve (true).</summary>
    public event Action<bool>? VisibilidadCambiada;

    public MapPanel()
    {
        Background = Avalonia.Media.Brushes.Transparent;
        if (App.UseGl)
        {
            _gl = new MapGlSurface();
            Children.Add(_gl);
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] MapPanel -> GL surface");
            ArrancarWatchdog();
        }
        else
        {
            _skia = new MapSkiaSurface();
            Children.Add(_skia);
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] MapPanel -> Skia surface");
        }
    }

    /// <summary>
    /// Push de snapshot. Llamar desde UI thread. Se proxia a la surface
    /// activa (Skia o GL); el desactivado nunca recibe datos.
    /// </summary>
    public void OnSnapshot(HudSnapshot snap)
    {
        // Misma red de seguridad que ReconciliarMapa en MainWindow, pero para la
        // surface: si el mapa está a la vista no puede quedar pausado, se haya
        // perdido el evento de visibilidad que se haya perdido. Reanudar() no
        // hace nada si ya está corriendo.
        if (IsVisible) _gl?.Reanudar();

        _ultimoSnap = snap;
        _skia?.OnSnapshot(snap);
        _gl?.OnSnapshot(snap);
    }

    // ---- watchdog -------------------------------------------------------

    private void ArrancarWatchdog()
    {
        if (_watchdog != null) return;
        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _watchdog.Tick += (_, _) => Vigilar();
        _watchdog.Start();
    }

    private void Vigilar()
    {
        var gl = _gl;
        if (gl == null) return;

        // Solo cuenta como falla si el mapa DEBERÍA estar dibujando: visible,
        // sin pausa y con datos llegando. Si no, que no haya frames es lo
        // correcto y no hay nada que arreglar.
        if (!IsVisible || _ultimoSnap == null)
        {
            _ultimoFrameVisto = gl.FramesRenderizados;
            _strikes = 0;
            return;
        }

        int frames = gl.FramesRenderizados;
        if (frames != _ultimoFrameVisto)
        {
            _ultimoFrameVisto = frames;
            _strikes = 0;
            return;
        }

        _strikes++;
        if (_strikes < StrikesParaRehacer) return;

        _strikes = 0;
        if (_resurrecciones >= MaxResurrecciones)
        {
            Console.Error.WriteLine(
                "[MapPanel] el contexto GL sigue muerto tras " + MaxResurrecciones +
                " intentos; se deja de reintentar (arrancar con --gl=off usa el render Skia).");
            _watchdog?.Stop();
            return;
        }

        _resurrecciones++;
        Console.Error.WriteLine("[MapPanel] contexto GL sin frames: rehaciendo la surface (intento "
                                + _resurrecciones + ")");
        RehacerSurface();
    }

    /// <summary>
    /// Tira la surface muerta y monta una nueva. El control nuevo dispara su
    /// propio OnOpenGlInit, que es lo único que consigue un contexto GL nuevo.
    /// </summary>
    private void RehacerSurface()
    {
        var vieja = _gl;
        try
        {
            if (vieja != null) Children.Remove(vieja);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MapPanel] no se pudo sacar la surface vieja: " + ex.Message);
        }

        var nueva = new MapGlSurface();
        _gl = nueva;
        Children.Add(nueva);
        _ultimoFrameVisto = -1;

        // Reaplicar lo que no vuelve solo. La cobertura, las guías, el tram y
        // los caminos los reenvían sus pollers en el próximo ciclo.
        if (_spVeh != null) nueva.SetVehicleSprite(_spVeh, _spVehW, _spVehH);
        if (_spRueda != null) nueva.SetWheelSprite(_spRueda, _spRuedaW, _spRuedaH);
        if (_spImpl != null) nueva.SetImplementSprite(_spImpl, _spImplW, _spImplH);
        if (_spPiso != null) nueva.SetFloorTexture(_spPiso, _spPisoW, _spPisoH);
        if (_ultimoSnap != null) nueva.OnSnapshot(_ultimoSnap);
        // La prescripción NO vuelve sola: su poller solo re-empuja cuando la
        // clave cambia, y recrear la surface no cambia ninguna clave. Sin esto,
        // tras una recreación del GL el shape desaparecía del mapa sin error —
        // fue exactamente el "no se ve nada" de cabina: el latido de la surface
        // vieja seguía mostrando 3 zonas mientras la visible no tenía ninguna.
        if (_ultimoShape != null) nueva.OnShape(_ultimoShape);
    }

    /// <summary>
    /// Push del worked area triangulado (Stage 2). Solo lo consume la
    /// surface GL — la Skia legacy no pinta coverage (volumen de
    /// triangulos no compite con DrawingContext). Cuando UseGl=off
    /// esta llamada es un no-op (no se instancia el poller).
    /// </summary>
    public void OnCoverage(CoverageSnapshot snap)
    {
        _gl?.OnCoverage(snap);
    }

    /// <summary>
    /// Sprite del vehículo elegido en Configuración. Solo lo dibuja el renderer
    /// GL; con --gl=off (Skia) es no-op y se sigue viendo el triángulo.
    /// </summary>
    public void SetVehicleSprite(byte[]? rgba, int width, int height)
    {
        _spVeh = rgba; _spVehW = width; _spVehH = height;
        _gl?.SetVehicleSprite(rgba, width, height);
    }

    /// <summary>Textura de la rueda delantera (se dibuja girada por direccion).</summary>
    public void SetWheelSprite(byte[]? rgba, int width, int height)
    {
        _spRueda = rgba; _spRuedaW = width; _spRuedaH = height;
        _gl?.SetWheelSprite(rgba, width, height);
    }

    /// <summary>Sprite del implemento (sembradora, etc.).</summary>
    public void SetImplementSprite(byte[]? rgba, int width, int height)
    {
        _spImpl = rgba; _spImplW = width; _spImplH = height;
        _gl?.SetImplementSprite(rgba, width, height);
    }

    /// <summary>Textura del piso (fondo del mapa, tileada en mundo). Cacheada
    /// acá como los sprites: el watchdog GL recrea la surface y toda capa que
    /// no se re-aplique desaparece sin error.</summary>
    public void SetFloorTexture(byte[]? rgba, int width, int height)
    {
        _spPiso = rgba; _spPisoW = width; _spPisoH = height;
        _gl?.SetFloorTexture(rgba, width, height);
    }

    /// <summary>
    /// Push de la polyline de guidance (Stage 3, AB/Curve/Contour).
    /// Ambas surfaces pintan la linea cian — Skia con StreamGeometry
    /// (paridad parcial para que --gl=off no pierda referencia visual).
    /// </summary>
    public void OnGuidance(GuidanceGeometrySnapshot snap)
    {
        _gl?.OnGuidance(snap);
        _skia?.OnGuidance(snap);
    }

    /// <summary>
    /// Push de la geometria del implemento (Stage 4a, secciones).
    /// Ambas surfaces pintan la barra coloreada por estado.
    /// </summary>
    public void OnTool(ToolGeometrySnapshot snap)
    {
        _gl?.OnTool(snap);
        _skia?.OnTool(snap);
    }

    /// <summary>
    /// Frena el render del mapa mientras otra pantalla lo tapa. La surface Skia
    /// no tiene tick propio (redibuja por snapshot), así que solo aplica a GL.
    /// </summary>
    public void Pausar() => _gl?.Pausar();

    /// <summary>Reanuda el render al volver al mapa.</summary>
    public void Reanudar() => _gl?.Reanudar();

    /// <summary>
    /// El mapa se pausa solo cuando lo tapan. Va acá y no en cada pantalla que
    /// lo oculta porque son ~14 lugares que hacen `_mapHost.IsVisible = false`:
    /// atarlo a la propiedad cubre todos, incluidos los que se agreguen después,
    /// sin depender de que alguien se acuerde de llamar a Pausar().
    /// </summary>
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            // La propiedad ya está actualizada cuando llega esta notificación.
            bool visible = IsVisible;
            if (visible) Reanudar();
            else Pausar();
            // El dueño (MainWindow) usa esto para frenar además los pollers que
            // solo alimentan al mapa; desde acá no los conocemos.
            VisibilidadCambiada?.Invoke(visible);
        }
    }

    // ---- Creación de AB en el mapa (toco A, manejo, toco B) ----
    public void BeginAbCreation() => _gl?.BeginAbCreation();
    public void SetAbPointA(double e, double n) => _gl?.SetAbPointA(e, n);
    public void EndAbCreation() => _gl?.EndAbCreation();

    /// <summary>
    /// Push de la geometria de tram (Stage 4b, wheel tracks + bnd).
    /// Ambas surfaces pintan tramlines + outer/inner segun displayMode.
    /// </summary>
    public void OnTram(TramGeometrySnapshot snap)
    {
        _gl?.OnTram(snap);
        _skia?.OnTram(snap);
    }

    /// <summary>
    /// Push de la geometria de caminos (Stage 5): youturn (giro de cabecera)
    /// + recorded path. Especifico de GL — la surface Skia legacy no pinta
    /// estos caminos.
    /// </summary>
    public void OnPaths(PathsGeometrySnapshot snap)
    {
        _gl?.OnPaths(snap);
    }

    /// <summary>
    /// Push de la prescripción (.shp): zonas con color por dosis. Específico
    /// de GL. null = se descargó el shape.
    /// </summary>
    public void OnShape(ShapeMapSnapshot? snap)
    {
        _ultimoShape = snap;
        _gl?.OnShape(snap);
    }

    // Última prescripción empujada, para reaplicarla si la surface se recrea.
    private ShapeMapSnapshot? _ultimoShape;

    // ---- vista de cámara (menú Navegación): 2D/3D/Norte 2D/tilt/grilla/día-noche.
    // No-op en la surface Skia legacy (_gl==null) — mismo criterio que
    // BeginAbCreation/OnCoverage/etc.
    public void SetHeadingUp(bool v) => _gl?.SetHeadingUp(v);
    public void SetPitchDeg(double deg) => _gl?.SetPitchDeg(deg);
    public void TiltBy(double deltaDeg) => _gl?.TiltBy(deltaDeg);
    public void ToggleGrid() => _gl?.ToggleGrid();
    public void ToggleDayNight() => _gl?.ToggleDayNight();

    // ---- input de cámara: zoom (rueda) / pan (arrastre) / reset (2 clicks) --

    protected override void OnPointerWheelChanged(Avalonia.Input.PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_gl == null) return;
        _gl.ZoomBy(e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12);
        e.Handled = true;
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_gl == null) return;
        if (e.ClickCount >= 2) { _gl.ResetCamera(); e.Handled = true; return; }
        _isPanning = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isPanning || _gl == null) return;
        var p = e.GetPosition(this);
        double rs = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _gl.PanByPixels(p.X - _lastPointer.X, p.Y - _lastPointer.Y, rs);
        _lastPointer = p;
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isPanning = false;
        e.Pointer.Capture(null);
    }
}
