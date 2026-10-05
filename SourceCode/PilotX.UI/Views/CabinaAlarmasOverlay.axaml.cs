// CabinaAlarmasOverlay.axaml.cs
//
// Banner cabin-critical arriba a la derecha. Dos fuentes (pedido 2026-08-07:
// "el mismo modal del nodo desconectado debería marcar los errores de
// VistaX — surco 1 tapado, surco 2 dosis no alcanzada"):
//
//   · NODOS: GET /api/nodos/unified cada 2s — nodos del implemento activo
//     que cayeron offline.
//   · VISTAX: GET /api/vistax/live en el mismo tick — surcos con falla
//     mientras se siembra: tapado / dosis no alcanzada / sin datos /
//     tolva vacía / exceso de dosis (el exceso entró por pedido del
//     2026-08-07: sembrar de más es plata en semilla). Solo con
//     monitoreo activo: parado, todos los surcos darían "tapado" y el
//     banner sería puro ruido.
//
// Comportamiento comun:
//   · Beep 880 Hz one-shot SOLO cuando entra una alarma NUEVA (UID de
//     nodo o surco+estado que no estaba).
//   · "Silenciar 10 min" suspende los beeps pero NO oculta el banner.
//   · La X descarta lo actual; reaparece solo si entra una alarma nueva.
//   · Sin alarmas, se oculta.
//   · Debajo de la lista, "Qué hacer: ..." (catálogo QueHacerAlarma): una
//     frase por tipo de alarma, dos como mucho. Sin entrada, no se muestra.
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
using AgroParallel.Cabina;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views;

public partial class CabinaAlarmasOverlay : UserControl
{
    private Border?    _alertRoot;
    private TextBlock? _tituloText;
    private TextBlock? _listaText;
    private TextBlock? _queHacerText;

    /// <summary>Cuántas soluciones distintas se muestran como mucho. El banner
    /// es un chip arriba del mapa: dos frases se leen, cinco no.</summary>
    private const int MaxQueHacer = 2;

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
        _queHacerText = this.FindControl<TextBlock>("QueHacerText");
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
        // El exceso también va al banner (pedido 2026-08-07): sembrar de más
        // es plata en semilla — el evaluator no lo cuenta como falla
        // productiva (no frena nada), pero el operario quiere verlo.
        "exceso"  => "exceso de dosis",
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
        var fallas = new List<(string clave, string texto, string estado)>();
        if (vx != null && vx.MonitoreoActivo && vx.Trenes != null)
        {
            foreach (var tren in vx.Trenes)
            {
                if (tren?.Surcos == null) continue;
                foreach (var s in tren.Surcos)
                {
                    if (s == null || s.Muted) continue;
                    var txt = TextoFalla(s.Estado);
                    if (txt == null)
                    {
                        // Aviso AMARILLO (nodo VistaX v3.1+): muchos dobles o
                        // fallas sostenido 20 s. Solo si el surco no tiene ya
                        // una falla roja — tapado manda.
                        if (s.SingulacionBaja)
                            fallas.Add(($"vx:{s.Bajada}:{QueHacerAlarma.SurcoSingulacion}",
                                PilotX.Cockpit.Bars.Traductor.T("Surco") + " " + s.Bajada + " " +
                                PilotX.Cockpit.Bars.Traductor.T("singulación baja") + " (" +
                                s.Singulacion.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %)",
                                QueHacerAlarma.SurcoSingulacion));
                        continue;
                    }
                    fallas.Add(($"vx:{s.Bajada}:{s.Estado}",
                        PilotX.Cockpit.Bars.Traductor.T("Surco") + " " + s.Bajada + " " +
                        PilotX.Cockpit.Bars.Traductor.T(txt), s.Estado ?? ""));
                }
            }
        }
        // Solo avisos de singulación (sin nodos caídos ni fallas rojas): el
        // banner va en ÁMBAR. Es calidad de siembra, no un surco sin semilla.
        bool soloAmarillo = offlines.Count == 0 && fallas.Count > 0 &&
                            fallas.All(f => f.estado == QueHacerAlarma.SurcoSingulacion);
        PintarSeveridad(soloAmarillo);

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
        else if (soloAmarillo)
            _tituloText.Text = t("Singulación baja") + " — VistaX";
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

        // ---- qué hacer -------------------------------------------------------
        // Una solución por TIPO de alarma, no por surco: cuatro surcos tapados
        // son un solo "revisá el tubo de bajada". Primero los nodos (sin nodo
        // no hay dato de nada), después los surcos en el orden en que llegan.
        var soluciones = new List<string>();
        void Sumar(string? qh)
        {
            if (!string.IsNullOrEmpty(qh) && !soluciones.Contains(qh!)) soluciones.Add(qh!);
        }
        if (offlines.Count > 0) Sumar(QueHacerAlarma.Para(QueHacerAlarma.NodoOffline));
        foreach (var f in fallas) Sumar(QueHacerAlarma.ParaSurco(f.estado));

        if (_queHacerText != null)
        {
            if (soluciones.Count == 0)
            {
                _queHacerText.IsVisible = false;
                _queHacerText.Text = "";
            }
            else
            {
                var qh = new StringBuilder(t("Qué hacer")).Append(": ");
                for (int i = 0; i < soluciones.Count && i < MaxQueHacer; i++)
                {
                    if (i > 0) qh.Append(' ');
                    qh.Append(t(soluciones[i]));
                }
                _queHacerText.Text = qh.ToString();
                _queHacerText.IsVisible = true;
            }
        }

        _alertRoot.IsVisible = true;

        // Beep one-shot por alarma nueva, respetando silenciado.
        bool hayNuevas = claves.Any(c => !_alertedUids.Contains(c));
        // Limpiar claves que ya se recuperaron (asi vuelven a beepear si recaen).
        _alertedUids.RemoveWhere(k => !claves.Contains(k));
        foreach (var c in claves) _alertedUids.Add(c);

        if (hayNuevas && DateTime.UtcNow >= _silencedUntilUtc)
            PlayBeep();
    }

    // Rojo (XAML) = nodo caído / surco tapado / dosis. Ámbar = solo avisos de
    // singulación. Los tonos oscuros mantienen el texto blanco legible al sol.
    private static readonly Avalonia.Media.IBrush _bgAmbar = new Avalonia.Media.LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint   = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#A86F00"), 0),
            new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse("#7A5000"), 1),
        },
    };
    private static readonly Avalonia.Media.IBrush _bordeAmbar =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4F3400"));
    private Avalonia.Media.IBrush? _bgRojo, _bordeRojo;
    private bool? _amarilloPintado;

    private void PintarSeveridad(bool amarillo)
    {
        if (_alertRoot == null || _amarilloPintado == amarillo) return;
        if (_bgRojo == null)
        {
            _bgRojo = _alertRoot.Background;
            _bordeRojo = _alertRoot.BorderBrush;
        }
        _alertRoot.Background  = amarillo ? _bgAmbar : _bgRojo;
        _alertRoot.BorderBrush = amarillo ? _bordeAmbar : _bordeRojo;
        _amarilloPintado = amarillo;
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

    // Beep de alarma. Console.Beep SOLO existe en Windows; en Linux se
    // sintetiza el MISMO tono (square 880 Hz / 600 ms, como el JS legacy) a un
    // WAV temporal una única vez y se toca con paplay/aplay. En Android el
    // aviso sonoro real lo hará el head vía su propio canal (pendiente).
    private static void PlayBeep()
    {
        try
        {
            Task.Run(() =>
            {
                // Guards dentro del lambda para que el analizador de plataforma
                // (CA1416) vea en qué OS corre cada rama.
                if (OperatingSystem.IsWindows())
                {
                    try { Console.Beep(880, 600); }
                    catch { /* silent: PC sin beeper o headless */ }
                }
                else if (OperatingSystem.IsLinux())
                {
                    try { BeepLinux(); }
                    catch { /* silent: sin paplay/aplay */ }
                }
            });
        }
        catch { /* silent */ }
    }

    private static string? _beepWav;   // WAV sintetizado una sola vez por proceso

    private static void BeepLinux()
    {
        if (_beepWav == null || !System.IO.File.Exists(_beepWav))
        {
            // WAV PCM 16-bit mono 8 kHz, onda cuadrada 880 Hz, 600 ms.
            const int rate = 8000, ms = 600, hz = 880;
            int n = rate * ms / 1000;
            var pcm = new byte[n * 2];
            int periodo = rate / hz;
            for (int i = 0; i < n; i++)
            {
                short v = (short)((i % periodo) < periodo / 2 ? 12000 : -12000);
                pcm[i * 2] = (byte)(v & 0xFF);
                pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            string ruta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pilotx-beep.wav");
            using (var fs = new System.IO.FileStream(ruta, System.IO.FileMode.Create))
            using (var w = new System.IO.BinaryWriter(fs))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + pcm.Length);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(pcm.Length); w.Write(pcm);
            }
            _beepWav = ruta;
        }

        foreach (var player in new[] { "paplay", "aplay" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(player, "\"" + _beepWav + "\"")
                { UseShellExecute = false, CreateNoWindow = true };
                System.Diagnostics.Process.Start(psi);
                return;
            }
            catch { /* probar el siguiente */ }
        }
    }
}
