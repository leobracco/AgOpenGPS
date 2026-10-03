// ============================================================================
// ClasificadorComandos.cs — qué comandos/endpoints MUEVEN la máquina.
//
// Regla: ACCIONAMIENTO = cualquier escritura que
//   a) mueve un actuador (volante, motores, válvulas, relés, levante),
//   b) cambia qué/cuánto se aplica o por dónde guía en vivo (secciones, dosis,
//      guía activa, posición/deriva, lote abierto, geometría), o
//   c) reinicia/flashea/apaga algo de lo que depende el trabajo.
// LECTURA = lecturas y escrituras que no tocan la máquina (pantalla, idioma,
// chat, teclado, preferencias, registros, backups).
// PARADA = soltar/frenar: pasa siempre, venga de quien venga.
//
// FAIL-CLOSED: un endpoint de escritura o un comando de guiado que NO está en
// la lista de neutros es ACCIONAMIENTO. Un endpoint nuevo queda protegido
// sin que nadie se acuerde de agregarlo acá; si es neutro, se lo suma a la
// lista (y al test).
//
// Vocabulario de guiado: GuidanceEngineHost.ExecuteCommand
// (GuidanceEngineHost.Commands.cs + TryComandoDeriva en .Deriva.cs).
// ============================================================================

using System;

namespace AgroParallel.Services.Control
{
    public static class ClasificadorComandos
    {
        // Comandos de guiado que sólo cambian lo que se DIBUJA.
        private static readonly string[] GuiadoNeutros =
        {
            "modo_banderillero", // lectura del desvío: luces
            "modo_piloto",       // lectura del desvío: número
            "tram_vista",        // qué tramlines se ven
        };

        // Escrituras neutras: prefijo de ruta relativa a /api (sin el /api).
        private static readonly string[] EndpointsNeutros =
        {
            "/control/",               // el propio protocolo de control
            "/chat/",
            "/teclado/",
            "/idioma",
            "/ventana",
            "/overlays",
            "/debug/",
            "/sonidos/",
            "/setup/",
            "/aog/botonera",           // layout de botones guardado
            "/orbitx/",                // vinculación con la nube
            "/pilotx/update/check",
            "/pilotx/update/download", // baja el ZIP; aplicar es aparte
            "/firmwares/",             // cache de .bin (flashear es /nodos/{uid}/ota o /usb/flash)
            "/config/backup",
            "/flags/",                 // banderas en el mapa
            "/tareas/",                // registro de la tarea
            "/nodos/renombrar",
            "/sistema/brillo",
            "/camaras/config",
            "/vistax/overlay",
            "/stormx/config",          // estación meteo: sin actuadores
            "/quantix/pid-marca",      // marca en el registro PID
        };

        // Paradas: frenar nunca depende de quién tiene el control.
        private static readonly string[] EndpointsParada =
        {
            "POST /corex-ecu/motor/stop",
            "DELETE /corex-ecu/calibration/pwm-sweep",
            "POST /widget-quantix/apagar",
        };

        // Mueven el VOLANTE: si el remoto que los usó se cae, se suelta la dirección.
        private static readonly string[] EndpointsDireccion =
        {
            "/steer/freedrive",        // manejo libre (+ /angle, /zero)
            "/steer/cal/",             // asistente de calibración de la dirección
            "/corex-ecu/motor/test",
            "/corex-ecu/calibration/pwm-sweep",
        };

        public const string RutaComandoGuiado = "/aog/guidance/command";

        /// <summary>Nivel de un comando de guiado (POST /api/aog/guidance/command,
        /// MQTT agp/aog/guidance/command). <paramref name="pilotoEnganchado"/>:
        /// "autosteer" con el piloto puesto es DESENGANCHAR → Parada.</summary>
        public static NivelComando ClasificarComandoGuiado(string comando, bool pilotoEnganchado)
        {
            string cmd = Normalizar(comando);
            if (cmd.Length == 0) return NivelComando.Lectura; // vacío: no ejecuta nada
            foreach (var n in GuiadoNeutros)
                if (cmd == n) return NivelComando.Lectura;
            if (cmd == "autosteer" && pilotoEnganchado) return NivelComando.Parada;
            return NivelComando.Accionamiento;
        }

        /// <summary>El comando engancha la dirección (hoy: "autosteer" con el piloto suelto).</summary>
        public static bool EsComandoDeDireccion(string comando, bool pilotoEnganchado) =>
            Normalizar(comando) == "autosteer" && !pilotoEnganchado;

        /// <summary>Nivel de un request HTTP. <paramref name="ruta"/> es relativa a
        /// /api (ej. "/steer/freedrive"); también acepta con el prefijo /api.</summary>
        public static NivelComando ClasificarEndpoint(string metodo, string ruta)
        {
            string m = (metodo ?? "").Trim().ToUpperInvariant();
            if (m == "GET" || m == "HEAD" || m == "OPTIONS" || m.Length == 0) return NivelComando.Lectura;

            string r = NormalizarRuta(ruta);
            if (string.Equals(r, RutaComandoGuiado, StringComparison.OrdinalIgnoreCase)) return NivelComando.PorComando;

            foreach (var p in EndpointsParada)
                if (string.Equals(m + " " + r, p, StringComparison.OrdinalIgnoreCase)) return NivelComando.Parada;

            foreach (var p in EndpointsNeutros)
                if (EmpiezaCon(r, p)) return NivelComando.Lectura;

            return NivelComando.Accionamiento;
        }

        /// <summary>El endpoint mueve el volante (manejo libre, asistente, prueba de motor).</summary>
        public static bool EsEndpointDeDireccion(string metodo, string ruta)
        {
            string m = (metodo ?? "").Trim().ToUpperInvariant();
            if (m != "POST" && m != "PUT") return false;
            string r = NormalizarRuta(ruta);
            foreach (var p in EndpointsDireccion)
                if (EmpiezaCon(r, p)) return true;
            return false;
        }

        private static bool EmpiezaCon(string ruta, string prefijo)
        {
            if (!ruta.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)) return false;
            // "/idioma" no tiene que cubrir "/idiomas-raros": el prefijo sin barra
            // final sólo matchea la ruta exacta o un sub-path.
            if (prefijo.EndsWith("/")) return true;
            return ruta.Length == prefijo.Length || ruta[prefijo.Length] == '/';
        }

        private static string Normalizar(string comando) =>
            (comando ?? "").Trim().ToLowerInvariant();

        private static string NormalizarRuta(string ruta)
        {
            string r = (ruta ?? "").Trim();
            int q = r.IndexOf('?');
            if (q >= 0) r = r.Substring(0, q);
            if (!r.StartsWith("/")) r = "/" + r;
            if (r.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) r = r.Substring(4);
            if (r.Length > 1 && r.EndsWith("/")) r = r.TrimEnd('/');
            return r;
        }
    }
}
