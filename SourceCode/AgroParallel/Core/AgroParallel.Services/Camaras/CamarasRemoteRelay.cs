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

        // Probes ISAPI por idx (1-based). Si nunca se probó (offline al arranque)
        // la entrada no existe y el reporte al cloud manda marca="" — OrbitX la
        // marcará como "marca_no_soportada" hasta que el probe termine.
        private readonly Dictionary<int, IsapiDeviceInfo> _probes = new Dictionary<int, IsapiDeviceInfo>();
        private DateTime _lastProbeRetryAt = DateTime.MinValue;

        // ── Logging a archivo: <PilotX>/camaras_relay.log ──
        // El usuario puede abrir este archivo para diagnosticar por qué un stream
        // no llega al cloud (ffmpeg ausente, RTSP rechazado, register 401, etc).
        private static readonly object _logLock = new object();
        internal static void Log(string msg)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg;
            try { System.Diagnostics.Debug.WriteLine("[CamRelay] " + line); } catch { }
            try
            {
                lock (_logLock)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "camaras_relay.log");
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(path);
                    }
                    catch { }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }
        }

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
            if (IsRunning) { Log("Start ignorado: ya estaba corriendo"); return; }
            if (!_cfg.Enabled)
            {
                LastError = "OrbitX deshabilitado";
                Log("Start abortado: OrbitX deshabilitado");
                return;
            }
            if (string.IsNullOrEmpty(_cfg.DeviceId) || string.IsNullOrEmpty(_cfg.DeviceToken))
            {
                LastError = "DeviceId/DeviceToken vacíos";
                Log("Start abortado: DeviceId/DeviceToken vacíos");
                return;
            }

            _cts = new CancellationTokenSource();
            IsRunning = true;
            LastError = null;

            int activas = 0;
            foreach (var c in _cams.camaras) if (c.activa) activas++;
            Log("Start device_id=" + _cfg.DeviceId + " streaming=" + _cfg.CamarasStreamingEnabled
                + " camaras_total=" + _cams.camaras.Count + " activas=" + activas);

            // Solo arrancamos ffmpeg si el streaming está habilitado. Sin streaming
            // igual reportamos al cloud para que el panel sepa que el tractor tiene
            // cámaras configuradas (aparecerán como offline).
            if (_cfg.CamarasStreamingEnabled)
            {
                string ff = ResolveFfmpeg();
                if (ff == null)
                {
                    LastError = "ffmpeg.exe no encontrado (configurar CamarasFfmpegPath o agregarlo al PATH)";
                    Log("ffmpeg.exe NO encontrado — no se publicarán streams. Configurar CamarasFfmpegPath o agregar al PATH.");
                }
                else
                {
                    Log("ffmpeg=" + ff);
                    for (int i = 0; i < _cams.camaras.Count; i++)
                    {
                        var c = _cams.camaras[i];
                        if (!c.activa) { Log("  cam" + (i + 1) + " '" + c.nombre + "' SKIP (inactiva)"); continue; }
                        int idx = i + 1;
                        Log("  cam" + idx + " '" + c.nombre + "' arrancando worker ffmpeg");
                        var w = new CamWorker(idx, c, _cams, _cfg, ff, _cts.Token);
                        _workers[idx] = w;
                        w.Start();
                    }
                }
            }
            else
            {
                Log("CamarasStreamingEnabled=false — solo se reportará metadata al cloud, no se publican streams");
            }

            // Probe ISAPI inicial — corre en paralelo a ffmpeg para no demorar el
            // arranque del relay. Las cámaras que respondan rápido aparecerán
            // como Hikvision en el primer register loop; las que demoren, en el
            // siguiente.
            _ = Task.Run(() => ProbeAllAsync(_cts.Token));

            // Loop que reporta estado al cloud cada 30s — corre siempre.
            _registerLoop = Task.Run(() => RegisterLoop(_cts.Token));
        }

        // ─────────────────────────────────────────────────────────
        // ISAPI probe — corre al Start y periódicamente para las cams sin info
        // ─────────────────────────────────────────────────────────
        private async Task ProbeAllAsync(CancellationToken ct)
        {
            var camsSnapshot = new List<Camara>(_cams.camaras);
            var tasks = new List<Task>();
            for (int i = 0; i < camsSnapshot.Count; i++)
            {
                int idx = i + 1;
                var c = camsSnapshot[i];
                if (!c.activa) continue;
                tasks.Add(Task.Run(async () =>
                {
                    var info = await HikvisionIsapi.ProbeDeviceInfoAsync(
                        c.ip, c.puerto, c.usuario, c.clave,
                        TimeSpan.FromSeconds(5), ct);
                    lock (_probes) { _probes[idx] = info; }
                    if (info.IsHikvision)
                        Log("cam" + idx + " ISAPI OK marca=hikvision modelo=" + info.Modelo + " fw=" + info.Firmware);
                    else
                        Log("cam" + idx + " ISAPI FAIL " + info.Error);
                }, ct));
            }
            try { await Task.WhenAll(tasks); } catch { }
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
                // Re-probar cualquier cam que todavía no tenga info válida
                // (cámara estaba offline al arranque, credenciales recién
                // cargadas, etc.). Cada 2 minutos como mucho.
                if ((DateTime.UtcNow - _lastProbeRetryAt).TotalSeconds > 120)
                {
                    _lastProbeRetryAt = DateTime.UtcNow;
                    var pending = new List<int>();
                    lock (_probes)
                    {
                        for (int i = 0; i < _cams.camaras.Count; i++)
                        {
                            int idx = i + 1;
                            if (!_cams.camaras[i].activa) continue;
                            IsapiDeviceInfo info;
                            if (!_probes.TryGetValue(idx, out info) || !info.IsHikvision)
                                pending.Add(idx);
                        }
                    }
                    if (pending.Count > 0) _ = Task.Run(() => ProbeAllAsync(ct));
                }

                try { await ReportEstado(ct); }
                catch (Exception ex) { LastError = "register: " + ex.Message; }
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { }
            }
        }

        private async Task ReportEstado(CancellationToken ct)
        {
            string url = _cfg.ServerUrl.TrimEnd('/') + "/api/camaras/registrar";
            var camsList = BuildEstadoCamaras();
            var body = new { camaras = camsList };
            var json = JsonSerializer.Serialize(body);
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Add("X-Device-ID", _cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var r = await _http.SendAsync(req, ct))
                {
                    int code = (int)r.StatusCode;
                    if (!r.IsSuccessStatusCode)
                    {
                        string snippet = "";
                        try { snippet = await r.Content.ReadAsStringAsync(); } catch { }
                        if (snippet.Length > 200) snippet = snippet.Substring(0, 200) + "…";
                        LastError = "register " + code;
                        Log("[register] FAIL " + code + " " + r.ReasonPhrase + " body=" + snippet);
                    }
                    else
                    {
                        Log("[register] OK " + code + " camaras=" + camsList.Count);
                    }
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

                IsapiDeviceInfo info = null;
                lock (_probes) { _probes.TryGetValue(idx, out info); }

                // Sin probe todavía → mandamos marca vacía. El server marca la
                // cámara como "marca_no_soportada" hasta el próximo ciclo. Esto
                // es a propósito: preferimos mostrarla en gris transitoriamente
                // antes que dejar pasar una cam no-Hik por un probe que falló.
                string marca    = info != null && info.IsHikvision ? "hikvision" : "";
                string modelo   = info != null ? info.Modelo : "";
                string firmware = info != null ? info.Firmware : "";
                string serial   = info != null ? info.Serial : "";

                // Canal "principal" Hikvision: <canal>01. El secundario suele
                // ser <canal>02 pero no lo enviamos hasta que el server lo use.
                int chCode = (c.canal <= 0 ? 1 : c.canal) * 100 + 1;
                var canales = new List<object>
                {
                    new
                    {
                        id = 1,
                        nombre = "Principal",
                        rtsp_path = "/Streaming/Channels/" + chCode,
                    }
                };

                list.Add(new
                {
                    idx       = idx,
                    nombre    = c.nombre,
                    marca     = marca,
                    modelo    = modelo,
                    firmware  = firmware,
                    serial    = serial,
                    canales   = canales,
                    activa    = c.activa,
                    online    = online,
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

                    // Args mínimos compatibles con ffmpeg 4.x → 7.x.
                    // Sin -stimeout (eliminado), sin -rw_timeout (no aceptado por
                    // todas las builds Windows). Si la conexión cuelga, ffmpeg
                    // tiene timeout interno y nuestro RunLoop reintenta con
                    // backoff — no necesitamos timeout custom.
                    //   -rtsp_transport tcp  → input option (forzar TCP a Hikvision)
                    //   -c copy              → no re-encodea (banda mínima)
                    //   -f rtsp              → output format
                    //   -rtsp_transport tcp  → output option (forzar TCP a MediaMTX)
                    string args =
                        "-hide_banner -loglevel warning " +
                        "-rtsp_transport tcp " +
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

                    // Loguear src/dst CON las credenciales enmascaradas para diagnóstico.
                    Log("cam" + _idx + " spawn ffmpeg src=" + Mask(srcUrl) + " dst=" + Mask(dstUrl));

                    string lastStderr = "";
                    try
                    {
                        _proc = Process.Start(psi);
                        if (_proc == null) throw new Exception("Process.Start devolvió null");

                        // Drenar stderr y guardar últimas líneas para diagnóstico.
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                string line;
                                while ((line = _proc.StandardError.ReadLine()) != null)
                                {
                                    if (string.IsNullOrWhiteSpace(line)) continue;
                                    lastStderr = line;
                                    // Loguear solo errores relevantes (warning/error/401/connection).
                                    if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                                        || line.IndexOf("401", StringComparison.Ordinal) >= 0
                                        || line.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0
                                        || line.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0)
                                        Log("cam" + _idx + " ffmpeg> " + line);
                                }
                            }
                            catch { }
                        });

                        // Si no se cae en 3s consideramos que está publicando OK
                        var startedAt = DateTime.UtcNow;
                        while (!_proc.HasExited && (DateTime.UtcNow - startedAt).TotalSeconds < 3)
                            await Task.Delay(250, ct);
                        IsPublishing = !_proc.HasExited;
                        if (IsPublishing) Log("cam" + _idx + " publicando OK");

                        // Esperar fin del proceso (o cancelación)
                        while (!_proc.HasExited && !ct.IsCancellationRequested)
                            await Task.Delay(500, ct);

                        if (_proc.HasExited)
                            Log("cam" + _idx + " ffmpeg salió code=" + _proc.ExitCode
                                + (string.IsNullOrEmpty(lastStderr) ? "" : " ultima_stderr=" + lastStderr));
                    }
                    catch (TaskCanceledException) { }
                    catch (Exception ex)
                    {
                        IsPublishing = false;
                        Log("cam" + _idx + " EX " + ex.GetType().Name + " " + ex.Message);
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

            // Enmascara user:pass en una URL para no leakear tokens al log.
            private static string Mask(string url)
            {
                if (string.IsNullOrEmpty(url)) return url;
                try
                {
                    var u = new Uri(url);
                    if (string.IsNullOrEmpty(u.UserInfo)) return url;
                    return url.Replace(u.UserInfo + "@", "***:***@");
                }
                catch { return "***"; }
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
                // orbitx.* está detrás de Cloudflare proxy (naranja) que sólo proxea
                // HTTP/HTTPS. RTSP/8554 no pasa. cam.* está en DNS-only y va directo
                // al droplet con MediaMTX. Auto-corregir si la config quedó vieja.
                if (!string.IsNullOrEmpty(host) &&
                    host.StartsWith("orbitx.", StringComparison.OrdinalIgnoreCase))
                {
                    host = "cam." + host.Substring("orbitx.".Length);
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
