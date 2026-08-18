// ============================================================================
// SonidosPanel.cs — las alarmas sonoras de cabina, NATIVAS (reemplaza
// pages/sonidos.html en PilotX.Desktop).
//
// Por qué se portó: era la última pantalla de "qué avisa la cabina" que salía
// por WebView. Abrirla despertaba Chromium entero (~300 MB) para tocar siete
// casillas y elegir un wav. Ahora es una card clara flotante con el mapa VIVO
// detrás, y el Desktop no instancia Chromium para nada de Sonidos.
//
// Qué quedó NATIVO (paridad completa con la página):
//   · lista de eventos del server (habilitado, sonido, umbral, sostenido,
//     repetir) con la misma visibilidad condicional por id que el JS;
//   · Silenciar todo (mute) — guarda el cfg completo, igual que la página;
//   · alarmas activas refrescadas cada 2 s;
//   · ▶ probar el sonido elegido EN PANTALLA (no el guardado);
//   · Subir sonido propio (.wav): el <input type=file> se reemplazó por el
//     file-picker del sistema (StorageProvider) — mismo POST de bytes crudos.
//
// Qué NO hace este panel (a propósito): NO suena. El que hace sonar la cabina
// es SoundAlarmPoller (500 ms, con su propio seq); acá se pide el estado con
// desde=long.MaxValue justamente para NO recibir disparos. Consumirlos acá
// sonaría todo dos veces. El ▶ usa el MISMO sink de audio del head
// (SoundAlarmPoller.WavSink) — cero código de audio nuevo.
//
// La página pages/sonidos.html NO se borra ni se toca: la sigue usando la PWA
// del celular y el módulo 🔔 Sonidos del Hub remoto.
//
// Regla que no se puede romper (spec, riesgo 2): la config se lee al ABRIR
// (y tras guardar/mutear). El tick de 2 s solo repinta las activas y el estado
// del mute — releerla en cada tick borraría lo que el operario está tipeando.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public sealed class SonidosPanel : Border, IPanelEmbebible
{
    // ---- paleta PilotX (misma que los demás paneles del cockpit) -----------
    private static readonly IBrush BgPanel    = new SolidColorBrush(Color.Parse("#FAFBFA"));
    private static readonly IBrush BgFila     = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BgSuave    = new SolidColorBrush(Color.Parse("#F5F7F4"));
    private static readonly IBrush Borde      = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush BordeSuave = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush TextoDim   = new SolidColorBrush(Color.Parse("#7A857B"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush Ok         = new SolidColorBrush(Color.Parse("#3D9A33"));
    private static readonly IBrush Warn       = new SolidColorBrush(Color.Parse("#B98A2E"));
    private static readonly IBrush Err        = new SolidColorBrush(Color.Parse("#D0504A"));
    private static readonly IBrush Backdrop   = new SolidColorBrush(Color.Parse("#66101612"));

    /// <summary>El operario tocó la X. El host decide (CloseSonidos).</summary>
    public Action? OnRequestCerrar { get; set; }

    /// <summary>Aviso corto para el operario — lo muestra MainWindow como toast,
    /// nunca modal (el modal trababa la cabina).</summary>
    public event Action<string>? Aviso;

    private SonidosClient? _client;
    private CancellationTokenSource? _cts;
    private SonidosConfigWire? _cfg;
    private List<string> _archivos = new();
    private readonly Dictionary<string, byte[]> _cacheWav = new();
    private long _archivosRev;        // revisión de /sounds vista en el último tick
    private bool _tieneArchivosRev;   // false = todavía sin dato (no tocar el cache)
    // Ventana de gracia tras tocar Silenciar: el tick no puede pisar el estado
    // que el operario acaba de elegir mientras el PUT viaja.
    private DateTime _muteTocadoUtc = DateTime.MinValue;

    // ---- controles persistentes -------------------------------------------
    private readonly Button _btnMute;
    private readonly TextBlock _btnMuteTexto;
    private readonly StackPanel _alarmasHost;
    private readonly StackPanel _eventosHost;
    private readonly TextBlock _msg;
    private readonly Border _picker;
    private readonly StackPanel _pickerLista;
    // Referencias de cabecera para ModoEmbebido (título y ✕ redundantes
    // cuando el panel vive adentro de la Configuración) y para
    // PillsDeContexto (las acciones que se mudan a la barra del shell).
    private readonly StackPanel _cabeceraTitulo;
    private readonly Button _btnCerrarPropio;
    private readonly StackPanel _cabeceraAcciones;

    /// <summary>Una fila de la lista: el evento del wire + sus controles.</summary>
    private sealed class Fila
    {
        public SonidoEventoWire Ev = new();
        public Border Marco = new();
        public Border Check = new();
        public TextBlock CheckTilde = new();
        public Button BtnSonido = new();
        public TextBlock BtnSonidoTexto = new();
        public string Sonido = "";
        public TextBox? Umbral;
        public TextBox? Sostenido;
        public TextBox? Repetir;
    }
    private readonly List<Fila> _filas = new();

    public SonidosPanel()
    {
        Background = BgPanel;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14);
        BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612");
        MaxWidth = 860;
        MaxHeight = 640;
        IsVisible = false;

        // ---------------- cabecera ----------------
        var titulo = new TextBlock
        {
            Text = "Sonidos", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Texto
        };
        var subtitulo = new TextBlock
        {
            Text = "Qué avisa la cabina, con qué sonido y cuándo",
            FontSize = 12, Foreground = TextoMuted
        };
        var izqCabecera = new StackPanel { Spacing = 2 };
        izqCabecera.Children.Add(titulo);
        izqCabecera.Children.Add(subtitulo);
        _cabeceraTitulo = izqCabecera;

        _btnMuteTexto = new TextBlock
        {
            Text = "Silenciar todo", FontSize = 13, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _btnMute = BotonPlano(_btnMuteTexto, 200);
        _btnMute.Click += (_, __) => _ = AlternarMuteAsync();

        var btnSubir = BotonTexto("Subir sonido propio (.wav)", 210);
        btnSubir.Click += (_, __) => _ = SubirWavAsync();

        var btnCerrar = BotonTexto("✕", 52);
        btnCerrar.Click += (_, __) => OnRequestCerrar?.Invoke();
        _btnCerrarPropio = btnCerrar;

        var derCabecera = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        derCabecera.Children.Add(_btnMute);
        derCabecera.Children.Add(btnSubir);
        derCabecera.Children.Add(btnCerrar);
        _cabeceraAcciones = derCabecera;

        var cabecera = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(izqCabecera, 0);
        Grid.SetColumn(derCabecera, 1);
        cabecera.Children.Add(izqCabecera);
        cabecera.Children.Add(derCabecera);

        // ---------------- alarmas activas ----------------
        _alarmasHost = new StackPanel { Spacing = 4 };
        var cardAlarmas = new Border
        {
            Background = BgFila, BorderBrush = BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = _alarmasHost
        };

        // ---------------- eventos ----------------
        var tituloEventos = new TextBlock
        {
            Text = "EVENTOS", FontSize = 10, FontWeight = FontWeight.SemiBold,
            Foreground = TextoDim, Margin = new Thickness(2, 12, 0, 6)
        };
        _eventosHost = new StackPanel { Spacing = 0 };
        var scroll = new ScrollViewer
        {
            Content = _eventosHost, MaxHeight = 320,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        // ---------------- pie ----------------
        var btnGuardar = BotonTexto("Guardar", 130);
        btnGuardar.Background = Verde;
        btnGuardar.BorderBrush = Verde;
        btnGuardar.Foreground = Brushes.White;
        btnGuardar.Click += (_, __) => _ = GuardarAsync();

        _msg = new TextBlock
        {
            Text = "", FontSize = 13, Foreground = TextoMuted,
            VerticalAlignment = VerticalAlignment.Center
        };

        var filaPie = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Margin = new Thickness(0, 10, 0, 0)
        };
        filaPie.Children.Add(btnGuardar);
        filaPie.Children.Add(_msg);

        var ayuda = new TextBlock
        {
            Text = "Umbral: a partir de qué desvío se considera alarma (solo dosis). " +
                   "Sostenido: cuántos segundos debe mantenerse antes de sonar. " +
                   "Repetir: si sigue activa, vuelve a sonar cada tantos segundos (0 = una sola vez).",
            FontSize = 11, Foreground = TextoDim, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 8, 2, 0)
        };

        var cuerpo = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto")
        };
        Grid.SetRow(cabecera, 0);       cuerpo.Children.Add(cabecera);
        Grid.SetRow(cardAlarmas, 1);    cuerpo.Children.Add(cardAlarmas);
        Grid.SetRow(tituloEventos, 2);  cuerpo.Children.Add(tituloEventos);
        Grid.SetRow(scroll, 3);         cuerpo.Children.Add(scroll);
        Grid.SetRow(filaPie, 4);        cuerpo.Children.Add(filaPie);
        Grid.SetRow(ayuda, 5);          cuerpo.Children.Add(ayuda);

        // ---------------- picker de sonido (overlay interno) ----------------
        // NADA de ComboBox/Flyout: sobre el mapa GL no se dibujan y encima no
        // logean error. Border + IsVisible + backdrop que cierra al tocar.
        _pickerLista = new StackPanel { Spacing = 4 };
        var pickerScroll = new ScrollViewer
        {
            Content = _pickerLista, MaxHeight = 320,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var pickerCard = new Border
        {
            Background = BgPanel, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12),
            Width = 340, MaxHeight = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 8 26 0 #33101612")
        };
        var pickerPila = new StackPanel { Spacing = 8 };
        pickerPila.Children.Add(new TextBlock
        {
            Text = "Elegí el sonido", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Texto
        });
        pickerPila.Children.Add(pickerScroll);
        var btnPickerCancelar = BotonTexto("Cancelar", 120);
        btnPickerCancelar.Click += (_, __) => _picker!.IsVisible = false;
        pickerPila.Children.Add(btnPickerCancelar);
        pickerCard.Child = pickerPila;
        // La card se come el toque para que no llegue al backdrop y lo cierre.
        pickerCard.PointerPressed += (_, e) => e.Handled = true;

        _picker = new Border { Background = Backdrop, IsVisible = false, ZIndex = 20, Child = pickerCard };
        _picker.PointerPressed += (_, __) => _picker!.IsVisible = false;

        var stage = new Panel();
        stage.Children.Add(cuerpo);
        stage.Children.Add(_picker);
        Child = stage;

        PintarMute();
        PintarAlarmas(null);
        // El diccionario se aplica UNA SOLA VEZ, al construir: Aplicar guarda el
        // primer texto de cada control y se lo vuelve a escribir encima en cada
        // pasada — llamarlo en cada render congelaría el botón de mute y el
        // mensaje de guardado en su primer valor (lección de NodosPanel). Todo
        // texto que escribe el código en runtime pasa por Traductor.T().
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    /// <summary>Adentro de la Configuración: este panel ES el Border de la
    /// tarjeta, así que se suelta el marco sobre sí mismo. El título grande y
    /// el ✕ propio se esconden; "Silenciar todo" y "Subir sonido propio"
    /// quedan en la fila compacta de arriba (son acciones).</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this);
        PanelEmbebido.Ocultar(_cabeceraTitulo);
        PanelEmbebido.Ocultar(_btnCerrarPropio);
    }

    /// <summary>"Silenciar todo" y "Subir sonido propio" van a la barra de
    /// contexto del shell; la fila de cabecera vieja queda oculta (el ✕
    /// propio adentro ya está oculto).</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(_cabeceraAcciones);

    public void Attach(SonidosClient client)
    {
        _client = client;
        _msg.Text = "";
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _picker.IsVisible = false;
        // La config se relee en la próxima apertura: entre medio pudo tocarla
        // el celular (la página HTML sigue viva) y el último PUT gana.
        _cfg = null;
        _ = _client?.TecladoAsync(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await TickAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            // 2 s, igual que la página: las activas no necesitan más y el que
            // suena (SoundAlarmPoller) va aparte a 500 ms.
            try { await Task.Delay(TimeSpan.FromMilliseconds(2000), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await TickAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cli = _client;
        if (cli == null) return;
        try
        {
            if (_cfg == null)
            {
                // Primera carga (o reintento tras "Hub no responde"): archivos
                // + config. Si CUALQUIERA falla, la lista queda en el aviso y
                // se reintenta en el próximo tick — igual que la página, que
                // mostraba "Sin conexión con PilotX.".
                var archivos = await cli.GetArchivosAsync(ct).ConfigureAwait(false);
                var cfg = await cli.GetConfigAsync(ct).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // El panel se cerró mientras la respuesta viajaba: si se
                    // dejara entrar, _cfg volvería a quedar cargado después del
                    // Detach que lo puso en null y la próxima apertura NO
                    // relee la config — el Guardar siguiente mandaría una
                    // config vieja y pisaría lo que tocó el celular.
                    if (ct.IsCancellationRequested) return;
                    if (archivos == null || cfg == null) { PintarSinConexion(); return; }
                    _archivos = archivos;
                    _cfg = cfg;
                    ConstruirLista();
                    PintarMute();
                });
            }

            var estado = await cli.GetEstadoAsync(long.MaxValue, ct).ConfigureAwait(false);

            // Algún .wav cambió (lo pisó el celular, o una copia a mano en la
            // carpeta): tirar el cache o el ▶ haría escuchar el sonido viejo.
            // Subir desde ACÁ ya lo invalida puntualmente; esto cubre el resto.
            if (estado != null)
            {
                if (_tieneArchivosRev && estado.ArchivosRev != _archivosRev)
                    lock (_cacheWav) _cacheWav.Clear();
                _archivosRev = estado.ArchivosRev;
                _tieneArchivosRev = true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                PintarAlarmas(estado);
                // Otro cliente (el celular) pudo mutear: el indicador lo sigue.
                if (estado != null && _cfg != null &&
                    (DateTime.UtcNow - _muteTocadoUtc).TotalSeconds > 3 &&
                    estado.Mute != _cfg.Mute)
                {
                    _cfg.Mute = estado.Mute;
                    PintarMute();
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { /* motor reiniciando: próximo tick */ }
    }

    // =========================================================================
    //  pintado
    // =========================================================================

    private void PintarSinConexion()
    {
        _eventosHost.Children.Clear();
        _filas.Clear();
        _eventosHost.Children.Add(new TextBlock
        {
            Text = PilotX.Cockpit.Bars.Traductor.T("Hub no responde"),
            FontSize = 13, Foreground = TextoDim, Margin = new Thickness(4, 10, 0, 10)
        });
    }

    private void PintarMute()
    {
        bool mute = _cfg != null && _cfg.Mute;
        _btnMuteTexto.Text = PilotX.Cockpit.Bars.Traductor.T(
            mute ? "SILENCIADO — tocar para activar" : "Silenciar todo");
        _btnMuteTexto.Foreground = mute ? Brushes.White : Texto;
        _btnMute.Background = mute ? Err : BgFila;
        _btnMute.BorderBrush = mute ? Err : Borde;
    }

    private void PintarAlarmas(SonidosEstadoWire? estado)
    {
        _alarmasHost.Children.Clear();
        var activas = estado?.Activas;
        if (activas == null || activas.Count == 0)
        {
            _alarmasHost.Children.Add(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T("Sin alarmas activas."),
                FontSize = 12, Foreground = TextoDim
            });
            return;
        }
        foreach (var a in activas)
        {
            var texto = new TextBlock
            {
                FontSize = 13, Foreground = Texto, TextWrapping = TextWrapping.Wrap
            };
            // El evento llega como id crudo (dosis_baja): el server no lo
            // resuelve a nombre lindo en "activas". Se busca en el cfg si está.
            texto.Text = NombreDeEvento(a.Evento) +
                         (string.IsNullOrWhiteSpace(a.Detalle) ? "" : " — " + a.Detalle);
            _alarmasHost.Children.Add(new Border
            {
                Background = BgSuave, BorderBrush = Warn,
                BorderThickness = new Thickness(4, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Child = texto
            });
        }
    }

    private string NombreDeEvento(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var evs = _cfg?.Eventos;
        if (evs != null)
            foreach (var e in evs)
                if (e.Id == id && !string.IsNullOrWhiteSpace(e.Nombre)) return e.Nombre;
        return id!;
    }

    // =========================================================================
    //  lista de eventos
    // =========================================================================

    private void ConstruirLista()
    {
        _eventosHost.Children.Clear();
        _filas.Clear();
        var evs = _cfg?.Eventos;
        if (evs == null || evs.Count == 0)
        {
            _eventosHost.Children.Add(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T("No hay eventos configurables."),
                FontSize = 13, Foreground = TextoDim, Margin = new Thickness(4, 10, 0, 10)
            });
            return;
        }
        foreach (var ev in evs) _eventosHost.Children.Add(ArmarFila(ev));
    }

    private Border ArmarFila(SonidoEventoWire ev)
    {
        // Misma visibilidad condicional por id que el JS (sonidos.js): los
        // flancos del piloto son instantáneos, por eso no llevan sostenido ni
        // repetir; el umbral solo tiene sentido en dosis.
        bool esDosis = ev.Id == "dosis_baja" || ev.Id == "dosis_alta";
        bool conSostenido = ev.Id != "piloto_on" && ev.Id != "piloto_off";

        var f = new Fila { Ev = ev, Sonido = ev.Sonido ?? "" };

        // ---- casilla habilitado (44x44: el CheckBox de 16 px no es táctil) --
        f.CheckTilde = new TextBlock
        {
            Text = "✓", FontSize = 20, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        f.Check = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Child = f.CheckTilde,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(f.Check, PilotX.Cockpit.Bars.Traductor.T("Avisar con este sonido"));
        f.Check.Tapped += (_, __) =>
        {
            f.Ev.Habilitado = !f.Ev.Habilitado;
            PintarCheck(f);
        };

        // ---- nombre + id técnico -------------------------------------------
        var pilaNombre = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        pilaNombre.Children.Add(new TextBlock
        {
            // El nombre viene del server ya en castellano: es dato, no label.
            Text = ev.Nombre ?? "", FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = Texto, TextWrapping = TextWrapping.Wrap
        });
        pilaNombre.Children.Add(new TextBlock
        {
            // El id técnico queda a la vista para diagnosticar por teléfono.
            Text = ev.Id ?? "", FontSize = 10, Foreground = TextoDim
        });

        // ---- selector de sonido --------------------------------------------
        f.BtnSonidoTexto = new TextBlock
        {
            FontSize = 13, Foreground = Texto,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        f.BtnSonido = BotonPlano(f.BtnSonidoTexto, 190);
        f.BtnSonido.Click += (_, __) => AbrirPicker(f);

        // ---- probar --------------------------------------------------------
        var btnProbar = BotonTexto("▶", 52);
        ToolTip.SetTip(btnProbar, PilotX.Cockpit.Bars.Traductor.T("Probar el sonido"));
        btnProbar.Click += (_, __) => _ = ProbarAsync(f);

        var linea1 = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        f.Check.Margin = new Thickness(0, 0, 10, 0);
        f.BtnSonido.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(f.Check, 0);   linea1.Children.Add(f.Check);
        Grid.SetColumn(pilaNombre, 1); linea1.Children.Add(pilaNombre);
        Grid.SetColumn(f.BtnSonido, 2); linea1.Children.Add(f.BtnSonido);
        Grid.SetColumn(btnProbar, 3); linea1.Children.Add(btnProbar);

        var pila = new StackPanel { Spacing = 8 };
        pila.Children.Add(linea1);

        if (esDosis || conSostenido)
        {
            var linea2 = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(54, 0, 0, 0)
            };
            if (esDosis)
            {
                // Un decimal como máximo: el server guarda double y no tiene
                // sentido mostrar 10,0 cuando el valor es redondo.
                f.Umbral = CampoNumerico(ev.UmbralPct <= 0 ? 10 : ev.UmbralPct, 1,
                                         "Umbral de dosis (%)");
                linea2.Children.Add(Mini("umbral ±"));
                linea2.Children.Add(f.Umbral);
                linea2.Children.Add(Mini("%"));
            }
            if (conSostenido)
            {
                f.Sostenido = CampoNumerico(ev.SostenidoSeg, 1, "Segundos sostenido");
                linea2.Children.Add(Mini("sostenido"));
                linea2.Children.Add(f.Sostenido);
                linea2.Children.Add(Mini("s"));

                f.Repetir = CampoNumerico(ev.RepetirSeg, 0, "Repetir cada (s)");
                linea2.Children.Add(Mini("repetir"));
                linea2.Children.Add(f.Repetir);
                linea2.Children.Add(Mini("s"));
            }
            pila.Children.Add(linea2);
        }

        f.Marco = new Border
        {
            Background = BgFila, BorderBrush = BordeSuave, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6), Child = pila
        };

        _filas.Add(f);
        PintarCheck(f);
        PintarSonido(f);
        return f.Marco;
    }

    /// <summary>Estado visual del habilitado: la fila entera se apaga EN VIVO
    /// (opacidad .55, igual que la clase .off del HTML), sin guardar nada.</summary>
    private static void PintarCheck(Fila f)
    {
        bool on = f.Ev.Habilitado;
        f.Check.Background = on ? Verde : BgFila;
        f.Check.BorderBrush = on ? Verde : Borde;
        f.CheckTilde.Foreground = on ? Brushes.White : BgFila;
        f.Marco.Opacity = on ? 1.0 : 0.55;
    }

    private void PintarSonido(Fila f)
    {
        bool falta = !string.IsNullOrEmpty(f.Sonido) && !_archivos.Contains(f.Sonido);
        // Mejora sobre el HTML: si el wav guardado ya no está, el select caía en
        // la primera opción y al guardar PISABA la elección del operario sin
        // avisar. Acá se conserva el nombre y se marca que falta.
        f.BtnSonidoTexto.Text = string.IsNullOrEmpty(f.Sonido)
            ? PilotX.Cockpit.Bars.Traductor.T("(sin sonido)")
            : f.Sonido + (falta ? " " + PilotX.Cockpit.Bars.Traductor.T("(no está)") : "");
        f.BtnSonidoTexto.Foreground = falta ? Err : Texto;
    }

    // =========================================================================
    //  picker de sonido (overlay interno, jamás un Flyout)
    // =========================================================================

    private void AbrirPicker(Fila f)
    {
        _pickerLista.Children.Clear();
        if (_archivos.Count == 0)
        {
            _pickerLista.Children.Add(new TextBlock
            {
                Text = PilotX.Cockpit.Bars.Traductor.T("No hay sonidos cargados."),
                FontSize = 12, Foreground = TextoDim, Margin = new Thickness(4)
            });
        }
        foreach (var nombre in _archivos)
        {
            string n = nombre;
            var b = BotonTexto(n, 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (n == f.Sonido) { b.Background = new SolidColorBrush(Color.Parse("#DCEFD8")); }
            b.Click += (_, __) =>
            {
                f.Sonido = n;
                PintarSonido(f);
                _picker.IsVisible = false;
            };
            _pickerLista.Children.Add(b);
        }
        _picker.IsVisible = true;
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    /// <summary>Vuelca TODOS los controles al cfg en memoria (el leerPantalla()
    /// del JS). Los números se clampean a los mismos rangos que declaraba el
    /// HTML; lo que no sea número queda como estaba (el JS lo hacía 0, que en
    /// umbral significaba "default 10" sin que el operario lo pidiera).</summary>
    private void LeerPantalla()
    {
        foreach (var f in _filas)
        {
            f.Ev.Sonido = f.Sonido;
            if (f.Umbral != null)
                f.Ev.UmbralPct = LeerNumero(f.Umbral, f.Ev.UmbralPct <= 0 ? 10 : f.Ev.UmbralPct, 1, 50, 1);
            if (f.Sostenido != null)
                f.Ev.SostenidoSeg = LeerNumero(f.Sostenido, f.Ev.SostenidoSeg, 0, 60, 1);
            if (f.Repetir != null)
                f.Ev.RepetirSeg = (int)LeerNumero(f.Repetir, f.Ev.RepetirSeg, 0, 120, 0);
        }
    }

    private async Task GuardarAsync()
    {
        var cli = _client;
        if (cli == null || _cfg == null) return;
        LeerPantalla();
        bool ok = await cli.PutConfigAsync(_cfg).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _msg.Text = ok
                ? "✓ " + PilotX.Cockpit.Bars.Traductor.T("guardado")
                : "✕ " + PilotX.Cockpit.Bars.Traductor.T("no se pudo guardar");
            _msg.Foreground = ok ? Ok : Err;
        });
    }

    /// <summary>Silenciar todo. Como en la página, GUARDA EL CFG COMPLETO
    /// (incluidas las ediciones en curso) y no muestra mensaje.</summary>
    private async Task AlternarMuteAsync()
    {
        var cli = _client;
        if (cli == null || _cfg == null) return;
        LeerPantalla();
        _cfg.Mute = !_cfg.Mute;
        _muteTocadoUtc = DateTime.UtcNow;
        PintarMute();
        await cli.PutConfigAsync(_cfg).ConfigureAwait(false);
    }

    /// <summary>Prueba el sonido elegido EN PANTALLA (no el guardado), igual que
    /// el ▶ del HTML. Suena aunque esté el mute: el mute es del poller, no del
    /// sink — si no, probar con la cabina silenciada no daría ninguna pista.</summary>
    private async Task ProbarAsync(Fila f)
    {
        var cli = _client;
        if (cli == null) return;
        if (string.IsNullOrEmpty(f.Sonido))
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Elegí primero un sonido"));
            return;
        }
        if (SoundAlarmPoller.WavSink == null)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
                "Este equipo no tiene salida de sonido configurada"));
            return;
        }
        byte[]? wav;
        lock (_cacheWav) _cacheWav.TryGetValue(f.Sonido, out wav);
        if (wav == null)
        {
            wav = await cli.GetWavAsync(f.Sonido).ConfigureAwait(false);
            if (wav != null) lock (_cacheWav) _cacheWav[f.Sonido] = wav;
        }
        if (wav == null)
        {
            Aviso?.Invoke(PilotX.Cockpit.Bars.Traductor.T("No se pudo leer el sonido") + ": " + f.Sonido);
            return;
        }
        try { SoundAlarmPoller.WavSink?.Invoke(wav); }
        catch { /* audio best-effort, igual que el poller */ }
    }

    /// <summary>Subir un .wav propio desde un pendrive. El &lt;input type=file&gt;
    /// del HTML abría el diálogo del sistema; acá lo abre StorageProvider y el
    /// POST es el MISMO (bytes crudos, sin multipart). Por eso esta pantalla ya
    /// no necesita Chromium para nada.</summary>
    private async Task SubirWavAsync()
    {
        var cli = _client;
        if (cli == null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        IReadOnlyList<IStorageFile> elegidos;
        try
        {
            elegidos = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = PilotX.Cockpit.Bars.Traductor.T("Elegí un sonido .wav"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("WAV") { Patterns = new[] { "*.wav" } }
                }
            });
        }
        catch { return; }
        if (elegidos == null || elegidos.Count == 0) return;

        var archivo = elegidos[0];
        byte[] datos;
        try
        {
            using var origen = await archivo.OpenReadAsync().ConfigureAwait(false);
            using var ms = new MemoryStream();
            await origen.CopyToAsync(ms).ConfigureAwait(false);
            datos = ms.ToArray();
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => MensajeError(ex.Message));
            return;
        }

        var r = await cli.SubirArchivoAsync(archivo.Name, datos).ConfigureAwait(false);
        if (!r.Ok)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                MensajeError(string.IsNullOrEmpty(r.Error)
                    ? PilotX.Cockpit.Bars.Traductor.T("no se pudo subir") : r.Error));
            return;
        }

        // Subir con un nombre que YA existía lo pisa en el server (sin aviso).
        // Si no se tira el wav viejo del cache, el ▶ seguiría haciendo escuchar
        // el sonido anterior y el operario creería que la subida no anduvo.
        lock (_cacheWav) _cacheWav.Remove(r.Archivo ?? "");

        var archivos = await cli.GetArchivosAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _msg.Text = "✓ " + r.Archivo + " " + PilotX.Cockpit.Bars.Traductor.T("subido");
            _msg.Foreground = Ok;
            if (archivos != null) _archivos = archivos;
            // Las ediciones en curso se conservan: se vuelcan al cfg antes de
            // rearmar la lista (mismo orden que leerPantalla()+pintar() del JS).
            LeerPantalla();
            ConstruirLista();
        });
    }

    private void MensajeError(string texto)
    {
        _msg.Text = "✕ " + texto;
        _msg.Foreground = Err;
    }

    // =========================================================================
    //  helpers de UI
    // =========================================================================

    private static TextBlock Mini(string texto) => new TextBlock
    {
        Text = PilotX.Cockpit.Bars.Traductor.T(texto), FontSize = 11, Foreground = TextoMuted,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Campo numérico táctil: el teclado nativo de PilotX se pide a
    /// mano al enfocar (no es automático) y el valor se clampea al salir.</summary>
    private TextBox CampoNumerico(double valor, int decimales, string titulo)
    {
        var t = new TextBox
        {
            Width = 76, Height = 40, FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Text = Formatear(valor, decimales),
            Background = BgFila, BorderBrush = Borde,
            VerticalAlignment = VerticalAlignment.Center
        };
        t.GotFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, titulo, true); };
        t.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
        return t;
    }

    private static string Formatear(double v, int decimales)
        => Math.Round(v, decimales).ToString(decimales == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);

    private static double LeerNumero(TextBox t, double anterior, double min, double max, int decimales)
    {
        double v;
        // La coma decimal del teclado castellano entra como coma: se normaliza
        // antes de parsear en cultura invariante.
        if (!double.TryParse((t.Text ?? "").Trim().Replace(',', '.'),
                             NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            v = anterior;
        if (v < min) v = min;
        if (v > max) v = max;
        v = Math.Round(v, decimales);
        t.Text = Formatear(v, decimales);
        return v;
    }

    private static Button BotonTexto(string texto, double ancho)
    {
        var b = new Button
        {
            Content = PilotX.Cockpit.Bars.Traductor.T(texto),
            Height = 42, FontSize = 13,
            Background = BgFila, Foreground = Texto,
            BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (ancho > 0) b.MinWidth = ancho;
        return b;
    }

    private static Button BotonPlano(TextBlock contenido, double ancho)
    {
        return new Button
        {
            Content = contenido,
            Height = 42, MinWidth = ancho,
            Background = BgFila, BorderBrush = Borde, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }
}
