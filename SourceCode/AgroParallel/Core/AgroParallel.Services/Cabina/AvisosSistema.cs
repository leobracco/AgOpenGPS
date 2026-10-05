// ============================================================================
// AvisosSistema.cs — cuando lo que falla no es el operario, es la maquina.
//
// Hermano de AvisosCabina. Ese explica por que PilotX no DEJA hacer algo (falta
// el lote, falta la guia). Este avisa que algo de abajo esta roto: la red, el
// puerto del GPS, los nodos que no aparecen.
//
// El problema que resuelve (Las Gringas, 2026-09-25, 45 MINUTOS de sembradora
// parada): un update reemplazo el .exe y Windows creo solo dos reglas de
// firewall que BLOQUEAN todo lo entrante. Los dos nodos QuantiX no pudieron
// conectarse al broker y PilotX les publico consigna a la nada 45 minutos. El
// GPS, que en esa pantalla entra por COM1, quedo tomado por un proceso zombi y
// el Engine se comia el "Access denied" en una linea de log.
//
// PilotX tenia TODO para saberlo —el broker arriba con cero clientes externos,
// dos nodos configurados y cero anunciados, el COM devolviendo acceso
// denegado— y en cabina se veia "no anda nada". Peor: el heartbeat al cloud es
// SALIENTE y Windows lo permite por defecto, asi que en OrbitX el equipo se
// veia sano mientras la maquina estaba parada.
//
// Regla de la casa, la misma de AvisosCabina: si algo no funciona, LO DICE Y
// DICE POR QUE. Y lo dice con el codigo AGP-* para que el operario lo pueda
// dictar por telefono.
//
// Todo lo de aca es funcion PURA sobre el estado: sin UI, sin HTTP, sin reloj,
// sin netsh. Quien junta el estado es AvisosCabinaService; aca solo se decide
// QUE decir, asi el texto que ve el operario se fija con tests en vez de
// comprobarse a mano arriba de una sembradora.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Cabina
{
    /// <summary>Que tan grave es. No hay mas niveles a proposito: en cabina, o
    /// frena el trabajo o no lo frena.</summary>
    public enum SeveridadAviso
    {
        /// <summary>Se puede seguir trabajando, pero hay que saberlo.</summary>
        Atencion = 0,
        /// <summary>Esto impide sembrar bien. Rojo.</summary>
        Critico = 1,
    }

    /// <summary>Que se sabe del firewall. `Desconocido` NO es `Ok`: en Linux, o
    /// si netsh no contesta, no se sabe — y sin saber no se grita.</summary>
    public enum FirewallEstado
    {
        Desconocido = 0,
        Ok = 1,
        Bloqueando = 2,
    }

    /// <summary>Un aviso listo para pintar. Inmutable: lo arma Evaluar.</summary>
    public sealed class AvisoSistema
    {
        /// <summary>Codigo AGP-* — lo que el operario dicta por telefono.</summary>
        public string Codigo { get; private set; }
        /// <summary>Una linea, lo que se lee de reojo manejando.</summary>
        public string Titulo { get; private set; }
        /// <summary>Que hacer. Nunca jerga tecnica: el operario es tractorista.</summary>
        public string Detalle { get; private set; }
        public SeveridadAviso Severidad { get; private set; }
        /// <summary>El cartel queda hasta que la condicion se resuelva (todos los
        /// de aca lo son: describen el AHORA, no un evento pasado).</summary>
        public bool Persistente { get; private set; }
        /// <summary>Evento de SonidosAlarmService, o null si no suena.</summary>
        public string EventoSonido { get; private set; }
        /// <summary>Accion que el operario puede tocar, o null. La resuelve el
        /// host (POST /api/avisos/accion).</summary>
        public string Accion { get; private set; }

        public AvisoSistema(string codigo, string titulo, string detalle,
                            SeveridadAviso severidad, string eventoSonido = null,
                            string accion = null)
        {
            Codigo = codigo;
            Titulo = titulo;
            Detalle = detalle;
            Severidad = severidad;
            Persistente = true;
            EventoSonido = eventoSonido;
            Accion = accion;
        }
    }

    /// <summary>Lo que hay que saber del sistema para poder explicar.</summary>
    public struct EstadoSistemaCabina
    {
        // ── Firewall ────────────────────────────────────────────────────────
        public FirewallEstado Firewall;

        /// <summary>El arreglo automatico esta disponible (esta el .bat del
        /// provisioning en la instalacion). Si no, no se ofrece un boton que no
        /// va a poder hacer nada.</summary>
        public bool FirewallSePuedeArreglar;

        // ── Nodos ───────────────────────────────────────────────────────────
        /// <summary>Nodos PERSISTIDOS y habilitados (no los mergeados por
        /// descubrimiento: esos son justamente los que no hay).</summary>
        public int NodosConfigurados;

        /// <summary>Nodos que se anunciaron ALGUNA vez desde que arranco la
        /// pantalla. Distinto de "online": uno que se cayo hace dos minutos ya
        /// lo cubre el banner de nodos offline; lo que este aviso detecta es que
        /// no aparecio NINGUNO, que es una falla de red o de corriente.</summary>
        public int NodosAnunciados;

        /// <summary>Segundos desde que arranco el motor. Ver GraciaNodosSeg.</summary>
        public double SegDesdeArranque;

        // ── GPS ─────────────────────────────────────────────────────────────
        /// <summary>Hay un puerto serie configurado para el GPS.</summary>
        public bool GpsPuertoConfigurado;
        /// <summary>Nombre del puerto ("COM1"), para que el detalle lo nombre.</summary>
        public string GpsPuertoNombre;
        public bool GpsPuertoAbierto;
        /// <summary>true si el puerto existe pero lo tiene otro proceso (acceso
        /// denegado). Distinto de "el puerto no existe", que es otro problema y
        /// otro mensaje.</summary>
        public bool GpsPuertoTomado;

        // ── Contexto ────────────────────────────────────────────────────────
        /// <summary>Hay lote abierto. Sube la severidad: parado en el galpon
        /// esto es informativo, a punto de sembrar es grave.</summary>
        public bool LoteAbierto;
    }

    public static class AvisosSistema
    {
        /// <summary>Cuanto se le da a los nodos antes de gritar. Un ESP32 tarda
        /// en bootear y engancharse al WiFi (el firmware QuantiX hasta la 3.0.1
        /// espera 15 s a proposito). Avisar a los 5 s seria ruido garantizado
        /// todas las mananas; un minuto no le cuesta nada a nadie y sigue siendo
        /// 44 minutos menos que lo de Las Gringas.</summary>
        public const double GraciaNodosSeg = 60.0;

        public const string CodFirewall = "AGP-NET-010";
        public const string CodNodos    = "AGP-MQTT-010";
        public const string CodGpsPuerto = "AGP-GPS-001";

        /// <summary>Accion del boton del aviso de firewall.</summary>
        public const string AccionArreglarFirewall = "arreglar_firewall";

        /// <summary>
        /// Que hay que decirle al operario ahora mismo, de lo mas grave a lo
        /// menos. Lista vacia = no hay nada roto.
        ///
        /// El ORDEN y la SUPRESION importan tanto como el texto: si el firewall
        /// esta bloqueando, el aviso de "no se conecto ningun equipo" es una
        /// CONSECUENCIA, no otra falla. Decir las dos cosas manda al operario a
        /// revisar cables que estan bien — el mismo criterio de un obstaculo a
        /// la vez que ya usa AvisosCabina.
        /// </summary>
        public static IReadOnlyList<AvisoSistema> Evaluar(EstadoSistemaCabina e)
        {
            var res = new List<AvisoSistema>();

            bool firewallBloquea = e.Firewall == FirewallEstado.Bloqueando;

            // 1) El firewall: es la causa raiz que tapa todo lo demas.
            if (firewallBloquea)
            {
                res.Add(new AvisoSistema(
                    CodFirewall,
                    "Windows está bloqueando la red de la máquina",
                    e.FirewallSePuedeArreglar
                        ? "Los equipos de la máquina no se van a poder conectar. " +
                          "Tocá Arreglar y aceptá el permiso que pide Windows."
                        : "Los equipos de la máquina no se van a poder conectar. " +
                          "Llamá a soporte y pasale este código: " + CodFirewall + ".",
                    SeveridadAviso.Critico,
                    eventoSonido: "firewall_bloquea",
                    accion: e.FirewallSePuedeArreglar ? AccionArreglarFirewall : null));
            }

            // 2) El puerto del GPS tomado por otro programa. Va ANTES que los
            //    nodos: sin GPS no se siembra, con nodos caidos se siembra mal.
            if (e.GpsPuertoConfigurado && !e.GpsPuertoAbierto && e.GpsPuertoTomado)
            {
                string puerto = string.IsNullOrEmpty(e.GpsPuertoNombre) ? "El puerto del GPS" : e.GpsPuertoNombre;
                res.Add(new AvisoSistema(
                    CodGpsPuerto,
                    "El GPS lo tiene agarrado otro programa",
                    puerto + " está ocupado. Cerrá el otro programa que usa el GPS, " +
                    "o reiniciá la pantalla.",
                    SeveridadAviso.Critico,
                    eventoSonido: "gps_puerto_ocupado"));
            }

            // 3) Nodos configurados y ninguno se anuncio.
            if (!firewallBloquea
                && e.NodosConfigurados > 0
                && e.NodosAnunciados <= 0
                && e.SegDesdeArranque >= GraciaNodosSeg)
            {
                res.Add(new AvisoSistema(
                    CodNodos,
                    "Ningún equipo de la máquina se conectó",
                    "Tenés " + e.NodosConfigurados + (e.NodosConfigurados == 1 ? " equipo" : " equipos") +
                    " cargados y no apareció ninguno. Revisá que tengan corriente y " +
                    "que el router de la máquina esté encendido.",
                    e.LoteAbierto ? SeveridadAviso.Critico : SeveridadAviso.Atencion,
                    eventoSonido: "nodos_sin_anunciar"));
            }

            return res;
        }

        /// <summary>El aviso mas grave, o null. Para el cartel unico de la barra:
        /// arriba del tractor se lee uno, no una lista.</summary>
        public static AvisoSistema MasGrave(EstadoSistemaCabina e)
        {
            var todos = Evaluar(e);
            return todos.Count > 0 ? todos[0] : null;
        }
    }
}
