// PerfilesPanel.axaml.cs
//
// Reemplazo nativo de pages/perfiles.html + js/perfiles.js. Un perfil = TODAS
// las configuraciones del vehículo (el XML entero de Vehicles/), así que esta
// pantalla es DESTRUCTIVA: crea, carga, copia, protege y borra perfiles.
//
// El porteo es 1:1 con el JS, a propósito:
//   · mismos endpoints, mismos verbos, mismo body y mismo casing en el wire
//     (PerfilesController — snake_case: is_job_started);
//   · mismos guards de habilitación de botones (cargar/borrar no aplican al
//     perfil en uso; cargar tampoco con lote abierto);
//   · mismos textos, en el mismo orden, y misma línea de estado (gris / ok /
//     err) con los mismos mensajes palabra por palabra;
//   · mismo flujo del diálogo: si la acción devuelve ok:false el diálogo QUEDA
//     ABIERTO con lo tipeado, para corregir.
//
// Lo que NO se toca acá: el motor. Cargar/crear un perfil rehace vehículo +
// herramienta del lado del Engine (EnginePerfilService.Activar → Settings.Load
// + RecargarVehiculo + SendSettings). Este panel solo dispara el POST y vuelve
// a leer la lista, igual que la página.
//
// Polling: 5 s (setInterval del JS). El catch de OperationCanceledException va
// SIEMPRE con `when (ct.IsCancellationRequested)`: sin eso un timeout se come
// el loop y la pantalla queda congelada en los valores viejos (memoria
// feedback_httpclient_timeout_kills_polling).
//
// El diálogo es Border + IsVisible, NUNCA Flyout/MenuFlyout: sobre el mapa GL
// los flyouts no se dibujan y encima no logean error.
//
// OJO con el idioma: Traductor.Aplicar(this) se llama UNA vez, en el ctor. No
// volver a llamarlo en los render — se guarda el primer texto de cada control y
// después lo reescribe, o sea que congelaría la lista y la línea de estado en
// el primer valor. Lo que escribe el código se traduce con T().
// Por eso mismo el panel se suscribe a Traductor.IdiomaCambio: el Aplicar que
// dispara el cambio de idioma repone el texto cacheado del XAML ("" en el
// título del diálogo, la nota y la línea de estado) y los blanqueaba. El
// handler (RepintarVivo) los vuelve a escribir después.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class PerfilesPanel : UserControl, IPanelEmbebible
{
    // ---- wire / estado -----------------------------------------------------
    private PerfilesClient? _client;
    private CancellationTokenSource? _cts;

    private List<PerfilItem> _perfiles = new List<PerfilItem>();
    private string? _seleccionado;
    private bool _jobAbierto;

    /// <summary>Lo que hace el botón "Aceptar" del diálogo (el `dlgAccion` del
    /// JS). null = no hay diálogo armado.</summary>
    private Func<Task<PerfilAccionResponse?>>? _dlgAccion;

    // Último texto que escribió el CÓDIGO (no el XAML). Hace falta para poder
    // reponerlo después de un cambio de idioma: ver RepintarVivo().
    private string _dlgTitulo = "";
    private string _dlgNota = "";
    private string _estadoMsg = "";
    private string _estadoCls = "";
    /// <summary>Ya llegó al menos un snapshot del motor. Mientras es false la
    /// lista dice "Cargando…" y NO hay que rearmarla como lista vacía.</summary>
    private bool _snapshotRecibido;

    // ---- paleta: los mismos valores de la página (theme.css / PilotXTheme) --
    private static readonly IBrush Texto      = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush TextoMuted = new SolidColorBrush(Color.Parse("#535E54"));
    private static readonly IBrush Verde      = new SolidColorBrush(Color.Parse("#4ABA3E"));
    private static readonly IBrush VerdeOk    = new SolidColorBrush(Color.Parse("#2F7A26"));
    private static readonly IBrush Rojo       = new SolidColorBrush(Color.Parse("#C0261F"));
    private static readonly IBrush BordeFila  = new SolidColorBrush(Color.Parse("#E2E7E2"));
    private static readonly IBrush BgSel      = new SolidColorBrush(Color.Parse("#EAF6E8"));
    private static readonly IBrush BgTagLock  = new SolidColorBrush(Color.Parse("#EDF1EC"));
    private static readonly IBrush BordeTag   = new SolidColorBrush(Color.Parse("#C5CFC5"));

    // ---- callbacks al host -------------------------------------------------

    /// <summary>El operario cerró el panel (✕).</summary>
    public Action? OnRequestCerrar { get; set; }

    // Sin evento Aviso a propósito: la página no tiene toasts. TODO lo que
    // informa (creando / copiando / borrando / cargado / errores) va a su
    // propia línea de estado, que es donde el operario lo busca.

    /// <summary>Atajo al diccionario de idiomas.</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    public PerfilesPanel()
    {
        InitializeComponent();
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);

        // El teclado nativo NO es automático por foco: cada TextBox lo pide con
        // la misma señal HTTP que manda keyboard.js en las páginas del Hub.
        // Nunca osk.exe.
        Teclado("InNombre", "Nombre");
        Teclado("InClave", "Clave");

        // Cambiar de idioma dispara un Aplicar sobre todo el árbol, y Aplicar
        // reescribe el texto que CACHEÓ la primera vez: el del título del
        // diálogo, el de la nota y el de la línea de estado es "" (los llena el
        // código), así que se blanqueaban solos. El Post corre DESPUÉS de ese
        // Aplicar y repinta lo vivo — mismo patrón que BanderasPanel.
        PilotX.Cockpit.Bars.Traductor.IdiomaCambio += () => Dispatcher.UIThread.Post(RepintarVivo);
    }

    /// <summary>Vuelve a escribir todo lo que pinta el código (y que el Aplicar
    /// del cambio de idioma deja en blanco). La lista sí se re-traduce porque se
    /// rearma con T(); el título/la nota/el estado se reponen tal como estaban:
    /// pasarlos otra vez por T() no serviría (ya no están en castellano).</summary>
    private void RepintarVivo()
    {
        // Sin snapshot todavía la lista dice "Cargando…": rearmarla acá pondría
        // "No hay perfiles todavía", que es otra cosa muy distinta.
        if (_snapshotRecibido) Render(); else PintarCargando();
        SetTexto("DlgTitulo", _dlgTitulo);
        SetTexto("DlgNota", _dlgNota);
        SetEstado(_estadoMsg, _estadoCls);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Teclado(string ctrl, string titulo)
    {
        var t = this.FindControl<TextBox>(ctrl);
        if (t == null) return;
        t.GotFocus  += (_, __) => { if (_client != null) _ = _client.TecladoAsync(true, T(titulo)); };
        t.LostFocus += (_, __) => { if (_client != null) _ = _client.TecladoAsync(false); };
    }

    /// <summary>Adentro de la Configuración: sin marco de tarjeta, sin título
    /// grande y sin ✕ propio.</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        PanelEmbebido.Ocultar(this.FindControl<Button>("BtnCerrar"));
    }

    /// <summary>"Backup ahora" y "Sumar perfil" van a la barra de contexto del
    /// shell; la fila de cabecera vieja queda oculta.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<StackPanel>("HeaderAcciones"));

    // =========================================================================
    //  ciclo de vida
    // =========================================================================

    public void Attach(PerfilesClient client)
    {
        _client = client;
        if (_cts != null) return;

        // Abrir el panel equivale a abrir la página: arranca limpio, con la
        // lista en "Cargando…" y sin selección.
        _seleccionado = null;
        _snapshotRecibido = false;
        SetEstado("", "");
        PintarCargando();
        PintarBotones();

        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        CerrarDialogo();
        _ = _client?.TecladoAsync(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await RefrescarAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await RefrescarAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>El `refrescar()` del JS, paso por paso.</summary>
    private async Task RefrescarAsync(CancellationToken ct)
    {
        if (_client == null) return;

        PerfilesSnapshotResponse? r;
        try
        {
            r = await _client.GetPerfilesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // JS: catch (e) → setEstado('Sin conexión: ' + e.message, 'err')
            await Dispatcher.UIThread.InvokeAsync(
                () => SetEstado(T("Sin conexión:") + " " + ex.Message, "err"));
            return;
        }

        if (ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => AplicarSnapshot(r));
    }

    private void AplicarSnapshot(PerfilesSnapshotResponse? r)
    {
        if (r == null || !r.Ok)
        {
            // JS: if (!r.ok) { setEstado('Servicio de perfiles no disponible', 'err'); return; }
            // La lista NO se limpia: queda lo último que se vio.
            SetEstado(T("Servicio de perfiles no disponible"), "err");
            return;
        }

        _perfiles = r.Perfiles ?? new List<PerfilItem>();
        _jobAbierto = r.IsJobStarted;
        _snapshotRecibido = true;

        if (_seleccionado != null && !_perfiles.Exists(p => p.Nombre == _seleccionado))
            _seleccionado = null;

        Render();

        // Igual que el JS: solo se escribe la línea si HAY lote abierto. Cuando
        // no lo hay, el mensaje anterior queda tal cual (no se limpia).
        if (_jobAbierto)
            SetEstado(T("Hay un lote abierto: cerralo para cambiar de perfil."), "");
    }

    // =========================================================================
    //  render
    // =========================================================================

    private void PintarCargando()
    {
        var host = this.FindControl<StackPanel>("ListaHost");
        if (host == null) return;
        host.Children.Clear();
        host.Children.Add(Vacio(T("Cargando…")));
    }

    private static TextBlock Vacio(string texto) => new TextBlock
    {
        Text = texto,
        Foreground = TextoMuted,
        FontSize = 13,
        Padding = new Thickness(18),
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private void Render()
    {
        var host = this.FindControl<StackPanel>("ListaHost");
        if (host == null) return;
        host.Children.Clear();

        if (_perfiles.Count == 0)
            host.Children.Add(Vacio(T("No hay perfiles todavía — creá uno con «Sumar perfil».")));

        foreach (var p in _perfiles)
            host.Children.Add(BuildFila(p));

        PintarBotones();
    }

    private Border BuildFila(PerfilItem p)
    {
        bool sel = p.Nombre == _seleccionado;

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var nombre = new TextBlock
        {
            // Nombre del operario: NUNCA se traduce.
            Text = p.Nombre ?? "",
            Foreground = Texto,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(nombre, 0);
        g.Children.Add(nombre);

        if (p.Protegido)
        {
            // Sin el candado 🔒: los emojis en Avalonia dependen de la fuente y
            // salen como tofu (mismo criterio que BanderasPanel/CabeceraLineas).
            var lockTag = Chip(T("protegido"), BgTagLock, TextoMuted, BordeTag);
            Grid.SetColumn(lockTag, 1);
            g.Children.Add(lockTag);
        }

        if (p.Activo)
        {
            var uso = Chip(T("EN USO"), Verde, Brushes.White, null);
            Grid.SetColumn(uso, 2);
            g.Children.Add(uso);
        }

        // Fila: borde inferior suave; seleccionada = fondo verde muy claro con
        // una barra verde de 3 px a la izquierda (el box-shadow inset del CSS).
        var interior = new Border
        {
            Child = g,
            Background = sel ? BgSel : Brushes.Transparent,
            BorderBrush = sel ? Verde : Brushes.Transparent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(9, 6, 12, 6),
            MinHeight = 52,
        };

        var fila = new Border
        {
            Child = interior,
            BorderBrush = BordeFila,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        fila.Tapped += (_, __) =>
        {
            // JS: toggle — tocar la fila seleccionada la deselecciona.
            _seleccionado = (_seleccionado == p.Nombre) ? null : p.Nombre;
            Render();
        };
        return fila;
    }

    /// <summary>Los dos tags de la fila (.tag.lock / .tag.uso del CSS). Se llama
    /// Chip y no Tag para no tapar la propiedad Tag que hereda el control.</summary>
    private static Border Chip(string texto, IBrush fondo, IBrush color, IBrush? borde) => new Border
    {
        Background = fondo,
        BorderBrush = borde ?? Brushes.Transparent,
        BorderThickness = new Thickness(borde == null ? 0 : 1),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = texto,
            Foreground = color,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
        },
    };

    /// <summary>Los guards del JS, tal cual:
    /// cargar/borrar no aplican al perfil en uso; cargar tampoco con lote abierto.</summary>
    private void PintarBotones()
    {
        PerfilItem? p = null;
        if (_seleccionado != null)
            p = _perfiles.Find(x => x.Nombre == _seleccionado);

        Habilitar("BtnCargar",      p != null && !p.Activo && !_jobAbierto);
        Habilitar("BtnCopiar",      p != null);
        Habilitar("BtnProteger",    p != null && !p.Protegido);
        Habilitar("BtnDesproteger", p != null && p.Protegido);
        Habilitar("BtnBorrar",      p != null && !p.Activo);
    }

    /// <summary>Habilita/deshabilita con el mismo apagado visual del CSS
    /// (.btn:disabled { opacity: 0.45; pointer-events: none; }).</summary>
    private void Habilitar(string ctrl, bool on)
    {
        var b = this.FindControl<Button>(ctrl);
        if (b == null) return;
        b.IsEnabled = on;
        b.Opacity = on ? 1.0 : 0.45;
    }

    private void SetEstado(string? msg, string cls)
    {
        // Se cachea para poder reponerlo tras un cambio de idioma (el Aplicar
        // deja este TextBlock en el "" que traía del XAML).
        _estadoMsg = msg ?? "";
        _estadoCls = cls ?? "";
        var tb = this.FindControl<TextBlock>("Estado");
        if (tb == null) return;
        tb.Text = msg ?? "";
        switch (cls)
        {
            case "err": tb.Foreground = Rojo;    tb.FontWeight = FontWeight.SemiBold; break;
            case "ok":  tb.Foreground = VerdeOk; tb.FontWeight = FontWeight.SemiBold; break;
            default:    tb.Foreground = TextoMuted; tb.FontWeight = FontWeight.Normal; break;
        }
    }

    // =========================================================================
    //  diálogo (el mismo genérico de la página: título + nota + campos)
    // =========================================================================

    private void AbrirDialogo(string titulo, string nota, bool nombre, bool desde, bool clave,
                              Func<Task<PerfilAccionResponse?>> aceptar)
    {
        _dlgTitulo = titulo ?? "";
        _dlgNota = nota ?? "";
        SetTexto("DlgTitulo", _dlgTitulo);
        SetTexto("DlgNota", _dlgNota);

        var campoNombre = this.FindControl<StackPanel>("CampoNombre");
        var campoDesde  = this.FindControl<StackPanel>("CampoDesde");
        var campoClave  = this.FindControl<StackPanel>("CampoClave");
        if (campoNombre != null) campoNombre.IsVisible = nombre;
        if (campoDesde  != null) campoDesde.IsVisible  = desde;
        if (campoClave  != null) campoClave.IsVisible  = clave;

        var inNombre = this.FindControl<TextBox>("InNombre");
        var inClave  = this.FindControl<TextBox>("InClave");
        if (inNombre != null) inNombre.Text = "";
        if (inClave  != null) inClave.Text  = "";

        if (desde) LlenarDesde();

        _dlgAccion = aceptar;

        var velo = this.FindControl<Border>("Velo");
        if (velo != null) velo.IsVisible = true;

        // El foco abre el teclado nativo (mismo orden que el JS).
        if (nombre) inNombre?.Focus();
        else if (clave) inClave?.Focus();
    }

    /// <summary>Opciones de "Copiar configuración desde": el blanco primero y
    /// después TODOS los perfiles; el que está en uso queda preseleccionado (si
    /// no hay ninguno activo, queda el blanco, igual que el &lt;select&gt;).</summary>
    private void LlenarDesde()
    {
        var combo = this.FindControl<ComboBox>("InDesde");
        if (combo == null) return;

        var items = new List<ComboBoxItem>
        {
            new ComboBoxItem { Content = T("(en blanco — configuración de fábrica)"), Tag = "" },
        };
        int seleccion = 0;
        foreach (var p in _perfiles)
        {
            items.Add(new ComboBoxItem
            {
                // El nombre del perfil NO se traduce; el sufijo sí.
                Content = (p.Nombre ?? "") + (p.Activo ? " (" + T("en uso") + ")" : ""),
                Tag = p.Nombre ?? "",
            });
            if (p.Activo) seleccion = items.Count - 1;
        }
        combo.ItemsSource = items;
        combo.SelectedIndex = seleccion;
    }

    private string DesdeElegido()
    {
        var combo = this.FindControl<ComboBox>("InDesde");
        if (combo?.SelectedItem is ComboBoxItem it && it.Tag is string s) return s;
        return "";
    }

    private void CerrarDialogo()
    {
        _dlgAccion = null;
        _dlgTitulo = "";
        _dlgNota = "";
        var velo = this.FindControl<Border>("Velo");
        if (velo != null) velo.IsVisible = false;
        _ = _client?.TecladoAsync(false);
    }

    private void OnDlgCancelarClick(object? sender, RoutedEventArgs e) => CerrarDialogo();

    private async void OnDlgOkClick(object? sender, RoutedEventArgs e)
    {
        var fn = _dlgAccion;
        if (fn == null) return;

        var ct = _cts?.Token ?? CancellationToken.None;
        PerfilAccionResponse? r;
        try
        {
            r = await fn();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // JS: catch (e) → setEstado('Error: ' + e.message, 'err'); return;
            SetEstado(T("Error:") + " " + ex.Message, "err");
            return;
        }

        if (r != null && !r.Ok)
        {
            // El diálogo QUEDA ABIERTO para corregir (lo tipeado no se pierde).
            SetEstado(string.IsNullOrEmpty(r.Error) ? T("La acción falló") : r.Error!, "err");
            return;
        }

        CerrarDialogo();
        await RefrescarAsync(ct);
    }

    // =========================================================================
    //  acciones
    // =========================================================================

    private void OnSumarClick(object? sender, RoutedEventArgs e)
    {
        AbrirDialogo(
            T("Sumar perfil"),
            T("Copia TODAS las configuraciones del vehículo elegido (o arranca en blanco) y lo deja en uso."),
            nombre: true, desde: true, clave: false,
            aceptar: () =>
            {
                var inNombre = this.FindControl<TextBox>("InNombre");
                string n = (inNombre?.Text ?? "").Trim();
                if (n.Length == 0)
                {
                    // El JS pinta el estado Y devuelve ok:false con el mismo
                    // texto (que lo vuelve a pintar). Se conserva tal cual.
                    SetEstado(T("Poné un nombre"), "err");
                    return Task.FromResult<PerfilAccionResponse?>(
                        new PerfilAccionResponse { Ok = false, Error = T("Poné un nombre") });
                }
                SetEstado(T("Creando") + " «" + n + "»…", "");
                return _client == null
                    ? Task.FromResult<PerfilAccionResponse?>(null)
                    : _client.NuevoAsync(n, DesdeElegido(), _cts?.Token ?? CancellationToken.None);
            });
    }

    private async void OnCargarClick(object? sender, RoutedEventArgs e)
    {
        if (_seleccionado == null || _client == null) return;
        string nombre = _seleccionado;
        var ct = _cts?.Token ?? CancellationToken.None;

        SetEstado(T("Cargando") + " «" + nombre + "»…", "");

        PerfilAccionResponse? r;
        try
        {
            r = await _client.CargarAsync(nombre, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // El JS NO tiene .catch en esta cadena: si el fetch falla, la
            // promesa queda sin atender y la pantalla se queda en "Cargando…".
            // Se replica (no se inventa un mensaje que la página no muestra).
            return;
        }

        // El JS lee `seleccionado` acá, no la copia: si el refresco de los 5 s
        // lo borró mientras iba el POST, el mensaje sale con el nombre vacío.
        // Se replica la lectura tardía; lo único que cambia es que en C# un
        // null concatena "" en vez del literal "null".
        bool ok = r != null && r.Ok;
        SetEstado(ok
            ? T("Perfil") + " «" + _seleccionado + "» " + T("cargado") + "."
            : (string.IsNullOrEmpty(r?.Error) ? T("No se pudo cargar") : r!.Error!),
            ok ? "ok" : "err");

        await RefrescarAsync(ct);
    }

    private void OnCopiarClick(object? sender, RoutedEventArgs e)
    {
        if (_seleccionado == null) return;
        string origen = _seleccionado;

        AbrirDialogo(
            T("Copiar") + " «" + origen + "»",
            T("Duplica el perfil con TODAS sus configuraciones. No cambia el perfil en uso."),
            nombre: true, desde: false, clave: false,
            aceptar: () =>
            {
                var inNombre = this.FindControl<TextBox>("InNombre");
                string n = (inNombre?.Text ?? "").Trim();
                if (n.Length == 0)
                    return Task.FromResult<PerfilAccionResponse?>(
                        new PerfilAccionResponse { Ok = false, Error = T("Poné un nombre") });
                SetEstado(T("Copiando…"), "");
                return _client == null
                    ? Task.FromResult<PerfilAccionResponse?>(null)
                    : _client.CopiarAsync(origen, n, _cts?.Token ?? CancellationToken.None);
            });
    }

    private void OnProtegerClick(object? sender, RoutedEventArgs e)
    {
        if (_seleccionado == null) return;
        string nombre = _seleccionado;

        AbrirDialogo(
            T("Proteger") + " «" + nombre + "»",
            T("Elegí una clave. Sin ella nadie va a poder borrar este perfil. Guardala bien: no se puede recuperar."),
            nombre: false, desde: false, clave: true,
            aceptar: () =>
            {
                var inClave = this.FindControl<TextBox>("InClave");
                return _client == null
                    ? Task.FromResult<PerfilAccionResponse?>(null)
                    : _client.ProtegerAsync(nombre, inClave?.Text ?? "", _cts?.Token ?? CancellationToken.None);
            });
    }

    private void OnDesprotegerClick(object? sender, RoutedEventArgs e)
    {
        if (_seleccionado == null) return;
        string nombre = _seleccionado;

        AbrirDialogo(
            T("Desproteger") + " «" + nombre + "»",
            T("Ingresá la clave con la que se protegió."),
            nombre: false, desde: false, clave: true,
            aceptar: () =>
            {
                var inClave = this.FindControl<TextBox>("InClave");
                return _client == null
                    ? Task.FromResult<PerfilAccionResponse?>(null)
                    : _client.DesprotegerAsync(nombre, inClave?.Text ?? "", _cts?.Token ?? CancellationToken.None);
            });
    }

    private void OnBorrarClick(object? sender, RoutedEventArgs e)
    {
        if (_seleccionado == null) return;
        string nombre = _seleccionado;
        var p = _perfiles.Find(x => x.Nombre == nombre);
        bool protegido = p != null && p.Protegido;

        AbrirDialogo(
            T("Borrar") + " «" + nombre + "»",
            protegido
                ? T("Perfil PROTEGIDO: ingresá la clave para borrarlo. Se pierde toda su configuración.")
                : T("Se borra el perfil con toda su configuración. Esta acción no se puede deshacer."),
            // Sin clave visible cuando no está protegido: el campo va igual en
            // el body, vacío (lo mismo que mandaba el JS).
            nombre: false, desde: false, clave: protegido,
            aceptar: () =>
            {
                var inClave = this.FindControl<TextBox>("InClave");
                SetEstado(T("Borrando…"), "");
                return _client == null
                    ? Task.FromResult<PerfilAccionResponse?>(null)
                    : _client.BorrarAsync(nombre, inClave?.Text ?? "", _cts?.Token ?? CancellationToken.None);
            });
    }

    private async void OnBackupClick(object? sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        var ct = _cts?.Token ?? CancellationToken.None;

        Habilitar("BtnBackup", false);
        SetEstado(T("Generando backup…"), "");
        try
        {
            var r = await _client.BackupAsync(ct);
            bool ok = r != null && r.Ok;
            SetEstado(ok
                ? T("Backup creado") + (string.IsNullOrEmpty(r!.Nombre) ? "." : ": " + r.Nombre)
                : T("No se pudo crear el backup") + (string.IsNullOrEmpty(r?.Detail) ? "." : ": " + r!.Detail),
                ok ? "ok" : "err");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            SetEstado(T("Error creando backup:") + " " + ex.Message, "err");
        }
        finally
        {
            Habilitar("BtnBackup", true);
        }
    }

    // =========================================================================
    //  helpers
    // =========================================================================

    private void SetTexto(string ctrl, string texto)
    {
        var tb = this.FindControl<TextBlock>(ctrl);
        if (tb != null) tb.Text = texto;
    }

    private void OnCerrarClick(object? sender, RoutedEventArgs e) => OnRequestCerrar?.Invoke();
}
