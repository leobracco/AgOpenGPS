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
    /// <summary>Desde el último snapshot recibido. Ver MaxEdadSnap.</summary>
    private readonly System.Diagnostics.Stopwatch _relojSnap =
        System.Diagnostics.Stopwatch.StartNew();

    // Detección de "composición muerta" por tormenta de context-lost.
    private long _perdidasVistas;
    private DateTime _ultimaVentanaTormenta = DateTime.UtcNow;
    private const long UmbralTormenta = 40;

    /// <summary>
    /// Ciclos sin un solo frame antes de dar el contexto por muerto. Con el
    /// tick de 1 s, dos strikes son ~2 s de negro antes de reaccionar.
    ///
    /// Eran 3 strikes de 4 s = 12 s, y 12 segundos de pantalla negra en el
    /// tractor no son un detalle: el operario asume que la app se colgó y la
    /// mata. La demora nunca fue una restricción técnica, era este número.
    ///
    /// Bajarlo es seguro porque ahora Vigilar() exige además que los datos
    /// estén FRESCOS (ver MaxEdadSnap): si el que se trabó es el poller y no
    /// el render, que no haya frames es lo correcto y no cuenta como falla.
    /// </summary>
    private const int StrikesParaRehacer = 2;

    /// <summary>
    /// Si el último snapshot es más viejo que esto, el problema no es el mapa:
    /// es que no están llegando datos. Sin este freno, con el tractor parado
    /// (que apaga _tickSuave) un hipo del poller se veía igual que un contexto
    /// muerto y disparaba una recreación al pedo — que cuesta su propio negro.
    /// </summary>
    private static readonly TimeSpan MaxEdadSnap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// NO hay tope de intentos. Lo había (5) con la idea de que si no revive a
    /// la quinta el problema es otro — cierto, el problema ES otro: Avalonia
    /// deja de llamar OnOpenGlRender y el contexto GL es del compositor, no de
    /// este control. Pero rendirse no arregla nada y sí empeora todo: medido el
    /// 2026-08-04, a los 5 intentos el mapa quedaba NEGRO PARA SIEMPRE hasta
    /// reiniciar PilotX a mano, y en cabina no hay quien haga eso.
    ///
    /// Reintentar siempre convierte eso en ~12 s de negro por episodio, porque
    /// la surface nueva SÍ vuelve a dibujar (24-39 fps medidos). Con
    /// StrikesParaRehacer el reintento ya viene espaciado ~12 s, así que aunque
    /// falle siempre no hay bucle apretado.
    /// </summary>
    private const int MaxResurrecciones = int.MaxValue;

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
            // Console.Error y no Debug.WriteLine: sin listener de Trace
            // registrado, Debug.WriteLine no llega a ningún lado en Release y
            // desde cabina no hay forma de saber qué render está corriendo.
            // Importa: con Skia no se pinta la cobertura, y "no veo lo
            // trabajado" se explica solo si esta línea está en el log.
            Console.Error.WriteLine("[MapPanel] render del mapa: OpenGL (--gl=on)"
                + (App.DiagSinEncuadre ? "  DIAG: SIN ENCUADRE" : "")
                + (App.DiagSinGeometria ? "  DIAG: SIN GEOMETRIA" : ""));
            ArrancarWatchdog();
        }
        else
        {
            _skia = new MapSkiaSurface();
            Children.Add(_skia);
            Console.Error.WriteLine("[MapPanel] render del mapa: Skia (default; "
                + "GL con --gl=on). Sin cobertura triangulada.");
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
        _relojSnap.Restart();       // frescura de los datos, para el watchdog
        _skia?.OnSnapshot(snap);
        _gl?.OnSnapshot(snap);
    }

    // ---- watchdog -------------------------------------------------------

    private void ArrancarWatchdog()
    {
        if (_watchdog != null) return;
        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
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
        // Datos viejos = el que se trabó es el poller, no el render. Que no
        // haya frames es lo correcto; recrear la surface acá sería cambiar un
        // problema que no tenemos por un negro que sí cuesta.
        if (!IsVisible || _ultimoSnap == null || _relojSnap.Elapsed > MaxEdadSnap)
        {
            _ultimoFrameVisto = gl.FramesRenderizados;
            _strikes = 0;
            return;
        }

        // ---- composición muerta (mapa negro CON frames avanzando) --------
        //
        // Tras un TDR el compositor puede quedar fallando la importación de
        // la textura del mapa ~20/s durante horas: OnOpenGlRender sigue
        // corriendo (los frames avanzan, el chequeo de abajo no salta) pero a
        // la pantalla no llega nada. Visto en vivo el 2026-08-05 (04:02 a
        // 06:49): telemetría fps=22, captura de pantalla en negro. La señal
        // es el contador del espía de FirstChance: si acumula a ritmo de
        // tormenta sostenida, la surface está dibujando a la nada y hay que
        // recrearla. Umbral 40 en ~10 s (la tormenta real es ~200): un
        // parpadeo aislado del driver mete 1-5 y no llega nunca.
        long perdidas = App.PerdidasDeContexto;
        if (_perdidasVistas == 0) _perdidasVistas = perdidas;
        if (perdidas - _perdidasVistas >= UmbralTormenta)
        {
            _perdidasVistas = perdidas;
            _resurrecciones++;
            Console.Error.WriteLine("[MapPanel] TORMENTA de context-lost ("
                + perdidas + " acumuladas): composicion muerta con frames avanzando — "
                + "rehaciendo la surface (intento " + _resurrecciones + ")");
            RehacerSurface();
            return;
        }
        if ((DateTime.UtcNow - _ultimaVentanaTormenta) > TimeSpan.FromSeconds(10))
        {
            _ultimaVentanaTormenta = DateTime.UtcNow;
            _perdidasVistas = perdidas;   // ventana deslizante de ~10 s
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

        // NO hay escalón de "empujón" antes de recrear. Se probó (2026-08-04)
        // despertar al compositor sin destruir el contexto, con cuatro
        // variantes: InvalidateVisual, reasignar RenderTransform, pedir frame
        // explícito y rebote de Opacity. Resultado: 29 intentos, 0 revividos.
        // El compositor no está esperando una invalidación — dejó de atender a
        // ESTE control, y lo único que consigue servicio es un control nuevo.
        // Mantener el escalón solo alargaba cada apagón 1-2 s para nada.
        if (_resurrecciones >= MaxResurrecciones)
        {
            Console.Error.WriteLine(
                "[MapPanel] el contexto GL sigue muerto tras " + MaxResurrecciones +
                " intentos; se deja de reintentar (arrancar con --gl=off usa el render Skia).");
            _watchdog?.Stop();
            return;
        }

        _resurrecciones++;
        // El rastro forense es la clave: qué categoría subió al GL y en qué
        // frame, contra el frame en el que murió. La subida cuyo frame quede
        // pegado al último renderizado es la sospechosa.
        Console.Error.WriteLine("[MapPanel] contexto GL sin frames: rehaciendo la surface (intento "
                                + _resurrecciones + "). Murio en f" + gl.FramesRenderizados
                                + "; ultimas subidas: " + gl.UltimaSubida
                                + "\n  caja negra (ultimos frames):" + gl.VolcarCajaNegra());
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
            // Apagar ANTES de sacarla del árbol. Remove() sola no la mata: sus
            // dos DispatcherTimer siguen agendados y con ellos queda viva la
            // surface entera — el _tickSuave de 30 Hz pidiendo frames de un
            // control que ya no se dibuja. Se vio en el log del 2026-08-04:
            // dos latidos alternados, el jubilado en fps=0 con edadFix
            // creciendo. Con MaxResurrecciones=5 eso son hasta 5 zombis.
            if (vieja != null)
            {
                vieja.Apagar();
                Children.Remove(vieja);
            }
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
        nueva.SetLightbarVisible(_lightbarOn);
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

    /// <summary>Prende/apaga el lightbar GL (lo apaga el cluster del piloto,
    /// que muestra el mismo XTE en número justo en esa franja).</summary>
    public void SetLightbarVisible(bool visible)
    {
        _lightbarOn = visible;
        _gl?.SetLightbarVisible(visible);
    }
    private bool _lightbarOn = true;

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
    /// Push de banderas del operario. Especifico de GL — la surface Skia
    /// legacy no las pinta.
    /// </summary>
    public void OnFlags(System.Collections.Generic.List<FlagPoint> flags)
    {
        _gl?.OnFlags(flags);
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

    // Zoom para los botones +/− de pantalla: la cabina es táctil y no tiene
    // rueda. Mismo paso que un tick de rueda para que se sienta igual.
    public void ZoomIn() { _gl?.ZoomBy(1.25); }
    public void ZoomOut() { _gl?.ZoomBy(1.0 / 1.25); }

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
