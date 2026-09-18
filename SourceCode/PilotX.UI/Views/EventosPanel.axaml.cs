// EventosPanel.axaml.cs
//
// Reemplazo nativo de pages/eventos.html (visor de eventos, ex ventana
// WinForms FormEventViewer). UI Avalonia + EventosClient (HTTP a EmbedIO).
// Sin WebView, sin JS.
//
// La logica es la MISMA que js/eventos.js, paso por paso:
//   · carga al abrir + boton "Actualizar" (SIN polling, igual que el form viejo)
//   · el boton se deshabilita mientras carga y se re-habilita siempre al final
//   · el pie dice "Cargando…", despues la ruta del archivo (o vacio si no vino)
//   · normaliza \r\n y \r a \n y recorta el blanco final (el log de sesion
//     usa \r como salto de linea, Log.EventWriter)
//   · arma "historico" + separador "──── Sesión actual ────" + sesion (o
//     "(sin eventos en esta sesión)"), unido con lineas en blanco
//   · scroll al final: lo ultimo es lo que importa
//   · si la request falla, el pie dice el error y el visor NO se toca
//
// API: Attach(EventosClient) carga; Detach() cancela lo que este en vuelo.
// MainWindow llama Attach() al abrir y Detach() al cerrar.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class EventosPanel : UserControl, IPanelEmbebible
{
    private EventosClient? _client;
    private CancellationTokenSource? _cts;

    // Textos EXACTOS de la pagina (eventos.js / eventos.html). No tocar: el
    // operario los lee igual en el celular y en la cabina. Son la CLAVE del
    // diccionario (castellano): lo que se pinta pasa siempre por T().
    private const string TxtCargando  = "Cargando…";
    private const string TxtError     = "No se pudo cargar el registro de eventos.";
    private const string TxtSeparador = "──── Sesión actual ────";
    private const string TxtSinSesion = "(sin eventos en esta sesión)";

    /// <summary>Atajo al diccionario de idiomas (mismo patron que PerfilesPanel).</summary>
    private static string T(string texto) => PilotX.Cockpit.Bars.Traductor.T(texto);

    /// <summary>
    /// Lo invoca el ✕ del header. El host (MainWindow) engancha aca su
    /// CloseEventos — mismo contrato que GpsDataPanel y los editores.
    /// </summary>
    public Action? OnRequestCerrar { get; set; }

    public EventosPanel()
    {
        InitializeComponent();
        // Sin esto la pantalla quedaba SIEMPRE en castellano ("Eventos",
        // "Actualizar", "Cargando…") aunque el equipo estuviera en en/pt: la
        // pagina HTML si traducia. Una sola vez, en el ctor — llamarlo en cada
        // render congelaria el pie en el primer texto que se le escribio.
        PilotX.Cockpit.Bars.Traductor.Aplicar(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Adentro de la Configuración: sin marco de tarjeta y sin la
    /// cabecera propia (título grande + ✕); los márgenes de 24 bajan a 0
    /// porque el padding del área de contenido lo pone el shell.</summary>
    public void ModoEmbebido()
    {
        PanelEmbebido.SoltarMarco(this.FindControl<Border>("Card"));
        PanelEmbebido.Ocultar(this.FindControl<StackPanel>("HeaderTitulo"));
        var raiz = this.FindControl<Grid>("ContenidoRaiz");
        if (raiz != null) raiz.Margin = new Thickness(0);
    }

    /// <summary>El botón "Actualizar" se va a la barra de contexto del shell;
    /// la cabecera vieja queda oculta.</summary>
    public Control? PillsDeContexto()
        => PanelEmbebido.FilaDeContexto(this.FindControl<Button>("BtnActualizar"));

    /// <summary>
    /// Inyecta el cliente y dispara la carga (equivale a abrir la página:
    /// eventos.js llama load() al final del script). Si el panel se reabre,
    /// vuelve a leer — el log cambió mientras tanto.
    /// </summary>
    public void Attach(EventosClient client)
    {
        _client = client;
        try { _cts?.Cancel(); } catch { }
        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token);
    }

    /// <summary>Cancela la request en vuelo. Lo llama MainWindow al cerrar el
    /// panel: cerrado no se hace red (esta pantalla no tiene polling, pero una
    /// lectura de 256 KB puede estar a mitad de camino).</summary>
    public void Detach()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void OnCerrarClick(object? s, RoutedEventArgs e)
        => OnRequestCerrar?.Invoke();

    private void OnActualizarClick(object? s, RoutedEventArgs e)
    {
        if (_client == null) return;
        _cts ??= new CancellationTokenSource();
        _ = LoadAsync(_cts.Token);
    }

    // ---------- Carga -----------------------------------------------------

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_client == null) return;

        // Mismo arranque que el JS: boton off + pie en "Cargando…".
        var btn  = this.FindControl<Button>("BtnActualizar");
        var hint = this.FindControl<TextBlock>("LogHint");
        if (btn  != null) btn.IsEnabled = false;
        if (hint != null) hint.Text = T(TxtCargando);

        var d = await _client.GetEventLogAsync(ct).ConfigureAwait(false);

        // Panel cerrado mientras leia: no se pinta nada (y no se re-habilita
        // el boton porque el proximo Attach lo vuelve a poner en marcha).
        if (ct.IsCancellationRequested) return;

        await Dispatcher.UIThread.InvokeAsync(() => Render(d));
    }

    private void Render(EventLogSnapshotDto? d)
    {
        var btn  = this.FindControl<Button>("BtnActualizar");
        var hint = this.FindControl<TextBlock>("LogHint");
        var box  = this.FindControl<TextBlock>("LogBox");
        var sv   = this.FindControl<ScrollViewer>("LogScroll");

        if (d == null)
        {
            // catch del fetch: solo cambia el pie, el visor conserva lo que
            // tenia ("—" en la primera carga).
            if (hint != null) hint.Text = T(TxtError);
            if (btn  != null) btn.IsEnabled = true;   // finally del JS
            return;
        }

        var history = Normalize(d.History);
        var session = Normalize(d.Session);

        var partes = new List<string>();
        if (history.Length > 0) partes.Add(history);
        partes.Add(T(TxtSeparador));
        partes.Add(session.Length > 0 ? session : T(TxtSinSesion));

        if (box  != null) box.Text  = string.Join("\n\n", partes);
        if (hint != null) hint.Text = d.File ?? string.Empty;

        // Lo ultimo es lo que importa: scroll al final. Se hace despues del
        // layout (el TextBlock recien acaba de cambiar de alto).
        if (sv != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                sv.Offset = new Vector(sv.Offset.X, max);
            }, DispatcherPriority.Background);
        }

        if (btn != null) btn.IsEnabled = true;        // finally del JS
    }

    /// <summary>Mismo normalize() de eventos.js: \r\n y \r sueltos pasan a \n
    /// (el buffer de sesion usa \r) y se recorta el blanco del final.</summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
    }
}
