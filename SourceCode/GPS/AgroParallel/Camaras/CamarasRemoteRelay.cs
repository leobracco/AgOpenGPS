// ============================================================================
// CamarasRemoteRelay.cs
//
// Servicio que toma cada cámara Hikvision activa de CamarasConfig, spawna un
// proceso ffmpeg sidecar por cámara, y la republica por RTSP push al servidor
// MediaMTX corriendo junto a OrbitX.
//
// Pipeline por cámara:
//   ffmpeg -rtsp_transport tcp -i rtsp://user:pass@LAN_IP:554/Streaming/...
//          -c copy -f rtsp rtsp://<deviceId>:<token>@<host>:<port>/<deviceId>_cam<N>
//
// -c copy = no re-encodea, ancho de banda mínimo (lo que sale de Hikvision es H264).
//
// Si ffmpeg muere (cámara offline, red caída, etc) lo reintentamos con backoff
// exponencial. Cada cámara corre en su propio Task.
//
// Periódicamente (cada 30s) hacemos POST a /api/camaras/registrar para que el
// panel sepa qué cámaras hay y cuáles están publicando.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.OrbitX;

namespace AgroParallel.Camaras
{
    public class CamarasRemoteRelay : IDisposable
    {
        private readonly OrbitXConfig _cfg;
        private CamarasConfig _cams;
        private readonly object _lock = new object();
        private readonly Dictionary<int, CamWorker> _workers = new Dictionary<int, CamWorker>();
        private CancellationTokenSource _cts;
        private Task _registerLoop;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        public CamarasRemoteRelay(OrbitXConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _cams = CamarasConfig.Load();
        }

        // ─────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────
        public void Start()
        {
            if (IsRunning) return;
            if (!_cfg.Enabled || !_cfg.CamarasStreamingEnabled)
            {
                LastError = "deshabilitado en OrbitXConfig";
                return;
            }
            if (string.IsNullOrEmpty(_cfg.DeviceId) || string.IsNullOrEmpty(_cfg.DeviceToken))
            {
                LastError = "DeviceId/DeviceToken vacíos";
                return;
            }

            string ff = ResolveFfmpeg();
            if (ff == null)
            {
                LastError = "ffmpeg.exe no encontrado (configurar CamarasFfmpegPath o agregarlo al PATH)";
                return;
            }

            _cts = new CancellationTokenSource();
            IsRunning = true;
            LastError = null;

            // Worker por cada cámara activa
            for (int i = 0; i < _cams.camaras.Count; i++)
            {
                var c = _cams.camaras[i];
                if (!c.activa) continue;
                int idx = i + 1;
                var w = new CamWorker(idx, c, _cams, _cfg, ff, _cts.Token);
                _workers[idx] = w;
                w.Start();
            }

            // Loop que reporta estado al cloud cada 30s
            _registerLoop = Task.Run(() => RegisterLoop(_cts.Token));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            lock (_lock)
            {
                foreach (var w in _workers.Values) w.Stop();
                _workers.Clear();
            }
        }

        public void Dispose() { Stop(); _http?.Dispose(); }

        // Refrescar config cuando el usuario edita cámaras desde el panel
        public void Reload()
        {
            bool wasRunning = IsRunning;
            Stop();
            _cams = CamarasConfig.Load();
            if (wasRunning) Start();
        }

        public IReadOnlyDictionary<int, bool> GetOnlineState()
        {
            var d = new Dictionary<int, bool>();
            lock (_lock)
            {
                foreach (var kv in _workers) d[kv.Key] = kv.Value.IsPublishing;
            }
            return d;
        }

        // ─────────────────────────────────────────────────────────
        // Resolve ffmpeg.exe
        // ─────────────────────────────────────────────────────────
        private string ResolveFfmpeg()
        {
            // 1) Path explícito en config
            if (!string.IsNullOrEmpty(_cfg.CamarasFfmpegPath) && File.Exists(_cfg.CamarasFfmpegPath))
                return _cfg.CamarasFfmpegPath;

            // 2) ffmpeg.exe junto al .exe principal
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            // 3) Subcarpeta ffmpeg/bin/ffmpeg.exe (instalación local)
            string sub = Path.Combine(appDir, "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(sub)) return sub;

            // 4) PATH del sistema
            string env = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in env.Split(Path.PathSeparator))
            {
                try
                {
                    string cand = Path.Combine(p, "ffmpeg.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────
        // Reportar estado al cloud — POST /api/camaras/registrar
        // ─────────────────────────────────────────────────────────
        private async Task RegisterLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await ReportEstado(ct); }
                catch (Exception ex) { LastError = "register: " + ex.Message; }
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { }
            }
        }

        private async Task ReportEstado(CancellationToken ct)
        {
            string url = _cfg.ServerUrl.TrimEnd('/') + "/api/camaras/registrar";
            var body = new
            {
                camaras = BuildEstadoCamaras(),
            };
            var json = JsonSerializer.Serialize(body);
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Add("X-Device-ID", _cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var r = await _http.SendAsync(req, ct))
                {
                    if (!r.IsSuccessStatusCode)
                        LastError = "register " + (int)r.StatusCode;
                }
            }
        }

        private List<object> BuildEstadoCamaras()
        {
            var list = new List<object>();
            for (int i = 0; i < _cams.camaras.Count; i++)
            {
                var c = _cams.camaras[i];
                int idx = i + 1;
                bool online = false;
                lock (_lock) { if (_workers.TryGetValue(idx, out var w)) online = w.IsPublishing; }
                list.Add(new
                {
                    idx = idx,
                    nombre = c.nombre,
                    activa = c.activa,
                    online = online,
                });
            }
            return list;
        }

        // ─────────────────────────────────────────────────────────
        // CamWorker — supervisión de un ffmpeg por cámara
        // ─────────────────────────────────────────────────────────
        private class CamWorker
        {
            private readonly int _idx;
            private readonly Camara _cam;
            private readonly CamarasConfig _cfgCams;
            private readonly OrbitXConfig _cfgOrbit;
            private readonly string _ffmpeg;
            private readonly CancellationToken _ct;
            private CancellationTokenSource _cts;
            private Process _proc;
            private DateTime _lastRunAt = DateTime.MinValue;
            public bool IsPublishing { get; private set; }

            public CamWorker(int idx, Camara cam, CamarasConfig cfgCams, OrbitXConfig cfgOrbit, string ffmpeg, CancellationToken ct)
            {
                _idx = idx; _cam = cam; _cfgCams = cfgCams; _cfgOrbit = cfgOrbit; _ffmpeg = ffmpeg; _ct = ct;
            }

            public void Start()
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                Task.Run(() => RunLoop(_cts.Token));
            }

            public void Stop()
            {
                IsPublishing = false;
                try { _cts?.Cancel(); } catch { }
                try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            }

            private async Task RunLoop(CancellationToken ct)
            {
                int backoff = 2000;
                while (!ct.IsCancellationRequested)
                {
                    string srcUrl = _cfgCams.RtspUrl(_cam);
                    string dstUrl = BuildPushUrl();

                    if (string.IsNullOrEmpty(srcUrl) || string.IsNullOrEmpty(dstUrl))
                    {
                        await Task.Delay(5000, ct).ContinueWith(_ => { });
                        continue;
                    }

                    string args =
                        "-hide_banner -loglevel warning " +
                        "-rtsp_transport tcp " +
                        "-stimeout 5000000 " +
                        "-i \"" + srcUrl + "\" " +
                        "-c copy " +
                        "-f rtsp " +
                        "-rtsp_transport tcp " +
                        "\"" + dstUrl + "\"";

                    var psi = new ProcessStartInfo(_ffmpeg, args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = false,
                    };
                    _lastRunAt = DateTime.UtcNow;

                    try
                    {
                        _proc = Process.Start(psi);
                        if (_proc == null) throw new Exception("Process.Start devolvió null");

                        // Drenar stderr para que no bloquee
                        _ = Task.Run(() =>
                        {
                            try { while (!_proc.StandardError.EndOfStream) _proc.StandardError.ReadLine(); }
                            catch { }
                        });

                        // Si no se cae en 3s consideramos que está publicando OK
                        var startedAt = DateTime.UtcNow;
                        while (!_proc.HasExited && (DateTime.UtcNow - startedAt).TotalSeconds < 3)
                            await Task.Delay(250, ct);
                        IsPublishing = !_proc.HasExited;

                        // Esperar fin del proceso (o cancelación)
                        while (!_proc.HasExited && !ct.IsCancellationRequested)
                            await Task.Delay(500, ct);
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception)
                    {
                        IsPublishing = false;
                    }
                    finally
                    {
                        try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
                        IsPublishing = false;
                    }

                    if (ct.IsCancellationRequested) break;

                    // Backoff exponencial hasta 30s
                    try { await Task.Delay(backoff, ct); } catch { }
                    backoff = Math.Min(backoff * 2, 30000);

                    // Si corrió >2 minutos OK, resetear backoff (caída transitoria)
                    if ((DateTime.UtcNow - _lastRunAt).TotalSeconds > 120) backoff = 2000;
                }
            }

            private string BuildPushUrl()
            {
                string host = _cfgOrbit.CamarasRtspHost;
                if (string.IsNullOrEmpty(host))
                {
                    // Derivar de ServerUrl
                    try
                    {
                        var u = new Uri(_cfgOrbit.ServerUrl);
                        host = u.Host;
                    }
                    catch { return null; }
                }
                int port = _cfgOrbit.CamarasRtspPort > 0 ? _cfgOrbit.CamarasRtspPort : 8554;
                string user = Uri.EscapeDataString(_cfgOrbit.DeviceId);
                string pass = Uri.EscapeDataString(_cfgOrbit.DeviceToken);
                string path = _cfgOrbit.DeviceId + "_cam" + _idx;
                return "rtsp://" + user + ":" + pass + "@" + host + ":" + port + "/" + path;
            }
        }
    }
}
