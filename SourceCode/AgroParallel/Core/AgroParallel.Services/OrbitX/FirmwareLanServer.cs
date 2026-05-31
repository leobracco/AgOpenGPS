// ============================================================================
// FirmwareLanServer.cs - HTTP server LAN para distribuir firmwares a nodos ESP32
//
// Sirve el cache local de FirmwareMirror a los nodos ESP32 que viven detrás
// del PC con PilotX (sin internet propio). Se monta cuando OrbitXSync arranca.
//
// Endpoints:
//   GET /healthz                                        → ok
//   GET /firmware/list                                  → catálogo JSON
//   GET /firmware/list/{producto}                       → versiones del producto
//   GET /firmware/{producto}/{version}/manifest.json    → metadata
//   GET /firmware/{producto}/{version}/firmware.bin     → stream del .bin
//
// Bind: http://+:<port>/  (todas las interfaces, requiere URL ACL en Windows
//                          o ejecutar como admin la primera vez).
// Si falla por permisos, fallback a http://localhost:<port>/ y log con la
// instrucción netsh para que el usuario habilite el bind público.
// ============================================================================

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgroParallel.OrbitX
{
    public class FirmwareLanServer : IDisposable
    {
        private readonly OrbitXConfig _cfg;
        private readonly Action<string> _log;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private string _cacheDir;

        public bool IsRunning { get; private set; }
        public string BoundPrefix { get; private set; }
        public string LastError { get; private set; }

        public FirmwareLanServer(OrbitXConfig cfg, Action<string> log = null)
        {
            _cfg = cfg;
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            if (IsRunning) return;
            _cacheDir = FirmwareMirror.ResolveCacheDir(_cfg);
            FirmwareMirror.EnsureDir(_cacheDir);

            int port = _cfg.FirmwareHttpPort > 0 ? _cfg.FirmwareHttpPort : 8088;

            // Intento 1: bind público (LAN). Falla con HttpListenerException si
            // no hay URL ACL ni permisos de admin.
            string publicPrefix = $"http://+:{port}/";
            string localPrefix  = $"http://localhost:{port}/";

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(publicPrefix);
                _listener.Start();
                BoundPrefix = publicPrefix;
            }
            catch (HttpListenerException ex)
            {
                _log($"FW server: bind público falló ({ex.Message}). " +
                     $"Para LAN OTA, ejecutar UNA VEZ como admin: " +
                     $"netsh http add urlacl url=http://+:{port}/ user=Everyone");
                try
                {
                    _listener?.Close();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(localPrefix);
                    _listener.Start();
                    BoundPrefix = localPrefix;
                    _log("FW server: arrancado solo en localhost (los nodos ESP32 NO podrán descargar)");
                }
                catch (Exception ex2)
                {
                    LastError = ex2.Message;
                    _log($"FW server falló: {ex2.Message}");
                    return;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log($"FW server falló: {ex.Message}");
                return;
            }

            IsRunning = true;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
            _log($"FW server LAN activo en {BoundPrefix}  (cache: {_cacheDir})");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        public void Dispose() => Stop();

        // ── Loop principal ──────────────────────────────────────────────────
        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { _log($"FW server accept: {ex.Message}"); continue; }

                _ = Task.Run(() => HandleRequest(ctx));
            }
        }

        // ── Handlers ────────────────────────────────────────────────────────
        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string p = ctx.Request.Url.AbsolutePath;
                ctx.Response.Headers["Cache-Control"] = "no-store";

                if (ctx.Request.HttpMethod != "GET" && ctx.Request.HttpMethod != "HEAD")
                {
                    SendJson(ctx, 405, new { error = "method not allowed" });
                    return;
                }

                if (p == "/healthz")
                {
                    SendJson(ctx, 200, new { ok = true, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                    return;
                }

                if (p == "/firmware/list" || p == "/firmware/list/")
                {
                    var list = FirmwareMirror.ListLocal(_cacheDir);
                    SendJson(ctx, 200, list);
                    return;
                }

                Match m;
                m = Regex.Match(p, @"^/firmware/list/([^/]+)/?$");
                if (m.Success)
                {
                    string producto = Uri.UnescapeDataString(m.Groups[1].Value);
                    var list = FirmwareMirror.ListLocal(_cacheDir);
                    var filtered = list.FindAll(x => string.Equals(x.producto, producto, StringComparison.OrdinalIgnoreCase));
                    SendJson(ctx, 200, filtered);
                    return;
                }

                m = Regex.Match(p, @"^/firmware/([^/]+)/([^/]+)/manifest\.json$");
                if (m.Success)
                {
                    string producto = Uri.UnescapeDataString(m.Groups[1].Value);
                    string version  = Uri.UnescapeDataString(m.Groups[2].Value);
                    string manPath  = FirmwareMirror.PathManifest(_cacheDir, producto, version);
                    if (!File.Exists(manPath)) { SendJson(ctx, 404, new { error = "not found" }); return; }
                    SendFile(ctx, manPath, "application/json");
                    return;
                }

                m = Regex.Match(p, @"^/firmware/([^/]+)/([^/]+)/firmware\.bin$");
                if (m.Success)
                {
                    string producto = Uri.UnescapeDataString(m.Groups[1].Value);
                    string version  = Uri.UnescapeDataString(m.Groups[2].Value);
                    string binPath  = FirmwareMirror.PathBin(_cacheDir, producto, version);
                    if (!File.Exists(binPath)) { SendJson(ctx, 404, new { error = "not found" }); return; }
                    SendFile(ctx, binPath, "application/octet-stream",
                        $"attachment; filename=\"{producto}-{version}.bin\"");
                    _log($"FW served {producto} {version} → {ctx.Request.RemoteEndPoint}");
                    return;
                }

                SendJson(ctx, 404, new { error = "not found" });
            }
            catch (Exception ex)
            {
                try { SendJson(ctx, 500, new { error = ex.Message }); } catch { }
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        // ── Helpers de respuesta ────────────────────────────────────────────
        private static void SendJson(HttpListenerContext ctx, int code, object body)
        {
            string json = JsonSerializer.Serialize(body);
            byte[] buf = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        }

        private static void SendFile(HttpListenerContext ctx, string filePath, string contentType,
            string contentDisposition = null)
        {
            var info = new FileInfo(filePath);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = info.Length;
            if (!string.IsNullOrEmpty(contentDisposition))
                ctx.Response.Headers["Content-Disposition"] = contentDisposition;

            using (var fs = File.OpenRead(filePath))
            {
                fs.CopyTo(ctx.Response.OutputStream);
            }
        }
    }
}
