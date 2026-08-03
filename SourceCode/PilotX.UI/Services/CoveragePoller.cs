// CoveragePoller.cs
//
// Poller dedicado para /api/aog/coverage. Cadencia 1 Hz (mucho mas lenta
// que el HUD a 4 Hz) porque el payload puede ser ~3 MB en jornadas
// largas. La revision incremental que devuelve el snapshot permite al
// receptor (MapGlSurface) saltarse la re-upload del VBO si no cambio
// nada — el costo de polling se reduce a parse + comparacion.
//
// Solo se instancia cuando App.UseGl == true. El MapSkiaSurface legacy
// nunca recibe coverage; consume solo el snapshot HUD (que ya trae el
// boundary). Cuando GL llegue a paridad y MapSkiaSurface se retire, el
// poller pasa a ser obligatorio.
//
// Lifetime: el padre (MainWindow) llama Start/Stop. Stop completa la
// task pendiente con cancelacion limpia para no dejar HttpClient activo
// al cerrar la app.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PilotX.Desktop.Services;

public sealed class CoveragePoller
{
    private readonly CoverageClient _client;
    private readonly Action<CoverageSnapshot> _onSnapshot;
    private readonly int _periodMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastRevision = -1;

    public CoveragePoller(CoverageClient client, Action<CoverageSnapshot> onSnapshot, int periodMs = 1000)
    {
        _client = client;
        _onSnapshot = onSnapshot;
        _periodMs = periodMs;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts; _cts = null;
        if (cts != null)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _loop = null;
    }

    // Modelo ACUMULADO: el server manda solo lo nuevo (protocolo incremental
    // con cursor "j:p:v") y acá se appendea. El merge y la publicación corren
    // en el hilo de UI para no mutar las listas mientras GL las lee.
    private CoverageSnapshot? _acum;

    // Cursor precomputado DESPUÉS de cada merge, en el hilo de UI: el loop de
    // fondo solo lee este string (leer las listas mientras la UI las muta
    // sería una carrera).
    private volatile string? _cursor;

    private string? ArmarCursor()
    {
        var secs = _acum?.Sections;
        if (secs == null || secs.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var s in secs)
        {
            int p = (s.Strips?.Count ?? 0) - 1;
            if (p < 0) { p = 0; }
            int v = (s.Strips != null && p < s.Strips.Count)
                ? (s.Strips[p].Vertices?.Count ?? 0) : 0;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(s.Index).Append(':').Append(p).Append(':').Append(v);
        }
        return sb.ToString();
    }

    private void Fusionar(CoverageSnapshot inc)
    {
        var basSecs = _acum!.Sections ??= new System.Collections.Generic.List<CoverageSection>();
        _acum.Revision = inc.Revision;
        _acum.FieldDirectory = inc.FieldDirectory;
        foreach (var sec in inc.Sections ?? new System.Collections.Generic.List<CoverageSection>())
        {
            CoverageSection? dst = null;
            foreach (var b in basSecs)
                if (b.Index == sec.Index) { dst = b; break; }
            if (dst == null)
            {
                dst = new CoverageSection { Index = sec.Index, Strips = new System.Collections.Generic.List<CoverageStrip>() };
                basSecs.Add(dst);
            }
            dst.Enabled = sec.Enabled;
            dst.Strips ??= new System.Collections.Generic.List<CoverageStrip>();
            // Rellenar hasta PatchBase (parches que nunca vimos, no debería pasar).
            while (dst.Strips.Count <= sec.PatchBase)
                dst.Strips.Add(new CoverageStrip { Vertices = new System.Collections.Generic.List<CoverageVertex>() });

            var strips = sec.Strips;
            if (strips == null || strips.Count == 0) continue;
            // strips[0] CONTINÚA el parche PatchBase; el resto son parches nuevos.
            var cont = dst.Strips[sec.PatchBase];
            cont.Vertices ??= new System.Collections.Generic.List<CoverageVertex>();
            if (strips[0].Vertices != null) cont.Vertices.AddRange(strips[0].Vertices!);
            for (int i = 1; i < strips.Count; i++) dst.Strips.Add(strips[i]);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await _client.GetSnapshotAsync(ct, _cursor).ConfigureAwait(false);
                bool hayNovedad = snap != null &&
                    (snap.Revision != _lastRevision || snap.Full);
                if (snap != null && hayNovedad)
                {
                    _lastRevision = snap.Revision;
                    // Merge + publish EN el hilo de UI (MapGlSurface lee las
                    // listas al subir el VBO; mutarlas desde acá sería carrera).
                    // InvokeAsync y no Post: serializa contra el próximo ciclo.
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (snap.Full || _acum == null) _acum = snap;
                        else Fusionar(snap);
                        _cursor = ArmarCursor();
                        _onSnapshot(_acum);
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PilotX.Desktop] CoveragePoller: " + ex.Message);
            }
            try { await Task.Delay(_periodMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
