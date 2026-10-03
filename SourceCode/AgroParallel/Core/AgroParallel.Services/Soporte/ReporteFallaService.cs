// ============================================================================
// ReporteFallaService.cs — "Reportar falla" en un toque: armar, encolar,
// subir a OrbitX y, si hace falta, copiar a un pendrive.
//
// Flujo:
//   1. La pantalla (PilotX.Desktop) saca la captura ANTES de abrir el panel,
//      el operario escribe qué pasó y toca Enviar → POST /api/soporte/reporte.
//   2. Crear() junta el contexto del Engine (logs, config, perfil, lote,
//      fuentes mudas), arma el ZIP sanitizado y lo guarda en la cola de disco.
//      Devuelve el CÓDIGO al toque: el operario lo dicta por teléfono aunque
//      todavía no haya subido nada.
//   3. Un bucle propio sube los pendientes a OrbitX por el endpoint de sync
//      que ya existe (POST /api/aog/sync, auth por dispositivo), con subtipo
//      propio "soporte_reporte" y es_lote=false. Sin internet espera y
//      reintenta espaciando (60 s → 15 min); al volver la red sale solo.
//   4. CopiarAPendrive() deja el ZIP en <pendrive>\PilotX-Reportes\.
//
// Por qué /api/aog/sync y no un endpoint nuevo: el server de OrbitX hoy no
// tiene uno para archivos de soporte (routes/soporte.js maneja diagnósticos,
// backup de config y chat, todo JSON chico). /api/aog/sync acepta binario en
// contenido_base64, hasta 50 MB de body, y guarda un doc aog_archivo por
// ruta_rel. Qué falta del lado server para LISTARLOS está en el comentario de
// ArmarPayloadSync.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.OrbitX;
using AgroParallel.Services.OrbitX;

namespace AgroParallel.Soporte
{
    public sealed class ResultadoCrearReporte
    {
        public bool Ok { get; set; }
        public string Codigo { get; set; }
        public string Estado { get; set; }
        public string Error { get; set; }
        public int Bytes { get; set; }
        public int Nivel { get; set; }
        public List<string> Omitidos { get; set; } = new List<string>();
    }

    public sealed class ResultadoPendrive
    {
        public bool Ok { get; set; }
        public string Ruta { get; set; }
        public string Error { get; set; }
    }

    public sealed class ReporteFallaService : IDisposable
    {
        public const string Subtipo = "soporte_reporte";

        // Lo que la pantalla puede mandar de sus propios logs: pocos, chicos y
        // con nombre de archivo simple (nunca una ruta).
        private const int MaxLogsPantalla = 6;
        private const int MaxBytesLogPantalla = 512 * 1024;
        private const int MaxDescripcion = 4000;

        private readonly ColaReportesFalla _cola;
        private readonly Func<OrbitXConfig> _cfgProvider;
        private readonly Func<EntradaReporteFalla> _contexto;
        private readonly Action<string> _log;
        private readonly BackoffReintentos _backoff =
            new BackoffReintentos(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(15));
        private readonly Random _rnd = new Random();
        private readonly SemaphoreSlim _subiendo = new SemaphoreSlim(1, 1);
        private HttpClient _http;
        private CancellationTokenSource _cts;
        private Task _bucle;

        /// <param name="contexto">Lo que aporta el host (Engine): archivos,
        /// perfil, lote, versión. Puede devolver null.</param>
        public ReporteFallaService(ColaReportesFalla cola, Func<OrbitXConfig> cfgProvider,
            Func<EntradaReporteFalla> contexto, Action<string> log)
        {
            _cola = cola ?? throw new ArgumentNullException(nameof(cola));
            _cfgProvider = cfgProvider ?? (() => null);
            _contexto = contexto;
            _log = log;
        }

        /// <summary>Unidades extraíbles listas (raíces). Settable para tests.</summary>
        public Func<IEnumerable<string>> UnidadesExtraibles { get; set; } = UnidadesExtraiblesDelSistema;

        /// <summary>Último error de subida (para el panel).</summary>
        public string UltimoError { get; private set; }

        private void Trace(string m)
        {
            try { if (_log != null) _log("[ReporteFalla] " + m); } catch { } // jamás romper por loguear
        }

        // ── Crear ───────────────────────────────────────────────────────────

        public ResultadoCrearReporte Crear(string descripcion, byte[] capturaPng, IDictionary<string, string> logsPantalla)
        {
            try
            {
                EntradaReporteFalla e = null;
                try { if (_contexto != null) e = _contexto(); }
                catch (Exception ex) { Trace("contexto del motor falló: " + ex.Message); }
                if (e == null) e = new EntradaReporteFalla();

                var cfg = SafeCfg();
                string codigo;
                lock (_rnd)
                {
                    do { codigo = ReporteFallaArmador.GenerarCodigo(_rnd); }
                    while (_cola.EstadoDe(codigo) != null);
                }

                e.Codigo = codigo;
                e.Fecha = DateTime.Now;
                e.Descripcion = Recortar(descripcion, MaxDescripcion);
                e.CapturaPng = capturaPng;
                if (cfg != null)
                {
                    if (string.IsNullOrEmpty(e.DeviceId)) e.DeviceId = cfg.DeviceId;
                    if (string.IsNullOrEmpty(e.TokenEquipo)) e.TokenEquipo = cfg.DeviceToken;
                }

                if (logsPantalla != null)
                {
                    int n = 0;
                    foreach (var kv in logsPantalla)
                    {
                        if (n >= MaxLogsPantalla) break;
                        string nombre = NombreSimple(kv.Key);
                        if (nombre == null || kv.Value == null) continue;
                        e.Archivos.Add(new ArchivoReporte
                        {
                            RutaZip = "pantalla/" + nombre,
                            Contenido = kv.Value,
                            Tipo = TipoArchivoReporte.Log,
                            MaxBytes = MaxBytesLogPantalla,
                        });
                        n++;
                    }
                }

                var r = ReporteFallaArmador.Armar(e);
                _cola.Guardar(codigo, r.Zip);
                Trace("reporte " + codigo + " armado (" + r.Zip.Length + " bytes, nivel " + r.Nivel + ")");

                // Que salga ya si hay red; si no, lo levanta el bucle.
                _backoff.RegistrarExito();
                Despertar();

                var res = new ResultadoCrearReporte
                {
                    Ok = true,
                    Codigo = codigo,
                    Estado = ColaReportesFalla.EnCola,
                    Bytes = r.Zip.Length,
                    Nivel = r.Nivel,
                };
                res.Omitidos.AddRange(r.Omitidos);
                return res;
            }
            catch (Exception ex)
            {
                Trace("no se pudo armar el reporte: " + ex.Message);
                return new ResultadoCrearReporte { Ok = false, Error = ex.Message };
            }
        }

        /// <summary>"en_cola", "subido" o null.</summary>
        public string Estado(string codigo) => _cola.EstadoDe(codigo);

        /// <summary>¿Hay vinculación con OrbitX como para intentar subir?</summary>
        public bool Vinculado
        {
            get
            {
                var cfg = SafeCfg();
                return cfg != null && cfg.Enabled && !string.IsNullOrEmpty(cfg.ServerUrl)
                    && !string.IsNullOrEmpty(cfg.DeviceId) && !string.IsNullOrEmpty(cfg.DeviceToken);
            }
        }

        // ── Pendrive ────────────────────────────────────────────────────────

        public ResultadoPendrive CopiarAPendrive(string codigo)
        {
            byte[] zip = _cola.Leer(codigo);
            if (zip == null) return new ResultadoPendrive { Ok = false, Error = "No existe el reporte " + codigo };

            List<string> unidades;
            try { unidades = (UnidadesExtraibles?.Invoke() ?? Enumerable.Empty<string>()).ToList(); }
            catch (Exception ex) { return new ResultadoPendrive { Ok = false, Error = "No se pudieron leer las unidades: " + ex.Message }; }
            if (unidades.Count == 0)
                return new ResultadoPendrive { Ok = false, Error = "No se encontró ningún pendrive. Conectalo y probá de nuevo." };

            string ultimoError = null;
            foreach (var u in unidades)
            {
                try
                {
                    string dir = Path.Combine(u, "PilotX-Reportes");
                    Directory.CreateDirectory(dir);
                    string destino = Path.Combine(dir, codigo + ".zip");
                    File.WriteAllBytes(destino, zip);
                    Trace("reporte " + codigo + " copiado a " + destino);
                    return new ResultadoPendrive { Ok = true, Ruta = destino };
                }
                catch (Exception ex) { ultimoError = ex.Message; }
            }
            return new ResultadoPendrive { Ok = false, Error = "No se pudo escribir en el pendrive: " + ultimoError };
        }

        private static IEnumerable<string> UnidadesExtraiblesDelSistema()
        {
            var res = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Removable && d.IsReady) res.Add(d.RootDirectory.FullName);
                }
                catch (IOException) { } // unidad que desaparece en el medio
            }
            return res;
        }

        // ── Subida a OrbitX ─────────────────────────────────────────────────

        public void Start()
        {
            if (_bucle != null) return;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _bucle = Task.Run(() => Bucle(ct));
        }

        private readonly SemaphoreSlim _despertador = new SemaphoreSlim(0, 1);

        private void Despertar()
        {
            try { if (_despertador.CurrentCount == 0) _despertador.Release(); }
            catch (SemaphoreFullException) { }
        }

        private async Task Bucle(CancellationToken ct)
        {
            // Primer intento enseguida: puede haber pendientes de una corrida
            // anterior que no llegaron a subir.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_backoff.PuedeIntentar(DateTime.UtcNow) && _cola.Pendientes().Count > 0)
                        await SubirPendientesAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Trace("bucle: " + ex.Message); }

                try { await _despertador.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Sube todos los pendientes (de a uno). false si alguno falló.</summary>
        public async Task<bool> SubirPendientesAsync(CancellationToken ct)
        {
            if (!Vinculado) return false;   // sin vinculación no es falla de red: queda en cola
            if (!await _subiendo.WaitAsync(0).ConfigureAwait(false)) return false;
            try
            {
                foreach (var codigo in _cola.Pendientes())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!await SubirUnoAsync(codigo, ct).ConfigureAwait(false))
                    {
                        var espera = _backoff.RegistrarFallo(DateTime.UtcNow);
                        Trace("reporte " + codigo + " sigue en cola (" + UltimoError + "); reintento en "
                              + (int)espera.TotalSeconds + " s");
                        return false;
                    }
                    _backoff.RegistrarExito();
                }
                return true;
            }
            finally { _subiendo.Release(); }
        }

        private async Task<bool> SubirUnoAsync(string codigo, CancellationToken ct)
        {
            var cfg = SafeCfg();
            byte[] zip = _cola.Leer(codigo);
            if (zip == null) return true;   // lo borraron a mano: nada que subir
            if (cfg == null || _http == null) { UltimoError = "sin configuración"; return false; }

            string descripcion = null;
            try { descripcion = LeerDescripcion(zip); } catch { } // solo para el doc; no es crítico

            var payload = ArmarPayloadSync(codigo, zip, cfg.DeviceId, SafeVersion(), descripcion,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            string url = cfg.ServerUrl.TrimEnd('/') + "/api/aog/sync";
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    if (!string.IsNullOrEmpty(cfg.EstabSlug)) req.Headers.Add("X-Estab-Slug", cfg.EstabSlug);
                    req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            _cola.MarcarEnviado(codigo);
                            UltimoError = null;
                            Trace("reporte " + codigo + " subido a OrbitX (" + zip.Length + " bytes)");
                            return true;
                        }
                        UltimoError = "HTTP " + (int)resp.StatusCode;
                        return false;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { UltimoError = "timeout"; return false; }
            catch (Exception ex) { UltimoError = ex.Message; return false; }
        }

        /// <summary>
        /// Body para POST /api/aog/sync. El server (routes/aog.js) guarda un doc
        /// tipo:"aog_archivo" con _id "aog_&lt;estab&gt;_soporte_RF-XXX-XXX.zip" y
        /// los campos ruta_rel, nombre, subtipo, es_lote, hash_md5, tamaño,
        /// device_id, ts y contenido_base64. Los campos extra (codigo, version,
        /// descripcion) HOY el server los ignora.
        ///
        /// Qué tendría que hacer el server para que soporte los vea (pendiente,
        /// fuera de este repo):
        ///   · GET /api/soporte/reportes (JWT, scoped por org): find en las orgDB
        ///     con selector { tipo:"aog_archivo", subtipo:"soporte_reporte" },
        ///     fields SIN contenido_base64, orden por ts; índice Mango en
        ///     [tipo, subtipo, ts].
        ///   · GET /api/soporte/reportes/:codigo/zip → decodifica contenido_base64.
        ///   · Búsqueda por código: el código está en `nombre` (RF-XXX-XXX.zip) y
        ///     en ruta_rel; ideal persistir también `codigo` y `descripcion`.
        ///   · Excluir subtipo "soporte_reporte" de los listados de archivos AOG
        ///     genéricos (pesan: hasta ~5,5 MB en base64 por doc).
        /// </summary>
        public static Dictionary<string, object> ArmarPayloadSync(string codigo, byte[] zip, string deviceId,
            string version, string descripcion, long tsMs)
        {
            return new Dictionary<string, object>
            {
                { "ruta_rel", "soporte/" + codigo + ".zip" },
                { "nombre", codigo + ".zip" },
                { "subtipo", Subtipo },
                { "producto", "PilotX" },
                { "es_lote", false },
                { "lote_nombre", null },
                { "hash_md5", Md5(zip) },
                { "tamano", zip.Length },
                { "ts", tsMs },
                { "device_id", deviceId ?? "" },
                { "contenido_base64", Convert.ToBase64String(zip) },
                // Extra para cuando el server los guarde:
                { "codigo", codigo },
                { "version", version ?? "" },
                { "descripcion", Recortar(descripcion, 500) ?? "" },
            };
        }

        private static string LeerDescripcion(byte[] zip)
        {
            using (var ms = new MemoryStream(zip))
            using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read))
            {
                var e = z.GetEntry("descripcion.txt");
                if (e == null) return null;
                using (var sr = new StreamReader(e.Open(), Encoding.UTF8)) return sr.ReadToEnd().Trim();
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private OrbitXConfig SafeCfg()
        {
            try { return _cfgProvider(); } catch { return null; }
        }

        private static string SafeVersion()
        {
            try { return PilotXSelfUpdate.Snapshot().CurrentVersion; } catch { return ""; }
        }

        private static string Md5(byte[] datos)
        {
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(datos)).Replace("-", "").ToLowerInvariant();
        }

        private static string Recortar(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>Nombre de archivo simple para los logs que manda la
        /// pantalla: letras, números, punto, guion y guion bajo. null = rechazado.</summary>
        internal static string NombreSimple(string nombre)
        {
            if (string.IsNullOrWhiteSpace(nombre) || nombre.Length > 60) return null;
            foreach (char c in nombre)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return null;
            if (nombre.StartsWith(".")) return null;
            return nombre;
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _http?.Dispose(); } catch { }
            _cts = null;
            _http = null;
        }
    }
}
