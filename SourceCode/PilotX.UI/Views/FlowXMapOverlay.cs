// ============================================================================
// FlowXMapOverlay.cs — overlay de PULVERIZACIÓN sobre el mapa (FlowX + StormX).
//
// Qué muestra, en orden de importancia para el que maneja:
//   1. los LITROS que va echando (l/ha aplicado), grande, con color según se
//      aleje del objetivo;
//   2. el objetivo y el caudal (l/min);
//   3. AUTO / MAN — y en MAN los botones − / + para corregir el caudal fijo
//      (l/min) sobre la marcha;
//   4. la franja StormX si la estación está conectada: viento + ráfaga, temp,
//      humedad y ΔT, coloreada por el veredicto de pulverización (la estación
//      es MÓVIL, va arriba de la pulverizadora — el dato es EN la máquina).
//
// Lo que NO hace: cortar secciones. Las secciones se siguen manejando con los
// botones de abajo de siempre (SectionX/pasada) — este overlay solo mira.
//
// Patrón QuantiXMapOverlay: nativo (sin WebView), fondo casi opaco (abajo hay
// mapa y un panel translúcido sobre pasto no se lee al sol), poll solo
// mientras está visible. Toggle: fx_overlay de overlayPrefs (Hub → FlowX).
//
// Escrituras: el modo AUTO/MAN y el l/min manual se cambian con
// GET /api/flowx/config → tocar producto[0] del nodo → POST el objeto ENTERO
// (el POST reemplaza el archivo: siempre se manda todo lo leído).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed class FlowXMapOverlay : Border
{
    // Paleta cockpit oscura (misma que QuantiXMapOverlay: legible al sol).
    private static readonly IBrush BgPanel  = new SolidColorBrush(Color.Parse("#F2101612"));
    private static readonly IBrush BgFila   = new SolidColorBrush(Color.Parse("#1B231E"));
    private static readonly IBrush Borde    = new SolidColorBrush(Color.Parse("#2A332C"));
    private static readonly IBrush Acento   = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ambar    = new SolidColorBrush(Color.Parse("#E2B53E"));
    private static readonly IBrush Rojo     = new SolidColorBrush(Color.Parse("#E15A5A"));
    private static readonly IBrush TextoHi  = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush TextoMid = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush TextoDim = new SolidColorBrush(Color.Parse("#8FA092"));

    private HttpClient? _http;
    private string _base = "";
    private DispatcherTimer? _timer;

    /// <summary>Se dispara cuando el operario arrastra el overlay. La posición
    /// la persiste quien lo hospeda (necesita las coords del contenedor).
    /// Mismo contrato que QuantiXMapOverlay.OnMovido.</summary>
    public Action<Point>? OnMovido { get; set; }

    // estado del último poll
    private string? _uid;              // nodo FlowX que se muestra (el 1ro online/config)
    private bool _modoManual;
    private double _manualLmin;
    private double _pasoLmin = 1;
    private double _dosisLha;          // objetivo l/ha (AUTO) — se corrige con − / + desde el overlay
    private double _pasoLha = 5;

    // controles
    private readonly Ellipse _dot;
    private readonly Button _btnModo;
    private readonly TextBlock _lha;       // el número grande
    private readonly TextBlock _sub;       // "Obj 80 l/ha · 42,3 l/min"
    private readonly StackPanel _manFila;  // − valor + (solo MAN)
    private readonly TextBlock _manVal;
    private readonly StackPanel _objFila;  // OBJ − l/ha + (AUTO): el objetivo que sigue el nodo
    private readonly TextBlock _objVal;
    private readonly Border _sxFila;       // franja StormX
    private readonly TextBlock _sxTexto;

    public FlowXMapOverlay()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 8, 10, 8);
        Width = 236;
        IsVisible = false;

        var raiz = new StackPanel { Spacing = 4 };

        // ---- header: ● FlowX · [AUTO/MAN] ----
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        _dot = new Ellipse { Width = 9, Height = 9, Fill = TextoDim, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_dot, 0);
        var tit = new TextBlock
        {
            Text = "FlowX", FontSize = 12, FontWeight = FontWeight.Bold,
            Foreground = TextoMid, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tit, 1);
        _btnModo = new Button
        {
            Content = "AUTO", MinWidth = 64, Height = 40, FontSize = 12.5, FontWeight = FontWeight.Bold,
            Background = BgFila, Foreground = Acento, BorderBrush = Acento, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _btnModo.Click += async (_, _) => await ToggleModo();
        Grid.SetColumn(_btnModo, 2);
        head.Children.Add(_dot);
        head.Children.Add(tit);
        head.Children.Add(_btnModo);

        // ---- litros grandes ----
        _lha = new TextBlock
        {
            Text = "—", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = TextoHi,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
            FontFamily = new FontFamily("Consolas, Segoe UI"),
        };
        _sub = new TextBlock
        {
            Text = "", FontSize = 11.5, Foreground = TextoDim, TextAlignment = TextAlignment.Center,
        };

        // ---- fila MAN: − valor + ----
        _manVal = new TextBlock
        {
            Text = "—", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = TextoHi,
            MinWidth = 84, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var menos = BotonPaso("−");
        var mas = BotonPaso("+");
        menos.Click += async (_, _) => await PasoManual(-1);
        mas.Click += async (_, _) => await PasoManual(+1);
        _manFila = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0), IsVisible = false,
        };
        _manFila.Children.Add(menos);
        _manFila.Children.Add(_manVal);
        _manFila.Children.Add(mas);

        // ---- fila OBJ (AUTO): − objetivo l/ha + ----
        // Pedido 2026-09-09: "en el overlay queremos cambiar los litros". Antes
        // los − / + solo existian en MAN y movian el caudal fijo (l/min); el
        // objetivo l/ha que sigue el nodo en AUTO solo se cambiaba en el editor.
        _objVal = new TextBlock
        {
            Text = "—", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = TextoHi,
            MinWidth = 84, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var objMenos = BotonPaso("−");
        var objMas = BotonPaso("+");
        objMenos.Click += async (_, _) => await PasoDosis(-1);
        objMas.Click += async (_, _) => await PasoDosis(+1);
        var objTit = new TextBlock
        {
            Text = "OBJ", FontSize = 10.5, Foreground = TextoDim,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
        };
        _objFila = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0), IsVisible = false,
        };
        _objFila.Children.Add(objTit);
        _objFila.Children.Add(objMenos);
        _objFila.Children.Add(_objVal);
        _objFila.Children.Add(objMas);

        // ---- franja StormX (solo si la estación está conectada) ----
        _sxTexto = new TextBlock
        {
            Text = "", FontSize = 11.5, Foreground = TextoMid,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        _sxFila = new Border
        {
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(0, 6, 0, 0), IsVisible = false,
            Child = _sxTexto,
        };

        raiz.Children.Add(head);
        raiz.Children.Add(_lha);
        raiz.Children.Add(_sub);
        raiz.Children.Add(_objFila);
        raiz.Children.Add(_manFila);
        raiz.Children.Add(_sxFila);
        Child = raiz;

        HabilitarArrastre();
    }

    // ---------- Arrastre (mismo mecanismo que QuantiXMapOverlay) -------------
    // El overlay vive en un Canvas (MapOverlaysHost); se corre con el dedo y la
    // posición se persiste en overlayPrefs.json. Un toque sobre los botones
    // (AUTO/MAN, − / +) es un comando, no un arrastre.

    private bool _arrastrando;
    private Point _origenPuntero;

    private void HabilitarArrastre()
    {
        PointerPressed += (s, e) =>
        {
            if (e.Source is Button || (e.Source as Control)?.Parent is Button) return;
            _arrastrando = true;
            _origenPuntero = e.GetPosition(Parent as Visual);
            e.Pointer.Capture(this);
        };
        PointerMoved += (s, e) =>
        {
            if (!_arrastrando) return;
            var p = e.GetPosition(Parent as Visual);
            var nuevo = new Point(
                Canvas.GetLeft(this) + (p.X - _origenPuntero.X),
                Canvas.GetTop(this) + (p.Y - _origenPuntero.Y));
            if (double.IsNaN(nuevo.X) || double.IsNaN(nuevo.Y)) return;
            // Clamp a los cuatro bordes: sin el tope derecho/abajo el widget se
            // podía arrastrar fuera de pantalla y "perderse".
            double maxX = double.MaxValue, maxY = double.MaxValue;
            if (Parent is Control host)
            {
                maxX = Math.Max(0, host.Bounds.Width  - Bounds.Width);
                maxY = Math.Max(0, host.Bounds.Height - Bounds.Height);
            }
            Canvas.SetLeft(this, Math.Min(Math.Max(0, nuevo.X), maxX));
            Canvas.SetTop(this, Math.Min(Math.Max(0, nuevo.Y), maxY));
            _origenPuntero = p;
        };
        PointerReleased += (s, e) =>
        {
            if (!_arrastrando) return;
            _arrastrando = false;
            e.Pointer.Capture(null);
            double x = Canvas.GetLeft(this), y = Canvas.GetTop(this);
            if (!double.IsNaN(x) && !double.IsNaN(y)) OnMovido?.Invoke(new Point(x, y));
        };
        PointerCaptureLost += (_, _) => _arrastrando = false;
    }

    private static Button BotonPaso(string txt) => new()
    {
        Content = txt, Width = 48, Height = 48, FontSize = 20, FontWeight = FontWeight.Bold,
        Background = BgFila, Foreground = TextoHi, BorderBrush = Borde, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    // ---- ciclo de vida ---------------------------------------------------------

    public void Attach(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = (baseUrl ?? "").TrimEnd('/');
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _timer.Tick += async (_, _) => await Poll();
        }
        _timer.Start();
        _ = Poll();
    }

    public void Detach() => _timer?.Stop();

    // ---- poll ------------------------------------------------------------------

    private async Task Poll()
    {
        if (!IsVisible || _http == null) return;

        // FlowX live: el nodo que se muestra es el primero online (o el primero
        // a secas). La pulverizadora típica es UN barral = UN nodo.
        // SIN ConfigureAwait(false): el tick corre en el hilo de UI y los
        // TextBlocks se tocan acá nomás (lección del crash de DireccionPanel).
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/flowx/live");
            var j = JsonNode.Parse(Limpio(body)) as JsonObject;
            var nodos = j?["nodos"] as JsonArray;
            JsonObject? nodo = null;
            if (nodos != null)
            {
                foreach (var n in nodos)
                {
                    if (n is not JsonObject o) continue;
                    if (nodo == null) nodo = o;
                    if (o["online"]?.GetValue<bool>() == true) { nodo = o; break; }
                }
            }

            if (nodo == null)
            {
                _dot.Fill = TextoDim;
                _lha.Text = "—";
                _sub.Text = "sin nodo FlowX";
            }
            else
            {
                _uid = nodo["uid"]?.GetValue<string>();
                bool online = nodo["online"]?.GetValue<bool>() ?? false;
                double lha = Num(nodo["caudal_lha"]);
                double lhaObj = Num(nodo["target_lha"]);
                double lmin = Num(nodo["caudal_lmin"]);

                _dot.Fill = online ? Acento : Rojo;
                _lha.Text = lha > 0 ? lha.ToString("F1", CultureInfo.InvariantCulture) : "0";

                // Color del número grande: verde cerca del objetivo, ámbar ±10%,
                // gris si no está aplicando.
                if (lha <= 0 || lhaObj <= 0) _lha.Foreground = TextoDim;
                else
                {
                    double desvio = Math.Abs(lha - lhaObj) / lhaObj;
                    _lha.Foreground = desvio <= 0.10 ? Acento : (desvio <= 0.25 ? Ambar : Rojo);
                }

                _sub.Text = (lhaObj > 0 ? "Obj " + lhaObj.ToString("F0", CultureInfo.InvariantCulture) + " l/ha · " : "")
                          + lmin.ToString("F1", CultureInfo.InvariantCulture) + " l/min"
                          + (_modoManual ? "  ·  MAN" : "");
            }
        }
        catch { /* engine ocupado: se deja lo último */ }

        // Config (modo/paso/manual): más barato que live, alcanza refrescarla
        // en el mismo latido — es chica y ya está en cache del engine.
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/flowx/config");
            var cfg = JsonNode.Parse(Limpio(body)) as JsonObject;
            var prod = PrimerProducto(cfg, _uid);
            if (prod != null)
            {
                _modoManual = prod["modo_manual"]?.GetValue<bool>() ?? false;
                _manualLmin = Num(prod["manual_lmin"]);
                double paso = Num(prod["paso_lmin"]);
                _pasoLmin = paso > 0 ? paso : 1;

                _dosisLha = Num(prod["dosis_lha"]);
                double pasoLha = Num(prod["paso_lha"]);
                _pasoLha = pasoLha > 0 ? pasoLha : 5;

                _btnModo.Content = _modoManual ? "MAN" : "AUTO";
                _btnModo.Foreground = _modoManual ? Ambar : Acento;
                _btnModo.BorderBrush = _modoManual ? Ambar : Acento;
                // AUTO: se corrige el objetivo l/ha. MAN: se corrige el caudal fijo l/min.
                _objFila.IsVisible = !_modoManual;
                _manFila.IsVisible = _modoManual;
                _objVal.Text = _dosisLha.ToString("F0", CultureInfo.InvariantCulture) + " l/ha";
                _manVal.Text = _manualLmin.ToString("F1", CultureInfo.InvariantCulture) + " l/min";
            }
        }
        catch { }

        // StormX: la estación va ARRIBA de la pulverizadora — si está online,
        // el viento/temp/ΔT son EN la máquina, que es el dato que vale para
        // decidir si se sigue pulverizando.
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/stormx/live");
            var j = JsonNode.Parse(Limpio(body)) as JsonObject;
            JsonObject? est = null;
            if (j?["nodos"] is JsonArray sxNodos)
                foreach (var n in sxNodos)
                    if (n is JsonObject o && o["online"]?.GetValue<bool>() == true) { est = o; break; }

            if (est == null) _sxFila.IsVisible = false;
            else
            {
                double vKmh = Num(est["wind_ms"]) * 3.6;
                double gKmh = Num(est["gust_ms"]) * 3.6;
                double temp = Num(est["temp_c"]);
                double hum = Num(est["hum_pct"]);
                double dt = Num(est["delta_t_c"]);
                string verdict = est["verdict"]?.GetValue<string>() ?? "";

                _sxTexto.Text = "☁ " + vKmh.ToString("F0", CultureInfo.InvariantCulture)
                    + " km/h (ráf " + gKmh.ToString("F0", CultureInfo.InvariantCulture) + ")"
                    + " · " + temp.ToString("F0", CultureInfo.InvariantCulture) + "°C"
                    + " · " + hum.ToString("F0", CultureInfo.InvariantCulture) + "%"
                    + (dt > 0 ? " · ΔT " + dt.ToString("F1", CultureInfo.InvariantCulture) : "");
                _sxTexto.Foreground = verdict switch
                {
                    "ok" => TextoMid,
                    "warn" => Ambar,
                    "" => TextoMid,
                    _ => Rojo,          // stop / fuera de condiciones
                };
                _sxFila.IsVisible = true;
            }
        }
        catch { _sxFila.IsVisible = false; }
    }

    // ---- escrituras (modo / manual) ---------------------------------------------

    private async Task ToggleModo()
    {
        await MutarProducto(prod => prod["modo_manual"] = !(prod["modo_manual"]?.GetValue<bool>() ?? false));
    }

    private async Task PasoManual(int dir)
    {
        await MutarProducto(prod =>
        {
            double v = Num(prod["manual_lmin"]) + dir * _pasoLmin;
            if (v < 0) v = 0;
            prod["manual_lmin"] = Math.Round(v, 1);
        });
    }

    /// <summary>Objetivo l/ha (AUTO): − / + de a paso_lha (default 5). Es lo que
    /// el bridge manda al nodo cada 2 s como target, asi que el cambio se ve en
    /// el caudal enseguida. Se persiste en la config de FlowX (dosis_lha).</summary>
    private async Task PasoDosis(int dir)
    {
        await MutarProducto(prod =>
        {
            double v = Num(prod["dosis_lha"]) + dir * _pasoLha;
            if (v < 0) v = 0;
            prod["dosis_lha"] = Math.Round(v, 1);
        });
    }

    /// <summary>GET config → mutar producto[0] del nodo mostrado → POST ENTERO.
    /// El POST /flowx/config reemplaza el archivo: nunca mandar parciales.</summary>
    private async Task MutarProducto(Action<JsonObject> mutar)
    {
        if (_http == null) return;
        try
        {
            var body = await _http.GetStringAsync(_base + "/api/flowx/config");
            var cfg = JsonNode.Parse(Limpio(body)) as JsonObject;
            var prod = PrimerProducto(cfg, _uid);
            if (cfg == null || prod == null) return;
            mutar(prod);
            cfg.Remove("ok");
            var contenido = new StringContent(cfg.ToJsonString(), Encoding.UTF8, "application/json");
            await _http.PostAsync(_base + "/api/flowx/config", contenido);
            await Poll();   // reflejar al toque (el bridge relee la config solo)
        }
        catch { }
    }

    private static JsonObject? PrimerProducto(JsonObject? cfg, string? uid)
    {
        if (cfg?["nodos"] is not JsonArray nodos) return null;
        JsonObject? elegido = null;
        foreach (var n in nodos)
        {
            if (n is not JsonObject o) continue;
            if (elegido == null) elegido = o;
            if (uid != null && o["uid"]?.GetValue<string>() == uid) { elegido = o; break; }
        }
        return (elegido?["productos"] as JsonArray)?[0] as JsonObject;
    }

    private static double Num(JsonNode? n)
    {
        if (n == null) return 0;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<int>(); } catch { return 0; } }
    }

    private static string Limpio(string s) => s.TrimStart('﻿');
}
