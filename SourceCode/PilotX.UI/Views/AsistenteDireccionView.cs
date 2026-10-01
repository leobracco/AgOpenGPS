// ============================================================================
// AsistenteDireccionView.cs — pantalla paso a paso del ASISTENTE DE
// CALIBRACIÓN DE LA DIRECCIÓN (Menú izquierdo › Dirección › Asistente).
//
// Vive ADENTRO del DireccionPanel (reemplaza sus pestañas mientras está
// abierto): nada de popups sobre el mapa GL. Táctil: botones grandes, un paso
// a la vez, "Cancelar" siempre visible.
//
// Toda la lógica está en el motor (EngineSteerCalService → SteerCalWizard);
// esto solo muestra el estado y manda acciones:
//   GET  /api/steer/cal            cada 300 ms (es además el latido de pantalla:
//                                  si deja de consultar 5 s, el motor cancela)
//   POST /api/steer/cal/iniciar
//   POST /api/steer/cal/accion     {"accion":"…"}
//   POST /api/steer/cal/latido     {"apretado":true|false}
//
// Hombre muerto: "MANTENÉ APRETADO" manda apretado=true cada 200 ms mientras
// el dedo está apoyado; al levantar (o perder el puntero) manda false en el
// acto. El motor corta solo si no recibe latido en 500 ms.
// ============================================================================

using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed class AsistenteDireccionView : Border
{
    // ---- paleta PilotX (misma que DireccionPanel) ----------------------------
    private static readonly IBrush BgCard     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgSel      = new SolidColorBrush(Color.Parse("#DCEFD8"));
    private static readonly IBrush BgSuave    = new SolidColorBrush(Color.Parse("#F5F7F4"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush BgRojo     = new SolidColorBrush(Color.Parse("#FBEAE9"));

    private HttpClient? _http;
    private string _base = "";

    /// <summary>El operario salió del asistente (Volver, o terminó/canceló y tocó Volver).</summary>
    public event Action? Salir;

    private DispatcherTimer? _poll;
    private DispatcherTimer? _latido;
    private bool _apretando;
    private bool _activo;
    private bool _consultando;

    // ---- controles ----
    private readonly TextBlock _paso;
    private readonly TextBlock _titulo;
    private readonly Border _barra;
    private readonly Border _barraFondo;
    private readonly TextBlock _mensaje;
    private readonly Border _mensajeCaja;
    private readonly TextBlock _medicion;
    private readonly TextBlock _vivo;
    private readonly StackPanel _chequeos = new() { Spacing = 4 };
    private readonly StackPanel _cambios = new() { Spacing = 4 };
    private readonly Border _hombreMuerto;
    private readonly TextBlock _hombreMuertoTxt;
    private readonly WrapPanel _acciones = new() { Orientation = Orientation.Horizontal };
    private readonly Button _btnCancelar;
    private readonly Button _btnVolver;

    public AsistenteDireccionView()
    {
        Background = Brushes.Transparent;
        IsVisible = false;

        _paso = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = TextoMuted };
        _titulo = new TextBlock
        {
            FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Texto, TextWrapping = TextWrapping.Wrap,
        };

        _barra = new Border { Background = Verde, CornerRadius = new CornerRadius(4), Width = 0, HorizontalAlignment = HorizontalAlignment.Left };
        _barraFondo = new Border
        {
            Height = 8, Background = BgSuave, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = _barra, Margin = new Thickness(0, 6, 0, 8),
        };

        _mensaje = new TextBlock { FontSize = 15, Foreground = Texto, TextWrapping = TextWrapping.Wrap };
        _mensajeCaja = new Border
        {
            Child = _mensaje, Background = BgCard, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10),
        };

        _medicion = new TextBlock
        {
            FontSize = 13, Foreground = TextoMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0),
        };
        _vivo = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Texto, Margin = new Thickness(2, 4, 0, 0),
        };

        _hombreMuertoTxt = new TextBlock
        {
            Text = "MANTENÉ APRETADO", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Texto,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        // Border (no Button): el Button se come el PointerPressed y acá hacen
        // falta apoyar Y levantar el dedo por separado.
        _hombreMuerto = new Border
        {
            Child = _hombreMuertoTxt, Height = 92, Margin = new Thickness(0, 10, 0, 0),
            Background = BgCard, BorderBrush = Verde, BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(14), IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand),
        };
        _hombreMuerto.PointerPressed += (_, e) =>
        {
            e.Pointer.Capture(_hombreMuerto);
            Apretar(true);
            e.Handled = true;
        };
        _hombreMuerto.PointerReleased += (_, e) => { Apretar(false); e.Handled = true; };
        _hombreMuerto.PointerCaptureLost += (_, _) => Apretar(false);

        _btnCancelar = BotonGrande("Cancelar", Rojo, BgCard, Rojo);
        _btnCancelar.Click += async (_, _) => await Accion("cancelar");
        _btnVolver = BotonGrande("Volver a Dirección", Texto, BgCard, Borde);
        _btnVolver.Click += (_, _) => Cerrar();

        var cabecera = new StackPanel { Spacing = 2 };
        cabecera.Children.Add(_paso);
        cabecera.Children.Add(_titulo);
        cabecera.Children.Add(_barraFondo);

        var cuerpo = new StackPanel { Spacing = 6 };
        cuerpo.Children.Add(_mensajeCaja);
        cuerpo.Children.Add(_medicion);
        cuerpo.Children.Add(_vivo);
        cuerpo.Children.Add(_chequeos);
        cuerpo.Children.Add(_cambios);
        var scroll = new ScrollViewer { Content = cuerpo, MaxHeight = 330 };

        var pie = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(_acciones, 0);
        var pieDer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pieDer.Children.Add(_btnVolver);
        pieDer.Children.Add(_btnCancelar);
        Grid.SetColumn(pieDer, 1);
        pie.Children.Add(_acciones);
        pie.Children.Add(pieDer);

        var root = new StackPanel();
        root.Children.Add(cabecera);
        root.Children.Add(scroll);
        root.Children.Add(_hombreMuerto);
        root.Children.Add(pie);
        Child = root;
    }

    // ---- ciclo de vida -----------------------------------------------------------

    public void Attach(HttpClient http, string? baseUrl)
    {
        _http = http;
        _base = (baseUrl ?? "").TrimEnd('/');
    }

    /// <summary>Abre la pantalla: retoma el asistente en curso o arranca uno nuevo.</summary>
    public async Task Abrir()
    {
        IsVisible = true;
        _titulo.Text = "Asistente de dirección";
        _paso.Text = "";
        Pintar(null);
        _mensaje.Text = "Conectando con PilotX…";

        var j = await Get();
        if (j == null) { NoDisponible(); return; }
        if (!(j["activo"]?.GetValue<bool>() ?? false))
            j = await Post("/api/steer/cal/iniciar", "{}");
        if (j == null || (!(j["ok"]?.GetValue<bool>() ?? false) && !(j["activo"]?.GetValue<bool>() ?? false)))
        {
            NoDisponible();
            return;
        }
        Pintar(j);

        if (_poll == null)
        {
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _poll.Tick += async (_, _) => await Refrescar();
        }
        _poll.Start();
    }

    /// <summary>
    /// Sale de la pantalla. Si el asistente sigue en curso lo CANCELA: la
    /// placa vuelve a la config de antes (nunca queda una prueba a medias).
    /// </summary>
    public void Cerrar()
    {
        Apretar(false);
        if (_activo) _ = Post("/api/steer/cal/accion", "{\"accion\":\"cancelar\"}");
        _activo = false;
        _poll?.Stop();
        IsVisible = false;
        Salir?.Invoke();
    }

    private void NoDisponible()
    {
        _poll?.Stop();
        _activo = false;
        _titulo.Text = "Asistente de dirección";
        _mensaje.Text = "El asistente no está disponible en este equipo (el motor de PilotX no lo ofrece). "
                      + "Calibrá a mano desde las pestañas de Dirección.";
        _mensajeCaja.BorderBrush = Rojo;
        _acciones.Children.Clear();
        _btnCancelar.IsVisible = false;
        _btnVolver.IsVisible = true;
        _hombreMuerto.IsVisible = false;
    }

    // ---- HTTP --------------------------------------------------------------------

    private async Task Refrescar()
    {
        if (!IsVisible || _consultando) return;
        _consultando = true;
        try
        {
            var j = await Get();
            if (j != null) Pintar(j);
        }
        finally { _consultando = false; }
    }

    private async Task Accion(string accion)
    {
        var j = await Post("/api/steer/cal/accion", "{\"accion\":\"" + accion + "\"}");
        if (j != null) Pintar(j);
    }

    private async Task<JsonObject?> Get()
    {
        if (_http == null) return null;
        try
        {
            var resp = await _http.GetAsync(_base + "/api/steer/cal");
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            return JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
        }
        catch { return null; }
    }

    private async Task<JsonObject?> Post(string path, string json)
    {
        if (_http == null) return null;
        try
        {
            var resp = await _http.PostAsync(_base + path, new StringContent(json, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            return JsonNode.Parse(body.TrimStart('﻿')) as JsonObject;
        }
        catch { return null; }
    }

    // ---- hombre muerto -----------------------------------------------------------

    private void Apretar(bool apretado)
    {
        if (apretado == _apretando) return;
        _apretando = apretado;
        _hombreMuerto.Background = apretado ? BgSel : BgCard;
        _hombreMuertoTxt.Text = apretado ? "APRETADO — soltá para frenar" : "MANTENÉ APRETADO";

        if (apretado)
        {
            if (_latido == null)
            {
                _latido = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _latido.Tick += (_, _) =>
                {
                    if (_apretando) _ = Post("/api/steer/cal/latido", "{\"apretado\":true}");
                };
            }
            _latido.Start();
            _ = Post("/api/steer/cal/latido", "{\"apretado\":true}");
        }
        else
        {
            _latido?.Stop();
            _ = Post("/api/steer/cal/latido", "{\"apretado\":false}");
        }
    }

    // ---- render ------------------------------------------------------------------

    private void Pintar(JsonObject? j)
    {
        if (j == null)
        {
            _acciones.Children.Clear();
            _chequeos.Children.Clear();
            _cambios.Children.Clear();
            _medicion.Text = "";
            _vivo.Text = "";
            _hombreMuerto.IsVisible = false;
            return;
        }

        _activo = B(j, "activo");
        int num = I(j, "numero"), total = I(j, "total");
        string paso = S(j, "paso");
        _paso.Text = num > 0 ? "Paso " + num + " de " + total : (paso == "resumen" ? "Resumen" : "");
        _titulo.Text = S(j, "titulo");

        double prog = Math.Max(0, Math.Min(1, D(j, "progreso")));
        double ancho = _barraFondo.Bounds.Width > 2 ? _barraFondo.Bounds.Width - 2 : 0;
        _barra.Width = prog * ancho;

        _mensaje.Text = S(j, "mensaje");
        bool err = B(j, "mensaje_error") || S(j, "fase") == "error";
        _mensajeCaja.BorderBrush = err ? Rojo : Borde;
        _mensajeCaja.Background = err ? BgRojo : BgCard;
        _medicion.Text = S(j, "medicion");
        _medicion.IsVisible = _medicion.Text.Length > 0;

        // En vivo: lo que el operario necesita mirar mientras calibra.
        int corr = I(j, "corriente");
        _vivo.Text = "Ángulo " + Grados(D(j, "angulo"))
                   + (B(j, "motor_activo") ? "   Objetivo " + Grados(D(j, "setpoint")) : "")
                   + "   Velocidad " + D(j, "velocidad").ToString("F1", CultureInfo.InvariantCulture).Replace('.', ',') + " km/h"
                   + (corr >= 0 ? "   Corriente " + corr : "");
        _vivo.IsVisible = _activo;

        PintarChequeos(j["chequeos"] as JsonArray);
        PintarCambios(j["cambios"] as JsonArray, paso == "resumen");

        bool hm = B(j, "hombre_muerto");
        _hombreMuerto.IsVisible = hm;
        if (!hm && _apretando) Apretar(false);

        _acciones.Children.Clear();
        if (B(j, "puede_empezar")) Agregar("Empezar", "empezar", true);
        if (B(j, "puede_aceptar")) { Agregar("Aceptar", "aceptar", true); Agregar("Rechazar", "rechazar", false); }
        if (B(j, "puede_siguiente")) Agregar("Siguiente", "siguiente", true);
        if (B(j, "puede_repetir")) Agregar("Repetir", "repetir", false);
        if (B(j, "puede_aplicar")) Agregar("Aplicar", "aplicar", true);
        if (B(j, "puede_deshacer")) Agregar("Deshacer", "deshacer", false);
        if (B(j, "puede_saltar")) Agregar("Saltar paso", "saltar", false);

        // Cancelar siempre visible mientras hay asistente; al terminar, Volver.
        _btnCancelar.IsVisible = _activo;
        _btnVolver.IsVisible = !_activo;
    }

    private void PintarChequeos(JsonArray? arr)
    {
        _chequeos.Children.Clear();
        if (arr == null) return;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            bool ok = B(o, "ok");
            _chequeos.Children.Add(new TextBlock
            {
                Text = (ok ? "✔  " : "✘  ") + S(o, "texto"),
                FontSize = 14, FontWeight = ok ? FontWeight.Normal : FontWeight.SemiBold,
                Foreground = ok ? Texto : Rojo, TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void PintarCambios(JsonArray? arr, bool resumen)
    {
        _cambios.Children.Clear();
        if (arr == null || arr.Count == 0) return;
        _cambios.Children.Add(new TextBlock
        {
            Text = resumen ? "Lo que cambia (antes → después)" : "Propuesta (antes → después)",
            FontSize = 12, FontWeight = FontWeight.Bold, Foreground = TextoMuted, Margin = new Thickness(2, 6, 0, 0),
        });
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
            var campo = new TextBlock
            {
                Text = S(o, "campo"), FontSize = 14, Foreground = Texto, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var valores = new TextBlock
            {
                Text = S(o, "antes") + "  →  " + S(o, "despues"), FontSize = 15, FontWeight = FontWeight.Bold,
                Foreground = Texto, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(campo, 0);
            Grid.SetColumn(valores, 1);
            g.Children.Add(campo);
            g.Children.Add(valores);
            _cambios.Children.Add(new Border
            {
                Child = g, Background = BgCard, BorderBrush = Borde, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6),
            });
        }
    }

    private void Agregar(string texto, string accion, bool principal)
    {
        var b = BotonGrande(texto, principal ? Brushes.White : Texto, principal ? Verde : BgCard, principal ? Verde : Borde);
        b.Margin = new Thickness(0, 0, 6, 6);
        b.Click += async (_, _) => await Accion(accion);
        _acciones.Children.Add(b);
    }

    private static Button BotonGrande(string txt, IBrush fg, IBrush bg, IBrush borde) => new()
    {
        Content = txt, Height = 54, MinWidth = 104, FontSize = 14, FontWeight = FontWeight.Bold,
        Foreground = fg, Background = bg, BorderBrush = borde, BorderThickness = new Thickness(2),
        CornerRadius = new CornerRadius(10), HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 0, 12, 0),
    };

    // ---- helpers de lectura --------------------------------------------------------

    private static string S(JsonObject j, string k)
    {
        try { return j[k]?.GetValue<string>() ?? ""; } catch { return ""; }
    }

    private static bool B(JsonObject j, string k)
    {
        try { return j[k]?.GetValue<bool>() ?? false; } catch { return false; }
    }

    private static int I(JsonObject j, string k)
    {
        var n = j[k];
        if (n == null) return 0;
        try { return n.GetValue<int>(); }
        catch { try { return (int)Math.Round(n.GetValue<double>()); } catch { return 0; } }
    }

    private static double D(JsonObject j, string k)
    {
        var n = j[k];
        if (n == null) return 0;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<int>(); } catch { return 0; } }
    }

    private static string Grados(double v) =>
        (v > 0 ? "+" : "") + v.ToString("F1", CultureInfo.InvariantCulture).Replace('.', ',') + "°";
}
