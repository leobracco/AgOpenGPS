// MapasPanel.axaml.cs
//
// Reemplazo nativo de pages/mapas.html + js/mapas.js — el preview de los mapas
// del lote: heatmap y puntos por surco de una sesión VistaX, más el boundary y
// la cabecera del lote.
//
// Port LITERAL de la lógica de la página:
//   · MISMOS endpoints (GET /api/mapas/sesiones · /sesion/{ts}/heatmap ·
//     /sesion/{ts}/puntos · /boundary · /headland) y el mismo tratamiento del
//     404: la capa simplemente no se dibuja, sin cartel de error.
//   · MISMO arranque: primero las capas del lote, después la lista de sesiones
//     (la más reciente queda elegida), después las capas de esa sesión, y el
//     encuadre inicial prioriza la SESIÓN con el lote de respaldo.
//   · MISMO comportamiento del desplegable: al cambiar de sesión se vuelve a
//     pedir la lista (para la meta), se recargan heatmap y puntos y se encuadra
//     a la sesión (o al lote si la sesión no tiene bbox).
//   · MISMOS defaults de las capas: heatmap y boundary encendidos, puntos y
//     cabecera apagados; el toggle NO recarga nada, solo muestra/esconde.
//   · MISMOS colores de las 4 clases del heatmap, del boundary (blanco), de la
//     cabecera (gris punteado) y de los puntos (verde / rojo si hay alerta).
//   · MISMO tooltip del punto: "surco N · SPM X" con SPM redondeado igual que
//     toFixed(0) de JavaScript.
//
// Leaflet se reemplaza por MapaLienzo (abajo en este mismo archivo): proyecta
// en Web Mercator igual que Leaflet y arrastra/acerca con el dedo. NO es el
// mapa GL de la cabina — es dibujo adentro de la card, así que no pelea con el
// compositor y el mapa vivo sigue detrás.
//
// La página HTML queda intacta: la usa la PWA del celular.
//
// API: Attach(MapasClient) arranca la carga; Detach() corta las llamadas en
// curso y cierra el selector. Sin polling — el original tampoco lo tiene.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class MapasPanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private MapasClient? _client;
    private CancellationTokenSource? _cts;

    private List<MapaSesionWire> _sesiones = new();
    private MapaSesionWire? _sesionActual;
    private double[]? _loteBbox;
    private double[]? _sesionBbox;

    // ---- paleta clara (tokens PilotXPanel* del theme) ----------------------
    private static readonly IBrush Superficie = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BordeAlto  = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoTenue = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush VerdeSuave = new SolidColorBrush(Color.Parse("#E8F4E5"));
    private static readonly IBrush VerdeTexto = new SolidColorBrush(Color.Parse("#2F7A26"));

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public MapasPanel()
    {
        InitializeComponent();

        // Estado inicial del lienzo = estado inicial de los checkboxes.
        var lienzo = this.FindControl<MapaLienzo>("Lienzo");
        if (lienzo != null)
        {
            lienzo.SetVisible("heatmap",  true);
            lienzo.SetVisible("puntos",   false);
            lienzo.SetVisible("boundary", true);
            lienzo.SetVisible("headland", false);
        }

        // El diccionario se aplica UNA SOLA VEZ, al construir (lección de
        // NodosPanel: llamarlo en cada render congela los textos dinámicos).
        // Todo lo que escribe el código pasa por T().
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(MapasClient client)
    {
        _client = client;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = BootAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        CerrarPicker();
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    // =========================================================================
    //  boot() del JS
    // =========================================================================

    private async Task BootAsync(CancellationToken ct)
    {
        if (_client == null) return;
        await CargarCapasLoteAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        await CargarSesionesAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        if (_sesionActual != null)
            await CargarCapasSesionAsync(_sesionActual.Ts ?? "", ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        // Fit inicial: prioridad a la sesión activa, fallback al lote.
        Encuadrar(_sesionBbox ?? _loteBbox);
    }

    // ------------------------------------------------------------- capas lote

    private async Task CargarCapasLoteAsync(CancellationToken ct)
    {
        if (_client == null) return;
        _loteBbox = null;

        var b = await _client.GetBoundaryAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        SetCapa("boundary", b);

        var h = await _client.GetHeadlandAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        SetCapa("headland", h);

        _loteBbox = MergeBbox(b?.Bbox, h?.Bbox);
    }

    // ---------------------------------------------------------- capas sesión

    private async Task CargarCapasSesionAsync(string ts, CancellationToken ct)
    {
        if (_client == null) return;
        _sesionBbox = null;

        if (string.IsNullOrEmpty(ts))
        {
            // Limpiar capas de sesión (igual que el JS con ts vacío).
            SetCapa("heatmap", null);
            SetCapa("puntos", null);
            return;
        }

        var h = await _client.GetHeatmapAsync(ts, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        SetCapa("heatmap", h);

        var p = await _client.GetPuntosAsync(ts, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        SetCapa("puntos", p);

        _sesionBbox = MergeBbox(h?.Bbox, p?.Bbox);
    }

    // -------------------------------------------------------------- sesiones

    private async Task CargarSesionesAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var data = await _client.GetSesionesAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        // 'Lote · ' + (data && data.lote ? data.lote : '–')
        SetTexto("LoteSub", T("Lote") + " · " +
            (data != null && !string.IsNullOrEmpty(data.Lote) ? data.Lote! : "–"));

        var lista = data?.Sesiones;
        if (lista == null || lista.Count == 0)
        {
            _sesiones = new List<MapaSesionWire>();
            _sesionActual = null;
            SetBotonSesion(T("— sin sesiones —"));
            SetTexto("SesionMeta", T("No hay sesiones VistaX para este lote."));
            ArmarPickerSesiones();
            return;
        }

        _sesiones = lista;
        // Default = la más reciente (primera de la lista, viene ordenada desc).
        _sesionActual = lista[0];
        SetBotonSesion(Etiqueta(lista[0]));
        ArmarPickerSesiones();
        ActualizarMetaSesion();
    }

    /// <summary>`s.fecha_iso || s.ts` — la etiqueta de cada &lt;option&gt;.</summary>
    private static string Etiqueta(MapaSesionWire s)
        => !string.IsNullOrEmpty(s.FechaIso) ? s.FechaIso! : (s.Ts ?? "");

    private void ActualizarMetaSesion()
    {
        var s = _sesionActual;
        if (s == null) { SetTexto("SesionMeta", "–"); return; }

        var partes = new List<string>();
        if (s.HasHeatmap)
            partes.Add(s.HeatmapCeldas.ToString(CultureInfo.InvariantCulture) + " " + T("celdas heatmap"));
        if (s.HasPuntos)
            partes.Add(s.Puntos.ToString(CultureInfo.InvariantCulture) + " " + T("puntos"));
        SetTexto("SesionMeta", partes.Count > 0 ? string.Join(" · ", partes) : T("sin datos exportados"));
    }

    // =========================================================================
    //  selector de sesión (el <select> del original)
    // =========================================================================

    private void ArmarPickerSesiones()
    {
        var host = this.FindControl<StackPanel>("PickerLista");
        if (host == null) return;
        host.Children.Clear();

        if (_sesiones.Count == 0)
        {
            // Única opción del <select>; no se puede "elegir" nada.
            host.Children.Add(new TextBlock
            {
                Text = T("— sin sesiones —"),
                Foreground = TextoTenue,
                FontSize = 14,
                Margin = new Thickness(4, 10, 4, 10)
            });
            return;
        }

        string activo = _sesionActual?.Ts ?? "";
        foreach (var s in _sesiones)
        {
            string ts = s.Ts ?? "";
            string etiqueta = Etiqueta(s);
            bool esActual = string.Equals(ts, activo, StringComparison.Ordinal);
            var b = new Button
            {
                Content = etiqueta,
                Background = esActual ? VerdeSuave : Superficie,
                Foreground = esActual ? VerdeTexto : Texto,
                BorderBrush = esActual ? VerdeTexto : BordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 48,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Click += (_, __) =>
            {
                CerrarPicker();
                SetBotonSesion(etiqueta);
                _ = CambiarSesionAsync(ts);
            };
            host.Children.Add(b);
        }
    }

    /// <summary>El handler del `change` del &lt;select&gt;: vuelve a pedir la
    /// lista para tener la meta actualizada, recarga las capas de la sesión y
    /// encuadra.</summary>
    private async Task CambiarSesionAsync(string ts)
    {
        if (_client == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;

        var data = await _client.GetSesionesAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        _sesionActual = null;
        if (data?.Sesiones != null)
            foreach (var s in data.Sesiones)
                if (string.Equals(s.Ts, ts, StringComparison.Ordinal)) { _sesionActual = s; break; }

        ActualizarMetaSesion();
        ArmarPickerSesiones();
        await CargarCapasSesionAsync(ts, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        Encuadrar(_sesionBbox ?? _loteBbox);
    }

    private void OnSesionClick(object? sender, RoutedEventArgs e)
    {
        var ov = this.FindControl<Border>("PickerOverlay");
        if (ov != null) ov.IsVisible = true;
    }

    private void OnPickerCancelarClick(object? sender, RoutedEventArgs e) => CerrarPicker();
    private void OnPickerBackdropPressed(object? sender, PointerPressedEventArgs e) => CerrarPicker();
    private void OnPickerCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void CerrarPicker()
    {
        var ov = this.FindControl<Border>("PickerOverlay");
        if (ov != null) ov.IsVisible = false;
    }

    // =========================================================================
    //  capas / vista
    // =========================================================================

    private void OnCapaCambio(object? sender, RoutedEventArgs e)
    {
        var lienzo = this.FindControl<MapaLienzo>("Lienzo");
        if (lienzo == null) return;
        lienzo.SetVisible("heatmap",  this.FindControl<CheckBox>("LayHeatmap")?.IsChecked == true);
        lienzo.SetVisible("puntos",   this.FindControl<CheckBox>("LayPuntos")?.IsChecked == true);
        lienzo.SetVisible("boundary", this.FindControl<CheckBox>("LayBoundary")?.IsChecked == true);
        lienzo.SetVisible("headland", this.FindControl<CheckBox>("LayHeadland")?.IsChecked == true);
    }

    private void OnFitLoteClick(object? sender, RoutedEventArgs e)
        => Encuadrar(_loteBbox ?? _sesionBbox);

    private void OnFitSesionClick(object? sender, RoutedEventArgs e)
        => Encuadrar(_sesionBbox ?? _loteBbox);

    private void OnZoomMasClick(object? sender, RoutedEventArgs e)
        => this.FindControl<MapaLienzo>("Lienzo")?.Zoom(2.0);

    private void OnZoomMenosClick(object? sender, RoutedEventArgs e)
        => this.FindControl<MapaLienzo>("Lienzo")?.Zoom(0.5);

    private void SetCapa(string nombre, MapaCapa? capa)
        => this.FindControl<MapaLienzo>("Lienzo")?.SetCapa(nombre, capa);

    private void Encuadrar(double[]? bbox)
        => this.FindControl<MapaLienzo>("Lienzo")?.Encuadrar(bbox);

    /// <summary>mergeBbox() del JS.</summary>
    private static double[]? MergeBbox(double[]? a, double[]? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return new[]
        {
            Math.Min(a[0], b[0]), Math.Min(a[1], b[1]),
            Math.Max(a[2], b[2]), Math.Max(a[3], b[3]),
        };
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    private void SetTexto(string nombre, string texto)
    {
        var tb = this.FindControl<TextBlock>(nombre);
        if (tb != null) tb.Text = texto;
    }

    private void SetBotonSesion(string texto)
    {
        var b = this.FindControl<Button>("BtnSesion");
        if (b != null) b.Content = texto;
    }
}

// =============================================================================
//  MapaLienzo — el <div id="mapaLeaflet"> del original.
//
//  Dibuja las 4 capas en coordenadas WGS84 proyectadas a Web Mercator (la misma
//  proyección EPSG:3857 que usa Leaflet, así que las formas se ven idénticas) y
//  maneja los gestos: un dedo arrastra, dos dedos hacen pinza, la rueda acerca
//  sobre el cursor. Los botones +/− de la card son el zoomControl de Leaflet.
//
//  Los colores son EXACTAMENTE los del mapas.js (son código de dato, no
//  decoración): clase 1..4 del heatmap, boundary blanco, cabecera gris
//  punteada, punto verde o rojo si trae alerta.
// =============================================================================
public sealed class MapaLienzo : Control
{
    // ---- capas -------------------------------------------------------------
    private MapaCapa? _heatmap, _puntos, _boundary, _headland;
    private bool _verHeatmap = true, _verPuntos, _verBoundary = true, _verHeadland;

    // ---- vista (centro proyectado + px por unidad de mercator) -------------
    private double _cx, _cy, _escala = 1;
    private bool _hayVista;
    private double[]? _bboxPendiente;   // encuadre pedido antes del primer layout

    // ---- gestos ------------------------------------------------------------
    private readonly Dictionary<int, Point> _punteros = new();
    private (Point origen, double cx, double cy)? _arrastre;
    private (double d, double escala, double cx, double cy, Point centro)? _pinza;
    /// <summary>El dedo se movió más de 6 px: fue arrastre, no toque.</summary>
    private bool _movio;

    // ---- tooltip del punto (bindTooltip sticky de Leaflet) -----------------
    private string _tooltip = "";
    private Point _tooltipPos;

    // ---- pinceles (mismos colores que mapas.js) ----------------------------
    private static readonly IBrush FondoMapa = new SolidColorBrush(Color.Parse("#16181A"));

    // fillOpacity 0.75 del heatmap → alpha 191.
    private static readonly IBrush Clase1 = new SolidColorBrush(Color.FromArgb(191, 0x2e, 0xcc, 0x40));
    private static readonly IBrush Clase2 = new SolidColorBrush(Color.FromArgb(191, 0xff, 0xdc, 0x00));
    private static readonly IBrush Clase3 = new SolidColorBrush(Color.FromArgb(191, 0xff, 0x85, 0x1b));
    private static readonly IBrush Clase4 = new SolidColorBrush(Color.FromArgb(191, 0xff, 0x41, 0x36));
    private static readonly IBrush ClaseX = new SolidColorBrush(Color.FromArgb(191, 0x7d, 0x8b, 0x80));

    // fillOpacity 0.85 de los puntos → alpha 217.
    private static readonly IBrush PuntoOk    = new SolidColorBrush(Color.FromArgb(217, 0x4B, 0xA6, 0x3F));
    private static readonly IBrush PuntoAlrt  = new SolidColorBrush(Color.FromArgb(217, 0xff, 0x41, 0x36));

    // weight 2 / opacity 0.9 del boundary; weight 1.5 / opacity 0.8 y dash 4 4
    // de la cabecera.
    private static readonly IPen PenBoundary = new Pen(
        new SolidColorBrush(Color.FromArgb(230, 0xff, 0xff, 0xff)), 2);
    private static readonly IPen PenHeadland = new Pen(
        new SolidColorBrush(Color.FromArgb(204, 0x7d, 0x8b, 0x80)), 1.5,
        new DashStyle(new double[] { 4, 4 }, 0));

    private static readonly IBrush TooltipFondo = new SolidColorBrush(Color.FromArgb(217, 0x10, 0x16, 0x12));
    private static readonly IBrush TooltipBorde = new SolidColorBrush(Color.FromArgb(60, 0xff, 0xff, 0xff));
    private static readonly IBrush TooltipTexto = new SolidColorBrush(Color.Parse("#F5F7F4"));

    /// <summary>Límite de latitud de Web Mercator (el de Leaflet).</summary>
    private const double MaxLat = 85.0511287798;

    public MapaLienzo()
    {
        ClipToBounds = true;
        SizeChanged += (_, __) =>
        {
            // El primer layout llega con Bounds en cero: recién ahí se puede
            // encuadrar de verdad.
            if (_bboxPendiente != null) Encuadrar(_bboxPendiente);
            InvalidateVisual();
        };
    }

    // =========================================================================
    //  API del panel
    // =========================================================================

    public void SetCapa(string nombre, MapaCapa? capa)
    {
        switch (nombre)
        {
            case "heatmap":  _heatmap = capa;  break;
            case "puntos":   _puntos = capa;   break;
            case "boundary": _boundary = capa; break;
            case "headland": _headland = capa; break;
        }
        if (nombre == "puntos") _tooltip = "";
        InvalidateVisual();
    }

    public void SetVisible(string nombre, bool visible)
    {
        switch (nombre)
        {
            case "heatmap":  _verHeatmap = visible;  break;
            case "puntos":   _verPuntos = visible;   break;
            case "boundary": _verBoundary = visible; break;
            case "headland": _verHeadland = visible; break;
        }
        if (nombre == "puntos" && !visible) _tooltip = "";
        InvalidateVisual();
    }

    /// <summary>fitBounds(bbox, {padding:[20,20]}) de Leaflet. bbox =
    /// [minLon, minLat, maxLon, maxLat]; null no hace nada (igual que el JS).</summary>
    public void Encuadrar(double[]? bbox)
    {
        if (bbox == null || bbox.Length < 4) return;

        double w = Bounds.Width, h = Bounds.Height;
        if (w < 2 || h < 2) { _bboxPendiente = bbox; return; }
        _bboxPendiente = null;

        double x0 = ProyX(bbox[0]), x1 = ProyX(bbox[2]);
        double y0 = ProyY(bbox[1]), y1 = ProyY(bbox[3]);
        if (x1 < x0) { (x0, x1) = (x1, x0); }
        if (y1 < y0) { (y0, y1) = (y1, y0); }

        double dx = x1 - x0, dy = y1 - y0;
        double util = Math.Max(1, w - 40), utilY = Math.Max(1, h - 40);   // padding [20,20]

        double escala;
        if (dx <= 1e-12 && dy <= 1e-12) escala = 100000;                  // bbox de un punto
        else if (dx <= 1e-12)           escala = utilY / dy;
        else if (dy <= 1e-12)           escala = util / dx;
        else                            escala = Math.Min(util / dx, utilY / dy);

        if (double.IsNaN(escala) || double.IsInfinity(escala) || escala <= 0) return;

        _escala = escala;
        _cx = (x0 + x1) / 2;
        _cy = (y0 + y1) / 2;
        _hayVista = true;
        InvalidateVisual();
    }

    /// <summary>Acerca (&gt;1) o aleja (&lt;1) desde el centro del lienzo.</summary>
    public void Zoom(double factor)
    {
        if (factor <= 0) return;
        _escala *= factor;
        _hayVista = true;
        InvalidateVisual();
    }

    // =========================================================================
    //  proyección
    // =========================================================================

    private static double ProyX(double lon) => lon;

    private static double ProyY(double lat)
    {
        double l = lat > MaxLat ? MaxLat : (lat < -MaxLat ? -MaxLat : lat);
        return 180.0 / Math.PI * Math.Log(Math.Tan(Math.PI / 4 + l * Math.PI / 360.0));
    }

    private Point APantalla(double lon, double lat)
        => new(Bounds.Width / 2 + (ProyX(lon) - _cx) * _escala,
               Bounds.Height / 2 - (ProyY(lat) - _cy) * _escala);

    // =========================================================================
    //  dibujo
    // =========================================================================

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        // Fondo propio: sin él el control no es hit-testable y el arrastre no
        // llegaría nunca.
        ctx.FillRectangle(FondoMapa, new Rect(Bounds.Size));
        if (!_hayVista) return;

        // ORDEN DE PINTADO = orden en que la página agrega las capas al mapa:
        // boundary y cabecera primero (loadLoteLayers), después heatmap y
        // puntos (loadSesionLayers), que por eso quedan arriba.
        if (_verBoundary && _boundary != null) DibujarLineas(ctx, _boundary, PenBoundary);
        if (_verHeadland && _headland != null) DibujarLineas(ctx, _headland, PenHeadland);
        if (_verHeatmap  && _heatmap  != null) DibujarHeatmap(ctx, _heatmap);
        if (_verPuntos   && _puntos   != null) DibujarPuntos(ctx, _puntos);

        if (_verPuntos && _tooltip.Length > 0) DibujarTooltip(ctx);
    }

    /// <summary>Polígonos del heatmap: sin borde (weight 0) y agrupados por
    /// color para no armar miles de geometrías sueltas.</summary>
    private void DibujarHeatmap(DrawingContext ctx, MapaCapa capa)
    {
        var grupos = new Dictionary<int, StreamGeometry>();
        var abiertos = new Dictionary<int, StreamGeometryContext>();
        double w = Bounds.Width, h = Bounds.Height;
        var buffer = new List<Point>(64);

        try
        {
            foreach (var f in capa.Features)
            {
                if (f.Trazos == null || f.Trazos.Count == 0) continue;
                int clave = ClaveClase(f.Clase);

                foreach (var anillo in f.Trazos)
                {
                    if (anillo == null || anillo.Length < 3) continue;

                    // Se proyecta el anillo y, de paso, se mide: las celdas que
                    // caen fuera del visor no entran a la geometría (un heatmap
                    // de una jornada son miles de celdas).
                    buffer.Clear();
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;
                    foreach (var p in anillo)
                    {
                        if (p == null || p.Length < 2) continue;
                        var q = APantalla(p[0], p[1]);
                        buffer.Add(q);
                        if (q.X < minX) minX = q.X;
                        if (q.X > maxX) maxX = q.X;
                        if (q.Y < minY) minY = q.Y;
                        if (q.Y > maxY) maxY = q.Y;
                    }
                    if (buffer.Count < 3) continue;
                    if (maxX < 0 || maxY < 0 || minX > w || minY > h) continue;

                    if (!grupos.TryGetValue(clave, out var geo))
                    {
                        geo = new StreamGeometry();
                        grupos[clave] = geo;
                        abiertos[clave] = geo.Open();
                    }
                    var gc = abiertos[clave];
                    gc.BeginFigure(buffer[0], true);
                    for (int i = 1; i < buffer.Count; i++) gc.LineTo(buffer[i]);
                    gc.EndFigure(true);
                }
            }
        }
        finally
        {
            foreach (var kv in abiertos) kv.Value.Dispose();
        }

        foreach (var kv in grupos)
            ctx.DrawGeometry(PincelClase(kv.Key), null, kv.Value);
    }

    /// <summary>colorClase() del JS: 1 verde, 2 amarillo, 3 naranja, 4 rojo,
    /// cualquier otra cosa gris.</summary>
    private static int ClaveClase(double clase)
    {
        if (double.IsNaN(clase)) return 0;
        int c = (int)clase;
        if (c != clase) return 0;          // Number() no entero → default gris
        return c is 1 or 2 or 3 or 4 ? c : 0;
    }

    private static IBrush PincelClase(int clave) => clave switch
    {
        1 => Clase1,
        2 => Clase2,
        3 => Clase3,
        4 => Clase4,
        _ => ClaseX,
    };

    private void DibujarLineas(DrawingContext ctx, MapaCapa capa, IPen pen)
    {
        var geo = new StreamGeometry();
        bool algo = false;
        using (var gc = geo.Open())
        {
            foreach (var f in capa.Features)
            {
                if (f.Trazos == null) continue;
                // Los anillos del boundary se cierran; una LineString no.
                bool cerrar = f.Tipo == "Polygon";
                foreach (var trazo in f.Trazos)
                {
                    if (trazo == null || trazo.Length < 2) continue;
                    bool abierta = false;
                    foreach (var p in trazo)
                    {
                        if (p == null || p.Length < 2) continue;
                        var q = APantalla(p[0], p[1]);
                        if (!abierta) { gc.BeginFigure(q, false); abierta = true; }
                        else gc.LineTo(q);
                    }
                    if (abierta) { gc.EndFigure(cerrar); algo = true; }
                }
            }
        }
        if (algo) ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>circleMarker radius 2 · verde, o rojo si el punto trae alerta.</summary>
    private void DibujarPuntos(DrawingContext ctx, MapaCapa capa)
    {
        double w = Bounds.Width, h = Bounds.Height;
        foreach (var f in capa.Features)
        {
            if (f.Punto == null || f.Punto.Length < 2) continue;
            var q = APantalla(f.Punto[0], f.Punto[1]);
            if (q.X < -8 || q.Y < -8 || q.X > w + 8 || q.Y > h + 8) continue;
            ctx.DrawEllipse(f.Alerta ? PuntoAlrt : PuntoOk, null, q, 2, 2);
        }
    }

    private void DibujarTooltip(DrawingContext ctx)
    {
        var ft = new FormattedText(
            _tooltip,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            TooltipTexto);

        double pw = ft.Width + 16, ph = ft.Height + 10;
        double x = _tooltipPos.X + 12, y = _tooltipPos.Y - ph - 6;
        if (x + pw > Bounds.Width) x = Bounds.Width - pw - 2;
        if (x < 2) x = 2;
        if (y < 2) y = _tooltipPos.Y + 12;

        var caja = new Rect(x, y, pw, ph);
        ctx.DrawRectangle(TooltipFondo, new Pen(TooltipBorde, 1), caja, 6, 6);
        ctx.DrawText(ft, new Point(x + 8, y + 5));
    }

    // =========================================================================
    //  gestos — mismos que el resto de los lienzos nativos del repo
    // =========================================================================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        _punteros[e.Pointer.Id] = p;

        if (_punteros.Count == 1)
        {
            _arrastre = (p, _cx, _cy);
            _movio = false;
        }
        else if (_punteros.Count == 2)
        {
            _arrastre = null;
            var it = _punteros.Values.GetEnumerator();
            it.MoveNext(); var a = it.Current;
            it.MoveNext(); var b = it.Current;
            _pinza = (Distancia(a, b), _escala, _cx, _cy,
                      new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2));
        }
        try { e.Pointer.Capture(this); } catch { }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (!_punteros.ContainsKey(e.Pointer.Id))
        {
            // Sin dedo apoyado: es el hover del mouse → tooltip sticky.
            ActualizarTooltip(p);
            return;
        }
        _punteros[e.Pointer.Id] = p;

        if (_pinza is { } pz && _punteros.Count == 2)
        {
            var it = _punteros.Values.GetEnumerator();
            it.MoveNext(); var a = it.Current;
            it.MoveNext(); var b = it.Current;
            double factor = Distancia(a, b) / (pz.d <= 0 ? 1 : pz.d);
            if (factor > 0)
            {
                double nuevaEscala = pz.escala * factor;
                // El punto del mapa bajo el centro de la pinza no se mueve.
                double mx = pz.centro.X - Bounds.Width / 2;
                double my = Bounds.Height / 2 - pz.centro.Y;
                _cx = pz.cx + mx / pz.escala - mx / nuevaEscala;
                _cy = pz.cy + my / pz.escala - my / nuevaEscala;
                _escala = nuevaEscala;
                _hayVista = true;
                InvalidateVisual();
            }
            return;
        }

        if (_arrastre is { } ar)
        {
            double dx = p.X - ar.origen.X, dy = p.Y - ar.origen.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 6) _movio = true;
            _cx = ar.cx - dx / _escala;
            _cy = ar.cy + dy / _escala;
            _hayVista = true;
            _tooltip = "";
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        bool eraArrastre = _arrastre != null;
        _punteros.Remove(e.Pointer.Id);
        if (_punteros.Count < 2) _pinza = null;
        _arrastre = null;

        // Toque sin desplazamiento = tap sobre un punto. En la pantalla táctil
        // no hay hover, y Leaflet en touch abre el tooltip del marker con el
        // toque: sin esto el "surco N · SPM X" solo se vería con mouse.
        if (eraArrastre && !_movio) ActualizarTooltip(p);
        _movio = false;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _punteros.Remove(e.Pointer.Id);
        _arrastre = null;
        _pinza = null;
        _movio = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_tooltip.Length > 0) { _tooltip = ""; InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        double nuevaEscala = _escala * factor;
        double mx = p.X - Bounds.Width / 2;
        double my = Bounds.Height / 2 - p.Y;
        _cx = _cx + mx / _escala - mx / nuevaEscala;
        _cy = _cy + my / _escala - my / nuevaEscala;
        _escala = nuevaEscala;
        _hayVista = true;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Tooltip sticky de los puntos: el más cercano dentro de 6 px
    /// (radio 2 del circleMarker + la tolerancia del canvas de Leaflet).</summary>
    private void ActualizarTooltip(Point p)
    {
        if (!_verPuntos || _puntos == null || !_hayVista)
        {
            if (_tooltip.Length > 0) { _tooltip = ""; InvalidateVisual(); }
            return;
        }

        string mejor = "";
        double mejorD = 6 * 6;
        foreach (var f in _puntos.Features)
        {
            if (f.Punto == null || f.Punto.Length < 2) continue;
            var q = APantalla(f.Punto[0], f.Punto[1]);
            double dx = q.X - p.X, dy = q.Y - p.Y;
            double d = dx * dx + dy * dy;
            if (d <= mejorD) { mejorD = d; mejor = f.Tooltip; }
        }

        if (!string.Equals(mejor, _tooltip, StringComparison.Ordinal) ||
            (mejor.Length > 0 && _tooltipPos != p))
        {
            _tooltip = mejor;
            _tooltipPos = p;
            InvalidateVisual();
        }
    }

    private static double Distancia(Point a, Point b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
