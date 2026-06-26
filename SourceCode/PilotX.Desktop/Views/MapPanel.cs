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

    public MapPanel()
    {
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
    /// Push de la geometria de tram (Stage 4b, wheel tracks + bnd).
    /// Ambas surfaces pintan tramlines + outer/inner segun displayMode.
    /// </summary>
    public void OnTram(TramGeometrySnapshot snap)
    {
        _gl?.OnTram(snap);
        _skia?.OnTram(snap);
    }
}
