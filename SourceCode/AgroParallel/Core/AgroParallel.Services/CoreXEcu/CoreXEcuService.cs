// ============================================================================
// CoreXEcuService.cs
// Proxy HTTP del Hub hacia el firmware CoreX-ECU (Teensy 4.1, v1.11+).
//
// Config persistida en corexEcu.json al lado del .exe del Hub. Si el archivo
// no existe, escribe el default (IP 192.168.5.126:80, timeout 3 s).
//
// Las llamadas al Teensy se hacen con un HttpClient compartido + timeout
// configurable (default 3 s; el firmware responde típico ~60 ms). Si el
// Teensy no responde, la UI recibe un snapshot con ok=false + errorCode
// AGP-NET-* + errorTechnical para soporte. Eso evita que la página se rompa
// si la LAN se cae mientras el operario está mirando el dashboard.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.OrbitX;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class CoreXEcuService : ICoreXEcuService
    {
        private const string FileName = "corexEcu.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly HttpClient _http;
        private readonly object _lock = new object();
        private CoreXEcuConfigDto _cfg;

        public CoreXEcuService()
        {
            // Timeout amplio del HttpClient; el corte fino lo hacemos con CTS por
            // request usando TimeoutMs del config. Así un cambio en el JSON no
            // requiere recrear el HttpClient.
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _cfg = LoadOrDefault();
        }

        // -------- Config --------------------------------------------------------

        public CoreXEcuConfigDto LoadConfig()
        {
            lock (_lock) return Clone(_cfg);
        }

        public void SaveConfig(CoreXEcuConfigDto dto)
        {
            if (dto == null) return;
            lock (_lock)
            {
                _cfg = Clone(dto);
                WriteAtomic(Path(), JsonSerializer.Serialize(_cfg, WriteOpts));
            }
        }

        /// <summary>
        /// RMW atómico bajo el mismo lock — evita que dos requests concurrentes
        /// que hagan LoadConfig → modify → SaveConfig pisen los cambios del otro.
        /// El delegate recibe una copia mutable; lo que se persiste es lo que
        /// quede en ella al volver. Si el delegate devuelve false el cambio se
        /// descarta (read-only inspect).
        /// </summary>
        public CoreXEcuConfigDto UpdateConfig(Func<CoreXEcuConfigDto, bool> mutate)
        {
            if (mutate == null) return LoadConfig();
            lock (_lock)
            {
                var copy = Clone(_cfg);
                bool persist = mutate(copy);
                if (persist)
                {
                    _cfg = copy;
                    WriteAtomic(Path(), JsonSerializer.Serialize(_cfg, WriteOpts));
                }
                return Clone(_cfg);
            }
        }

        /// <summary>
        /// Escritura atómica: escribe a `.tmp` y reemplaza el destino con File.Replace.
        /// Sin esto, un crash entre WriteAllText y disk-flush deja el JSON truncado
        /// y la próxima carga cae a defaults — perdemos la config del operario.
        /// </summary>
        private static void WriteAtomic(string path, string contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            if (File.Exists(path))
            {
                // File.Replace garantiza rename atómico en NTFS y preserva atributos.
                // backupFileName=null → no guarda backup.
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        private CoreXEcuConfigDto LoadOrDefault()
        {
            string p = Path();
            if (!File.Exists(p))
            {
                var def = new CoreXEcuConfigDto();
                try { WriteAtomic(p, JsonSerializer.Serialize(def, WriteOpts)); }
                catch { /* swallow — corremos sin persistir */ }
                return def;
            }
            try
            {
                return JsonSerializer.Deserialize<CoreXEcuConfigDto>(File.ReadAllText(p), ReadOpts)
                    ?? new CoreXEcuConfigDto();
            }
            catch
            {
                // Archivo corrupto: NO sobrescribimos — el operario puede tener
                // la IP correcta editada a mano. Devolvemos defaults sólo para
                // que el Hub arranque.
                return new CoreXEcuConfigDto();
            }
        }

        private static string Path()
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
        }

        private static CoreXEcuConfigDto Clone(CoreXEcuConfigDto src)
        {
            return new CoreXEcuConfigDto
            {
                Enabled = src.Enabled,
                Ip = src.Ip,
                Port = src.Port,
                TimeoutMs = src.TimeoutMs,
                WasSource = NormalizeWasSource(src.WasSource)
            };
        }

        /// <summary>Mapea la preferencia persistida del Hub a un valor que entiende
        /// el firmware. v1.11+ acepta keya / ads_se / ads_diff; v1.14 agrega
        /// "bno_was" (BNO085 en modo RVC sobre Serial3) que la UI ofrece como
        /// tercer botón "YAW". Si el firmware del tractor todavía está en
        /// v1.11..v1.13 va a contestar 400 invalid_source y el controller lo
        /// mapea a AGP-NET-400 para el operario.
        ///
        /// Compat de schemas viejos:
        /// - "analog" (Hub pre-v1.11) → "ads_se"
        /// - "yaw"    (Hub pre-v1.14) → "bno_was" — antes la UI mandaba "yaw"
        ///   directo y el firmware lo rechazaba; persistirlo así dejaba la
        ///   preferencia rota hasta que el operario tocara el botón. Acá se
        ///   reescribe al nombre canónico apenas se carga.</summary>
        private static string NormalizeWasSource(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "keya";
            string s = raw.Trim().ToLowerInvariant();
            if (s == "keya" || s == "ads_se" || s == "ads_diff" || s == "bno_was") return s;
            if (s == "analog") return "ads_se"; // migración del schema viejo
            if (s == "yaw")    return "bno_was"; // migración del schema pre-v1.14
            return "keya";
        }

        private string BaseUrl(CoreXEcuConfigDto cfg)
        {
            return "http://" + (cfg.Ip ?? "") + ":" + cfg.Port;
        }

        private int Timeout(CoreXEcuConfigDto cfg)
        {
            return cfg.TimeoutMs > 0 ? cfg.TimeoutMs : 3000;
        }

        // -------- /api/status ---------------------------------------------------

        public async Task<CoreXEcuStatusDto> GetStatusAsync()
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return StubStatus("AGP-NET-100", "Comunicación con CoreX-ECU deshabilitada en la config.", "");

            string url = BaseUrl(cfg) + "/api/status";
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                {
                    var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return StubStatus("AGP-NET-101", "El CoreX-ECU respondió HTTP " + (int)resp.StatusCode + ".", url);

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body))
                        return StubStatus("AGP-NET-102", "El CoreX-ECU devolvió un body vacío.", url);

                    var dto = JsonSerializer.Deserialize<CoreXEcuStatusDto>(body, ReadOpts)
                              ?? new CoreXEcuStatusDto();
                    dto.Ok = true;
                    dto.Source = "teensy";
                    return dto;
                }
            }
            catch (TaskCanceledException)
            {
                return StubStatus("AGP-NET-002", "El CoreX-ECU no respondió a tiempo. Verificá la IP y que esté encendido.", url);
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return StubStatus(mapped.Code, mapped.Friendly, mapped.Technical);
            }
        }

        // -------- /api/params (GET) ---------------------------------------------

        public async Task<CoreXEcuParamsDto> GetParamsAsync()
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return StubParams("AGP-NET-100", "Comunicación CoreX-ECU deshabilitada.");

            string url = BaseUrl(cfg) + "/api/params";
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                {
                    var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return StubParams("AGP-NET-101", "HTTP " + (int)resp.StatusCode);

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<CoreXEcuParamsDto>(body, ReadOpts)
                              ?? new CoreXEcuParamsDto();
                    dto.Ok = true;
                    return dto;
                }
            }
            catch (TaskCanceledException)
            {
                return StubParams("AGP-NET-002", "Timeout consultando /api/params.");
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return StubParams(mapped.Code, mapped.Friendly);
            }
        }

        // -------- /api/params (POST) --------------------------------------------

        public async Task<CoreXEcuParamsUpdateResultDto> UpdateParamsAsync(Dictionary<string, object> patch)
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuParamsUpdateResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };
            if (patch == null || patch.Count == 0)
                return new CoreXEcuParamsUpdateResultDto { Ok = false, ErrorCode = "AGP-NET-103", Error = "Patch vacío." };

            string url = BaseUrl(cfg) + "/api/params";
            // Body FLAT — el firmware enrutea cada clave a su grupo. Las claves
            // fuera de rango se ignoran silenciosamente y `updated.*` lo refleja.
            string payload = JsonSerializer.Serialize(patch);

            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return new CoreXEcuParamsUpdateResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "HTTP " + (int)resp.StatusCode };

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<CoreXEcuParamsUpdateResultDto>(body, ReadOpts)
                              ?? new CoreXEcuParamsUpdateResultDto();
                    return dto;
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuParamsUpdateResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuParamsUpdateResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        // -------- /api/wassrc (v1.11+) -----------------------------------------

        public async Task<CoreXEcuWassrcDto> GetWassrcAsync()
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuWassrcDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            string url = BaseUrl(cfg) + "/api/wassrc";
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                {
                    var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return new CoreXEcuWassrcDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "HTTP " + (int)resp.StatusCode };

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<CoreXEcuWassrcDto>(body, ReadOpts)
                              ?? new CoreXEcuWassrcDto();
                    dto.Ok = true;
                    return dto;
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuWassrcDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout consultando /api/wassrc." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuWassrcDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        public async Task<CoreXEcuWassrcUpdateResultDto> SetWassrcAsync(string source)
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuWassrcUpdateResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            // Pre-validación local — evita ida y vuelta para typos. El firmware
            // igual valida y responde 400 invalid_source si llega algo raro.
            string normalized = NormalizeWasSource(source);

            string url = BaseUrl(cfg) + "/api/wassrc";
            string payload = JsonSerializer.Serialize(new CoreXEcuWassrcRequestDto { Source = normalized });
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        var dto = JsonSerializer.Deserialize<CoreXEcuWassrcUpdateResultDto>(body, ReadOpts)
                                  ?? new CoreXEcuWassrcUpdateResultDto();
                        dto.Ok = true;
                        return dto;
                    }

                    int code = (int)resp.StatusCode;
                    if (code == 400)
                    {
                        // El firmware devuelve { error: "invalid_source", options: [...] }
                        var bad = JsonSerializer.Deserialize<CoreXEcuWassrcUpdateResultDto>(body, ReadOpts)
                                  ?? new CoreXEcuWassrcUpdateResultDto();
                        bad.Ok = false;
                        bad.ErrorCode = "AGP-NET-400";
                        bad.Error = "Fuente WAS no reconocida por el firmware.";
                        return bad;
                    }

                    var errDto = ParseFirmwareError(body);
                    return new CoreXEcuWassrcUpdateResultDto
                    {
                        Ok = false,
                        ErrorCode = "AGP-NET-101",
                        Error = "HTTP " + code + (string.IsNullOrEmpty(errDto.Error) ? "" : " · " + errDto.Error)
                    };
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuWassrcUpdateResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout cambiando fuente WAS." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuWassrcUpdateResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        // -------- /api/zero -----------------------------------------------------

        public async Task<CoreXEcuZeroResultDto> ForceZeroAsync()
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuZeroResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            string url = BaseUrl(cfg) + "/api/zero";
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var content = new StringContent("", Encoding.UTF8, "application/json"))
                {
                    var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return new CoreXEcuZeroResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "HTTP " + (int)resp.StatusCode };

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<CoreXEcuZeroResultDto>(body, ReadOpts)
                              ?? new CoreXEcuZeroResultDto();
                    return dto;
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuZeroResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuZeroResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        // -------- /api/reboot ---------------------------------------------------

        public async Task<bool> RebootAsync()
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled) return false;
            string url = BaseUrl(cfg) + "/api/reboot";
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var content = new StringContent("", Encoding.UTF8, "application/json"))
                {
                    var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    return resp.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        // -------- /api/motor/test (v1.09+) --------------------------------------

        public async Task<CoreXEcuMotorTestResultDto> MotorTestAsync(int pwm, int durationMs)
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuMotorTestResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            // Clamp acá también — sirve para evitar tráfico inútil si el JS manda fuera de rango.
            if (pwm < -200) pwm = -200; else if (pwm > 200) pwm = 200;
            if (durationMs < 100) durationMs = 100; else if (durationMs > 5000) durationMs = 5000;

            string url = BaseUrl(cfg) + "/api/motor/test";
            string payload = JsonSerializer.Serialize(new CoreXEcuMotorTestRequestDto { Pwm = pwm, DurationMs = durationMs });
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        var dto = JsonSerializer.Deserialize<CoreXEcuMotorTestResultDto>(body, ReadOpts)
                                  ?? new CoreXEcuMotorTestResultDto();
                        dto.Ok = true;
                        return dto;
                    }

                    // 409 → guidance_active. Parseamos {error,detail} del firmware.
                    var errDto = ParseFirmwareError(body);
                    if ((int)resp.StatusCode == 409)
                    {
                        return new CoreXEcuMotorTestResultDto
                        {
                            Ok = false,
                            ErrorCode = "AGP-NET-409",
                            Error = "El motor no puede moverse: hay guidance externa activa. Pausá la dirección automática primero.",
                            Detail = errDto.Detail ?? errDto.Error ?? ""
                        };
                    }
                    return new CoreXEcuMotorTestResultDto
                    {
                        Ok = false,
                        ErrorCode = "AGP-NET-101",
                        Error = "HTTP " + (int)resp.StatusCode,
                        Detail = errDto.Detail ?? errDto.Error ?? ""
                    };
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuMotorTestResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout enviando motor/test." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuMotorTestResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        // -------- /api/firmware (OTA Teensy via FlasherX) -----------------------

        public async Task<CoreXEcuFlashResultDto> FlashFirmwareAsync(string version)
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };
            if (string.IsNullOrWhiteSpace(version))
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-103", Error = "Falta la versión a flashear." };
            // Sanitizar: rechazar separadores de path / `..` antes de armar la ruta.
            if (version.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || version.Contains(".."))
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-103", Error = "Versión inválida." };

            // Resolver el .hex en el MISMO cache que sirve el Firmware Manager.
            OrbitXConfig ocfg;
            try { ocfg = OrbitXConfig.Load(); } catch { ocfg = new OrbitXConfig(); }
            string cacheDir = FirmwareMirror.ResolveCacheDir(ocfg);
            string hexPath = FirmwareMirror.PathBin(cacheDir, "corex-ecu", version);
            if (!File.Exists(hexPath))
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-SYS-009", Error = "No encontré ese firmware en el cache. Subilo o sincronizalo primero.", Detail = "corex-ecu/" + version };

            string url = BaseUrl(cfg) + "/api/firmware";
            long size = new FileInfo(hexPath).Length;

            // El flasheo bloquea unos segundos: el firmware consume el body char a
            // char (yield al IMU) y recién responde 200 antes del flash_move(). El
            // _http compartido tiene Timeout=15s; usamos un cliente dedicado con
            // timeout amplio para no cortar a mitad de un .hex de ~1.3 MB.
            try
            {
                using (var flashHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                using (var fs = File.OpenRead(hexPath))
                using (var content = new StreamContent(fs))
                {
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = size;

                    var resp = await flashHttp.PostAsync(url, content).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                        return new CoreXEcuFlashResultDto { Ok = true, Version = version, BytesSent = size };

                    var errDto = ParseFirmwareError(body);
                    int code = (int)resp.StatusCode;
                    if (code == 409)
                        return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-409", Error = "No se puede actualizar con el guiado activo. Pausá la dirección automática primero.", Detail = errDto.Detail ?? errDto.Error ?? "" };
                    if (code == 400)
                        return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "El firmware rechazó el archivo (HEX inválido o no es de este equipo).", Detail = errDto.Error ?? errDto.Detail ?? "" };
                    if (code == 413)
                        return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "El archivo es demasiado grande para la unidad.", Detail = errDto.Error ?? "" };
                    return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "HTTP " + code, Detail = errDto.Detail ?? errDto.Error ?? "" };
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout enviando el firmware." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuFlashResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        // -------- /api/motor/stop ----------------------------------------------

        public Task<CoreXEcuOkResultDto> MotorStopAsync()
        {
            return PostEmptyOkAsync("/api/motor/stop");
        }

        // -------- /api/calibration/pwm-sweep (v1.10+) --------------------------

        public async Task<CoreXEcuSweepStartResultDto> StartSweepAsync(int stepDurationMs, int settleMs)
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuSweepStartResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            // Defaults + clamp según spec.
            if (stepDurationMs <= 0) stepDurationMs = 1500;
            if (settleMs <= 0) settleMs = 400;
            if (stepDurationMs < 500) stepDurationMs = 500; else if (stepDurationMs > 3000) stepDurationMs = 3000;
            if (settleMs < 100) settleMs = 100; else if (settleMs > 2000) settleMs = 2000;

            string url = BaseUrl(cfg) + "/api/calibration/pwm-sweep";
            string payload = JsonSerializer.Serialize(new CoreXEcuSweepStartRequestDto
            {
                StepDurationMs = stepDurationMs,
                SettleMs = settleMs
            });
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                {
                    var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    int code = (int)resp.StatusCode;

                    // El firmware contesta 202 Accepted en el éxito.
                    if (resp.IsSuccessStatusCode || code == 202)
                    {
                        var dto = JsonSerializer.Deserialize<CoreXEcuSweepStartResultDto>(body, ReadOpts)
                                  ?? new CoreXEcuSweepStartResultDto();
                        dto.Ok = true;
                        return dto;
                    }

                    var errDto = ParseFirmwareError(body);
                    if (code == 409)
                    {
                        string friendly = string.Equals(errDto.Error, "guidance_active", StringComparison.OrdinalIgnoreCase)
                            ? "No se puede calibrar: hay guidance externa activa. Pausá la dirección automática."
                            : "Ya hay un barrido en curso. Esperá a que termine o cancelalo.";
                        return new CoreXEcuSweepStartResultDto
                        {
                            Ok = false,
                            ErrorCode = "AGP-NET-409",
                            Error = friendly,
                            Detail = errDto.Detail ?? errDto.Error ?? ""
                        };
                    }
                    return new CoreXEcuSweepStartResultDto
                    {
                        Ok = false,
                        ErrorCode = "AGP-NET-101",
                        Error = "HTTP " + code,
                        Detail = errDto.Detail ?? errDto.Error ?? ""
                    };
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuSweepStartResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout iniciando sweep." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuSweepStartResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        public async Task<CoreXEcuSweepStatusDto> GetSweepAsync()
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuSweepStatusDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            string url = BaseUrl(cfg) + "/api/calibration/pwm-sweep";
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                {
                    var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return new CoreXEcuSweepStatusDto { Ok = false, ErrorCode = "AGP-NET-101", Error = "HTTP " + (int)resp.StatusCode };

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<CoreXEcuSweepStatusDto>(body, ReadOpts)
                              ?? new CoreXEcuSweepStatusDto();
                    dto.Ok = true;
                    return dto;
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuSweepStatusDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout consultando sweep." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuSweepStatusDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        public async Task<CoreXEcuOkResultDto> CancelSweepAsync()
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuOkResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            string url = BaseUrl(cfg) + "/api/calibration/pwm-sweep";
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var req = new HttpRequestMessage(HttpMethod.Delete, url))
                {
                    var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var errDto = ParseFirmwareError(body);
                        return new CoreXEcuOkResultDto
                        {
                            Ok = false,
                            ErrorCode = "AGP-NET-101",
                            Error = "HTTP " + (int)resp.StatusCode,
                            Detail = errDto.Detail ?? errDto.Error ?? ""
                        };
                    }
                    return new CoreXEcuOkResultDto { Ok = true };
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuOkResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout cancelando sweep." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuOkResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        // -------- Helpers -------------------------------------------------------

        /// <summary>POST con body vacío → { ok }. Mismo patrón que /api/zero + /api/reboot.</summary>
        private async Task<CoreXEcuOkResultDto> PostEmptyOkAsync(string path)
        {
            var cfg = LoadConfig();
            if (!cfg.Enabled)
                return new CoreXEcuOkResultDto { Ok = false, ErrorCode = "AGP-NET-100", Error = "Comunicación CoreX-ECU deshabilitada." };

            string url = BaseUrl(cfg) + path;
            try
            {
                using (var cts = new CancellationTokenSource(Timeout(cfg)))
                using (var content = new StringContent("", Encoding.UTF8, "application/json"))
                {
                    var resp = await _http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var errDto = ParseFirmwareError(body);
                        return new CoreXEcuOkResultDto
                        {
                            Ok = false,
                            ErrorCode = "AGP-NET-101",
                            Error = "HTTP " + (int)resp.StatusCode,
                            Detail = errDto.Detail ?? errDto.Error ?? ""
                        };
                    }
                    return new CoreXEcuOkResultDto { Ok = true };
                }
            }
            catch (TaskCanceledException)
            {
                return new CoreXEcuOkResultDto { Ok = false, ErrorCode = "AGP-NET-002", Error = "Timeout." };
            }
            catch (Exception ex)
            {
                var mapped = AgpErrorMapper.FromException(ex);
                return new CoreXEcuOkResultDto { Ok = false, ErrorCode = mapped.Code, Error = mapped.Friendly };
            }
        }

        /// <summary>Parsea el body de error estándar del firmware { error, detail }.
        /// Devuelve un dto vacío si el body no se puede leer — la UI igual ve el
        /// AGP-* code que armamos en el caller.</summary>
        private sealed class FirmwareErrorDto
        {
            [JsonPropertyName("error")]  public string Error { get; set; }
            [JsonPropertyName("detail")] public string Detail { get; set; }
        }
        private static FirmwareErrorDto ParseFirmwareError(string body)
        {
            try { return JsonSerializer.Deserialize<FirmwareErrorDto>(body, ReadOpts) ?? new FirmwareErrorDto(); }
            catch { return new FirmwareErrorDto(); }
        }

        private static CoreXEcuStatusDto StubStatus(string code, string friendly, string technical)
        {
            return new CoreXEcuStatusDto
            {
                Ok = false,
                Source = "stub",
                ErrorCode = code,
                Error = friendly,
                ErrorTechnical = technical
            };
        }

        private static CoreXEcuParamsDto StubParams(string code, string friendly)
        {
            return new CoreXEcuParamsDto
            {
                Ok = false,
                ErrorCode = code,
                Error = friendly
            };
        }
    }
}
