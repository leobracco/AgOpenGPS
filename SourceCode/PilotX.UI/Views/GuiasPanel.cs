// ============================================================================
// GuiasPanel.cs — el gestor de guías, NATIVO (15vo port, reemplaza tracks.html).
//
// Por qué se portó: Guías es el diálogo que más se abre MANEJANDO, y era el
// único de la labor que levantaba Chromium (~300 MB y un pico de CPU) en el
// momento donde menos sobra. Portado, el WebView no se despierta nunca
// durante el trabajo: queda solo para la configuración con el tractor parado.
//
// Qué gana el operario además de la memoria:
//   · el MAPA SIGUE VIVO detrás (la ventana HTML lo tapaba): al grabar una
//     curva se ve la traza dibujándose mientras se maneja;
//   · sin ventana flotante que arrastrar — es un panel sobre el mapa, como
//     el menú SISTEMA o el de Herramientas.
//
// Estructura: un Border flotante con 4 "pantallas" (StackPanels que se
// prenden de a una): MENÚ de entrada → AB delega en el flujo nativo que ya
// existía (StartAbCreate: tocás A, manejás, tocás B, todo en el mapa);
// CURVA graba contra /api/tracks/record-curve-*; LISTA gestiona las
// guardadas; NOMBRE es el paso final de curva/renombrar/duplicar.
//
// El teclado para el nombre es la ventana nativa de PilotX (TecladoWindow):
// se pide por POST /api/teclado/abrir — misma señal que mandan las páginas
// HTML — y las teclas entran solas por SendInput porque el TextBox nativo
// conserva el foco (la ventana del teclado es NOACTIVATE).
//
// Todo el wire es el MISMO /api/tracks/* que usaba tracks.html: cero cambios
// de backend, la página HTML sigue existiendo para el Hub remoto/Android.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PilotX.Desktop.Views;

public sealed class GuiasPanel : Border
{
    // ---- paleta PilotX (misma que los otros paneles del cockpit) ----------
    private static readonly IBrush BgPanel   = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila    = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgFilaSel = new SolidColorBrush(Color.Parse("#DCEFD8"));
    private static readonly IBrush Borde     = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto     = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted= new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde     = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Rojo      = new SolidColorBrush(Color.Parse("#D0504A"));

    private HttpClient? _http;
    private string _base = "";

    /// <summary>El operario cerró el panel (cualquier pantalla).</summary>
    public event Action? Cerrado;

    /// <summary>Aviso corto para el operario (lo muestra MainWindow como
    /// toast). Nació con el OK del listado: "no sé si hizo algo" era un
    /// reporte de banco (2026-08-11).</summary>
    public event Action<string>? Aviso;
    /// <summary>Eligió "AB": el host arranca el flujo nativo del mapa.</summary>
    public event Action? CrearAbPedido;

    // ---- estado ------------------------------------------------------------
    private sealed class TrackDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("mode")] public string? Mode { get; set; }
        [JsonPropertyName("is_visible")] public bool IsVisible { get; set; }
    }
    private sealed class EstadoDto
    {
        [JsonPropertyName("tracks")] public List<TrackDto>? Tracks { get; set; }
        [JsonPropertyName("selected_idx")] public int SelectedIdx { get; set; } = -1;
        [JsonPropertyName("active_idx")] public int ActiveIdx { get; set; } = -1;
    }

    private EstadoDto _estado = new();
    private bool _todasVisibles = true;

    // pantallas
    private readonly StackPanel _scMenu;
    private readonly Grid _scLista;
    private readonly StackPanel _scCurva;
    private readonly StackPanel _scNombre;
    private readonly TextBlock _titulo;

    private readonly StackPanel _listaFilas;
    private readonly Button _btnIrLista;

    // curva
    private readonly TextBlock _curvaEstado;
    private readonly Button _btnCurvaPausa;
    private DispatcherTimer? _curvaTimer;
    private bool _grabando;

    // nombre (paso final de curva / renombrar / duplicar)
    private readonly TextBox _txtNombre;
    private readonly TextBlock _nombreLbl;
    private string _nombrePara = "";   // "curva" | "rename" | "duplicate"

    public GuiasPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        Width = 480;
        IsVisible = false;

        _titulo = new TextBlock
        {
            Text = "Guías", FontSize = 13, FontWeight = FontWeight.Bold,
            Foreground = TextoMuted, Margin = new Thickness(4, 0, 0, 8)
        };

        _scMenu   = ArmarMenu(out _btnIrLista);
        _scLista  = ArmarLista(out _listaFilas);
        _scCurva  = ArmarCurva(out _curvaEstado, out _btnCurvaPausa);
        _scNombre = ArmarNombre(out _nombreLbl, out _txtNombre);

        var stage = new Panel();
        stage.Children.Add(_scMenu);
        stage.Children.Add(_scLista);
        stage.Children.Add(_scCurva);
        stage.Children.Add(_scNombre);

        var root = new StackPanel();
        root.Children.Add(_titulo);
        root.Children.Add(stage);
        Child = root;
    }

    /// <summary>Inyección tardía del canal HTTP (mismo criterio que Attach de los otros paneles).</summary>
    public void Attach(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = baseUrl.TrimEnd('/');
        TrazaGuias("Attach base=" + _base);
    }

    /// <summary>Abre el panel en el menú de entrada (o directo en la lista si se pide).</summary>
    public async void Abrir(bool directoALista = false)
    {
        TrazaGuias("Abrir(directoALista=" + directoALista + ")");
        await CargarEstadoAsync();
        // Reintento único: el estado a veces no carga en instancias largas
        // (falla intermitente tragada por los catch — reporte 2026-08-11
        // "toco Guías y no lista las guías") y sin él HayGuias da falso: el
        // panel caía al menú de crear con el listado escondido, mientras el
        // auto-select del mapa activaba una guía solo. Un retry a los 300 ms
        // cubre el hipo; la traza (pilotx-guias.log) queda para la raíz.
        if (!HayGuias)
        {
            await Task.Delay(300);
            await CargarEstadoAsync();
            TrazaGuias("Abrir: reintento de estado -> HayGuias=" + HayGuias);
        }
        TrazaGuias("Abrir -> HayGuias=" + HayGuias + " => " + (directoALista && HayGuias ? "lista" : "menu"));
        Mostrar(directoALista && HayGuias ? "lista" : "menu");
        IsVisible = true;
        // Los textos que arma este código (filas, estados) nacen en castellano;
        // el diccionario los traduce acá igual que a los del XAML.
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    public void Cerrar()
    {
        TrazaGuias("Cerrar()");
        PararTimerCurva();
        _ = TecladoAsync(false);
        IsVisible = false;
        Cerrado?.Invoke();
    }

    private bool HayGuias => _estado.Tracks != null && _estado.Tracks.Count > 0;

    // =========================================================================
    //  pantallas
    // =========================================================================

    private void Mostrar(string cual)
    {
        _scMenu.IsVisible   = cual == "menu";
        _scLista.IsVisible  = cual == "lista";
        _scCurva.IsVisible  = cual == "curva";
        _scNombre.IsVisible = cual == "nombre";
        _titulo.Text = cual switch
        {
            "lista"  => "Guías guardadas",
            "curva"  => "Curva",
            "nombre" => "Nombre de la guía",
            _        => "Guías",
        };
        if (cual == "lista") RefrescarLista();
        if (cual != "nombre") _ = TecladoAsync(false);
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    // ---- MENÚ de entrada: AB / AB+Curva / Guías guardadas / cerrar ---------
    private StackPanel ArmarMenu(out Button btnIrLista)
    {
        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        fila.Children.Add(BotonGrande("AB Line", "ABTrackAB.png", () =>
        {
            // El flujo AB nativo ya existía (tocás A en el mapa, manejás,
            // tocás B): esto solo le pasa la posta y cierra el panel.
            Cerrar();
            CrearAbPedido?.Invoke();
        }));
        fila.Children.Add(BotonGrande("AB + Curva", "ABTrackCurve.png", () => Mostrar("curva")));
        btnIrLista = BotonGrande("Guías guardadas", "ABLinesHideShow.png", () => Mostrar("lista"));
        fila.Children.Add(btnIrLista);

        var pie = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };
        pie.Children.Add(BotonIcono("Cancel64.png", "Cerrar", Cerrar, 70, 54));

        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(fila);
        p.Children.Add(pie);
        return p;
    }

    // ---- LISTA: filas + columna izquierda (edición) y derecha (orden/uso) --
    private Grid ArmarLista(out StackPanel filas)
    {
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            IsVisible = false
        };

        var colIzq = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 8, 0) };
        colIzq.Children.Add(BotonIcono("Trash.png",        "Borrar guía",          async () => { await PostAsync("/delete"); RefrescarLista(); }));
        colIzq.Children.Add(BotonIcono("FileEditName.png", "Editar nombre",        () => AbrirNombre("rename")));
        colIzq.Children.Add(BotonIcono("FileCopy.png",     "Duplicar",             () => AbrirNombre("duplicate")));
        colIzq.Children.Add(BotonIcono("ABSwapPoints.png", "Invertir A↔B",         async () => { await PostAsync("/swap-ab"); RefrescarLista(); }));
        colIzq.Children.Add(BotonIcono("Cancel64.png",     "Cancelar (descartar)", async () => { await PostAsync("/cancel"); Cerrar(); }));
        Grid.SetColumn(colIzq, 0);

        filas = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = filas, Height = 300,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var marco = new Border
        {
            Child = scroll, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Background = BgFila
        };
        Grid.SetColumn(marco, 1);

        var colDer = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
        colDer.Children.Add(BotonIcono("UpArrow64.png",       "Subir",                 async () => { await PostAsync("/move-up"); RefrescarLista(); }));
        colDer.Children.Add(BotonIcono("DnArrow64.png",       "Bajar",                 async () => { await PostAsync("/move-down"); RefrescarLista(); }));
        colDer.Children.Add(BotonIcono("ABLinesHideShow.png", "Mostrar/ocultar todas", async () =>
        {
            _todasVisibles = !_todasVisibles;
            await PostAsync("/toggle-all", new { visible = _todasVisibles });
            RefrescarLista();
        }));
        colDer.Children.Add(BotonIcono("AddNew.png", "Nueva guía",             () => Mostrar("menu")));
        colDer.Children.Add(BotonIcono("OK64.png",   "Usar guía seleccionada", async () =>
        {
            if (!HayGuias)
            {
                // /use sin guías desactiva la guía y puede apagar el piloto:
                // con la lista vacía el tilde no tiene nada que confirmar.
                Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No hay guías para activar"));
                return;
            }
            await PostAsync("/use");
            // /use devuelve solo {ok} — refrescar el estado para saber QUÉ
            // guía quedó activa y decírselo al operario: que el tilde nunca
            // deje la duda de si hizo algo.
            await CargarEstadoAsync();
            var act = _estado.ActiveIdx;
            var ts = _estado.Tracks;
            if (act >= 0 && ts != null && act < ts.Count)
            {
                string n = string.IsNullOrWhiteSpace(ts[act].Name) ? ("Guía " + (act + 1)) : ts[act].Name!;
                Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Guía activada") + ": " + n);
            }
            else
            {
                Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo activar la guía"));
            }
            Cerrar();
        }));
        Grid.SetColumn(colDer, 2);

        g.Children.Add(colIzq);
        g.Children.Add(marco);
        g.Children.Add(colDer);
        return g;
    }

    private void RefrescarLista()
    {
        _listaFilas.Children.Clear();
        var ts = _estado.Tracks;
        if (ts == null || ts.Count == 0)
        {
            _listaFilas.Children.Add(new TextBlock
            {
                Text = "No hay guías en el lote.", Foreground = TextoMuted,
                Margin = new Thickness(10), FontSize = 15
            });
            return;
        }

        for (int i = 0; i < ts.Count; i++)
        {
            int idx = i;
            var t = ts[i];

            var fila = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("40,*,46"),
                Height = 48
            };

            string icono = (t.Mode ?? "").ToLowerInvariant() switch
            {
                "ab"                    => "TrackLine.png",
                "pivot" or "waterpivot" => "TrackPivot.png",
                _                       => "TrackCurve.png",
            };
            var img = new Image { Source = Icono(icono), Width = 30, Height = 26 };
            Grid.SetColumn(img, 0);

            // El NOMBRE de la guía es dato del operario: no se traduce jamás.
            var nombre = new Button
            {
                Content = string.IsNullOrWhiteSpace(t.Name) ? ("Guía " + (idx + 1)) : t.Name,
                Background = idx == _estado.SelectedIdx ? BgFilaSel : BgFila,
                Foreground = t.IsVisible ? Texto : TextoMuted,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 16, Padding = new Thickness(10, 0, 0, 0), Height = 48,
            };
            nombre.Click += async (_, __) =>
            {
                if (!t.IsVisible) return;   // el nativo solo selecciona visibles
                await PostAsync("/select", new { index = idx });
                RefrescarLista();
            };
            Grid.SetColumn(nombre, 1);

            var vis = new Button
            {
                Background = t.IsVisible ? Verde : Rojo,
                BorderThickness = new Thickness(0),
                Width = 42, Height = 42, CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            ToolTip.SetTip(vis, "Mostrar/ocultar");
            vis.Click += async (_, __) =>
            {
                await PostAsync("/toggle-visibility", new { index = idx });
                RefrescarLista();
            };
            Grid.SetColumn(vis, 2);

            fila.Children.Add(img);
            fila.Children.Add(nombre);
            fila.Children.Add(vis);

            _listaFilas.Children.Add(new Border
            {
                Child = fila,
                BorderBrush = Borde, BorderThickness = new Thickness(0, 0, 0, 1)
            });
        }
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    // ---- CURVA: grabar manejando -------------------------------------------
    private StackPanel ArmarCurva(out TextBlock estado, out Button pausa)
    {
        var p = new StackPanel { Spacing = 10, IsVisible = false };

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        fila.Children.Add(BotonIcono("LetterABlue.png", "Iniciar grabación / agregar punto",
            async () => await CurvaIniciarAsync(), 82, 66));
        pausa = BotonIcono("boundaryPause.png", "Pausa/Reanudar",
            async () => await PostAsync("/record-curve-pause"), 82, 66);
        pausa.IsEnabled = false;
        fila.Children.Add(pausa);
        p.Children.Add(fila);

        estado = new TextBlock
        {
            Text = "Tocá A al empezar la pasada", Foreground = TextoMuted,
            FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center
        };
        p.Children.Add(estado);

        var pie = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        pie.Children.Add(BotonIcono("Cancel64.png", "Cancelar (descartar)", async () =>
        {
            PararTimerCurva();
            if (_grabando) await PostAsync("/record-curve-cancel");
            _grabando = false;
            Mostrar("menu");
        }, 82, 66));
        pie.Children.Add(BotonIcono("OK64.png", "Terminar", () =>
        {
            // El nombre se confirma en el paso siguiente; recién ahí se corta
            // la grabación (record-curve-b con el nombre elegido).
            PararTimerCurva();
            AbrirNombre("curva");
        }, 82, 66));
        p.Children.Add(pie);
        return p;
    }

    private async Task CurvaIniciarAsync()
    {
        await PostAsync("/record-curve-a");
        _grabando = true;
        _btnCurvaPausa.IsEnabled = true;
        _curvaEstado.Text = PilotX.Cockpit.Bars.Traductor.T("Grabando");
        PararTimerCurva();
        // El conteo de puntos confirma que la traza está creciendo — mismo
        // feedback que daba la página, pero acá además se VE en el mapa vivo.
        _curvaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _curvaTimer.Tick += async (_, __) =>
        {
            var st = await GetAsync("/record-status");
            if (st != null && st.Value.TryGetProperty("points", out var pts))
                _curvaEstado.Text = PilotX.Cockpit.Bars.Traductor.T("Grabando") + " · " + pts.GetInt32() + " pts";
        };
        _curvaTimer.Start();
    }

    private void PararTimerCurva()
    {
        try { _curvaTimer?.Stop(); } catch { }
        _curvaTimer = null;
    }

    // ---- NOMBRE: paso final de curva / renombrar / duplicar ----------------
    private StackPanel ArmarNombre(out TextBlock lbl, out TextBox txt)
    {
        var p = new StackPanel { Spacing = 10, IsVisible = false };

        lbl = new TextBlock { Text = "Nombre de la guía", Foreground = TextoMuted, FontSize = 13 };
        p.Children.Add(lbl);

        txt = new TextBox
        {
            FontSize = 18, Height = 52,
            Background = new SolidColorBrush(Color.Parse("#F5FBFF")),
        };
        // Enfocar el campo pide el teclado nativo de PilotX por la MISMA señal
        // que mandan las páginas HTML. Las teclas entran solas: la ventana del
        // teclado nunca se activa, así que el TextBox no pierde el foco.
        txt.GotFocus  += (_, __) => _ = TecladoAsync(true);
        txt.LostFocus += (_, __) => _ = TecladoAsync(false);
        p.Children.Add(txt);

        var pie = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        pie.Children.Add(BotonIcono("Cancel64.png", "Cancelar (descartar)", async () =>
        {
            if (_nombrePara == "curva") { await PostAsync("/record-curve-cancel"); _grabando = false; }
            Mostrar(_nombrePara == "curva" ? "menu" : "lista");
        }, 82, 66));
        pie.Children.Add(BotonIcono("OK64.png", "Confirmar", async () => await ConfirmarNombreAsync(), 82, 66));
        p.Children.Add(pie);
        return p;
    }

    private void AbrirNombre(string para)
    {
        _nombrePara = para;
        var t = (_estado.Tracks != null && _estado.SelectedIdx >= 0 &&
                 _estado.SelectedIdx < _estado.Tracks.Count) ? _estado.Tracks[_estado.SelectedIdx] : null;
        _txtNombre.Text = para switch
        {
            "rename"    => t?.Name ?? "",
            "duplicate" => (string.IsNullOrWhiteSpace(t?.Name) ? "Guía" : t!.Name) + " Copia",
            _           => "Cu " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
        };
        _nombreLbl.Text = para == "rename"
            ? PilotX.Cockpit.Bars.Traductor.T("Editar nombre")
            : PilotX.Cockpit.Bars.Traductor.T("Nombre de la guía");
        Mostrar("nombre");
        _txtNombre.Focus();
    }

    private async Task ConfirmarNombreAsync()
    {
        string nombre = (_txtNombre.Text ?? "").Trim();
        switch (_nombrePara)
        {
            case "curva":
                await PostAsync("/record-curve-b", new { name = nombre });
                _grabando = false;
                // Guía creada: ya se ve en el mapa, el panel no tiene más nada
                // que hacer (mismo criterio que pidió el usuario 2026-08-05).
                Cerrar();
                return;
            case "rename":
                await PostAsync("/rename", new { name = nombre });
                break;
            case "duplicate":
                await PostAsync("/duplicate", new { name = nombre });
                break;
        }
        await CargarEstadoAsync();
        Mostrar("lista");
    }

    // =========================================================================
    //  wire /api/tracks — el MISMO que usaba tracks.html
    // =========================================================================

    private async Task CargarEstadoAsync()
    {
        var s = await GetAsync("/state");
        if (s != null) AplicarEstado(s.Value);
        // "Guías guardadas" solo aparece si hay guías (pedido 2026-08-05).
        _btnIrLista.IsVisible = HayGuias;
    }

    private void AplicarEstado(JsonElement raiz)
    {
        try
        {
            var nuevo = raiz.Deserialize<EstadoDto>();
            if (nuevo != null) _estado = nuevo;
            TrazaGuias("estado aplicado: tracks=" + (_estado.Tracks?.Count ?? -1));
        }
        catch (Exception ex) { TrazaGuias("Deserialize FALLO: " + ex); }
    }

    private async Task<JsonElement?> GetAsync(string ruta)
    {
        if (_http == null) { TrazaGuias("GET " + ruta + ": _http NULL (sin Attach)"); return null; }
        try
        {
            var json = await _http.GetStringAsync(_base + "/api/tracks" + ruta);
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (Exception ex) { TrazaGuias("GET " + _base + "/api/tracks" + ruta + " FALLO: " + ex); return null; }
    }

    // Traza a archivo: los catch mudos de este panel se comieron un bug entero
    // ("toco Guías y no lista las guías", 2026-08-11) — en Release no hay
    // Debug.WriteLine y acá no llega ningún logger. Mismo criterio que la
    // traza del WebView (%TEMP%\pilotx-webview.log).
    private static void TrazaGuias(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pilotx-guias.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
        }
        catch { /* la traza nunca puede romper el panel */ }
    }

    private async Task PostAsync(string ruta, object? body = null)
    {
        if (_http == null) return;
        TrazaGuias("POST " + ruta + " body=" + (body == null ? "{}" : JsonSerializer.Serialize(body)));
        try
        {
            using var contenido = new StringContent(
                body == null ? "{}" : JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_base + "/api/tracks" + ruta, contenido);
            var json = await resp.Content.ReadAsStringAsync();
            // Los POST de gestión devuelven el estado nuevo: se aprovecha para
            // no hacer un GET extra por cada toque.
            var raiz = JsonDocument.Parse(json).RootElement;
            if (raiz.TryGetProperty("tracks", out _)) AplicarEstado(raiz.Clone());
        }
        catch { /* el toque no aplicó; la lista queda como estaba */ }
    }

    private async Task TecladoAsync(bool abrir)
    {
        if (_http == null) return;
        try
        {
            using var contenido = new StringContent(
                abrir ? "{\"numerico\":false,\"titulo\":\"Nombre de la guía\"}" : "{}",
                Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _base + "/api/teclado/" + (abrir ? "abrir" : "cerrar"), contenido);
        }
        catch { /* sin teclado nativo el campo sigue editable con teclado físico */ }
    }

    // =========================================================================
    //  helpers de UI
    // =========================================================================

    private static Bitmap? Icono(string nombre)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(
                new Uri("avares://PilotX.Cockpit.Bars/Assets/tracks/" + nombre)));
        }
        catch { return null; }
    }

    private static Button BotonIcono(string icono, string tip, Action accion,
                                     double w = 64, double h = 56)
    {
        var b = new Button
        {
            Width = w, Height = h,
            Background = Brushes.White,
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Image { Source = Icono(icono), Width = w - 26, Height = h - 18 },
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, __) => accion();
        return b;
    }

    private static Button BotonGrande(string texto, string icono, Action accion)
    {
        var pila = new StackPanel { Spacing = 4 };
        pila.Children.Add(new Image
        {
            Source = Icono(icono), Width = 64, Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        pila.Children.Add(new TextBlock
        {
            Text = texto, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Foreground = Texto, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 110
        });
        var b = new Button
        {
            Width = 124, Height = 108,
            Background = Brushes.White,
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = pila,
        };
        b.Click += (_, __) => accion();
        return b;
    }
}
