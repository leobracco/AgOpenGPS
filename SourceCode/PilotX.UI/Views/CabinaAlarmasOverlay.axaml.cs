// CabinaAlarmasOverlay.axaml.cs
//
// Banner cabin-critical arriba a la derecha. Dos fuentes (pedido 2026-08-07:
// "el mismo modal del nodo desconectado debería marcar los errores de
// VistaX — surco 1 tapado, surco 2 dosis no alcanzada"):
//
//   · NODOS: GET /api/nodos/unified cada 2s — nodos del implemento activo
//     que cayeron offline.
//   · VISTAX: GET /api/vistax/live en el mismo tick — surcos en falla
//     PRODUCTIVA mientras se siembra: tapado / dosis no alcanzada / sin
//     datos / tolva vacía. El exceso NO va al banner (de más siembra, no
//     de menos — mismo criterio que VxSurcoEvaluator, que no lo marca
//     como alerta). Solo con monitoreo activo: parado, todos los surcos
//     darían "tapado" y el banner sería puro ruido.
//
// Comportamiento comun:
//   · Beep 880 Hz one-shot SOLO cuando entra una alarma NUEVA (UID de
//     nodo o surco+estado que no estaba).
//   · "Silenciar 10 min" suspende los beeps pero NO oculta el banner.
//   · La X descarta lo actual; reaparece solo si entra una alarma nueva.
//   · Sin alarmas, se oculta.
//
// NO oculta el mapa ni interfiere con otros overlays — se acopla arriba
// con ZIndex alto + VerticalAlignment=Top.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class CabinaAlarmasOverlay : UserControl
{
    private Border?    _alertRoot;
    private TextBlock? _tituloText;
    private TextBlock? _listaText;

    private NodosClient? _client;
    private CancellationTokenSource? _pollCts;

    // Beep one-shot por UID nuevo (igual al JS legacy).
    private readonly HashSet<string> _alertedUids = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _silencedUntilUtc = DateTime.MinValue;

    // Descarte con la X: mientras el conjunto de UIDs offline no crezca, el chip
    // queda oculto. Si entra un UID nuevo (no descartado), reaparece.
    private readonly HashSet<string> _dismissedUids = new(StringComparer.OrdinalIgnoreCase);
    private bool _dismissed;

    public CabinaAlarmasOverlay()
    {
        InitializeComponent();
        _alertRoot  = this.FindControl<Border>("AlertRoot");
        _tituloText = this.FindControl<TextBlock>("TituloText");
        _listaText  = this.FindControl<TextBlock>("ListaText");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private VistaXClient? _vxClient;

    /// <summary>
    /// Arranca el polling. Llamar desde MainWindow al iniciar la app.
    /// El overlay se gestiona solo despues de eso (mostrar/ocultar segun
    /// haya alarmas o no). <paramref name="vistax"/> es opcional: sin el,
    /// el banner solo muestra nodos offline (comportamiento anterior).
    /// </summary>
    public void Attach(NodosClient client, VistaXClient? vistax = null)
    {
        _client = client;
        _vxClient = vistax;
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    public void Detach()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        if (_alertRoot != null) _alertRoot.IsVisible = false;
        _alertedUids.Clear();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        if (_client == null) return;
        // Primera vuelta inmediata + cada 2s.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var data = await _client.GetUnifiedAsync(ct).ConfigureAwait(false);
                VistaXLiveSnapshot? vx = null;
                if (_vxClient != null)
                    vx = await _vxClient.GetLiveAsync(ct).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ApplySnapshot(data, vx));
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal al detach */ }
    }

    /// <summary>Texto de operario para el estado de falla de un surco.
    /// Devuelve null para los estados que NO van al banner.</summary>
    private static string? TextoFalla(string? estado) => estado switch
    {
        "tapado"  => "tapado",
        "bajo"    => "dosis no alcanzada",
        "no-data" => "sin datos",
        "alerta"  => "tolva vacía",
        // "exceso" NO alarma: de más siembra, no de menos (VxSurcoEvaluator).
        _ => null,
    };

    private void ApplySnapshot(NodosUnifiedResponse? data, VistaXLiveSnapshot? vx)
    {
        if (_alertRoot == null || _tituloText == null || _listaText == null) return;

        // ---- fuente 1: nodos del implemento activo offline ------------------
        var offlines = (data != null && data.Ok && data.Nodos != null)
            ? data.Nodos.Where(n => n.DelImplementoActivo && !n.Online).ToList()
            : new List<NodoUnified>();

        // ---- fuente 2: surcos VistaX en falla productiva ---------------------
        // Solo con monitoreo activo: parado, cada surco daría "tapado" y el
        // banner sería puro ruido. Clave = surco+estado, así un surco que pasa
        // de "bajo" a "tapado" cuenta como alarma NUEVA (y beepea).
        var fallas = new List<(string clave, string texto)>();
        if (vx != null && vx.MonitoreoActivo && vx.Trenes != null)
        {
            foreach (var tren in vx.Trenes)
            {
                if (tren?.Surcos == null) continue;
                foreach (var s in tren.Surcos)
                {
                    if (s == null || s.Muted) continue;
                    var txt = TextoFalla(s.Estado);
                    if (txt == null) continue;
                    fallas.Add(($"vx:{s.Bajada}:{s.Estado}",
                        PilotX.Cockpit.Bars.Traductor.T("Surco") + " " + s.Bajada + " " +
                        PilotX.Cockpit.Bars.Traductor.T(txt)));
                }
            }
        }

        if (offlines.Count == 0 && fallas.Count == 0)
        {
            _alertedUids.Clear();
            _dismissedUids.Clear();
            _dismissed = false;
            _alertRoot.IsVisible = false;
            return;
        }

        // Claves de alarma vigentes (uid de nodo + surco:estado de VistaX).
        var claves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in offlines)
            if (!string.IsNullOrEmpty(n.Uid)) claves.Add(n.Uid!);
        foreach (var f in fallas) claves.Add(f.clave);

        // Si estaba descartado y NO entró ninguna alarma nueva, seguir oculto.
        if (_dismissed)
        {
            bool hayNuevaNoDescartada = claves.Any(c => !_dismissedUids.Contains(c));
            if (!hayNuevaNoDescartada) { _alertRoot.IsVisible = false; return; }
            _dismissed = false; // reapareció por una alarma nueva
        }

        // ---- título + lista --------------------------------------------------
        var t = PilotX.Cockpit.Bars.Traductor.T;
        if (offlines.Count > 0 && fallas.Count > 0)
            _tituloText.Text = t("Implemento offline") + " + " + t("fallas de siembra");
        else if (offlines.Count > 0)
        {
            var slug = string.IsNullOrEmpty(data!.ImplementoSlug) ? "" : " — " + data.ImplementoSlug;
            _tituloText.Text = t("Implemento offline") + slug;
        }
        else
            _tituloText.Text = t("Fallas de siembra") + " — VistaX";

        var sb = new StringBuilder();
        foreach (var n in offlines)
        {
            if (sb.Length > 0) sb.Append("    ");
            var label = string.IsNullOrEmpty(n.Alias) ? (n.Uid ?? "") : n.Alias;
            sb.Append("• ").Append(label);
            if (!string.IsNullOrEmpty(n.Tipo))
                sb.Append(' ').Append('(').Append(n.Tipo).Append(')');
        }
        foreach (var f in fallas)
        {
            if (sb.Length > 0) sb.Append("    ");
            sb.Append("• ").Append(f.texto);
        }
        _listaText.Text = sb.ToString();

        _alertRoot.IsVisible = true;

        // Beep one-shot por alarma nueva, respetando silenciado.
        bool hayNuevas = claves.Any(c => !_alertedUids.Contains(c));
        // Limpiar claves que ya se recuperaron (asi vuelven a beepear si recaen).
        _alertedUids.RemoveWhere(k => !claves.Contains(k));
        foreach (var c in claves) _alertedUids.Add(c);

        if (hayNuevas && DateTime.UtcNow >= _silencedUntilUtc)
            PlayBeep();
    }

    private void OnSilenciarClick(object? sender, RoutedEventArgs e)
    {
        _silencedUntilUtc = DateTime.UtcNow.AddMinutes(10);
        // El chip sigue visible. Si entra un UID nuevo despues del fin del
        // silenciado, vuelve a beepear.
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Cabina-alarmas silenciada 10 min");
    }

    // Ocultar (X): descarta los UIDs offline actuales y esconde el chip. Vuelve
    // a aparecer solo si entra un nodo nuevo en alarma (uno no descartado).
    private void OnOcultarClick(object? sender, RoutedEventArgs e)
    {
        _dismissed = true;
        foreach (var uid in _alertedUids) _dismissedUids.Add(uid);
        if (_alertRoot != null) _alertRoot.IsVisible = false;
    }

    // Beep de alarma. Console.Beep SOLO existe en Windows (en Android/otros
    // lanza PlatformNotSupported), así que se guarda por plataforma: en Android
    // el aviso sonoro real lo hará el head vía su propio canal (pendiente).
    // 880 Hz / 600 ms — mismo tono que el JS legacy (oscilador square @ 880).
    private static void PlayBeep()
    {
        try
        {
            Task.Run(() =>
            {
                // Guard dentro del lambda para que el analizador de plataforma
                // (CA1416) vea que Console.Beep solo corre en Windows.
                if (!OperatingSystem.IsWindows()) return;
                try { Console.Beep(880, 600); }
                catch { /* silent: PC sin beeper o headless */ }
            });
        }
        catch { /* silent */ }
    }
}
