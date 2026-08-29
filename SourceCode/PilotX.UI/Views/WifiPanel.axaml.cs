// WifiPanel.axaml.cs
//
// Reemplazo nativo de pages/wifi.html + js/wifi.js. WiFi PROPIO de PilotX: la
// lista de redes con señal y estado real, y la conexión con el teclado de
// PilotX (nunca el del sistema operativo, inservible en kiosko).
//
// Esta pantalla es la que usa el técnico cuando la cabina se quedó sin red, así
// que el port es literal, sin "mejoras":
//   · MISMOS endpoints y verbos (GET /api/red/wifi, POST /api/red/wifi/conectar
//     con { ssid, clave } en el cuerpo, POST /api/red/wifi/desconectar).
//   · MISMO flujo de conexión: se lee la clave del campo, se deshabilita el
//     botón con "Conectando…", y al volver o se cierra el panel y se relista, o
//     se pinta el error que mandó el servicio TAL CUAL ("no-conecto (clave
//     incorrecta o red fuera de alcance)", "service-unavailable", …).
//   · MISMOS estados y textos: "Buscando redes…", "No se ven redes WiFi.
//     Revisá que el equipo tenga WiFi y tocá Actualizar.", "WiFi no disponible
//     en este equipo.", "No se pudo consultar el WiFi.", "No se pudo conectar.",
//     "Sin conexión", "Conectada".
//   · MISMO refresco de fondo de 20 s, PAUSADO mientras hay una red elegida o
//     una conexión en curso (si no, la lista se re-arma abajo del dedo del
//     operario y le borra la clave a medio escribir).
//   · MISMO orden de la lista: el que manda el servicio (conectada primero,
//     después por señal). Acá no se reordena nada.
//
// EL ESCANEO FORZADO SE CONSERVA: el fix histórico (8e25bf16 — "aparecían solo
// las redes ya conectadas") está en WifiServiceWindows.Escanear(), que arranca
// con ForzarScanNativo() antes de parsear netsh. Ese código corre porque este
// panel pega al MISMO GET /api/red/wifi que usaba el JS. No hay ninguna ruta
// alternativa "solo listar" ni cache local de redes en el panel.
//
// Diferencia con el JS, a propósito y por convención del repo: donde el fetch
// entraba a un catch mudo y pintaba un texto fijo, acá ese MISMO texto queda
// arriba y se le suma el código AGP-* dictable y el detalle técnico plegado
// (AgpErrorMapper.FromException).
//
// API: Attach(RedWifiClient) lista y arranca el refresco; Detach() corta el
// polling, cierra el teclado nativo y vuelve el panel a estado limpio.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class WifiPanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private RedWifiClient? _client;
    private CancellationTokenSource? _cts;

    private List<WifiRedWire> _redes = new();
    private WifiEstadoWire? _ultimoEstado;   // lo último que dijo la pill (para repintar al cambiar de idioma)
    private string? _seleccionada;      // ssid elegido (panel de clave abierto)
    private bool _conectando;
    private bool _idiomaEnganchado;     // suscripción viva a Traductor.IdiomaCambio
    private string? _ultimoError;       // texto rojo del panel de conexión
    private string? _errorCodigo;       // AGP-NET-* de la excepción (si hubo)
    private string? _errorAmigable;     // mensaje amable del mapper
    private string? _errorTecnico;      // detalle plegado para soporte

    /// <summary>El campo de clave VIVO. No se busca con FindControl: lo crea el
    /// código en cada pintada y los controles creados a mano no entran al name
    /// scope del XAML (FindControl devolvería null y la clave viajaría vacía).</summary>
    private TextBox? _campoClave;

    /// <summary>Refresco de fondo. Mismo intervalo que el setInterval del JS.</summary>
    private static readonly TimeSpan Refresco = TimeSpan.FromSeconds(20);

    // ---- textos EXACTOS de la página (claves del diccionario) --------------
    private const string TxtBuscando   = "Buscando redes…";
    private const string TxtSinRedes   = "No se ven redes WiFi. Revisá que el equipo tenga WiFi y tocá Actualizar.";
    private const string TxtSinWifi    = "WiFi no disponible en este equipo.";
    private const string TxtSinConsulta= "No se pudo consultar el WiFi.";
    private const string TxtSinConexion= "Sin conexión";
    private const string TxtNoConecto  = "No se pudo conectar.";
    private const string TxtConectada  = "Conectada";
    private const string TxtConectar   = "Conectar";
    private const string TxtConectando = "Conectando…";
    private const string TxtDesconectar= "Desconectar";
    private const string TxtClave      = "Clave de la red";
    private const string TxtConClave   = "Con clave";      // reemplaza al 🔒 del original

    // ---- paleta clara (tokens PilotXPanel* del theme) ----------------------
    private static readonly IBrush Superficie  = new SolidColorBrush(Color.Parse("#FFFFFF"));  // PilotXPanelSurface
    private static readonly IBrush Superficie2 = new SolidColorBrush(Color.Parse("#EDF1EC"));  // PilotXPanelSurface2
    private static readonly IBrush Borde       = new SolidColorBrush(Color.Parse("#E2E7E2"));  // PilotXPanelBorder
    private static readonly IBrush BordeAlto   = new SolidColorBrush(Color.Parse("#C5CFC5"));  // PilotXPanelBorderHigh
    private static readonly IBrush Texto       = new SolidColorBrush(Color.Parse("#101612"));  // PilotXPanelText
    private static readonly IBrush TextoTenue  = new SolidColorBrush(Color.Parse("#535E54"));  // PilotXPanelTextDim
    private static readonly IBrush Verde       = new SolidColorBrush(Color.Parse("#4ABA3E"));  // PilotXPanelAccent
    private static readonly IBrush VerdeTexto  = new SolidColorBrush(Color.Parse("#2F7A26"));  // PilotXPanelAccentText / Ok
    private static readonly IBrush VerdeSuave  = new SolidColorBrush(Color.Parse("#E8F4E5"));  // PilotXPanelOkSoft
    private static readonly IBrush Rojo        = new SolidColorBrush(Color.Parse("#C0261F"));  // PilotXPanelErr

    private static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    /// <summary>Una sola instancia: la fila es tocable (cursor de mano con
    /// mouse, irrelevante con el dedo).</summary>
    private static readonly Cursor Mano = new Cursor(StandardCursorType.Hand);

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public WifiPanel()
    {
        InitializeComponent();
        MostrarCargando();
        // El diccionario se aplica UNA SOLA VEZ, al construir: Aplicar guarda el
        // primer texto de cada control y se lo reescribe encima en cada pasada —
        // llamarlo en los render congelaría la pill de estado en su primer
        // valor (lección de NodosPanel/FirmwaresPanel). Todo lo que escribe el
        // código pasa por T().
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    /// <summary>Cambio de idioma en caliente: sin esto la pantalla quedaba en
    /// el idioma anterior hasta reabrirla. Se engancha en Attach y se SUELTA en
    /// Detach: IdiomaCambio es un evento estático y una suscripción de por vida
    /// deja al panel repintando desde el fondo, cerrado, para siempre. Con el
    /// panel cerrado no hace falta: al reabrirlo, Attach vuelve a listar y
    /// repinta todo.</summary>
    private void EngancharIdioma()
    {
        if (_idiomaEnganchado) return;
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio += OnIdiomaCambio;
        _idiomaEnganchado = true;
    }

    private void SoltarIdioma()
    {
        if (!_idiomaEnganchado) return;
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio -= OnIdiomaCambio;
        _idiomaEnganchado = false;
    }

    /// <summary>Repinta lo que escribe el código. Ojo con dos cosas:
    /// · si el operario tiene el panel de clave abierto NO se rearma la lista —
    ///   el rebuild destruye el campo y le borra la clave a medio escribir (es
    ///   la misma razón por la que el refresco de 20 s se pausa);
    /// · sin redes todavía el cartel que está a la vista puede ser "Buscando
    ///   redes…" o un error, y rearmar acá pondría "No se ven redes WiFi", que
    ///   dice otra cosa. Se deja y lo arregla la próxima pasada.</summary>
    private void OnIdiomaCambio() => Dispatcher.UIThread.Post(() =>
    {
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
        PintarEstado(_ultimoEstado);
        if (_conectando || _seleccionada != null) return;
        if (_redes.Count > 0) Pintar();
    });

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    /// <summary>La pill de estado y el botón "Actualizar" van a la barra de
    /// contexto del shell; la fila de cabecera vieja queda oculta.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<StackPanel>("HeaderPills"));

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Inyecta el cliente, lista con "Buscando redes…" y arranca el
    /// refresco de fondo (equivale a abrir la página: wifi.js llama cargar(true)
    /// al final del script y deja el setInterval corriendo).</summary>
    public void Attach(RedWifiClient client)
    {
        _client = client;
        EngancharIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();
        _ = CargarAsync(true, _cts.Token);
        _ = RefrescoAsync(_cts.Token);
    }

    /// <summary>Corta el polling y lo que esté en vuelo. El panel vuelve a
    /// estado limpio: al reabrirlo se comporta como una página recién cargada.</summary>
    public void Detach()
    {
        SoltarIdioma();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _ = _client?.TecladoAsync(false);
        _seleccionada = null;
        _conectando = false;
        _campoClave = null;
        LimpiarError();
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();

    /// <summary>Botón "Actualizar": suelta la selección, limpia el error y
    /// vuelve a listar mostrando "Buscando redes…".</summary>
    private void OnActualizarClick(object? sender, RoutedEventArgs e)
    {
        _cts ??= new CancellationTokenSource();
        _seleccionada = null;
        LimpiarError();
        _ = CargarAsync(true, _cts.Token);
    }

    // =========================================================================
    //  refresco de fondo (setInterval de 20 s)
    // =========================================================================

    private async Task RefrescoAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Refresco, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            // Pausado con el panel de clave abierto para no re-armar la lista
            // abajo del dedo, y durante una conexión en curso.
            if (_seleccionada == null && !_conectando)
                await CargarAsync(false, ct).ConfigureAwait(false);
        }
    }

    // =========================================================================
    //  cargar() del JS
    // =========================================================================

    private async Task CargarAsync(bool mostrarCargando, CancellationToken ct)
    {
        if (_client == null) return;
        if (mostrarCargando)
            await Dispatcher.UIThread.InvokeAsync(MostrarCargando);

        var r = await _client.GetRedesAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || r.Cancelado) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (r.ErrorRed != null)
            {
                // catch del fetch: el JS pinta el cartel y NO toca la pill de
                // estado (se queda con lo último que se supo). Se replica.
                MostrarAviso(T(TxtSinConsulta), r.ErrorCodigo, r.ErrorAmigable, r.ErrorTecnico);
                return;
            }
            var d = r.Datos;
            if (d == null || !d.Ok)
            {
                MostrarAviso(T(TxtSinWifi), null, null, null);
                PintarEstado(null);
                return;
            }
            _redes = d.Redes ?? new List<WifiRedWire>();
            PintarEstado(d.Estado);
            Pintar();
        });
    }

    // =========================================================================
    //  conectar() / desconectar() del JS
    // =========================================================================

    private async Task ConectarAsync(string ssid)
    {
        if (_client == null) return;
        // La clave se lee ANTES de repintar (el rebuild destruye el campo, igual
        // que el innerHTML del original).
        string clave = _campoClave?.Text ?? "";

        _conectando = true;
        LimpiarError();
        Pintar();

        var ct = _cts?.Token ?? CancellationToken.None;
        var r = await _client.ConectarAsync(ssid, clave, ct).ConfigureAwait(true);
        if (r.Cancelado) return;

        _conectando = false;
        if (r.ErrorRed != null)
        {
            _ultimoError = T(TxtNoConecto);
            _errorCodigo = r.ErrorCodigo;
            _errorAmigable = r.ErrorAmigable;
            _errorTecnico = r.ErrorTecnico;
            Pintar();
            return;
        }
        if (r.Datos != null && r.Datos.Ok)
        {
            _seleccionada = null;
            LimpiarError();
            await CargarAsync(false, ct).ConfigureAwait(true);
            return;
        }
        // `d.error || 'No se pudo conectar.'` — el texto del servicio va TAL
        // CUAL: es el que dice si la clave está mal o si la red no está.
        string? err = r.Datos?.Error;
        _ultimoError = string.IsNullOrEmpty(err) ? T(TxtNoConecto) : err!;
        _errorCodigo = null;
        _errorAmigable = null;
        _errorTecnico = null;
        Pintar();
    }

    private async Task DesconectarAsync()
    {
        if (_client == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        // El JS ignora la respuesta: pase lo que pase suelta la selección y
        // vuelve a listar con "Buscando redes…".
        await _client.DesconectarAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        _seleccionada = null;
        await CargarAsync(true, ct).ConfigureAwait(true);
    }

    // Olvidar (borrar el perfil guardado): deja de reconectar sola y saca la
    // clave. Después vuelve a listar.
    private async Task OlvidarAsync(string ssid)
    {
        if (_client == null || string.IsNullOrEmpty(ssid)) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        await _client.OlvidarAsync(ssid, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        _seleccionada = null;
        await CargarAsync(true, ct).ConfigureAwait(true);
    }

    private Button BotonOlvidar(string ssid)
    {
        var b = new Button
        {
            Content = "Olvidar",
            Background = Superficie2,
            Foreground = Rojo,
            BorderBrush = BordeAlto,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 44,
            Padding = new Thickness(18, 0, 18, 0),
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        b.Click += (_, __) => _ = OlvidarAsync(ssid);
        return b;
    }

    // =========================================================================
    //  pintado
    // =========================================================================

    private void MostrarCargando() => MostrarAviso(T(TxtBuscando), null, null, null);

    /// <summary>.wf-vacio: cartel centrado. Cuando la falla vino de una
    /// excepción se le suma el código AGP dictable y el detalle técnico
    /// plegado (convención del repo).</summary>
    private void MostrarAviso(string texto, string? codigo, string? amigable, string? tecnico)
    {
        var host = this.FindControl<StackPanel>("ListaHost");
        if (host == null) return;
        host.Children.Clear();

        var pila = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(16, 18, 16, 18),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        pila.Children.Add(new TextBlock
        {
            Text = texto,
            Foreground = TextoTenue,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        if (!string.IsNullOrEmpty(codigo))
        {
            pila.Children.Add(new TextBlock
            {
                Text = codigo + " · " + T(amigable ?? ""),
                Foreground = Rojo,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        if (!string.IsNullOrWhiteSpace(tecnico))
        {
            pila.Children.Add(new Expander
            {
                Header = T("Detalle técnico (soporte)"),
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                Content = new TextBlock
                {
                    Text = tecnico,
                    Foreground = TextoTenue,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }
        host.Children.Add(pila);
    }

    /// <summary>pintarEstado(): la pill dice "SSID · IP" en verde cuando hay
    /// conexión, y "Sin conexión" en gris cuando no.</summary>
    private void PintarEstado(WifiEstadoWire? estado)
    {
        _ultimoEstado = estado;
        var box = this.FindControl<Border>("EstadoPillBox");
        var txt = this.FindControl<TextBlock>("EstadoPill");
        if (box == null || txt == null) return;

        if (estado != null && estado.Conectado)
        {
            txt.Text = (estado.Ssid ?? "") +
                       (string.IsNullOrEmpty(estado.Ip) ? "" : " · " + estado.Ip);
            txt.Foreground = VerdeTexto;
            box.BorderBrush = VerdeTexto;
        }
        else
        {
            txt.Text = T(TxtSinConexion);
            txt.Foreground = TextoTenue;
            box.BorderBrush = BordeAlto;
        }
    }

    /// <summary>pintar(): rearma la lista entera, igual que el innerHTML del
    /// original.</summary>
    private void Pintar()
    {
        var host = this.FindControl<StackPanel>("ListaHost");
        if (host == null) return;

        _campoClave = null;   // lo vuelve a crear FilaRed si la elegida lo pide

        if (_redes.Count == 0)
        {
            MostrarAviso(T(TxtSinRedes), null, null, null);
            return;
        }

        host.Children.Clear();
        foreach (var r in _redes) host.Children.Add(FilaRed(r));

        // Enfocar la clave recién pintada: dispara el teclado propio del host
        // (el GotFocus del campo manda POST /api/teclado/abrir).
        var clave = _campoClave;
        if (!_conectando && clave != null)
            Dispatcher.UIThread.Post(() => { try { clave.Focus(); } catch { } },
                                     DispatcherPriority.Background);
    }

    /// <summary>Una red (.wf-red): fila con barras + SSID + candado + % + badge,
    /// y —si está elegida— el panel de conexión abajo.</summary>
    private Control FilaRed(WifiRedWire r)
    {
        bool sel = _seleccionada != null && _seleccionada == (r.Ssid ?? "");

        var pila = new StackPanel { Spacing = 0 };

        // ---- .wf-red-fila ----
        var fila = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            MinHeight = 44,
            Background = Brushes.Transparent
        };

        var barras = Barras(NivelSenal(r.SenalPct));
        Grid.SetColumn(barras, 0);

        var ssid = new TextBlock
        {
            Text = r.Ssid ?? "",
            Foreground = Texto,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };
        Grid.SetColumn(ssid, 1);

        var candado = new TextBlock
        {
            // El original ponía el candado 🔒. Acá va el texto: en Avalonia el
            // emoji depende de la fuente instalada y en la pantalla de cabina
            // sale cuadradito.
            Text = r.Segura ? T(TxtConClave) : "",
            Foreground = TextoTenue,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(candado, 2);

        var senal = new TextBlock
        {
            Text = r.SenalPct.ToString(CultureInfo.InvariantCulture) + "%",
            Foreground = TextoTenue,
            FontSize = 12,
            FontFamily = Mono,
            MinWidth = 42,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(senal, 3);

        var badge = new Border
        {
            IsVisible = r.Conectada,
            Background = VerdeSuave,
            BorderBrush = VerdeTexto,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = T(TxtConectada),
                Foreground = VerdeTexto,
                FontSize = 11,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(badge, 4);

        fila.Children.Add(barras);
        fila.Children.Add(ssid);
        fila.Children.Add(candado);
        fila.Children.Add(senal);
        fila.Children.Add(badge);
        pila.Children.Add(fila);

        // ---- .wf-conectar (solo en la elegida) ----
        if (sel) pila.Children.Add(PanelConexion(r));

        var caja = new Border
        {
            Background = Superficie,
            BorderBrush = sel ? BordeAlto : Borde,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Cursor = Mano,
            Child = pila
        };

        // Toque en la fila: alterna la selección. Igual que el JS, un toque en
        // el campo de clave o en un botón NO colapsa el panel.
        string ssidClave = r.Ssid ?? "";
        caja.Tapped += (_, e) =>
        {
            if (EsControlInteractivo(e.Source as Visual, caja)) return;
            _seleccionada = (_seleccionada == ssidClave) ? null : ssidClave;
            LimpiarError();
            Pintar();
        };

        return caja;
    }

    /// <summary>Panel de conexión desplegado bajo la red elegida.</summary>
    private Control PanelConexion(WifiRedWire r)
    {
        var pila = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 2) };

        // Borde superior (.wf-conectar { border-top }).
        pila.Children.Add(new Border { Height = 1, Background = Borde, Margin = new Thickness(0, 0, 0, 4) });

        if (!string.IsNullOrEmpty(_ultimoError))
        {
            pila.Children.Add(new TextBlock
            {
                Text = _ultimoError,
                Foreground = Rojo,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(_errorCodigo))
            {
                pila.Children.Add(new TextBlock
                {
                    Text = _errorCodigo + " · " + T(_errorAmigable ?? ""),
                    Foreground = Rojo,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            if (!string.IsNullOrWhiteSpace(_errorTecnico))
            {
                pila.Children.Add(new Expander
                {
                    Header = T("Detalle técnico (soporte)"),
                    IsExpanded = false,
                    Content = new TextBlock
                    {
                        Text = _errorTecnico,
                        Foreground = TextoTenue,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }
        }

        if (r.Conectada)
        {
            var btn = new Button
            {
                Content = T(TxtDesconectar),
                Background = Superficie2,
                Foreground = Texto,
                BorderBrush = BordeAlto,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 44,
                Padding = new Thickness(18, 0, 18, 0),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            btn.Click += (_, __) => _ = DesconectarAsync();
            var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            fila.Children.Add(btn);
            fila.Children.Add(BotonOlvidar(r.Ssid ?? ""));
            pila.Children.Add(fila);
            return pila;
        }

        var linea = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        if (r.Segura)
        {
            var clave = new TextBox
            {
                Text = "",
                Watermark = T(TxtClave),
                PasswordChar = '●',
                MinHeight = 44,
                FontSize = 14,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 0, 10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Superficie,
                Foreground = Texto,
                BorderBrush = BordeAlto,
                Margin = new Thickness(0, 0, 8, 0)
            };
            // Teclado NATIVO de PilotX (nunca osk.exe): el campo lo pide a mano,
            // igual que keyboard.js con un input type="password" (qwerty).
            clave.GotFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, false, T(TxtClave)); };
            clave.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
            Grid.SetColumn(clave, 0);
            linea.Children.Add(clave);
            _campoClave = clave;
        }

        string ssidClave = r.Ssid ?? "";
        var conectar = new Button
        {
            Content = _conectando ? T(TxtConectando) : T(TxtConectar),
            IsEnabled = !_conectando,
            Background = Verde,
            Foreground = Texto,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            MinHeight = 44,
            MinWidth = 130,
            Padding = new Thickness(18, 0, 18, 0),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        conectar.Click += (_, __) => _ = ConectarAsync(ssidClave);
        Grid.SetColumn(conectar, 1);
        linea.Children.Add(conectar);

        pila.Children.Add(linea);

        // Si ya nos conectamos alguna vez, ofrecer borrar el perfil guardado
        // (con su clave) aunque ahora no esté conectada.
        if (r.Guardada)
        {
            var olvidar = BotonOlvidar(ssidClave);
            olvidar.Margin = new Thickness(0, 8, 0, 0);
            olvidar.HorizontalAlignment = HorizontalAlignment.Left;
            pila.Children.Add(olvidar);
        }

        return pila;
    }

    /// <summary>Barras de señal (4 niveles), mismas alturas que el CSS.</summary>
    private static Control Barras(int nivel)
    {
        var pila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        double[] altos = { 5, 8, 12, 16 };
        for (int i = 0; i < 4; i++)
        {
            pila.Children.Add(new Rectangle
            {
                Width = 4,
                Height = altos[i],
                RadiusX = 1,
                RadiusY = 1,
                VerticalAlignment = VerticalAlignment.Bottom,
                Fill = i < nivel ? Verde : Borde
            });
        }
        return pila;
    }

    /// <summary>nivelSenal(): 75+ = 4, 50+ = 3, 25+ = 2, resto 1.</summary>
    private static int NivelSenal(int pct)
    {
        if (pct >= 75) return 4;
        if (pct >= 50) return 3;
        if (pct >= 25) return 2;
        return 1;
    }

    private void LimpiarError()
    {
        _ultimoError = null;
        _errorCodigo = null;
        _errorAmigable = null;
        _errorTecnico = null;
    }

    /// <summary>¿El toque cayó sobre un botón o sobre el campo de clave? El JS
    /// hace lo mismo con `e.target.tagName === 'INPUT'` y con
    /// `closest('[data-act]')`: esos toques no colapsan el panel.</summary>
    private static bool EsControlInteractivo(Visual? origen, Visual tope)
    {
        var v = origen;
        while (v != null && !ReferenceEquals(v, tope))
        {
            if (v is Button || v is TextBox || v is Expander) return true;
            v = v.GetVisualParent();
        }
        return false;
    }
}
