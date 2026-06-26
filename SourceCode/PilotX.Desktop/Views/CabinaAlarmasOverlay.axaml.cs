// CabinaAlarmasOverlay.axaml.cs
//
// Banner cabin-critical: nodos del implemento ACTIVO que cayeron offline.
// Reemplazo nativo de pages/cabina-alarmas.html. Vive arriba del mapa,
// siempre polling cuando la app esta abierta (no requiere navegacion).
//
// Comportamiento:
//   · Cada 2s GET /api/nodos/unified, filtra nodos
//     `del_implemento_activo && !online`.
//   · Si hay >=1, banner visible con la lista (alias o uid + tipo).
//   · Beep 880 Hz one-shot SOLO cuando entra un UID nuevo en alarma.
//   · "Silenciar 10 min" suspende los beeps pero NO oculta el banner
//     (es informacion importante; vuelve a beepear si entra UID nuevo
//     despues del fin del silenciado).
//   · Cuando no hay nodos offline, se oculta.
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

    public CabinaAlarmasOverlay()
    {
        InitializeComponent();
        _alertRoot  = this.FindControl<Border>("AlertRoot");
        _tituloText = this.FindControl<TextBlock>("TituloText");
        _listaText  = this.FindControl<TextBlock>("ListaText");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Arranca el polling. Llamar desde MainWindow al iniciar la app.
    /// El overlay se gestiona solo despues de eso (mostrar/ocultar segun
    /// haya nodos offline o no).
    /// </summary>
    public void Attach(NodosClient client)
    {
        _client = client;
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
                await Dispatcher.UIThread.InvokeAsync(() => ApplySnapshot(data));
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal al detach */ }
    }

    private void ApplySnapshot(NodosUnifiedResponse? data)
    {
        if (_alertRoot == null || _tituloText == null || _listaText == null) return;

        if (data == null || !data.Ok || data.Nodos == null)
            return; // tolerante — si el WebHost no esta, esperamos al proximo tick

        var offlines = data.Nodos
            .Where(n => n.DelImplementoActivo && !n.Online)
            .ToList();

        if (offlines.Count == 0)
        {
            _alertedUids.Clear();
            _alertRoot.IsVisible = false;
            return;
        }

        // Render lista — alias o uid + tipo entre parentesis.
        var slug = string.IsNullOrEmpty(data.ImplementoSlug) ? "" : " — " + data.ImplementoSlug;
        _tituloText.Text = "Nodos del implemento offline" + slug;

        var sb = new StringBuilder();
        for (int i = 0; i < offlines.Count; i++)
        {
            if (i > 0) sb.Append("    ");
            var n = offlines[i];
            var label = string.IsNullOrEmpty(n.Alias) ? (n.Uid ?? "") : n.Alias;
            sb.Append("• ").Append(label);
            if (!string.IsNullOrEmpty(n.Tipo))
                sb.Append(' ').Append('(').Append(n.Tipo).Append(')');
        }
        _listaText.Text = sb.ToString();

        _alertRoot.IsVisible = true;

        // Beep one-shot por UID nuevo, respetando silenciado.
        var vivos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hayNuevos = false;
        foreach (var n in offlines)
        {
            var uid = n.Uid ?? "";
            if (string.IsNullOrEmpty(uid)) continue;
            vivos.Add(uid);
            if (!_alertedUids.Contains(uid)) hayNuevos = true;
        }
        // Limpiar UIDs que ya volvieron online (asi vuelven a beepear si recaen).
        _alertedUids.RemoveWhere(k => !vivos.Contains(k));
        foreach (var uid in vivos) _alertedUids.Add(uid);

        if (hayNuevos && DateTime.UtcNow >= _silencedUntilUtc)
            PlayBeep();
    }

    private void OnSilenciarClick(object? sender, RoutedEventArgs e)
    {
        _silencedUntilUtc = DateTime.UtcNow.AddMinutes(10);
        // El banner sigue visible. Si entra un UID nuevo despues del fin del
        // silenciado, vuelve a beepear.
        System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] Cabina-alarmas silenciada 10 min");
    }

    // Console.Beep funciona en Windows .NET; corre en background para no bloquear UI.
    // 880 Hz / 600 ms — mismo tono que el JS legacy (oscilador square @ 880).
    private static void PlayBeep()
    {
        try
        {
            Task.Run(() =>
            {
                try { Console.Beep(880, 600); }
                catch { /* silent: PC sin beeper o headless */ }
            });
        }
        catch { /* silent */ }
    }
}
