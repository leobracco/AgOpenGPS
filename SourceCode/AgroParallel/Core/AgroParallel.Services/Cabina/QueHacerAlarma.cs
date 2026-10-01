// ============================================================================
// QueHacerAlarma.cs — a cada alarma conocida, una linea de "Que hacer".
//
// Idea del T-Wave de Sensor: la alarma no solo dice QUE pasa, dice que hacer.
// Arriba del tractor "Surco 4 tapado" o "Implemento offline" sin mas obliga al
// operario a adivinar — o a llamar. Una frase corta y accionable ("Revisá el
// cable de la antena", "Cargá semilla en la tolva") resuelve la mayoria sin
// telefono.
//
// Reglas:
//  · Solo alarmas que conocemos. Codigo sin entrada → null → la pantalla no
//    muestra nada. Un consejo inventado manda a tocar donde no es: peor que
//    callarse. Por eso AGP-*-009 (no clasificadas) no tienen entrada.
//  · Criollo, corto (una o dos frases, se lee de reojo), sin jerga: nada de
//    "broker", "MQTT" ni nombres de excepcion.
//  · Rutas de menu SOLO si estan verificadas en la UI real; si no, se dice
//    QUE hacer sin decir donde.
//
// Funcion PURA, solo System: sin UI, sin HTTP, sin reloj. La enlaza tambien
// PilotX.UI (mismo criterio que AvisosCabina) y el texto se fija con tests.
// ============================================================================

// Enlazado en PilotX.UI (net9, Nullable enable): ahi "null = sin entrada" es
// el contrato, no un descuido. En netstandard2.0 (C# 7.3) NET no esta
// definido y la directiva ni se lee.
#if NET
#nullable disable
#endif

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AgroParallel.Cabina
{
    public static class QueHacerAlarma
    {
        // ── Claves de alarmas de cabina que no traen codigo AGP-* ───────────

        /// <summary>Nodo del implemento activo que se cayo (banner de cabina).</summary>
        public const string NodoOffline = "nodo-offline";

        /// <summary>Prefijo de los estados de surco VistaX del banner.</summary>
        public const string PrefijoSurco = "vistax-";

        /// <summary>Desenganches automaticos del piloto (VigiaDesacople y RTK).</summary>
        public const string PilotoSinGps = "piloto-sin-gps";
        public const string PilotoLejosDeLaGuia = "piloto-lejos-guia";
        public const string PilotoSinRtk = "piloto-sin-rtk";

        private static readonly Dictionary<string, string> _queHacer =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Cabina: nodos y siembra ─────────────────────────────────────
            { NodoOffline,
              "Revisá la corriente y el cable del nodo. Cuando vuelve, la alarma se va sola." },
            { PrefijoSurco + "tapado",
              "Pará y revisá el tubo de bajada de ese surco." },
            { PrefijoSurco + "bajo",
              "Revisá que haya semilla y que el dosificador de ese surco gire bien." },
            { PrefijoSurco + "no-data",
              "Revisá el cable y la ficha del sensor de ese surco." },
            { PrefijoSurco + "alerta",
              "Cargá semilla en la tolva." },
            { PrefijoSurco + "exceso",
              "Revisá la dosis cargada y la calibración del dosificador." },

            // ── Piloto que se solto solo ────────────────────────────────────
            { PilotoSinGps,
              "Revisá el cable de la antena. Tocá Piloto de nuevo cuando vuelva la señal." },
            { PilotoLejosDeLaGuia,
              "Llevá el tractor a mano cerca de la guía y tocá Piloto de nuevo." },
            { PilotoSinRtk,
              "Esperá a que el GPS vuelva a verde (RTK) y tocá Piloto de nuevo." },

            // ── Red de la maquina (nodos) ───────────────────────────────────
            { "AGP-MQTT-001",
              "Cerrá y volvé a abrir PilotX: la conexión con los nodos arranca con él." },
            { "AGP-MQTT-002",
              "Esperá unos segundos. Si sigue, cerrá y volvé a abrir PilotX." },
            // MQTT-003/004 (direccion/credenciales) quedan SIN entrada a
            // proposito: dependen de una config que el operario no toca.
            { "AGP-MQTT-005",
              "Se reconecta sola. Si se repite seguido, cerrá y volvé a abrir PilotX." },
            { "AGP-MQTT-010",
              "Revisá que los equipos tengan corriente y que el router de la máquina esté prendido." },

            // ── Red / CoreX ─────────────────────────────────────────────────
            { "AGP-NET-000",
              "Cerrá y volvé a abrir PilotX." },
            { "AGP-NET-002",
              "Revisá que el CoreX-ECU esté prendido y que su IP esté bien cargada." },
            { "AGP-NET-010",
              "Tocá Arreglar en el aviso y aceptá el permiso que pide Windows." },
            { "AGP-NET-100",
              "Activá la comunicación con el CoreX-ECU en su configuración." },
            { "AGP-NET-409",
              "Soltá el piloto y probá de nuevo." },

            // ── GPS ─────────────────────────────────────────────────────────
            { "AGP-GPS-001",
              "Cerrá el otro programa que usa el GPS, o reiniciá la pantalla." },

            // ── Sistema ─────────────────────────────────────────────────────
            { "AGP-SYS-004",
              "Cerrá el programa que tenga abierto el archivo y probá de nuevo." },
            { "AGP-SYS-005",
              "Revisá que quede espacio libre en el disco de la pantalla." },
            { "AGP-SYS-007",
              "Cerrá el lote y volvé a abrirlo." },

            // ── USB (flasheo de nodos) ──────────────────────────────────────
            { "AGP-USB-001",
              "Cerrá el otro programa que usa ese puerto y probá de nuevo." },
            { "AGP-USB-002",
              "Mantené apretado BOOT en el nodo y reintentá. Si no, cambiá el cable." },
            { "AGP-USB-003",
              "Reintentá. Si sigue, probá con otro cable u otro puerto USB." },
            { "AGP-USB-006",
              "Reintentá y aceptá el permiso de administrador que pide Windows." },
            { "AGP-USB-007",
              "Esperá a que termine el flasheo que está en curso." },
        };

        private static readonly Regex _codigoAgp =
            new Regex(@"AGP-[A-Z]+-\d{3}", RegexOptions.CultureInvariant);

        /// <summary>Todos los codigos con entrada (para tests y diagnostico).</summary>
        public static IEnumerable<string> Codigos { get { return _queHacer.Keys; } }

        /// <summary>Que hacer ante esta alarma, o null si no hay entrada (y
        /// entonces no se muestra nada).</summary>
        public static string Para(string codigo)
        {
            if (string.IsNullOrWhiteSpace(codigo)) return null;
            string s;
            return _queHacer.TryGetValue(codigo.Trim(), out s) ? s : null;
        }

        /// <summary>Que hacer para un surco VistaX en ese estado ("tapado",
        /// "bajo", "no-data", "alerta", "exceso"). null si no es falla.</summary>
        public static string ParaSurco(string estado)
        {
            if (string.IsNullOrWhiteSpace(estado)) return null;
            return Para(PrefijoSurco + estado.Trim());
        }

        /// <summary>
        /// Clave del desenganche del piloto a partir del texto del aviso (el que
        /// arman VigiaDesacople y el corte por RTK), o null. El motor no manda
        /// codigo con el aviso: se reconoce por el comienzo, que es fijo.
        /// </summary>
        public static string CodigoDePiloto(string aviso)
        {
            if (string.IsNullOrEmpty(aviso)) return null;
            int i = aviso.IndexOf("Piloto desenganchado:", StringComparison.Ordinal);
            if (i < 0) return null;
            string resto = aviso.Substring(i);
            if (resto.IndexOf("sin señal de GPS", StringComparison.Ordinal) >= 0) return PilotoSinGps;
            if (resto.IndexOf("de la guía", StringComparison.Ordinal) >= 0) return PilotoLejosDeLaGuia;
            if (resto.IndexOf("RTK", StringComparison.Ordinal) >= 0) return PilotoSinRtk;
            return null;
        }

        /// <summary>
        /// Que hacer para una linea del registro de eventos: primero un codigo
        /// AGP-* adentro de la linea, despues un desenganche del piloto. null
        /// si la linea no es una alarma conocida.
        /// </summary>
        public static string EnLinea(string linea)
        {
            if (string.IsNullOrEmpty(linea)) return null;
            var m = _codigoAgp.Match(linea);
            if (m.Success) return Para(m.Value);
            string piloto = CodigoDePiloto(linea);
            return piloto != null ? Para(piloto) : null;
        }

        /// <summary>
        /// El registro de eventos con un renglon "→ Que hacer: ..." debajo de
        /// cada linea que es una alarma conocida. Las demas lineas quedan
        /// igual. Lineas separadas por '\n' (el visor ya normaliza \r).
        /// <paramref name="traducir"/> pasa por el Traductor de la pantalla;
        /// null = castellano.
        /// </summary>
        public static string AnotarLog(string texto, Func<string, string> traducir = null)
        {
            if (string.IsNullOrEmpty(texto)) return "";
            Func<string, string> t = traducir ?? (x => x);

            var lineas = texto.Split('\n');
            var sb = new StringBuilder(texto.Length + 64);
            for (int i = 0; i < lineas.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(lineas[i]);
                string qh = EnLinea(lineas[i]);
                if (qh != null)
                    sb.Append('\n').Append("    → ").Append(t("Qué hacer")).Append(": ").Append(t(qh));
            }
            return sb.ToString();
        }
    }
}
