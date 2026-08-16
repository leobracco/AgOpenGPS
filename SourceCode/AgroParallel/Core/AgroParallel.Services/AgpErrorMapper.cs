// ============================================================================
// AgpErrorMapper.cs
// Mapea excepciones internas a (codigo, mensaje amigable) para que el operario
// no vea stack traces ni nombres de tipo .NET en la UI, y para que un bot de
// soporte (WhatsApp/llamada) pueda interpretar el código sin ambigüedad.
//
// Códigos:
//   AGP-MQTT-001   Broker no responde (LAN o servicio caído)
//   AGP-MQTT-002   Timeout conectando al broker
//   AGP-MQTT-003   Dirección de broker inválida / no se pudo resolver
//   AGP-MQTT-004   Credenciales del broker rechazadas
//   AGP-MQTT-005   Conexión cerrada por el broker
//   AGP-MQTT-006   Protocolo MQTT inesperado (versión / handshake)
//   AGP-MQTT-009   Falla MQTT no clasificada (queda el detalle técnico)
//   AGP-NET-001    Sin red (interfaz caída)
//   AGP-NET-009    Falla de red no clasificada
//   AGP-SYS-009    Excepción no clasificada
//
// IMPORTANTE: el detalle técnico (exception type + message) se conserva como
// campo aparte para soporte/log; nunca se pinta en la UI principal.
// ============================================================================

using System;

namespace AgroParallel.Services
{
    public sealed class AgpError
    {
        public string Code { get; set; }
        public string Friendly { get; set; }
        public string Technical { get; set; }

        public AgpError(string code, string friendly, string technical)
        {
            Code = code;
            Friendly = friendly;
            Technical = technical;
        }
    }

    public static class AgpErrorMapper
    {
        /// <summary>
        /// Mapea una excepción genérica a un AgpError con código humano y
        /// mensaje en español. La pista que más diferencia los casos del
        /// broker MQTT es la sub-excepción de socket: revisamos InnerException.
        /// </summary>
        public static AgpError FromException(Exception ex)
        {
            if (ex == null) return new AgpError("AGP-SYS-009", "Error desconocido.", "(null)");

            string technical = ex.GetType().Name + ": " + (ex.Message ?? "");
            // Buscar la causa-raíz más informativa.
            Exception root = ex;
            while (root.InnerException != null) root = root.InnerException;
            string rootTypeName = root.GetType().Name;
            string rootMsg = root.Message ?? "";
            string fullText = (ex.Message ?? "") + " || " + rootMsg;

            // ---- MQTT ----------------------------------------------------------
            string exTypeName = ex.GetType().Name;
            bool isMqttException =
                exTypeName.IndexOf("Mqtt", StringComparison.OrdinalIgnoreCase) >= 0
                || exTypeName.IndexOf("MQTTnet", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isMqttException || rootTypeName == "SocketException")
            {
                // Por SocketErrorCode si es Sockets.
                int? socketCode = TryGetSocketErrorCode(root);
                if (socketCode.HasValue)
                {
                    switch (socketCode.Value)
                    {
                        // ConnectionRefused / HostUnreachable / NetworkUnreachable
                        case 10061: case 10065: case 10051:
                            return new AgpError("AGP-MQTT-001",
                                "No se pudo contactar al broker MQTT. Verificá que CoreX esté abierto en la PC del tractor.",
                                technical);
                        // TimedOut
                        case 10060:
                            return new AgpError("AGP-MQTT-002",
                                "El broker MQTT no respondió a tiempo. Probá reconectar; si persiste, reiniciá CoreX.",
                                technical);
                        // HostNotFound / NoData
                        case 11001: case 11004:
                            return new AgpError("AGP-MQTT-003",
                                "La dirección del broker es inválida o no se pudo resolver el nombre.",
                                technical);
                    }
                }
                // Fallback por texto.
                string lo = fullText.ToLowerInvariant();
                if (lo.Contains("refused") || lo.Contains("no se puede establecer una conexión"))
                {
                    return new AgpError("AGP-MQTT-001",
                        "No se pudo contactar al broker MQTT. Verificá que CoreX esté abierto en la PC del tractor.",
                        technical);
                }
                if (lo.Contains("timed out") || lo.Contains("timeout") || lo.Contains("se ha agotado"))
                {
                    return new AgpError("AGP-MQTT-002",
                        "El broker MQTT no respondió a tiempo. Probá reconectar; si persiste, reiniciá CoreX.",
                        technical);
                }
                if (lo.Contains("unspecified") || lo.Contains("no such host") || lo.Contains("hostnotfound"))
                {
                    return new AgpError("AGP-MQTT-003",
                        "La dirección del broker es inválida o no se pudo resolver el nombre.",
                        technical);
                }
                if (lo.Contains("auth") || lo.Contains("not authorized") || lo.Contains("bad user") || lo.Contains("password"))
                {
                    return new AgpError("AGP-MQTT-004",
                        "El broker rechazó las credenciales del nodo. Revisá usuario/contraseña del broker.",
                        technical);
                }
                if (lo.Contains("disconnect") || lo.Contains("connection closed") || lo.Contains("conexión") && lo.Contains("cerrada"))
                {
                    return new AgpError("AGP-MQTT-005",
                        "La conexión con el broker se cortó. Reintentando…",
                        technical);
                }
                if (lo.Contains("protocol") || lo.Contains("handshake"))
                {
                    return new AgpError("AGP-MQTT-006",
                        "El broker no respondió un MQTT válido (versión incompatible).",
                        technical);
                }
                return new AgpError("AGP-MQTT-009",
                    "Falla del broker MQTT. Reintentando.",
                    technical);
            }

            // ---- Red / Sockets fuera de MQTT -----------------------------------
            if (rootTypeName == "SocketException")
            {
                return new AgpError("AGP-NET-009", "Falla de red.", technical);
            }
            if (exTypeName.Contains("Http") || rootTypeName.Contains("Http"))
            {
                return new AgpError("AGP-NET-001", "Falla de red al contactar el servicio.", technical);
            }

            // ---- Fallas del sistema -------------------------------------------
            //
            // Estas caían todas en AGP-SYS-009, o sea que el operario reportaba
            // "salió un error" y no había forma de saber por dónde arrancar.
            // Separar los tipos más comunes de .NET le da a soporte un punto de
            // partida sin tener que pedirle el detalle técnico al operario.
            switch (rootTypeName)
            {
                case "IndexOutOfRangeException":
                case "ArgumentOutOfRangeException":
                    return new AgpError("AGP-SYS-001",
                        "Un valor quedó fuera de rango. Suele ser una configuración con un número que el equipo no esperaba (ancho, secciones, offsets).",
                        technical);

                case "NullReferenceException":
                    return new AgpError("AGP-SYS-002",
                        "Faltaba un dato que el programa daba por hecho. Suele pasar con un lote o un perfil a medio cargar.",
                        technical);

                case "FileNotFoundException":
                case "DirectoryNotFoundException":
                    return new AgpError("AGP-SYS-003",
                        "No se encontró un archivo o carpeta que hacía falta. Revisá que el lote y el perfil de vehículo existan.",
                        technical);

                case "UnauthorizedAccessException":
                    return new AgpError("AGP-SYS-004",
                        "Windows no dejó leer o escribir un archivo. Puede ser permisos, o el archivo abierto en otro programa.",
                        technical);

                case "IOException":
                    return new AgpError("AGP-SYS-005",
                        "Falló una lectura o escritura en disco. Si es en la pantalla de la cabina, revisá el espacio libre.",
                        technical);

                case "FormatException":
                case "OverflowException":
                    return new AgpError("AGP-SYS-006",
                        "Un número vino con un formato que el equipo no pudo interpretar. Suele ser una config cargada a mano.",
                        technical);

                case "InvalidOperationException":
                    return new AgpError("AGP-SYS-007",
                        "Se intentó una acción en un momento en que no correspondía. Probá cerrar el lote y volver a abrirlo.",
                        technical);
            }

            return new AgpError("AGP-SYS-009", "Algo salió mal. El equipo de soporte puede ayudarte con el código de error.", technical);
        }

        // Sockets.SocketException.SocketErrorCode (reflection — evitamos hard-dep
        // a System.Net.Sockets en este proyecto, que está en netstandard2.0).
        private static int? TryGetSocketErrorCode(Exception ex)
        {
            if (ex == null) return null;
            try
            {
                var prop = ex.GetType().GetProperty("ErrorCode");
                if (prop != null)
                {
                    var v = prop.GetValue(ex);
                    if (v is int i) return i;
                }
                var prop2 = ex.GetType().GetProperty("SocketErrorCode");
                if (prop2 != null)
                {
                    var v = prop2.GetValue(ex);
                    if (v != null) return Convert.ToInt32(v);
                }
            }
            catch { }
            return null;
        }
    }
}
