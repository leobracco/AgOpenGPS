// ============================================================================
// ChatPanel.cs — chat de soporte nativo (capa 1), embebido en la Configuración.
//
// Una conversación humano↔pantalla: el operario escribe desde la cabina y del
// otro lado contesta soporte (o, en capa 2, el bot). Todo el transporte vive en
// el Engine (ChatSoporteService, pull al cloud sin abrir puertos); este panel
// sólo habla con el host local por /api/chat/* vía ChatPanelClient.
//
// Patrón OrbitXPanel: UserControl embebible (IPanelEmbebible), pinceles desde
// los tokens del theme (P), textos por T(), teclado virtual propio de PilotX.
// Construido en código (sin XAML): es una pantalla chica y estable.
//
// Poll: mientras el panel está abierto pide /api/chat/mensajes cada 2 s. Esa
// consulta, del lado del Engine, MARCA leído y acelera el poll al cloud, así
// que basta con estar abierto para que la conversación fluya y el badge del
// menú se apague.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public sealed class ChatPanel : UserControl, IPanelEmbebible
{
    private ChatPanelClient? _client;
    private CancellationTokenSource? _cts;
    private long _revPintada = -1;
    private bool _enviando;
    private bool _resolviendo;

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    // ---- controles ---------------------------------------------------------
    private readonly Border _card;
    private readonly StackPanel _headerTitulo;
    private readonly Button _btnCerrar;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _lista;
    private readonly TextBlock _vacio;
    private readonly TextBox _input;
    private readonly Button _btnEnviar;
    private readonly TextBlock _hint;

    private static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    // ---- pinceles desde los tokens del theme (igual que OrbitXPanel) -------
    private static readonly Dictionary<string, string> Respaldo = new()
    {
        ["PilotXPanelCard"]       = "#FAFBFA",
        ["PilotXPanelSurface"]    = "#FFFFFF",
        ["PilotXPanelSurface2"]   = "#EDF1EC",
        ["PilotXPanelBg"]         = "#F5F7F4",
        ["PilotXPanelBorder"]     = "#E2E7E2",
        ["PilotXPanelBorderHigh"] = "#C5CFC5",
        ["PilotXPanelText"]       = "#101612",
        ["PilotXPanelTextDim"]    = "#535E54",
        ["PilotXPanelAccent"]     = "#4ABA3E",
        ["PilotXPanelOk"]         = "#2F7A26",
        ["PilotXPanelOkSoft"]     = "#E8F4E5",
    };

    private readonly Dictionary<string, IBrush> _pinceles = new();

    private IBrush P(string token)
    {
        if (_pinceles.TryGetValue(token, out var cache)) return cache;
        try
        {
            if (this.TryFindResource(token, out var v) && v is IBrush ib)
            {
                _pinceles[token] = ib;
                return ib;
            }
        }
        catch { }
        return new SolidColorBrush(Color.Parse(Respaldo.TryGetValue(token, out var hex) ? hex : "#535E54"));
    }

    // =========================================================================
    //  construcción
    // =========================================================================

    public ChatPanel()
    {
        // Header: título + ✕ (se ocultan en modo embebido, el shell ya los pone).
        var titulo = new TextBlock
        {
            Text = T("Soporte"),
            Foreground = P("PilotXPanelText"),
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var subt = new TextBlock
        {
            Text = T("Chateá con soporte técnico"),
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tituloCol = new StackPanel { Spacing = 2 };
        tituloCol.Children.Add(titulo);
        tituloCol.Children.Add(subt);

        _btnCerrar = new Button
        {
            Content = "✕",
            Foreground = P("PilotXPanelTextDim"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 20,
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _btnCerrar.Click += (_, __) => OnRequestCerrar?.Invoke();

        _headerTitulo = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12)
        };
        _headerTitulo.Children.Add(tituloCol);
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_headerTitulo, 0);
        Grid.SetColumn(_btnCerrar, 1);
        headerGrid.Children.Add(_headerTitulo);
        headerGrid.Children.Add(_btnCerrar);

        // Lista de mensajes.
        _lista = new StackPanel { Spacing = 8 };
        _vacio = new TextBlock
        {
            Text = T("Todavía no hay mensajes. Escribí abajo para empezar."),
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 16, 4, 0)
        };
        _lista.Children.Add(_vacio);

        _scroll = new ScrollViewer
        {
            Content = _lista,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(2, 0, 8, 0)
        };

        // Barra de entrada.
        _input = new TextBox
        {
            Watermark = T("Escribí un mensaje…"),
            Foreground = P("PilotXPanelText"),
            Background = P("PilotXPanelSurface"),
            BorderBrush = P("PilotXPanelBorderHigh"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 48,
            FontSize = 15,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 4000
        };
        _input.KeyDown += OnInputKeyDown;
        _input.GotFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, T("Mensaje"), false); };
        _input.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };

        _btnEnviar = new Button
        {
            Content = T("Enviar"),
            Background = P("PilotXPanelAccent"),
            // Texto OSCURO sobre el verde de marca (regla del theme: al sol el
            // blanco sobre #4ABA3E no se lee).
            Foreground = P("PilotXPanelText"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            MinHeight = 48,
            Padding = new Thickness(22, 0, 22, 0),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _btnEnviar.Click += (_, __) => _ = EnviarAsync();

        var barra = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 10, 0, 0)
        };
        _input.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_input, 0);
        Grid.SetColumn(_btnEnviar, 1);
        barra.Children.Add(_input);
        barra.Children.Add(_btnEnviar);

        _hint = new TextBlock
        {
            Text = "",
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 2, 0),
            IsVisible = false
        };

        // Cuerpo: header / lista (crece) / barra / hint.
        var cuerpo = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto")
        };
        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(barra, 2);
        Grid.SetRow(_hint, 3);
        cuerpo.Children.Add(headerGrid);
        cuerpo.Children.Add(_scroll);
        cuerpo.Children.Add(barra);
        cuerpo.Children.Add(_hint);

        _card = new Border
        {
            Background = P("PilotXPanelCard"),
            BorderBrush = P("PilotXPanelBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = cuerpo
        };

        Content = _card;
    }

    // =========================================================================
    //  IPanelEmbebible
    // =========================================================================

    /// <summary>Dentro de la Configuración: sin marco de tarjeta y sin ✕ propio
    /// (el shell ya pone todo eso).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(_card);
        PanelEmbebido.Ocultar(_headerTitulo);
        PanelEmbebido.Ocultar(_btnCerrar);
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(ChatPanelClient client)
    {
        _client = client;
        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();
        _ = BuclePollAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _ = _client?.TecladoAsync(false);
    }

    // =========================================================================
    //  poll
    // =========================================================================

    private async Task BuclePollAsync(CancellationToken ct)
    {
        await RefrescarAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(2000), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await RefrescarAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RefrescarAsync(CancellationToken ct)
    {
        if (_client == null) return;
        var r = await _client.MensajesAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || r.Cancelado) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (r.Datos == null || !r.Datos.Ok) return;   // service-unavailable: no repintar
            if (r.Datos.Rev == _revPintada) return;        // sin novedad
            _revPintada = r.Datos.Rev;
            Pintar(r.Datos);
        });
    }

    private void Pintar(ChatMensajesWire d)
    {
        _lista.Children.Clear();

        var msgs = d.Mensajes;
        if (msgs == null || msgs.Count == 0)
        {
            _lista.Children.Add(_vacio);
            return;
        }

        foreach (var m in msgs)
        {
            bool esPropuesta = string.Equals(m.Tipo, "propuesta_config", StringComparison.OrdinalIgnoreCase)
                               && m.Payload != null;
            _lista.Children.Add(esPropuesta ? TarjetaPropuesta(m) : Burbuja(m));
        }

        // Ir al último mensaje.
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Una burbuja: operario a la derecha (verde suave), soporte/bot a
    /// la izquierda (superficie). Debajo, la hora en chico.</summary>
    private Control Burbuja(ChatMensajeWire m)
    {
        bool mio = string.Equals(m.Rol, "operario", StringComparison.OrdinalIgnoreCase);

        var texto = new TextBlock
        {
            Text = m.Texto ?? "",
            Foreground = P("PilotXPanelText"),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        };

        var pie = new TextBlock
        {
            Text = Etiqueta(m, mio),
            Foreground = P("PilotXPanelTextDim"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var pila = new StackPanel { Spacing = 0 };
        pila.Children.Add(texto);
        pila.Children.Add(pie);

        var globo = new Border
        {
            Background = mio ? P("PilotXPanelOkSoft") : P("PilotXPanelSurface"),
            BorderBrush = mio ? P("PilotXPanelAccent") : P("PilotXPanelBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = 520,
            Child = pila,
            HorizontalAlignment = mio ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        return globo;
    }

    /// <summary>"vos · 14:32" / "soporte · 14:30" / "bot · 14:30".</summary>
    private static string Etiqueta(ChatMensajeWire m, bool mio)
    {
        string quien = mio ? "vos"
            : (string.Equals(m.Rol, "bot", StringComparison.OrdinalIgnoreCase) ? "bot" : "soporte");
        return quien + " · " + Hora(m.Ts);
    }

    private static string Hora(long ts)
    {
        if (ts <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ts)
                .ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        }
        catch { return ""; }
    }

    // =========================================================================
    //  propuestas de config (capa 2)
    // =========================================================================

    /// <summary>Una propuesta del bot: tarjeta destacada con la lista de cambios
    /// y, si sigue pendiente, botones Aceptar/Rechazar. La regla es que el
    /// operario decide sí o sí — nada se aplica solo.</summary>
    private Control TarjetaPropuesta(ChatMensajeWire m)
    {
        var pila = new StackPanel { Spacing = 8 };

        // Encabezado.
        pila.Children.Add(new TextBlock
        {
            Text = "⚙  " + T("Sugerencia de configuración"),
            Foreground = P("PilotXPanelText"),
            FontSize = 15,
            FontWeight = FontWeight.Bold
        });

        // Texto del bot (la explicación de por qué).
        if (!string.IsNullOrWhiteSpace(m.Texto))
            pila.Children.Add(new TextBlock
            {
                Text = m.Texto,
                Foreground = P("PilotXPanelText"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            });

        // Lista de cambios: clave  valor_actual → valor_nuevo.
        var cambios = m.Payload?.Cambios;
        if (cambios != null && cambios.Count > 0)
        {
            var box = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var c in cambios)
            {
                string actual = string.IsNullOrEmpty(c.ValorActual) ? "—" : c.ValorActual;
                string nuevo = string.IsNullOrEmpty(c.ValorNuevo) ? "—" : c.ValorNuevo;
                box.Children.Add(new TextBlock
                {
                    Text = (c.Clave ?? "?") + ":   " + actual + "  →  " + nuevo,
                    Foreground = P("PilotXPanelText"),
                    FontFamily = Mono,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            pila.Children.Add(new Border
            {
                Background = P("PilotXPanelSurface2"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Child = box
            });
        }

        string estado = (m.Payload?.Estado ?? "pendiente").ToLowerInvariant();

        if (estado == "pendiente")
        {
            long ts = m.Ts;

            var btnAceptar = new Button
            {
                Content = T("Aceptar"),
                Background = P("PilotXPanelAccent"),
                Foreground = P("PilotXPanelText"),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                MinHeight = 44,
                Padding = new Thickness(20, 0, 20, 0),
                FontSize = 14,
                FontWeight = FontWeight.SemiBold
            };
            btnAceptar.Click += (_, __) => _ = ResolverAsync(ts, "aceptar");

            var btnRechazar = new Button
            {
                Content = T("Rechazar"),
                Background = Brushes.Transparent,
                Foreground = P("PilotXPanelTextDim"),
                BorderBrush = P("PilotXPanelBorderHigh"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                MinHeight = 44,
                Padding = new Thickness(20, 0, 20, 0),
                FontSize = 14,
                Margin = new Thickness(8, 0, 0, 0)
            };
            btnRechazar.Click += (_, __) => _ = ResolverAsync(ts, "rechazar");

            var fila = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };
            fila.Children.Add(btnAceptar);
            fila.Children.Add(btnRechazar);
            pila.Children.Add(fila);
        }
        else
        {
            // Resuelta: mostrar el estado sin botones.
            string etiqueta = estado == "aplicada" ? T("✓ Aplicada")
                            : estado == "aceptada" ? T("✓ Aceptada")
                            : estado == "rechazada" ? T("✕ Rechazada")
                            : estado;
            pila.Children.Add(new TextBlock
            {
                Text = etiqueta + "  ·  " + Hora(m.Ts),
                Foreground = estado == "rechazada" ? P("PilotXPanelTextDim") : P("PilotXPanelOk"),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        return new Border
        {
            Background = P("PilotXPanelOkSoft"),
            BorderBrush = P("PilotXPanelAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 2, 0, 2),
            Child = pila
        };
    }

    /// <summary>Manda la decisión del operario al Engine (que aplica lo seguro y
    /// avisa al cloud) y refresca para repintar la tarjeta ya resuelta.</summary>
    private async Task ResolverAsync(long ts, string decision)
    {
        if (_client == null || _resolviendo) return;
        _resolviendo = true;
        var ct = _cts?.Token ?? CancellationToken.None;

        var r = await _client.ResolverPropuestaAsync(ts, decision, ct).ConfigureAwait(true);

        if (!r.Cancelado)
        {
            if (r.Ok)
            {
                if (!string.IsNullOrEmpty(r.Detalle)) MostrarHint(r.Detalle);
                else MostrarHint(null);
                // Forzar repintado en el próximo refresh (cambió el estado local).
                _revPintada = -1;
                _ = RefrescarAsync(ct);
            }
            else
            {
                MostrarHint(r.Excepcion != null
                    ? T("No se pudo procesar: ") + r.Excepcion
                    : T("No se pudo procesar la sugerencia."));
            }
        }

        _resolviendo = false;
    }

    // =========================================================================
    //  enviar
    // =========================================================================

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter manda; Shift+Enter no (por si algún día se permite multilínea).
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            _ = EnviarAsync();
        }
    }

    private async Task EnviarAsync()
    {
        if (_client == null || _enviando) return;
        string texto = (_input.Text ?? "").Trim();
        if (texto.Length == 0) return;

        _enviando = true;
        _btnEnviar.IsEnabled = false;
        var ct = _cts?.Token ?? CancellationToken.None;

        var r = await _client.EnviarAsync(texto, ct).ConfigureAwait(true);

        if (!r.Cancelado)
        {
            if (r.Ok)
            {
                _input.Text = "";
                MostrarHint(null);
                // Traer el eco local en el acto en vez de esperar al próximo tick.
                _ = RefrescarAsync(ct);
            }
            else
            {
                MostrarHint(r.Excepcion != null
                    ? T("No se pudo enviar: ") + r.Excepcion
                    : T("No se pudo enviar. ¿El tractor está vinculado a OrbitX?"));
            }
        }

        _enviando = false;
        _btnEnviar.IsEnabled = true;
    }

    private void MostrarHint(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            _hint.IsVisible = false;
            _hint.Text = "";
        }
        else
        {
            _hint.Text = texto;
            _hint.IsVisible = true;
        }
    }
}
