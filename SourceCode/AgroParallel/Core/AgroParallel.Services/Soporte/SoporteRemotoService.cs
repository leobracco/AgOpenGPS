// ============================================================================
// SoporteRemotoService.cs - Canal de diagnostico remoto PilotX <-> OrbitX.
//
// Por que existe:
//   Cuando una pantalla en el campo falla, hoy la unica forma de ver que pasa
//   es dictarle comandos por telefono a quien este parado adelante. El
//   2026-09-05 eso costo varias horas y una maquina parada: la pregunta
//   "existe este archivo?" tardaba diez minutos en tener respuesta.
//
// Como funciona:
//   No abre ningun puerto en el tractor. La pantalla PREGUNTA cada 20 s si hay
//   algun diagnostico pendiente, corre la funcion del catalogo y devuelve el
//   texto. Todo sale de adentro hacia afuera, sobre la misma autenticacion por
//   dispositivo que ya usa el sync (X-Device-ID + X-Auth-Token). Sin conexion
//   no pasa nada: reintenta espaciando.
//
//     GET  /api/soporte/pendientes   -> [{ id, accion, params, timeout_seg }]
//     POST /api/soporte/resultado    <- { id, ok, salida, ms }
//
// Que se puede pedir:
//   UNICAMENTE lo que esta en AccionesSoporte: ocho funciones nuestras, de
//   solo lectura, escritas a mano, con la salida acotada y las credenciales
//   tapadas (estado, sistema, red, puertos, procesos, firewall, logs, nodos).
//   El "accion" que llega del cloud es una CLAVE de diccionario, no un comando:
//   si no esta en el catalogo se rechaza y se responde con la lista de las que
//   existen. No hay ninguna ruta por la que un texto arbitrario llegue a
//   ejecutarse.
//
// Limites que no se negocian:
//   . Solo el token de ESTE equipo puede leer sus pendientes.
//   . La salida se sanitiza (tokens y contrasenas) y se corta en 256 KB.
//   . Timeout por accion, tope 300 s, para que nada quede colgado.
//   . Una a la vez: no se paralelizan diagnosticos sobre la misma maquina.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgroParallel.Soporte
{
    public sealed class SoporteRemotoService : IDisposable
    {
        // 20 s: rapido para que una sesion de soporte no sea desesperante, y
        // lento para que 25 pantallas no le hagan 4 requests por segundo a un
        // droplet de 1 GB.
        private const int IntervaloSeg = 20;

        // Cuando el cloud no contesta, no insistir cada 20 s: se va espaciando
        // hasta 5 minutos. Una pantalla sin internet no tiene que gastar
        // bateria ni llenar el log.
        private const int IntervaloMaxSeg = 300;

        private readonly HttpClient _http;
        private readonly Func<OrbitX.OrbitXConfig> _cfgProvider;
        private readonly Action<string> _log;
        private CancellationTokenSource _cts;
        private Task _bucle;
        private int _esperaSeg = IntervaloSeg;

        public SoporteRemotoService(Func<OrbitX.OrbitXConfig> cfgProvider, Action<string> log = null)
        {
            if (cfgProvider == null) throw new ArgumentNullException(nameof(cfgProvider));
            _cfgProvider = cfgProvider;
            _log = log;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        private void Trace(string m)
        {
            try { if (_log != null) _log("[Soporte] " + m); } catch { }
        }

        public void Start()
        {
            if (_bucle != null) return;
            _cts = new CancellationTokenSource();
            _bucle = Task.Run(() => Bucle(_cts.Token));
            Trace("canal de diagnostico arriba (consulta cada " + IntervaloSeg + " s)");
        }

        private async Task Bucle(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(_esperaSeg), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    bool ok = await Ciclo(ct).ConfigureAwait(false);
                    // Backoff solo ante fallas de red. Con el cloud respondiendo
                    // se vuelve al ritmo normal enseguida.
                    _esperaSeg = ok ? IntervaloSeg : Math.Min(_esperaSeg * 2, IntervaloMaxSeg);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Trace("ciclo fallo: " + ex.Message);
                    _esperaSeg = Math.Min(_esperaSeg * 2, IntervaloMaxSeg);
                }
            }
        }

        // ── Backup de configuracion al cloud ────────────────────────────────
        private DateTime _ultimoBackup = DateTime.MinValue;
        private static readonly TimeSpan BackupCada = TimeSpan.FromHours(6);

        private async Task QuizasSubirBackup(string baseUrl, OrbitX.OrbitXConfig cfg, CancellationToken ct)
        {
            if (DateTime.UtcNow - _ultimoBackup < BackupCada) return;

            // Junta los .json de config del equipo. El server deduplica por
            // firma, asi que reintentar no cuesta nada.
            var cfgObj = RecolectarConfig();
            if (cfgObj == null) return;

            string ver = "";
            try { ver = OrbitX.PilotXSelfUpdate.DetectCurrentVersion(); } catch { }

            string cuerpo;
            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("device_id", cfg.DeviceId);
                    w.WriteString("version", ver);
                    w.WritePropertyName("config");
                    JsonSerializer.Serialize(w, cfgObj);
                    w.WriteEndObject();
                }
                cuerpo = Encoding.UTF8.GetString(ms.ToArray());
            }

            using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/soporte/config-backup"))
            {
                req.Headers.Add("X-Device-ID", cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                req.Content = new StringContent(cuerpo, Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        _ultimoBackup = DateTime.UtcNow;
                        Trace("backup de config subido");
                    }
                    else if ((int)resp.StatusCode != 404)
                    {
                        Trace("backup config -> HTTP " + (int)resp.StatusCode);
                    }
                }
            }
        }

        // Lee los .json de configuracion de ConfigRoot y de GuidanceEngineData
        // en un objeto {archivo: contenido}. Solo config, no lotes ni cache.
        private static Dictionary<string, object> RecolectarConfig()
        {
            var res = new Dictionary<string, object>();
            string cfgRoot = AgroParallel.Common.AgpPaths.ConfigRoot;
            AgregarJsons(res, cfgRoot, "");                                   // *.json de la raiz
            AgregarJsons(res, System.IO.Path.Combine(cfgRoot, "GuidanceEngineData"), "GuidanceEngineData/");
            AgregarJsons(res, System.IO.Path.Combine(cfgRoot, "GuidanceEngineData", "Vehicles"), "Vehicles/");
            return res.Count > 0 ? res : null;
        }

        private static void AgregarJsons(Dictionary<string, object> dst, string dir, string prefijo)
        {
            try
            {
                if (!System.IO.Directory.Exists(dir)) return;
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var fi = new System.IO.FileInfo(f);
                        if (fi.Length > 512 * 1024) continue;   // un .json de config no pesa tanto
                        string txt = System.IO.File.ReadAllText(f);
                        // Guardar como valor JSON (no string escapado) si parsea.
                        try { dst[prefijo + fi.Name] = JsonSerializer.Deserialize<JsonElement>(txt); }
                        catch { dst[prefijo + fi.Name] = txt; }
                    }
                    catch { /* un archivo ilegible no aborta el backup */ }
                }
            }
            catch { }
        }

        /// <summary>Un ciclo: pedir pendientes, correrlos, devolver la salida.
        /// false si el cloud no respondio (para espaciar reintentos).</summary>
        private async Task<bool> Ciclo(CancellationToken ct)
        {
            var cfg = _cfgProvider();
            if (cfg == null || !cfg.Enabled) return true;
            if (string.IsNullOrEmpty(cfg.ServerUrl) ||
                string.IsNullOrEmpty(cfg.DeviceId) ||
                string.IsNullOrEmpty(cfg.DeviceToken))
                return true;   // sin identidad todavia: no es una falla de red

            string baseUrl = cfg.ServerUrl.TrimEnd('/');

            // Backup de config al cloud: una vez al arrancar y despues cada 6 h.
            // La config del equipo (vehiculo, implemento, secciones, nodos) solo
            // vive en el disco de la PC; si se rompe, se pierde. Best-effort: si
            // falla no corta el ciclo de soporte.
            try { await QuizasSubirBackup(baseUrl, cfg, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Trace("backup config: " + ex.Message); }

            string json;

            using (var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/soporte/pendientes"))
            {
                req.Headers.Add("X-Device-ID", cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        // 404 = el server todavia no tiene el modulo de soporte.
                        // No vale la pena reportarlo cada 20 s.
                        if ((int)resp.StatusCode != 404)
                            Trace("pendientes -> HTTP " + (int)resp.StatusCode);
                        return (int)resp.StatusCode < 500;
                    }
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }

            List<Pendiente> pendientes;
            try { pendientes = Parsear(json); }
            catch (Exception ex) { Trace("no pude leer los pendientes: " + ex.Message); return true; }
            if (pendientes.Count == 0) return true;

            Trace(pendientes.Count + " diagnostico(s) pendiente(s)");

            // De a uno. Dos acciones a la vez sobre la misma maquina se pisan
            // (por ejemplo dos que leen el mismo log) y no aportan nada.
            foreach (var p in pendientes)
            {
                if (ct.IsCancellationRequested) return true;
                var r = Correr(p, cfg);
                await Devolver(baseUrl, cfg, r, ct).ConfigureAwait(false);
            }
            return true;
        }

        private sealed class Pendiente
        {
            public string Id;
            public string Accion;
            public Dictionary<string, string> Params = new Dictionary<string, string>();
            public int TimeoutSeg = AccionesSoporte.TimeoutDefaultSeg;
        }

        private sealed class Resultado
        {
            public string Id;
            public bool Ok;
            public string Salida;
            public long Ms;
        }

        private static List<Pendiente> Parsear(string json)
        {
            var lista = new List<Pendiente>();
            using (var doc = JsonDocument.Parse(json))
            {
                var raiz = doc.RootElement;
                // Acepta [..] o {"comandos":[..]}: que el server pueda cambiar
                // de forma sin dejar mudas a las pantallas ya instaladas.
                JsonElement arr;
                if (raiz.ValueKind == JsonValueKind.Array) arr = raiz;
                else if (!raiz.TryGetProperty("comandos", out arr)) return lista;
                if (arr.ValueKind != JsonValueKind.Array) return lista;

                foreach (var e in arr.EnumerateArray())
                {
                    var p = new Pendiente();
                    JsonElement v;
                    if (e.TryGetProperty("id", out v)) p.Id = v.GetString();
                    if (e.TryGetProperty("accion", out v)) p.Accion = v.GetString();
                    int ts;
                    if (e.TryGetProperty("timeout_seg", out v) && v.TryGetInt32(out ts))
                        p.TimeoutSeg = Math.Min(Math.Max(ts, 1), AccionesSoporte.TimeoutMaxSeg);
                    if (e.TryGetProperty("params", out v) && v.ValueKind == JsonValueKind.Object)
                        foreach (var kv in v.EnumerateObject())
                            p.Params[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                                ? kv.Value.GetString() : kv.Value.ToString();
                    if (!string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Accion))
                        lista.Add(p);
                }
            }
            return lista;
        }

        /// <summary>Corre UNA funcion del catalogo. Lo que llega del cloud es
        /// una clave de diccionario: si no esta registrada, no se corre nada.</summary>
        private Resultado Correr(Pendiente p, OrbitX.OrbitXConfig cfg)
        {
            var sw = Stopwatch.StartNew();
            var r = new Resultado { Id = p.Id };

            var accion = AccionesSoporte.Buscar(p.Accion);
            if (accion == null)
            {
                r.Ok = false;
                r.Salida = "Diagnostico desconocido: " + p.Accion +
                           ". Disponibles: " + string.Join(", ", AccionesSoporte.Catalogo.Keys);
                sw.Stop(); r.Ms = sw.ElapsedMilliseconds;
                Trace("rechazado, no esta en el catalogo: " + p.Accion);
                return r;
            }

            try
            {
                // En su propio task para poder cortarlo por timeout: una funcion
                // que se cuelgue no puede dejar mudo el canal.
                string salida = null;
                var tarea = Task.Run(() => { salida = accion.Ejecutar(p.Params); });
                if (!tarea.Wait(TimeSpan.FromSeconds(p.TimeoutSeg)))
                {
                    r.Ok = false;
                    r.Salida = "'" + p.Accion + "' paso los " + p.TimeoutSeg + " s y se corto.";
                }
                else
                {
                    r.Ok = true;
                    r.Salida = salida ?? "";
                }
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Salida = "Fallo '" + p.Accion + "': " + ex.Message;
            }

            // Tapar credenciales y acotar SIEMPRE, salga bien o mal: un stack
            // trace puede llevar adentro el token del equipo.
            r.Salida = AccionesSoporte.Acotar(
                AccionesSoporte.Sanitizar(r.Salida, cfg.DeviceToken));

            sw.Stop(); r.Ms = sw.ElapsedMilliseconds;
            Trace(p.Accion + " -> " + (r.Ok ? "ok" : "error") + " (" + r.Ms + " ms)");
            return r;
        }

        private async Task Devolver(string baseUrl, OrbitX.OrbitXConfig cfg, Resultado r, CancellationToken ct)
        {
            try
            {
                string cuerpo;
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var w = new Utf8JsonWriter(ms))
                    {
                        w.WriteStartObject();
                        w.WriteString("id", r.Id);
                        w.WriteString("device_id", cfg.DeviceId);
                        w.WriteBoolean("ok", r.Ok);
                        w.WriteString("salida", r.Salida ?? "");
                        w.WriteNumber("ms", r.Ms);
                        w.WriteEndObject();
                    }
                    cuerpo = Encoding.UTF8.GetString(ms.ToArray());
                }

                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/soporte/resultado"))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    req.Content = new StringContent(cuerpo, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            Trace("resultado -> HTTP " + (int)resp.StatusCode);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Trace("no pude devolver el resultado: " + ex.Message); }
        }

        public void Dispose()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_bucle != null) _bucle.Wait(2000); } catch { }
            try { if (_http != null) _http.Dispose(); } catch { }
            if (_cts != null) { _cts.Dispose(); _cts = null; }
            _bucle = null;
        }
    }
}
