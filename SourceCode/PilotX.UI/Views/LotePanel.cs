// ============================================================================
// LotePanel.cs — el menú de lote (FormJob), NATIVO (16vo port, ex lote.html).
//
// Segundo diálogo de labor portado (después de Guías): abrir/cerrar lote se
// toca varias veces al día y levantaba Chromium cada vez. Como panel sobre el
// mapa, además, abrir un lote se VE: el lindero y la cobertura aparecen
// detrás mientras cargan, que era el progreso que la ventana tapaba.
//
// Pantallas: MENÚ (Continuar / Abrir / Nuevo / Desde existente / Entrar al
// lote / Cerrar lote) → LISTA (abrir, o elegir plantilla) → NOMBRE (nuevo o
// clonado). Deep-links del submenú LOTE de la barra izquierda: "abrir" y
// "nuevo" saltan directo a su pantalla, igual que hacía ?do= en lote.js.
//
// "Desde existente" acá FUNCIONA: la página mandaba POST {} y el backend
// exige {template, name} — devolvía body-invalido desde siempre y la ventana
// se cerraba como si nada. El flujo real es elegir el lote plantilla de la
// lista, ponerle nombre al nuevo, y clonar lindero+guías+cabecera+banderas
// (la cobertura pintada NO se copia: es trabajo del lote viejo).
//
// Sin KML ni ISO-XML en el menú: KML salió por pedido del usuario
// (2026-08-06) y la importación quedó para el Hub.
//
// Abrir un lote NO espera la respuesta: se dispara y el panel se cierra en
// el acto — el lote termina de cargar contra el mapa. Es el mismo criterio
// que ya tenía la página (el operario veía la ventana "colgada" varios
// segundos sin saber si había registrado el toque).
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

namespace PilotX.Desktop.Views;

public sealed class LotePanel : Border
{
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush RojoTexto  = new SolidColorBrush(Color.Parse("#C0504A"));

    private HttpClient? _http;
    private string _base = "";

    /// <summary>El panel se cerró (por acción o por la X).</summary>
    public event Action? Cerrado;

    private sealed class LoteDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("area_ha")] public double? AreaHa { get; set; }
    }

    private string? _actual;          // lote abierto ahora (null = ninguno)
    private List<LoteDto> _lotes = new();

    private readonly TextBlock _titulo;
    private readonly Grid _scMenu;
    private readonly StackPanel _scLista;
    private readonly StackPanel _scNombre;

    private readonly TextBlock _pie;          // "Abierto: X" / "Sin lote abierto"
    private readonly Button _btnCerrarLote;
    private readonly StackPanel _listaFilas;
    private readonly TextBlock _listaTitulo;
    private readonly TextBox _txtNombre;
    private readonly TextBlock _nombreLbl;

    // La lista sirve para dos cosas; el modo decide qué hace el toque.
    private string _modoLista = "abrir";      // "abrir" | "plantilla"
    private string _plantilla = "";           // elegida para "desde existente"

    public LotePanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        Width = 540;
        IsVisible = false;

        _titulo = new TextBlock
        {
            Text = "Lote", FontSize = 13, FontWeight = FontWeight.Bold,
            Foreground = TextoMuted, Margin = new Thickness(4, 0, 0, 8)
        };

        _scMenu   = ArmarMenu(out _pie, out _btnCerrarLote);
        _scLista  = ArmarLista(out _listaTitulo, out _listaFilas);
        _scNombre = ArmarNombre(out _nombreLbl, out _txtNombre);

        var stage = new Panel();
        stage.Children.Add(_scMenu);
        stage.Children.Add(_scLista);
        stage.Children.Add(_scNombre);

        var root = new StackPanel();
        root.Children.Add(_titulo);
        root.Children.Add(stage);
        Child = root;
    }

    public void Attach(HttpClient http, string baseUrl)
    {
        _http = http;
        _base = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Abre el panel. <paramref name="pantalla"/>: "menu" | "abrir" | "nuevo"
    /// (los deep-links del submenú LOTE, igual que ?do= en la página).
    /// </summary>
    public async void Abrir(string pantalla = "menu")
    {
        await CargarActualAsync();
        switch (pantalla)
        {
            case "abrir":
                _modoLista = "abrir";
                Mostrar("lista");
                await CargarListaAsync();
                break;
            case "nuevo":
                AbrirNombre(paraPlantilla: false);
                break;
            default:
                Mostrar("menu");
                break;
        }
        IsVisible = true;
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    public void Cerrar()
    {
        _ = TecladoAsync(false);
        IsVisible = false;
        Cerrado?.Invoke();
    }

    // =========================================================================
    //  pantallas
    // =========================================================================

    private void Mostrar(string cual)
    {
        _scMenu.IsVisible   = cual == "menu";
        _scLista.IsVisible  = cual == "lista";
        _scNombre.IsVisible = cual == "nombre";
        _titulo.Text = cual switch
        {
            "lista"  => _modoLista == "plantilla" ? "Desde existente" : "Abrir lote",
            "nombre" => "Nuevo lote",
            _        => "Lote",
        };
        if (cual != "nombre") _ = TecladoAsync(false);
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    // ---- MENÚ (FormJob): grid 2 columnas -----------------------------------
    private Grid ArmarMenu(out TextBlock pie, out Button btnCerrarLote)
    {
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };

        void Poner(Control c, int fila, int col)
        {
            Grid.SetRow(c, fila); Grid.SetColumn(c, col);
            g.Children.Add(c);
        }

        // Columna derecha = lo de todos los días (Continuar primero);
        // izquierda = lo ocasional. Mismo reparto que tenía FormJob.
        var btnContinuar = BotonMenu("Continuar", "FilePrevious.png", esPrimario: true, async () =>
        {
            // El backend resuelve __resume__ = "el último que estuvo abierto".
            DispararAbrir(_actual ?? "__resume__");
            await Task.CompletedTask;
        });
        var btnAbrir = BotonMenu("Abrir", "FileOpen.png", false, async () =>
        {
            _modoLista = "abrir";
            Mostrar("lista");
            await CargarListaAsync();
        });
        var btnEntrar = BotonMenu("Entrar al lote", "AutoManualIsAuto.png", false, async () =>
        {
            // El más cercano a la posición GPS actual; sin cercanos, la lista.
            var cerca = await GetAsync("/api/lotes?near=1");
            var lista = ParsearLotes(cerca);
            if (lista.Count > 0 && !string.IsNullOrWhiteSpace(lista[0].Name))
            {
                DispararAbrir(lista[0].Name!);
            }
            else
            {
                _modoLista = "abrir";
                Mostrar("lista");
                await CargarListaAsync();
            }
        });
        btnCerrarLote = BotonMenu("Cerrar lote", "FileClose.png", false, async () =>
        {
            await PostAsync("/api/lotes/close");
            Cerrar();
        });
        ((TextBlock)((StackPanel)btnCerrarLote.Content!).Children[1]).Foreground = RojoTexto;

        var btnNuevo = BotonMenu("Nuevo lote", "FileNew.png", false, () =>
        {
            AbrirNombre(paraPlantilla: false);
            return Task.CompletedTask;
        });
        var btnExistente = BotonMenu("Desde existente", "FileExisting.png", false, async () =>
        {
            _modoLista = "plantilla";
            Mostrar("lista");
            await CargarListaAsync();
        });

        Poner(btnNuevo,      0, 0); Poner(btnContinuar, 0, 1);
        Poner(btnExistente,  1, 0); Poner(btnAbrir,     1, 1);
        Poner(btnEntrar,     2, 0); Poner(btnCerrarLote,2, 1);

        pie = new TextBlock
        {
            Text = "Sin lote abierto", FontSize = 12, Foreground = TextoMuted,
            Margin = new Thickness(6, 10, 0, 0)
        };
        var pieYSalir = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pieYSalir.Children.Add(pie);
        var salir = new Button
        {
            Content = "✕", Width = 46, Height = 40, FontSize = 16,
            Background = Brushes.White, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(salir, "Cerrar");
        salir.Click += (_, __) => Cerrar();
        Grid.SetColumn(salir, 1);
        pieYSalir.Children.Add(salir);
        Grid.SetRow(pieYSalir, 3); Grid.SetColumnSpan(pieYSalir, 2);
        g.Children.Add(pieYSalir);

        return g;
    }

    // ---- LISTA (abrir o elegir plantilla) ----------------------------------
    private StackPanel ArmarLista(out TextBlock titulo, out StackPanel filas)
    {
        var p = new StackPanel { Spacing = 8, IsVisible = false };

        var cab = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var volver = new Button
        {
            Content = "‹ Volver", Height = 40, Padding = new Thickness(12, 0),
            Background = Brushes.White, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        };
        volver.Click += (_, __) => Mostrar("menu");
        cab.Children.Add(volver);
        titulo = new TextBlock
        {
            Text = "", FontSize = 12, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(titulo, 1);
        cab.Children.Add(titulo);
        p.Children.Add(cab);

        filas = new StackPanel { Spacing = 4 };
        p.Children.Add(new Border
        {
            Child = new ScrollViewer
            {
                Content = filas, Height = 320,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            },
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Background = BgFila, Padding = new Thickness(6)
        });
        return p;
    }

    private async Task CargarListaAsync()
    {
        _listaFilas.Children.Clear();
        _listaFilas.Children.Add(new TextBlock
        {
            Text = "Cargando…", Foreground = TextoMuted, Margin = new Thickness(8)
        });
        _listaTitulo.Text = _modoLista == "plantilla"
            ? PilotX.Cockpit.Bars.Traductor.T("Tocá el lote que querés usar de plantilla")
            : PilotX.Cockpit.Bars.Traductor.T("Tocá un lote para abrirlo");

        var raiz = await GetAsync("/api/lotes");
        _lotes = ParsearLotes(raiz);

        _listaFilas.Children.Clear();
        if (_lotes.Count == 0)
        {
            _listaFilas.Children.Add(new TextBlock
            {
                Text = "No hay lotes", Foreground = TextoMuted, Margin = new Thickness(8)
            });
            PilotX.Cockpit.Bars.Traductor.Aplicar(this);
            return;
        }

        foreach (var l in _lotes)
        {
            string nombre = l.Name ?? "";
            if (nombre.Length == 0) continue;

            var fila = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 48 };
            // Nombre del lote = dato del operario: no se traduce.
            var b = new Button
            {
                Content = nombre,
                Background = nombre == _actual ? new SolidColorBrush(Color.Parse("#DCEFD8")) : BgFila,
                Foreground = Texto, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 16, Padding = new Thickness(10, 0, 0, 0), Height = 48,
            };
            b.Click += (_, __) =>
            {
                if (_modoLista == "plantilla")
                {
                    _plantilla = nombre;
                    AbrirNombre(paraPlantilla: true);
                }
                else
                {
                    DispararAbrir(nombre);
                }
            };
            fila.Children.Add(b);

            if (l.AreaHa != null)
            {
                var ha = new TextBlock
                {
                    Text = l.AreaHa.Value.ToString("0.0", CultureInfo.CurrentCulture) + " ha",
                    Foreground = TextoMuted, FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0)
                };
                Grid.SetColumn(ha, 1);
                fila.Children.Add(ha);
            }
            _listaFilas.Children.Add(new Border
            {
                Child = fila, BorderBrush = Borde,
                BorderThickness = new Thickness(0, 0, 0, 1)
            });
        }
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    // ---- NOMBRE (nuevo lote, o nombre del clon) ----------------------------
    private StackPanel ArmarNombre(out TextBlock lbl, out TextBox txt)
    {
        var p = new StackPanel { Spacing = 10, IsVisible = false };

        lbl = new TextBlock { Text = "Nombre del lote", Foreground = TextoMuted, FontSize = 13 };
        p.Children.Add(lbl);

        txt = new TextBox
        {
            FontSize = 18, Height = 52, Watermark = "Ej: Lote 12 — Maíz",
            Background = new SolidColorBrush(Color.Parse("#F5FBFF")),
        };
        txt.GotFocus  += (_, __) => _ = TecladoAsync(true);
        txt.LostFocus += (_, __) => _ = TecladoAsync(false);
        p.Children.Add(txt);

        var pie = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var cancelar = new Button
        {
            Content = "Cancelar", Height = 46, Padding = new Thickness(16, 0),
            Background = Brushes.White, BorderBrush = Borde,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        };
        cancelar.Click += (_, __) => Mostrar("menu");
        var crear = new Button
        {
            Content = "Crear", Height = 46, Padding = new Thickness(20, 0),
            Background = Verde, Foreground = Brushes.White,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            FontWeight = FontWeight.Bold,
        };
        crear.Click += async (_, __) => await CrearAsync();
        pie.Children.Add(cancelar);
        pie.Children.Add(crear);
        p.Children.Add(pie);
        return p;
    }

    private void AbrirNombre(bool paraPlantilla)
    {
        _txtNombre.Text = paraPlantilla && _plantilla.Length > 0 ? _plantilla + " 2" : "";
        _nombreLbl.Text = paraPlantilla
            ? PilotX.Cockpit.Bars.Traductor.T("Nombre del lote nuevo (clonado de") + " " + _plantilla + ")"
            : PilotX.Cockpit.Bars.Traductor.T("Nombre del lote");
        if (!paraPlantilla) _plantilla = "";
        Mostrar("nombre");
        _txtNombre.Focus();
    }

    private async Task CrearAsync()
    {
        string nombre = (_txtNombre.Text ?? "").Trim();
        if (nombre.Length == 0) return;

        if (_plantilla.Length > 0)
        {
            // Clonar: lindero + guías + cabecera + banderas del lote plantilla.
            // La cobertura pintada NO (applied=false): es trabajo del lote viejo,
            // arrastrarla haría que el anti-solape saltee lo "ya sembrado".
            await PostAsync("/api/lotes/from-existing", new
            {
                template = _plantilla, name = nombre,
                applied = false, flags = true, guidance = true, headland = true
            });
        }
        else
        {
            await PostAsync("/api/lotes/create?name=" + Uri.EscapeDataString(nombre));
        }
        Cerrar();
    }

    // =========================================================================
    //  wire /api/lotes
    // =========================================================================

    private async Task CargarActualAsync()
    {
        var c = await GetAsync("/api/lotes/current");
        _actual = null;
        if (c != null && c.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            _actual = n.GetString();

        if (!string.IsNullOrEmpty(_actual))
        {
            // El nombre del lote va tal cual (dato), el prefijo se traduce.
            _pie.Text = PilotX.Cockpit.Bars.Traductor.T("Abierto:") + " " + _actual;
            _btnCerrarLote.IsEnabled = true;
        }
        else
        {
            _pie.Text = PilotX.Cockpit.Bars.Traductor.T("Sin lote abierto");
            _btnCerrarLote.IsEnabled = false;
        }
    }

    /// <summary>
    /// Dispara la apertura y cierra el panel SIN esperar: abrir carga lindero,
    /// cobertura y guías (varios segundos) y el progreso se ve en el mapa.
    /// </summary>
    private void DispararAbrir(string nombre)
    {
        _ = PostAsync("/api/lotes/open?name=" + Uri.EscapeDataString(nombre));
        Cerrar();
    }

    private static List<LoteDto> ParsearLotes(JsonElement? raiz)
    {
        var lista = new List<LoteDto>();
        if (raiz == null) return lista;
        try
        {
            var r = raiz.Value;
            var arr = r.ValueKind == JsonValueKind.Array ? r
                    : (r.TryGetProperty("lotes", out var l) && l.ValueKind == JsonValueKind.Array ? l : default);
            if (arr.ValueKind != JsonValueKind.Array) return lista;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                    lista.Add(new LoteDto { Name = e.GetString() });
                else
                {
                    var d = e.Deserialize<LoteDto>();
                    if (d != null) lista.Add(d);
                }
            }
        }
        catch { }
        return lista;
    }

    private async Task<JsonElement?> GetAsync(string ruta)
    {
        if (_http == null) return null;
        try
        {
            var json = await _http.GetStringAsync(_base + ruta);
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch { return null; }
    }

    private async Task PostAsync(string ruta, object? body = null)
    {
        if (_http == null) return;
        try
        {
            using var contenido = new StringContent(
                body == null ? "{}" : JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_base + ruta, contenido);
        }
        catch { /* si el POST no salió, el efecto no ocurre y se ve en el mapa */ }
    }

    private async Task TecladoAsync(bool abrir)
    {
        if (_http == null) return;
        try
        {
            using var contenido = new StringContent(
                abrir ? "{\"numerico\":false,\"titulo\":\"Nombre del lote\"}" : "{}",
                Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(
                _base + "/api/teclado/" + (abrir ? "abrir" : "cerrar"), contenido);
        }
        catch { }
    }

    // =========================================================================
    //  helpers de UI
    // =========================================================================

    private static Bitmap? Icono(string nombre)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(
                new Uri("avares://PilotX.Cockpit.Bars/Assets/lote/" + nombre)));
        }
        catch { return null; }
    }

    private static Button BotonMenu(string texto, string icono, bool esPrimario, Func<Task> accion)
    {
        var pila = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        pila.Children.Add(new Image { Source = Icono(icono), Width = 40, Height = 40 });
        pila.Children.Add(new TextBlock
        {
            Text = texto, FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = esPrimario ? Verde : Texto,
            VerticalAlignment = VerticalAlignment.Center
        });
        var b = new Button
        {
            Height = 64, Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 0),
            Background = Brushes.White,
            BorderBrush = esPrimario ? Verde : Borde,
            BorderThickness = new Thickness(esPrimario ? 2 : 1),
            CornerRadius = new CornerRadius(10),
            Content = pila,
        };
        b.Click += async (_, __) => { try { await accion(); } catch { } };
        return b;
    }
}
