// ============================================================================
// SonidosAlarmService.cs — detección de eventos de alarma sonora de cabina.
//
// Evalúa cada 500 ms contra el estado que el motor ya tiene:
//   · piloto_on / piloto_off   — flanco de IsAutoSteerOn (IAogStateProvider)
//   · dosis_baja / dosis_alta  — motores QuantiX: error % sostenido vs target
//   · motor_parado             — target > 0 y rpm 0 sostenido
//   · tubo_sin_semilla         — VistaX: surco en alerta sostenida
//   · tolva_sin_semilla        — VistaX: TODOS los surcos activos de un tren
//                                sin caída sostenida (≈ tolva vacía)
//
// La detección corre siempre; el MUTE solo silencia (la pantalla muestra las
// alarmas igual). Quién SUENA es el cliente (Desktop/HTML): acá solo se emiten
// "disparos" numerados (seq) que el cliente consume con ?desde=seq — así un
// reinicio del cliente no re-suena todo el historial.
//
// Los umbrales/sostenidos son por-evento y configurables (sonidos.json).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class SonidosAlarmService : IDisposable
    {
        private readonly IAogStateProvider _state;
        private readonly INodoRegistryService _nodos;
        private readonly IVistaXLiveService _vistax;
        private System.Timers.Timer _timer;
        private readonly object _lock = new object();

        private SonidosConfigDto _cfg;
        private DateTime _cfgStamp;

        // Estado por evento+clave (un motor_parado por motor, un tubo por surco).
        private sealed class Episodio
        {
            public DateTime CondicionDesde;   // cuándo empezó a cumplirse
            public bool Activo;               // ya disparó y sigue activo
            public DateTime UltimoDisparo;
            public string Detalle;
        }
        private readonly Dictionary<string, Episodio> _episodios = new Dictionary<string, Episodio>();

        private long _seq;
        private readonly List<SonidoDisparoDto> _historial = new List<SonidoDisparoDto>();
        private const int MaxHistorial = 64;

        private bool _pilotoPrevio;
        private bool _tienePilotoPrevio;

        public SonidosAlarmService(IAogStateProvider state, INodoRegistryService nodos,
            IVistaXLiveService vistax = null)
        {
            _state = state;
            _nodos = nodos;
            _vistax = vistax;
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new System.Timers.Timer { Interval = 500, AutoReset = true };
            _timer.Elapsed += (s, e) => { try { Tick(); } catch { /* la alarma nunca voltea al motor */ } };
            _timer.Start();
        }

        public void Dispose()
        {
            _timer?.Stop(); _timer?.Dispose(); _timer = null;
        }

        // ---- config -----------------------------------------------------------

        private const string ConfigFile = "sonidos.json";

        public static SonidosConfigDto ConfigDefault()
        {
            return new SonidosConfigDto
            {
                Mute = false,
                Eventos = new List<SonidoEventoDto>
                {
                    new SonidoEventoDto { Id = "piloto_on",  Nombre = "Piloto activado",   Sonido = "steer_on.wav" },
                    new SonidoEventoDto { Id = "piloto_off", Nombre = "Piloto desactivado", Sonido = "steer_off.wav" },
                    new SonidoEventoDto { Id = "dosis_baja", Nombre = "Dosis no alcanza",  Sonido = "dosis_baja.wav",
                        UmbralPct = 10, SostenidoSeg = 5, RepetirSeg = 15 },
                    new SonidoEventoDto { Id = "dosis_alta", Nombre = "Dosis sobrepasada", Sonido = "dosis_alta.wav",
                        UmbralPct = 10, SostenidoSeg = 5, RepetirSeg = 15 },
                    new SonidoEventoDto { Id = "motor_parado", Nombre = "Motor sin girar", Sonido = "alarma.wav",
                        SostenidoSeg = 3, RepetirSeg = 10 },
                    new SonidoEventoDto { Id = "tubo_sin_semilla", Nombre = "Tubo de bajada sin semilla", Sonido = "alarma.wav",
                        SostenidoSeg = 4, RepetirSeg = 12 },
                    new SonidoEventoDto { Id = "tolva_sin_semilla", Nombre = "Tolva sin semilla", Sonido = "tolva.wav",
                        SostenidoSeg = 4, RepetirSeg = 8 },
                }
            };
        }

        public SonidosConfigDto GetConfig()
        {
            lock (_lock)
            {
                if (_cfg != null && (DateTime.UtcNow - _cfgStamp).TotalSeconds < 2) return _cfg;
                var path = System.IO.Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, ConfigFile);
                SonidosConfigDto dto = null;
                try
                {
                    dto = AgroParallel.Common.AtomicJson.Read<SonidosConfigDto>(path,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { }
                if (dto == null || dto.Eventos == null || dto.Eventos.Count == 0)
                    dto = ConfigDefault();
                else
                {
                    // Eventos nuevos del código que el JSON viejo no tiene: sumarlos
                    // con su default (upgrade sin perder lo configurado).
                    foreach (var def in ConfigDefault().Eventos)
                        if (!dto.Eventos.Any(e => e.Id == def.Id)) dto.Eventos.Add(def);
                }
                _cfg = dto; _cfgStamp = DateTime.UtcNow;
                return dto;
            }
        }

        public bool SaveConfig(SonidosConfigDto dto)
        {
            if (dto == null) return false;
            lock (_lock)
            {
                var path = System.IO.Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, ConfigFile);
                try
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(dto,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    AgroParallel.Common.AtomicJson.Write(path, json);
                    _cfg = dto; _cfgStamp = DateTime.UtcNow;
                    return true;
                }
                catch { return false; }
            }
        }

        // ---- estado para clientes --------------------------------------------

        public SonidosEstadoDto GetEstado(long desdeSeq)
        {
            lock (_lock)
            {
                var est = new SonidosEstadoDto { Seq = _seq, Mute = GetConfig().Mute };
                foreach (var kv in _episodios)
                {
                    if (!kv.Value.Activo) continue;
                    est.Activas.Add(new SonidoDisparoDto
                    {
                        Evento = kv.Key.Split('|')[0],
                        Detalle = kv.Value.Detalle ?? ""
                    });
                }
                foreach (var d in _historial)
                    if (d.Seq > desdeSeq) est.Disparos.Add(d);
                return est;
            }
        }

        // ---- detección --------------------------------------------------------

        private void Tick()
        {
            var cfg = GetConfig();
            var ahora = DateTime.UtcNow;
            AogStateSnapshot snap = null;
            try { snap = _state?.GetSnapshot(); } catch { }

            // Piloto: flanco, no sostenido — el episodio es el instante.
            if (snap != null)
            {
                bool piloto = snap.IsAutoSteerOn;
                if (_tienePilotoPrevio && piloto != _pilotoPrevio)
                    Disparar(cfg, piloto ? "piloto_on" : "piloto_off", "", ahora);
                _pilotoPrevio = piloto; _tienePilotoPrevio = true;
            }

            // Motores QuantiX: dosis y motor parado.
            try
            {
                var vivos = _nodos?.GetAll();
                if (vivos != null)
                {
                    foreach (var n in vivos)
                    {
                        if (n?.MotorsLive == null) continue;
                        foreach (var m in n.MotorsLive)
                        {
                            string suf = n.Uid + ":M" + m.Id;
                            bool trabajando = m.PpsTarget > 0.5;
                            double errPct = trabajando ? (m.PpsReal - m.PpsTarget) / m.PpsTarget * 100.0 : 0;

                            Evaluar(cfg, "dosis_baja", suf, trabajando && errPct < -UmbralDe(cfg, "dosis_baja"),
                                "M" + m.Id + ": " + errPct.ToString("F0") + "% por debajo", ahora);
                            Evaluar(cfg, "dosis_alta", suf, trabajando && errPct > UmbralDe(cfg, "dosis_alta"),
                                "M" + m.Id + ": +" + errPct.ToString("F0") + "% por encima", ahora);
                            Evaluar(cfg, "motor_parado", suf, trabajando && m.Rpm <= 0 && m.PpsReal < 0.5,
                                "M" + m.Id + " no gira con consigna activa", ahora);
                        }
                    }
                }
            }
            catch { }

            // VistaX: tubo y tolva.
            try
            {
                var vx = _vistax != null && _vistax.IsRunning ? _vistax.GetSnapshot() : null;
                if (vx != null && vx.MonitoreoActivo && vx.Trenes != null)
                {
                    foreach (var tren in vx.Trenes)
                    {
                        if (tren?.Surcos == null || tren.Surcos.Count == 0) continue;
                        int activos = 0, sinSemilla = 0;
                        foreach (var s in tren.Surcos)
                        {
                            if (s == null || s.Muted || s.SeccionCortada) continue;
                            if (s.Estado == "no-data") continue;   // sensor caído ≠ sin semilla
                            activos++;
                            bool sin = s.Spm < 1;
                            if (sin) sinSemilla++;
                            Evaluar(cfg, "tubo_sin_semilla", tren.Tren + ":" + s.Bajada, sin,
                                "bajada " + s.Bajada + " sin caída de semilla", ahora);
                        }
                        // Tolva: todos los surcos activos del tren en cero. Un solo
                        // tubo tapado no es la tolva; todos juntos, sí.
                        Evaluar(cfg, "tolva_sin_semilla", "tren" + tren.Tren,
                            activos >= 2 && sinSemilla == activos,
                            "tren " + (string.IsNullOrEmpty(tren.Nombre) ? tren.Tren.ToString() : tren.Nombre)
                              + ": ningún surco tira semilla", ahora);
                    }
                }
            }
            catch { }
        }

        private static double UmbralDe(SonidosConfigDto cfg, string id)
        {
            var e = cfg.Eventos.FirstOrDefault(x => x.Id == id);
            return e != null && e.UmbralPct > 0 ? e.UmbralPct : 10;
        }

        /// <summary>Condición sostenida → dispara; caída → cierra episodio.</summary>
        private void Evaluar(SonidosConfigDto cfg, string eventoId, string clave,
            bool condicion, string detalle, DateTime ahora)
        {
            var ev = cfg.Eventos.FirstOrDefault(x => x.Id == eventoId);
            if (ev == null) return;
            string k = eventoId + "|" + clave;

            lock (_lock)
            {
                Episodio ep;
                if (!_episodios.TryGetValue(k, out ep))
                {
                    ep = new Episodio();
                    _episodios[k] = ep;
                }

                if (!condicion)
                {
                    ep.Activo = false;
                    ep.CondicionDesde = DateTime.MinValue;
                    return;
                }

                if (ep.CondicionDesde == DateTime.MinValue) ep.CondicionDesde = ahora;
                ep.Detalle = detalle;

                double sostenida = (ahora - ep.CondicionDesde).TotalSeconds;
                if (!ep.Activo && sostenida >= Math.Max(ev.SostenidoSeg, 0))
                {
                    ep.Activo = true;
                    ep.UltimoDisparo = ahora;
                    Emitir(ev, detalle);
                }
                else if (ep.Activo && ev.RepetirSeg > 0 &&
                         (ahora - ep.UltimoDisparo).TotalSeconds >= ev.RepetirSeg)
                {
                    ep.UltimoDisparo = ahora;
                    Emitir(ev, detalle);
                }
            }
        }

        /// <summary>Disparo instantáneo (flancos, sin sostenido).</summary>
        private void Disparar(SonidosConfigDto cfg, string eventoId, string detalle, DateTime ahora)
        {
            var ev = cfg.Eventos.FirstOrDefault(x => x.Id == eventoId);
            if (ev == null) return;
            lock (_lock) Emitir(ev, detalle);
        }

        // Llamar con _lock tomado.
        private void Emitir(SonidoEventoDto ev, string detalle)
        {
            if (!ev.Habilitado) return;
            _seq++;
            _historial.Add(new SonidoDisparoDto
            {
                Seq = _seq,
                Evento = ev.Id,
                Sonido = ev.Sonido ?? "",
                Detalle = detalle ?? ""
            });
            while (_historial.Count > MaxHistorial) _historial.RemoveAt(0);
        }
    }
}
