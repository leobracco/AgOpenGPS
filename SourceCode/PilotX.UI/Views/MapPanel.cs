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
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

/// <summary>
/// Vista principal del mapa de guiado. Wrapper que delega el render a
/// una surface interna (Skia o GL).
/// </summary>
public sealed class MapPanel : Grid
{
    private readonly MapSkiaSurface? _skia;
    private readonly MapGlSurface? _gl;

    // Estado del pan (arrastre). El input del mapa se maneja ACÁ (en el Grid
    // contenedor) porque OpenGlControlBase no recibe eventos de puntero de
    // forma confiable; con Background=Transparent el Grid sí los recibe.
    private bool _isPanning;
    private Avalonia.Point _lastPointer;

    public MapPanel()
    {
        Background = Avalonia.Media.Brushes.Transparent;
        if (App.UseGl)
        {
            _gl = new MapGlSurface();
            Children.Add(_gl);
            System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] MapPanel -> GL surface");
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
        _skia?.OnSnapshot(snap);
        _gl?.OnSnapshot(snap);
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
        _gl?.SetVehicleSprite(rgba, width, height);
    }

    /// <summary>Textura de la rueda delantera (se dibuja girada por direccion).</summary>
    public void SetWheelSprite(byte[]? rgba, int width, int height)
    {
        _gl?.SetWheelSprite(rgba, width, height);
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
