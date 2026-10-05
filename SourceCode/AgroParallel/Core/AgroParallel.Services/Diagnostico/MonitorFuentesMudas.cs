// ============================================================================
// MonitorFuentesMudas.cs — avisa UNA vez cuando una fuente de datos se calla y
// UNA vez cuando vuelve, con la duración del corte.
//
// Por qué existe:
//   "Se cortó el piloto" no alcanza para diagnosticar. ¿Se calló el GPS, el
//   módulo de dirección, la red entera? Hasta ahora el registro de eventos no
//   lo decía: los cortes de un par de segundos no dejaban rastro y al otro día
//   nadie podía saber qué pasó en la cabecera. Con esto, el registro queda:
//
//     12:03:12-> Fuente muda: GPS (NMEA) — sin datos hace 3.1 s
//     12:03:49-> Fuente volvió: GPS (NMEA) — estuvo muda 37 s
//
//   y el resumen entra en el reporte de falla (diagnostico/fuentes.txt).
//
// Reglas:
//   · Clase PURA: no lee relojes ni toca disco. El que la alimenta le pasa el
//     "último visto" de cada fuente y la hora actual; así se testea sin dormir.
//   · Una fuente que NUNCA habló no se reporta: un equipo sin módulo de
//     máquina no tiene que llenar el registro de "máquina muda".
//   · Un aviso por transición, nunca uno por tick.
//
// Idea tomada de AgOpenWeb (Shared/AgOpenWeb.Services/SourceSilenceMonitor.cs,
// © AgOpenWeb Contributors, GPL-3.0/Apache-2.0 según el archivo). Esta es una
// reescritura propia: aquí el monitor no marca paquetes, evalúa "último visto"
// por fuente (PilotX ya guarda esas marcas en CoreX y en el motor), soporta
// fuentes por estado booleano (nodos MQTT con LWT) y lleva cortes y tiempo mudo
// acumulado para el reporte.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AgroParallel.Diagnostico
{
    /// <summary>Una transición: la fuente se calló o volvió.</summary>
    public sealed class EventoFuente
    {
        public string Clave { get; set; }
        public string Nombre { get; set; }
        /// <summary>false = se calló; true = volvió.</summary>
        public bool Volvio { get; set; }
        /// <summary>Cuándo se detectó la transición.</summary>
        public DateTime Momento { get; set; }
        /// <summary>Último momento en que la fuente estaba viva antes del corte.</summary>
        public DateTime Desde { get; set; }
        /// <summary>Duración del corte (solo cuando volvió).</summary>
        public TimeSpan? Duracion { get; set; }
        /// <summary>Texto listo para el registro de eventos.</summary>
        public string Mensaje { get; set; }
    }

    /// <summary>Foto del estado de una fuente.</summary>
    public sealed class EstadoFuente
    {
        public string Clave { get; set; }
        public string Nombre { get; set; }
        public bool Vista { get; set; }
        public bool Muda { get; set; }
        public DateTime? UltimoVisto { get; set; }
        public DateTime? MudaDesde { get; set; }
        public int Cortes { get; set; }
        public TimeSpan TiempoMudo { get; set; }
    }

    public sealed class MonitorFuentesMudas
    {
        private sealed class Fuente
        {
            public string Clave;
            public string Nombre;
            public TimeSpan Umbral;
            public bool Vista;
            public bool Muda;
            public DateTime UltimoVisto;
            public DateTime Desde;
            public int Cortes;
            public TimeSpan TiempoMudo;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Fuente> _fuentes = new Dictionary<string, Fuente>(StringComparer.Ordinal);
        private readonly List<string> _orden = new List<string>();
        private readonly LinkedList<EventoFuente> _eventos = new LinkedList<EventoFuente>();
        private readonly int _maxEventos;

        public MonitorFuentesMudas(int maxEventos = 100)
        {
            _maxEventos = Math.Max(1, maxEventos);
        }

        /// <summary>Da de alta (o renombra / cambia el umbral de) una fuente con
        /// marca de tiempo: muda = más de <paramref name="umbral"/> sin datos.</summary>
        public void Registrar(string clave, string nombre, TimeSpan umbral)
        {
            if (string.IsNullOrEmpty(clave)) throw new ArgumentException("clave vacía", nameof(clave));
            lock (_lock)
            {
                Fuente f = Obtener(clave, nombre);
                f.Umbral = umbral;
            }
        }

        /// <summary>
        /// Evalúa una fuente registrada a partir de su "último visto".
        /// null = nunca se la vio. Devuelve el evento si hubo transición.
        /// Clave no registrada: se ignora (null).
        /// </summary>
        public EventoFuente Observar(string clave, DateTime? ultimoVisto, DateTime ahora)
        {
            lock (_lock)
            {
                Fuente f;
                if (clave == null || !_fuentes.TryGetValue(clave, out f)) return null;
                bool vivo = ultimoVisto.HasValue && (ahora - ultimoVisto.Value) <= f.Umbral;
                return Evaluar(f, vivo, ultimoVisto, ahora);
            }
        }

        /// <summary>
        /// Evalúa una fuente por estado (vivo/no vivo), dándola de alta si hace
        /// falta. Es para las que ya traen su propio veredicto, como los nodos
        /// MQTT (Online lo decide el LWT del broker o el barrido de 15 s).
        /// </summary>
        public EventoFuente ObservarEstado(string clave, string nombre, bool vivo, DateTime? ultimoVisto, DateTime ahora)
        {
            if (string.IsNullOrEmpty(clave)) return null;
            lock (_lock)
            {
                Fuente f = Obtener(clave, nombre);
                return Evaluar(f, vivo, ultimoVisto, ahora);
            }
        }

        private Fuente Obtener(string clave, string nombre)
        {
            Fuente f;
            if (!_fuentes.TryGetValue(clave, out f))
            {
                f = new Fuente { Clave = clave };
                _fuentes[clave] = f;
                _orden.Add(clave);
            }
            if (!string.IsNullOrEmpty(nombre)) f.Nombre = nombre;
            if (string.IsNullOrEmpty(f.Nombre)) f.Nombre = clave;
            return f;
        }

        private EventoFuente Evaluar(Fuente f, bool vivo, DateTime? ultimoVisto, DateTime ahora)
        {
            if (!f.Vista)
            {
                if (!vivo) return null;             // nunca habló: no hay nada que reportar
                f.Vista = true;
                f.UltimoVisto = ultimoVisto ?? ahora;
                return null;
            }

            if (vivo)
            {
                DateTime visto = ultimoVisto ?? ahora;
                if (visto > f.UltimoVisto) f.UltimoVisto = visto;
                if (!f.Muda) return null;

                f.Muda = false;
                TimeSpan dur = f.UltimoVisto - f.Desde;
                if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
                f.TiempoMudo += dur;
                return Agregar(new EventoFuente
                {
                    Clave = f.Clave,
                    Nombre = f.Nombre,
                    Volvio = true,
                    Momento = ahora,
                    Desde = f.Desde,
                    Duracion = dur,
                    Mensaje = "Fuente volvió: " + f.Nombre + " — estuvo muda " + FormatearDuracion(dur),
                });
            }

            if (f.Muda) return null;                // ya avisado

            f.Muda = true;
            f.Cortes++;
            // Desde: lo último que se sabe vivo. Con marca de tiempo es ese
            // último visto; sin ella, lo último que registramos nosotros.
            f.Desde = f.UltimoVisto;
            TimeSpan hace = ahora - f.Desde;
            if (hace < TimeSpan.Zero) hace = TimeSpan.Zero;
            return Agregar(new EventoFuente
            {
                Clave = f.Clave,
                Nombre = f.Nombre,
                Volvio = false,
                Momento = ahora,
                Desde = f.Desde,
                Mensaje = "Fuente muda: " + f.Nombre + " — sin datos hace " + FormatearDuracion(hace),
            });
        }

        private EventoFuente Agregar(EventoFuente ev)
        {
            _eventos.AddLast(ev);
            while (_eventos.Count > _maxEventos) _eventos.RemoveFirst();
            return ev;
        }

        /// <summary>Últimos eventos (los más viejos primero), acotados.</summary>
        public IReadOnlyList<EventoFuente> EventosRecientes()
        {
            lock (_lock) return _eventos.ToList();
        }

        /// <summary>Foto de todas las fuentes, en orden de alta.</summary>
        public IReadOnlyList<EstadoFuente> Estado()
        {
            lock (_lock)
            {
                return _orden.Select(k =>
                {
                    var f = _fuentes[k];
                    return new EstadoFuente
                    {
                        Clave = f.Clave,
                        Nombre = f.Nombre,
                        Vista = f.Vista,
                        Muda = f.Muda,
                        UltimoVisto = f.Vista ? (DateTime?)f.UltimoVisto : null,
                        MudaDesde = f.Muda ? (DateTime?)f.Desde : null,
                        Cortes = f.Cortes,
                        TiempoMudo = f.TiempoMudo,
                    };
                }).ToList();
            }
        }

        /// <summary>Texto para el reporte de falla: estado por fuente + eventos.</summary>
        public string Resumen(DateTime ahora)
        {
            var sb = new StringBuilder();
            sb.AppendLine("FUENTES DE DATOS (monitor de fuente muda)");
            sb.AppendLine("=========================================");
            foreach (var e in Estado())
            {
                string estado;
                if (!e.Vista) estado = "nunca habló desde que arrancó el motor";
                else if (e.Muda) estado = "MUDA hace " + FormatearDuracion(ahora - e.MudaDesde.Value);
                else estado = "viva (último dato hace " + FormatearDuracion(ahora - e.UltimoVisto.Value) + ")";
                sb.Append("· ").Append(e.Nombre).Append(": ").Append(estado);
                if (e.Cortes > 0)
                    sb.Append(" — ").Append(e.Cortes.ToString(CultureInfo.InvariantCulture))
                      .Append(e.Cortes == 1 ? " corte" : " cortes")
                      .Append(", ").Append(FormatearDuracion(e.TiempoMudo)).Append(" muda en total");
                sb.AppendLine();
            }

            var evs = EventosRecientes();
            sb.AppendLine();
            sb.AppendLine("ÚLTIMOS CORTES");
            sb.AppendLine("--------------");
            if (evs.Count == 0) sb.AppendLine("(ninguno)");
            foreach (var ev in evs)
                sb.Append(Hora(ev.Momento)).Append("  ").AppendLine(ev.Mensaje);
            return sb.ToString();
        }

        private static string Hora(DateTime t)
        {
            DateTime local = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
            return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>"2.3 s", "37 s", "2 min 05 s", "1 h 02 min". Invariante a
        /// propósito (el Desktop corre en InvariantGlobalization).</summary>
        public static string FormatearDuracion(TimeSpan d)
        {
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            double s = d.TotalSeconds;
            if (s < 10) return s.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            if (s < 60) return ((int)Math.Round(s, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " s";
            long total = (long)Math.Floor(s);
            if (total < 3600)
                return (total / 60).ToString(CultureInfo.InvariantCulture) + " min "
                     + (total % 60).ToString("00", CultureInfo.InvariantCulture) + " s";
            return (total / 3600).ToString(CultureInfo.InvariantCulture) + " h "
                 + ((total % 3600) / 60).ToString("00", CultureInfo.InvariantCulture) + " min";
        }
    }
}
