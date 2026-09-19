// ============================================================================
// ChatSoporteService.cs - Chat de soporte PilotX <-> OrbitX (capa 1).
//
// Por que existe:
//   El canal de diagnostico (SoporteRemotoService) resuelve "correr una funcion
//   nuestra y devolver texto", pero no deja hablar con el operario. Muchas veces
//   lo que hace falta es una conversacion: "abri tal pantalla", "que ves?",
//   "mandame una foto". Este servicio es el TRANSPORTE de ese chat, reusando el
//   mismo patron pull del canal de soporte.
//
// Como funciona:
//   No abre ningun puerto en el tractor. La pantalla PREGUNTA cada pocos
//   segundos si hay mensajes nuestros/bot sin entregar y los guarda en un
//   historial en memoria; cuando el operario escribe, se POSTEA hacia el cloud.
//   Todo sale de adentro hacia afuera, sobre la misma autenticacion por
//   dispositivo que ya usa el sync (X-Device-ID + X-Auth-Token).
//
//     GET  /api/soporte/chat/pendientes  -> { mensajes:[{rol,texto,ts}] }
//     POST /api/soporte/chat/mensaje     <- { texto }
//
//   El GET del cloud MARCA entregado lo que devuelve: un mensaje llega una sola
//   vez, por eso el historial se mantiene ACA en memoria y no se vuelve a pedir.
//
// Ritmo:
//   Poll adaptativo. Mientras el operario esta en el chat (la UI avisa via
//   NotificarActividad) se pregunta cada 3 s para que la conversacion fluya;
//   con el panel cerrado baja a 20 s. Ante fallas de red se espacia hasta 2 min.
//
// A quien se lo sirve:
//   Al proceso mismo (la UI habla con el Engine por HTTP local). El Engine
//   expone /api/chat/* apoyandose en este servicio (Estado/Mensajes/EnviarAsync).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgroParallel.Soporte
{
    /// <summary>Un cambio de config sugerido dentro de una propuesta del bot.
    /// Los valores viajan como texto (pueden ser numeros o strings) — se parsean
    /// al aplicar.</summary>
    public sealed class ChatCambio
    {
        public string Clave { get; set; }
        public string ValorActual { get; set; }
        public string ValorNuevo { get; set; }
    }

    /// <summary>Payload de un mensaje tipo "propuesta_config": la lista de
    /// cambios que sugiere el bot y en que estado esta
    /// (pendiente/aceptada/rechazada/aplicada). La regla dura es que NADA se
    /// aplica solo: el operario Acepta/Rechaza en la pantalla.</summary>
    public sealed class ChatPropuesta
    {
        public List<ChatCambio> Cambios { get; set; } = new List<ChatCambio>();
        public string Estado { get; set; } = "pendiente";
    }

    /// <summary>Un mensaje del chat, tal como lo ve la UI. rol es
    /// "operario" (lo escribio el del tractor), "soporte" (una persona nuestra)
    /// o "bot" (la IA, capa 2).</summary>
    public sealed class ChatMensaje
    {
        public string Rol { get; set; }
        public string Texto { get; set; }
        /// <summary>Epoch en milisegundos (el ts que pone el server).</summary>
        public long Ts { get; set; }
        /// <summary>"texto" (default) o "propuesta_config".</summary>
        public string Tipo { get; set; } = "texto";
        /// <summary>Solo presente cuando Tipo == "propuesta_config".</summary>
        public ChatPropuesta Payload { get; set; }
    }

    /// <summary>Foto del estado del chat para la UI. Rev sube con cada cambio,
    /// asi la pantalla detecta novedad sin comparar listas.</summary>
    public sealed class ChatSnapshot
    {
        public long Rev { get; set; }
        public bool NoLeidos { get; set; }
        public List<ChatMensaje> Mensajes { get; set; } = new List<ChatMensaje>();
    }

    public sealed class ChatSoporteService : IDisposable
    {
        // Con el operario mirando el chat: rapido, para que no se sienta trabado.
        private const int ActivoSeg = 3;
        // Con el panel cerrado: lento, para no castigar a un droplet de 1 GB con
        // 25 pantallas preguntando cada 3 s de gusto.
        private const int InactivoSeg = 20;
        // Ante fallas de red se va espaciando hasta aca.
        private const int MaxSeg = 120;
        // El historial en memoria no crece sin limite: alcanza para la sesion.
        private const int MaxHist = 200;
        // Cuanto dura "activo" desde la ultima senal de la UI.
        private static readonly TimeSpan VentanaActiva = TimeSpan.FromSeconds(45);

        private readonly HttpClient _http;
        private readonly Func<OrbitX.OrbitXConfig> _cfgProvider;
        private readonly Action<string> _log;
        private CancellationTokenSource _cts;
        private Task _bucle;
        // Para despertar el poll en el acto cuando el operario abre el chat o
        // manda un mensaje, sin esperar a que venza el Task.Delay en curso.
        private readonly SemaphoreSlim _despertar = new SemaphoreSlim(0, 1);

        private readonly object _lock = new object();
        private readonly List<ChatMensaje> _hist = new List<ChatMensaje>();
        private long _rev;
        private bool _noLeidos;
        private DateTime _activoHasta = DateTime.MinValue;
        private int _esperaSeg = InactivoSeg;

        public ChatSoporteService(Func<OrbitX.OrbitXConfig> cfgProvider, Action<string> log = null)
        {
            if (cfgProvider == null) throw new ArgumentNullException(nameof(cfgProvider));
            _cfgProvider = cfgProvider;
            _log = log;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        private void Trace(string m)
        {
            try { if (_log != null) _log("[ChatSoporte] " + m); } catch { }
        }

        public void Start()
        {
            if (_bucle != null) return;
            _cts = new CancellationTokenSource();
            _bucle = Task.Run(() => Bucle(_cts.Token));
            Trace("canal de chat arriba");
        }

        // =====================================================================
        //  API para el Engine (lo consume ChatController)
        // =====================================================================

        /// <summary>Estado liviano para el badge del menu: no toca "leido" ni
        /// acelera el poll. Lo consulta el shell cada tanto aunque el chat este
        /// cerrado.</summary>
        public (long Rev, bool NoLeidos) Estado()
        {
            lock (_lock) return (_rev, _noLeidos);
        }

        /// <summary>La conversacion entera. La pide la UI con el panel abierto:
        /// marca todo como leido (el operario lo esta viendo) y pone el canal en
        /// modo activo para que el poll acelere.</summary>
        public ChatSnapshot Mensajes()
        {
            NotificarActividad();
            lock (_lock)
            {
                _noLeidos = false;
                var copia = new List<ChatMensaje>(_hist.Count);
                foreach (var m in _hist)
                    copia.Add(new ChatMensaje
                    {
                        Rol = m.Rol, Texto = m.Texto, Ts = m.Ts,
                        Tipo = m.Tipo,
                        Payload = ClonarPayload(m.Payload)
                    });
                return new ChatSnapshot { Rev = _rev, NoLeidos = false, Mensajes = copia };
            }
        }

        /// <summary>Marca el canal como "activo" y despierta el poll. La UI la
        /// llama al abrir el chat y en cada interaccion.</summary>
        public void NotificarActividad()
        {
            _activoHasta = DateTime.UtcNow + VentanaActiva;
            try { if (_despertar.CurrentCount == 0) _despertar.Release(); } catch { }
        }

        /// <summary>El operario manda un mensaje. Lo postea al cloud y, si sale
        /// bien, lo agrega al historial local para que aparezca en el acto (el
        /// pendientes NO devuelve los propios). false = no se pudo enviar.</summary>
        public async Task<bool> EnviarAsync(string texto, CancellationToken ct = default)
        {
            texto = (texto ?? "").Trim();
            if (texto.Length == 0) return false;
            if (texto.Length > 4000) texto = texto.Substring(0, 4000);

            var cfg = _cfgProvider();
            if (!HayIdentidad(cfg)) return false;
            string baseUrl = cfg.ServerUrl.TrimEnd('/');

            try
            {
                string cuerpo;
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var w = new Utf8JsonWriter(ms))
                    {
                        w.WriteStartObject();
                        w.WriteString("texto", texto);
                        w.WriteEndObject();
                    }
                    cuerpo = Encoding.UTF8.GetString(ms.ToArray());
                }

                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/soporte/chat/mensaje"))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    req.Content = new StringContent(cuerpo, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            Trace("enviar -> HTTP " + (int)resp.StatusCode);
                            return false;
                        }
                    }
                }

                // Eco local: el pendientes del cloud solo trae soporte/bot, nunca
                // los del propio operario. Sin esto el mensaje recien mandado no
                // se veria hasta un hipotetico refresh.
                Agregar("operario", texto, AhoraMs(), esNuestro: false);
                NotificarActividad();
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { Trace("enviar fallo: " + ex.Message); return false; }
        }

        // =====================================================================
        //  poll
        // =====================================================================

        private async Task Bucle(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int baseSeg = DateTime.UtcNow < _activoHasta ? ActivoSeg : InactivoSeg;
                // Espera que se corta si la UI avisa actividad (abrir chat, enviar).
                var demora = Task.Delay(TimeSpan.FromSeconds(_esperaSeg), ct);
                var senal = _despertar.WaitAsync(ct);
                try { await Task.WhenAny(demora, senal).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested) return;

                try
                {
                    bool ok = await Ciclo(ct).ConfigureAwait(false);
                    // Con el cloud respondiendo se vuelve al ritmo (rapido o lento
                    // segun actividad); si falla, se espacia.
                    baseSeg = DateTime.UtcNow < _activoHasta ? ActivoSeg : InactivoSeg;
                    _esperaSeg = ok ? baseSeg : Math.Min(_esperaSeg * 2, MaxSeg);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Trace("ciclo fallo: " + ex.Message);
                    _esperaSeg = Math.Min(_esperaSeg * 2, MaxSeg);
                }
            }
        }

        /// <summary>Un ciclo: pedir los mensajes nuestros/bot sin entregar y
        /// sumarlos al historial. false = el cloud no respondio (para espaciar).</summary>
        private async Task<bool> Ciclo(CancellationToken ct)
        {
            var cfg = _cfgProvider();
            // Sin identidad todavia no es una falla de red: la pantalla no esta
            // vinculada, no hay nada que preguntar.
            if (!HayIdentidad(cfg)) return true;
            string baseUrl = cfg.ServerUrl.TrimEnd('/');

            string json;
            using (var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/soporte/chat/pendientes"))
            {
                req.Headers.Add("X-Device-ID", cfg.DeviceId);
                req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        // 404 = el server todavia no tiene el modulo de chat.
                        if ((int)resp.StatusCode != 404)
                            Trace("pendientes -> HTTP " + (int)resp.StatusCode);
                        return (int)resp.StatusCode < 500;
                    }
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }

            int nuevos = 0;
            try
            {
                using (var doc = JsonDocument.Parse(Limpio(json)))
                {
                    if (doc.RootElement.TryGetProperty("mensajes", out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in arr.EnumerateArray())
                        {
                            string rol = Str(e, "rol");
                            string texto = Str(e, "texto");
                            if (string.IsNullOrEmpty(texto)) continue;
                            long ts = TsDe(e);
                            string tipo = Str(e, "tipo");
                            ChatPropuesta payload = LeerPayload(e);
                            Agregar(string.IsNullOrEmpty(rol) ? "soporte" : rol, texto, ts,
                                    esNuestro: true,
                                    tipo: string.IsNullOrEmpty(tipo) ? "texto" : tipo,
                                    payload: payload);
                            nuevos++;
                        }
                    }
                }
            }
            catch (Exception ex) { Trace("no pude leer los pendientes: " + ex.Message); return true; }

            if (nuevos > 0) Trace(nuevos + " mensaje(s) nuevo(s)");
            return true;
        }

        // =====================================================================
        //  helpers
        // =====================================================================

        private void Agregar(string rol, string texto, long ts, bool esNuestro,
                             string tipo = "texto", ChatPropuesta payload = null)
        {
            lock (_lock)
            {
                _hist.Add(new ChatMensaje
                {
                    Rol = rol, Texto = texto, Ts = ts,
                    Tipo = string.IsNullOrEmpty(tipo) ? "texto" : tipo,
                    Payload = payload
                });
                if (_hist.Count > MaxHist) _hist.RemoveRange(0, _hist.Count - MaxHist);
                // Un mensaje que llega del cloud (soporte/bot) prende el badge;
                // el eco del propio operario no.
                if (esNuestro) _noLeidos = true;
                _rev++;
            }
        }

        // =====================================================================
        //  propuestas de config (capa 2)
        // =====================================================================

        /// <summary>Busca por ts la propuesta pendiente y devuelve una COPIA de
        /// sus cambios, para que el Engine sepa que aplicar. null si no existe o
        /// no es una propuesta.</summary>
        public ChatPropuesta BuscarPropuesta(long ts)
        {
            lock (_lock)
            {
                foreach (var m in _hist)
                    if (m.Ts == ts && m.Tipo == "propuesta_config" && m.Payload != null)
                        return ClonarPayload(m.Payload);
                return null;
            }
        }

        /// <summary>Marca en el historial local el estado resuelto de una
        /// propuesta, asi la UI la repinta sin botones en el proximo tick.</summary>
        private void MarcarPropuestaLocal(long ts, string estado)
        {
            lock (_lock)
            {
                foreach (var m in _hist)
                    if (m.Ts == ts && m.Tipo == "propuesta_config" && m.Payload != null)
                    {
                        m.Payload.Estado = estado;
                        _rev++;
                        return;
                    }
            }
        }

        /// <summary>Reporta al cloud la decision del operario sobre una propuesta
        /// (aceptada/rechazada/aplicada) y actualiza el estado local. El detalle
        /// dice que se aplico y que quedo manual. false = no se pudo avisar.</summary>
        public async Task<bool> ResolverPropuestaAsync(long ts, string estado, string detalle, CancellationToken ct = default)
        {
            var cfg = _cfgProvider();
            if (!HayIdentidad(cfg)) return false;
            string baseUrl = cfg.ServerUrl.TrimEnd('/');

            try
            {
                string cuerpo;
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var w = new Utf8JsonWriter(ms))
                    {
                        w.WriteStartObject();
                        w.WriteNumber("ts", ts);
                        w.WriteString("estado", estado ?? "aceptada");
                        if (!string.IsNullOrEmpty(detalle)) w.WriteString("detalle", detalle);
                        w.WriteEndObject();
                    }
                    cuerpo = Encoding.UTF8.GetString(ms.ToArray());
                }

                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/soporte/chat/propuesta"))
                {
                    req.Headers.Add("X-Device-ID", cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", cfg.DeviceToken);
                    req.Content = new StringContent(cuerpo, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            Trace("propuesta -> HTTP " + (int)resp.StatusCode);
                            // Igual marcamos local: el operario ya decidio, no lo
                            // dejamos con los botones puestos aunque falle el aviso.
                            MarcarPropuestaLocal(ts, estado);
                            return false;
                        }
                    }
                }

                MarcarPropuestaLocal(ts, estado);
                NotificarActividad();
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Trace("propuesta fallo: " + ex.Message);
                MarcarPropuestaLocal(ts, estado);
                return false;
            }
        }

        private static ChatPropuesta ClonarPayload(ChatPropuesta p)
        {
            if (p == null) return null;
            var c = new ChatPropuesta { Estado = p.Estado };
            if (p.Cambios != null)
                foreach (var x in p.Cambios)
                    c.Cambios.Add(new ChatCambio { Clave = x.Clave, ValorActual = x.ValorActual, ValorNuevo = x.ValorNuevo });
            return c;
        }

        /// <summary>Lee el payload {cambios:[{clave,valor_actual,valor_nuevo}],
        /// estado} de un mensaje del cloud. Tolera valores numericos o string.</summary>
        private static ChatPropuesta LeerPayload(JsonElement e)
        {
            if (!e.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object)
                return null;
            var prop = new ChatPropuesta { Estado = Str(p, "estado") ?? "pendiente" };
            if (p.TryGetProperty("cambios", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    prop.Cambios.Add(new ChatCambio
                    {
                        Clave = Str(c, "clave"),
                        ValorActual = Escalar(c, "valor_actual"),
                        ValorNuevo = Escalar(c, "valor_nuevo"),
                    });
                }
            }
            return prop;
        }

        /// <summary>Un valor de cambio, sea numero, bool o string, como texto.</summary>
        private static string Escalar(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var v)) return null;
            switch (v.ValueKind)
            {
                case JsonValueKind.String: return v.GetString();
                case JsonValueKind.Number: return v.GetRawText();
                case JsonValueKind.True:   return "true";
                case JsonValueKind.False:  return "false";
                default: return null;
            }
        }

        private static bool HayIdentidad(OrbitX.OrbitXConfig cfg)
            => cfg != null &&
               !string.IsNullOrEmpty(cfg.ServerUrl) &&
               !string.IsNullOrEmpty(cfg.DeviceId) &&
               !string.IsNullOrEmpty(cfg.DeviceToken);

        private static long AhoraMs()
            => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString() : null;

        /// <summary>El ts del server viene como numero (Date.now()); por las
        /// dudas se acepta tambien string. Si no hay, se estampa ahora.</summary>
        private static long TsDe(JsonElement e)
        {
            if (e.TryGetProperty("ts", out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
            }
            return AhoraMs();
        }

        private static string Limpio(string json)
            => (json ?? "").TrimStart('﻿', '​').TrimStart();

        public void Dispose()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_bucle != null) _bucle.Wait(2000); } catch { }
            try { if (_http != null) _http.Dispose(); } catch { }
            try { _despertar.Dispose(); } catch { }
            if (_cts != null) { _cts.Dispose(); _cts = null; }
            _bucle = null;
        }
    }
}
