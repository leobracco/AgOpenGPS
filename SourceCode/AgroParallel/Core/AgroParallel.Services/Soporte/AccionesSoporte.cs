// ============================================================================
// AccionesSoporte.cs — catálogo de acciones de soporte remoto.
//
// Es el CAMINO NORMAL del soporte remoto: acciones escritas y probadas por
// nosotros, que no reciben comandos del cloud sino a lo sumo parámetros
// validados. El shell libre existe aparte, apagado por defecto (ver spec
// 2026-09-05-soporte-remoto-orbitx.md).
//
// Reglas que valen para TODA acción que se agregue acá:
//   · NO ejecuta lo que le mandan: el nombre de la acción se busca en el
//     catálogo; si no está, se rechaza. Nunca se concatena entrada del cloud
//     en una línea de comando.
//   · Sale TEXTO plano, acotado (ver Limites.MaxSalida). Lo lee un humano en
//     el panel, no una máquina.
//   · NUNCA devuelve credenciales. Toda salida pasa por Sanitizar(), que tapa
//     el token del equipo y el contenido de orbitX.json. Un pedido de logs no
//     puede terminar filtrando el token con el que se comanda la máquina.
//   · Las acciones que TOCAN la máquina (reiniciar) se marcan EsAccion=true;
//     el panel las trata distinto y pide confirmación.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AgroParallel.Soporte
{
    /// <summary>Una acción del catálogo.</summary>
    public sealed class AccionSoporte
    {
        public string Nombre;
        /// <summary>Qué hace, en criollo. Lo muestra el panel.</summary>
        public string Descripcion;
        /// <summary>true = modifica la máquina (reiniciar). El panel confirma.</summary>
        public bool EsAccion;
        /// <summary>Ejecuta y devuelve texto. Recibe los params ya validados.</summary>
        public Func<IDictionary<string, string>, string> Ejecutar;
    }

    public static class AccionesSoporte
    {
        /// <summary>Tope de salida: 256 KB. Más que eso no lo lee nadie y
        /// llena CouchDB.</summary>
        public const int MaxSalida = 256 * 1024;

        /// <summary>Timeout por acción (s). El cloud puede pedir menos, no más.</summary>
        public const int TimeoutDefaultSeg = 60;
        public const int TimeoutMaxSeg = 300;

        private static readonly Dictionary<string, AccionSoporte> _cat =
            new Dictionary<string, AccionSoporte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Catálogo (nombre → acción). Sólo lectura.</summary>
        public static IReadOnlyDictionary<string, AccionSoporte> Catalogo => _cat;

        /// <summary>Busca una acción. null si no existe (→ se rechaza).</summary>
        public static AccionSoporte Buscar(string nombre)
        {
            if (string.IsNullOrWhiteSpace(nombre)) return null;
            AccionSoporte a;
            return _cat.TryGetValue(nombre.Trim(), out a) ? a : null;
        }

        static AccionesSoporte()
        {
            Reg("estado", "Versión de PilotX, si el motor responde y perfil activo", false, Estado);
            Reg("sistema", "Windows, RAM, disco y CPU", false, Sistema);
            Reg("red", "IPs, gateway, DNS y si llega a OrbitX", false, Red);
            Reg("puertos", "Quién escucha en 5180 / 1883 / 8888", false, Puertos);
            Reg("procesos", "Qué procesos de PilotX corren y desde qué ruta", false, Procesos);
            Reg("firewall", "Perfiles de firewall y reglas de PilotX", false, Firewall);
            Reg("logs_pilotx", "Últimas líneas del log de eventos (param: lineas)", false, LogsPilotX);
            Reg("nodos", "Config de nodos vista por la pantalla", false, Nodos);

            // --- FlowX: operacion remota ACOTADA (no es un proxy abierto) ---
            // Cada accion hace UNA cosa nombrada contra la API local del Engine
            // (loopback). No reciben rutas ni comandos libres: solo parametros
            // validados. Asi, si el token del equipo se filtra, el dano posible
            // esta enumerado, no es "cualquier cosa".
            Reg("flowx_diag", "FlowX: caudal, PWM, objetivo, config y secciones AOG", false, FlowxDiag);
            Reg("flowx_pwm", "FlowX: mueve la valvula a un PWM (params: uid, pwm -4095..4095, seg 1..30). Corta solo.", true, FlowxPwm);
            Reg("flowx_pisos", "FlowX: graba pwm_min de arranque (params: uid, pos, neg 0..4095)", true, FlowxPisos);
            Reg("flowx_config", "FlowX: ajusta config en PilotX (params: uid, pwm_min, dosis_lha, modo_manual, manual_lmin, meter_cal)", true, FlowxConfigSet);
            Reg("secciones_manual", "Maestro de secciones en manual (param: on = 1/0)", true, SeccionesManual);

            // --- Nodos y red: solo lectura ---
            Reg("nodos_live", "Nodos que ve el Engine: online/offline, IP, version, ultimo visto", false, NodosLive);
            Reg("nodo_estado", "Matriz wifi/mqtt/target/status de un nodo (param: uid)", false, NodoEstado);
            Reg("ping", "Ping a una IP privada de la LAN (param: ip)", false, PingLan);
            // Corte de secciones: para ver a distancia si el tilde de curva esta
            // apagado, como esta el enganche y que velocidad/dosis recibe cada
            // motor QuantiX (Gringas 2026-09-10: "no van todos a la misma
            // velocidad" y desde el panel no se veia nada de esto).
            Reg("corte_config", "Config del corte (rumbo, anticipacion, secciones, implemento) + motores QuantiX + live", false, CorteConfig);
        }

        private static void Reg(string n, string d, bool esAccion,
                                Func<IDictionary<string, string>, string> f)
            => _cat[n] = new AccionSoporte { Nombre = n, Descripcion = d, EsAccion = esAccion, Ejecutar = f };

        // ---------------------------------------------------------------- //
        //  Acciones                                                          //
        // ---------------------------------------------------------------- //

        private static string Estado(IDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("== PilotX ==");
            sb.AppendLine("version   : " + VersionPilotX());
            sb.AppendLine("instalado : " + AppContext.BaseDirectory);
            // TickCount64 no existe en netstandard2.0; el int se castea a uint
            // para que no dé negativo pasados los 24,8 días de encendido.
            sb.AppendLine("uptime PC : " +
                TimeSpan.FromMilliseconds((uint)Environment.TickCount).ToString(@"d\d\ hh\:mm"));
            sb.AppendLine();
            sb.AppendLine("== Motor de guiado ==");
            sb.AppendLine(SondearHttp("http://127.0.0.1:5180/api/aog/state", "motor (5180)"));
            return sb.ToString();
        }

        private static string Sistema(IDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SO        : " + Environment.OSVersion);
            sb.AppendLine("equipo    : " + Environment.MachineName);
            sb.AppendLine("usuario   : " + Environment.UserName);
            sb.AppendLine("CPUs      : " + Environment.ProcessorCount);
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "disco {0}  : {1:F1} GB libres de {2:F1} GB",
                    d.Name, d.AvailableFreeSpace / 1073741824.0, d.TotalSize / 1073741824.0));
            }
            catch (Exception ex) { sb.AppendLine("disco     : (no se pudo leer: " + ex.Message + ")"); }
            return sb.ToString();
        }

        private static string Red(IDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("== Interfaces ==");
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var ip = ni.GetIPProperties();
                    foreach (var u in ip.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        sb.AppendLine(string.Format("{0,-28} {1}", Recortar(ni.Name, 28), u.Address));
                    }
                    foreach (var g in ip.GatewayAddresses)
                        if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            sb.AppendLine(string.Format("{0,-28} gateway {1}", "", g.Address));
                }
            }
            catch (Exception ex) { sb.AppendLine("(error leyendo interfaces: " + ex.Message + ")"); }

            sb.AppendLine();
            sb.AppendLine("== Salida ==");
            sb.AppendLine(SondearHttp("https://orbitx.agroparallel.com/", "OrbitX"));
            return sb.ToString();
        }

        private static string Puertos(IDictionary<string, string> p)
        {
            // Los tres puertos que ya nos comieron horas: 5180 (WebHost),
            // 1883 (broker local) y 8888 (wire de módulos).
            var sb = new StringBuilder();
            sb.AppendLine("Escuchas en 5180 / 1883 / 8888 (vacío = libre):");
            string salida = CorrerProceso("netstat.exe", new[] { "-ano" }, 20);
            foreach (var l in salida.Split('\n'))
            {
                if (l.IndexOf(":5180", StringComparison.Ordinal) >= 0 ||
                    l.IndexOf(":1883", StringComparison.Ordinal) >= 0 ||
                    l.IndexOf(":8888", StringComparison.Ordinal) >= 0)
                    sb.AppendLine(l.TrimEnd());
            }
            return sb.ToString();
        }

        private static string Procesos(IDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            string[] nombres = { "PilotX.Desktop", "PilotX.GuidanceEngine", "PilotX.Bars.Host", "AgroParallel.Updater" };
            foreach (var n in nombres)
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(n); } catch { continue; }
                if (ps.Length == 0) { sb.AppendLine(string.Format("{0,-24} NO corre", n)); continue; }
                foreach (var pr in ps)
                {
                    string ruta = "";
                    try { ruta = pr.MainModule?.FileName ?? ""; } catch { ruta = "(sin acceso)"; }
                    sb.AppendLine(string.Format("{0,-24} pid {1,-7} {2}", n, pr.Id, ruta));
                }
            }
            return sb.ToString();
        }

        private static string Firewall(IDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("== Perfiles ==");
            sb.AppendLine(CorrerProceso("netsh.exe",
                new[] { "advfirewall", "show", "allprofiles", "state" }, 20));
            sb.AppendLine("== Reglas de PilotX ==");
            sb.AppendLine(CorrerProceso("netsh.exe",
                new[] { "advfirewall", "firewall", "show", "rule", "name=all", "dir=in" }, 30)
                .Split('\n').Where(l => l.IndexOf("PilotX", StringComparison.OrdinalIgnoreCase) >= 0)
                .DefaultIfEmpty("(ninguna regla con 'PilotX' en el nombre)")
                .Aggregate(new StringBuilder(), (a, l) => a.AppendLine(l.TrimEnd())).ToString());
            return sb.ToString();
        }

        private static string LogsPilotX(IDictionary<string, string> p)
        {
            int lineas = LeerInt(p, "lineas", 200, 1, 500);
            string ruta = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AgOpenGPS", "Logs", "AgOpenGPS_Events_Log.txt");
            if (!File.Exists(ruta)) return "No existe el log: " + ruta;
            try
            {
                var todas = File.ReadAllLines(ruta);
                var ult = todas.Skip(Math.Max(0, todas.Length - lineas));
                return string.Format("({0} últimas de {1} líneas)\n", lineas, todas.Length)
                       + string.Join("\n", ult);
            }
            catch (Exception ex) { return "No se pudo leer el log: " + ex.Message; }
        }

        private static string Nodos(IDictionary<string, string> p)
        {
            string ruta = Path.Combine(AppContext.BaseDirectory, "nodos.json");
            if (!File.Exists(ruta)) return "No hay nodos.json en " + AppContext.BaseDirectory;
            try { return File.ReadAllText(ruta); }
            catch (Exception ex) { return "No se pudo leer nodos.json: " + ex.Message; }
        }

        // ---------------------------------------------------------------- //
        //  Helpers                                                           //
        // ---------------------------------------------------------------- //

        /// <summary>
        /// Tapa credenciales antes de que la salida salga del equipo. Se aplica
        /// SIEMPRE, tanto al catálogo como al shell: el panel es un lugar
        /// legítimo para leer logs, pero no para cosechar tokens.
        /// </summary>
        public static string Sanitizar(string texto, string tokenEquipo)
        {
            if (string.IsNullOrEmpty(texto)) return texto ?? "";

            // ORDEN A PROPÓSITO: primero el patrón clave:valor, después el token
            // exacto. Al revés, el regex volvía a matchear sobre la marca recién
            // puesta ("X-Auth-Token: «token oculto»") y el resultado dependía de
            // cómo estuviera escrita la línea. Tapado queda igual en los dos
            // órdenes; así además es predecible.
            string s = System.Text.RegularExpressions.Regex.Replace(
                texto, "(\"?(?:device_token|token|password|pass|auth_token)\"?\\s*[:=]\\s*\"?)([^\",;\\s]{6,})",
                "$1«oculto»", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // El token del equipo, esté donde esté (una URL, un log, un dump).
            if (!string.IsNullOrEmpty(tokenEquipo) && tokenEquipo.Length >= 8)
                s = s.Replace(tokenEquipo, "«oculto»");

            return s;
        }

        /// <summary>Corta la salida al tope y lo dice (no truncar en silencio).</summary>
        public static string Acotar(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return "";
            if (texto.Length <= MaxSalida) return texto;
            return texto.Substring(0, MaxSalida)
                   + "\n\n… salida cortada en " + MaxSalida + " caracteres …";
        }

        private static int LeerInt(IDictionary<string, string> p, string clave, int def, int min, int max)
        {
            if (p == null) return def;
            string v;
            if (!p.TryGetValue(clave, out v)) return def;
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return def;
            return Math.Max(min, Math.Min(max, n));
        }

        private static string VersionPilotX()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                var attr = asm?.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attr != null && attr.Length > 0)
                    return ((System.Reflection.AssemblyInformationalVersionAttribute)attr[0]).InformationalVersion;
                return asm?.GetName().Version?.ToString() ?? "(desconocida)";
            }
            catch { return "(desconocida)"; }
        }

        // ---------------------------------------------------------------- //
        //  FlowX remoto (acotado). Todo contra 127.0.0.1:5180, sin rutas    //
        //  ni comandos libres: cada accion valida sus propios parametros.   //
        // ---------------------------------------------------------------- //

        private const string FlowxBase = "http://127.0.0.1:5180";

        private static string HttpGet(string url)
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                var r = http.GetAsync(url).GetAwaiter().GetResult();
                return r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        private static string HttpPost(string url, string json)
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                var cont = new System.Net.Http.StringContent(json ?? "", Encoding.UTF8, "application/json");
                var r = http.PostAsync(url, cont).GetAwaiter().GetResult();
                return "HTTP " + (int)r.StatusCode + " " + r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        // UID de nodo: letras/numeros y el guion de los uids con prefijo
        // ("QX-C458…", "VX-F88F…"), para no armar URLs raras. Sin el guion,
        // nodo_estado rechazaba todos los QuantiX/VistaX (bug hasta 1.0.68).
        private static string UidValido(IDictionary<string, string> p)
        {
            string uid = Leer(p, "uid");
            if (string.IsNullOrWhiteSpace(uid)) return null;
            uid = uid.Trim();
            foreach (char c in uid)
                if (!char.IsLetterOrDigit(c) && c != '-') return null;
            return uid;
        }

        private static string Leer(IDictionary<string, string> p, string clave)
        {
            if (p == null) return null;
            foreach (var kv in p)
                if (string.Equals(kv.Key, clave, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        private static string FlowxDiag(IDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            try { sb.AppendLine("== live =="); sb.AppendLine(HttpGet(FlowxBase + "/api/flowx/live")); }
            catch (Exception ex) { sb.AppendLine("live: " + ex.GetBaseException().Message); }
            try { sb.AppendLine("== config =="); sb.AppendLine(HttpGet(FlowxBase + "/api/flowx/config")); }
            catch (Exception ex) { sb.AppendLine("config: " + ex.GetBaseException().Message); }
            try
            {
                string st = HttpGet(FlowxBase + "/api/aog/state");
                sb.AppendLine("== secciones AOG ==");
                foreach (var campo in new[] { "\"is_job_started\"", "\"is_section_manual_on\"",
                    "\"num_sections\"", "\"section_states\"", "\"section_on_request\"" })
                {
                    int i = st.IndexOf(campo, StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        int fin = st.IndexOfAny(new[] { ',', '}', ']' }, i + campo.Length + 1);
                        if (fin > i) sb.AppendLine("  " + st.Substring(i, fin - i + 1).Trim());
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("state: " + ex.GetBaseException().Message); }
            return sb.ToString();
        }

        private static string FlowxPwm(IDictionary<string, string> p)
        {
            string uid = UidValido(p);
            if (uid == null) return "falta 'uid' valido del nodo FlowX";
            int pwm = LeerInt(p, "pwm", 0, -4095, 4095);
            int seg = LeerInt(p, "seg", 8, 1, 30);   // tope duro 30 s: nunca queda clavada
            var sb = new StringBuilder();
            sb.AppendLine("moviendo " + uid + " a pwm=" + pwm + " por " + seg + " s");
            string cmd = FlowxBase + "/api/flowx/" + uid + "/cmd?verb=manual_pwm";
            try
            {
                for (int i = 0; i < seg; i++)
                {
                    HttpPost(cmd, "{\"producto_id\":0,\"value\":" + pwm + "}");
                    System.Threading.Thread.Sleep(1000);
                    if (i == seg - 1 || i % 3 == 0)
                    {
                        try
                        {
                            string live = HttpGet(FlowxBase + "/api/flowx/live");
                            int j = live.IndexOf("\"caudal_lmin\"", StringComparison.Ordinal);
                            string cau = j >= 0 ? live.Substring(j, Math.Min(28, live.Length - j)) : "?";
                            sb.AppendLine("  t+" + (i + 1) + "s  " + cau);
                        }
                        catch { }
                    }
                }
            }
            finally
            {
                try { HttpPost(FlowxBase + "/api/flowx/" + uid + "/cmd?verb=manual_stop", "{\"producto_id\":0}"); } catch { }
                sb.AppendLine("cortado (manual_stop).");
            }
            return sb.ToString();
        }

        private static string FlowxPisos(IDictionary<string, string> p)
        {
            string uid = UidValido(p);
            if (uid == null) return "falta 'uid' valido del nodo FlowX";
            var sb = new StringBuilder();
            string cmd = FlowxBase + "/api/flowx/" + uid + "/cmd?verb=save_pwm_min";
            string pos = Leer(p, "pos"), neg = Leer(p, "neg");
            if (pos != null)
            {
                int v = LeerInt(p, "pos", 800, 0, 4095);
                try { sb.AppendLine("pos=" + v + " -> " + HttpPost(cmd, "{\"producto_id\":0,\"dir\":\"pos\",\"value\":" + v + "}")); }
                catch (Exception ex) { sb.AppendLine("pos: " + ex.GetBaseException().Message); }
            }
            if (neg != null)
            {
                int v = LeerInt(p, "neg", 800, 0, 4095);
                try { sb.AppendLine("neg=" + v + " -> " + HttpPost(cmd, "{\"producto_id\":0,\"dir\":\"neg\",\"value\":" + v + "}")); }
                catch (Exception ex) { sb.AppendLine("neg: " + ex.GetBaseException().Message); }
            }
            if (pos == null && neg == null) return "no diste ni 'pos' ni 'neg'";

            // El piso POSITIVO tambien va a la config de PilotX. Motivo: el
            // puente le manda al nodo el pwm_min de flowX.json en CADA target
            // (cada 2 s) y pisa lo que se grabe en el nodo. Grabar solo en el
            // nodo duraba 2 segundos: se descubrio en campo el 2026-09-06 con
            // la reguladora quedando un 36% corta de dosis. PilotX es la unica
            // fuente de verdad del piso positivo; el negativo vive en el nodo
            // porque el puente no lo manda.
            if (pos != null)
            {
                int v = LeerInt(p, "pos", 800, 0, 4095);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.PwmMin = v, "pwm_min=" + v));
            }
            return sb.ToString();
        }

        // Aplica un cambio al producto 0 del nodo en flowX.json y guarda. El
        // puente relee la config en cada ciclo, asi que el cambio viaja solo.
        private static string GuardarEnConfigPilotX(string uid, Action<AgroParallel.FlowX.FxProducto> cambio, string etiqueta)
        {
            try
            {
                var cfg = AgroParallel.FlowX.FlowXConfig.Load();
                AgroParallel.FlowX.FxNodoConfig nodo = null;
                foreach (var n in cfg.Nodos)
                    if (string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase)) { nodo = n; break; }
                if (nodo == null && cfg.Nodos.Count > 0) nodo = cfg.Nodos[0];
                if (nodo == null) return "config PilotX: no hay nodos en flowX.json";
                if (nodo.Productos.Count == 0) nodo.Productos.Add(new AgroParallel.FlowX.FxProducto());
                cambio(nodo.Productos[0]);
                cfg.Save();
                return "config PilotX (" + nodo.Uid + "): " + etiqueta + " guardado";
            }
            catch (Exception ex)
            {
                return "config PilotX: no se pudo guardar (" + ex.Message + ")";
            }
        }

        private static string FlowxConfigSet(IDictionary<string, string> p)
        {
            string uid = UidValido(p) ?? "";
            var sb = new StringBuilder();
            int tocados = 0;

            string s;
            if ((s = Leer(p, "pwm_min")) != null)
            {
                int v = LeerInt(p, "pwm_min", 800, 0, 4095);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.PwmMin = v, "pwm_min=" + v)); tocados++;
            }
            if ((s = Leer(p, "dosis_lha")) != null)
            {
                double v = LeerDouble(p, "dosis_lha", 100, 0, 2000);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.DosisLha = v, "dosis_lha=" + v)); tocados++;
            }
            if ((s = Leer(p, "modo_manual")) != null)
            {
                bool v = LeerInt(p, "modo_manual", 0, 0, 1) == 1;
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.ModoManual = v, "modo_manual=" + v)); tocados++;
            }
            if ((s = Leer(p, "manual_lmin")) != null)
            {
                double v = LeerDouble(p, "manual_lmin", 0, 0, 500);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.ManualLmin = v, "manual_lmin=" + v)); tocados++;
            }
            if ((s = Leer(p, "meter_cal")) != null)
            {
                double v = LeerDouble(p, "meter_cal", 100, 0.1, 100000);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.MeterCal = v, "meter_cal=" + v)); tocados++;
            }
            // Ganancias del PID de valvula (firmware FlowX 1.9.12+). Unidades:
            // kp = PWM por L/min de error, ki = PWM por (L/min*s), kd = PWM por
            // (L/min/s). Arranque razonable: 250 / 80 / 0. El firmware trata
            // kp < 20 como config legada y usa esos defaults.
            if ((s = Leer(p, "kp")) != null)
            {
                double v = LeerDouble(p, "kp", 250, 0, 5000);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.Kp = v, "kp=" + v)); tocados++;
            }
            if ((s = Leer(p, "ki")) != null)
            {
                double v = LeerDouble(p, "ki", 80, 0, 5000);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.Ki = v, "ki=" + v)); tocados++;
            }
            if ((s = Leer(p, "kd")) != null)
            {
                double v = LeerDouble(p, "kd", 0, 0, 5000);
                sb.AppendLine(GuardarEnConfigPilotX(uid, prod => prod.Kd = v, "kd=" + v)); tocados++;
            }
            if (tocados == 0) return "no diste ningun parametro (pwm_min, dosis_lha, modo_manual, manual_lmin, meter_cal, kp, ki, kd)";
            return sb.ToString();
        }

        private static double LeerDouble(IDictionary<string, string> p, string clave, double def, double min, double max)
        {
            string v = Leer(p, clave);
            if (v == null) return def;
            double n;
            if (!double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return def;
            return Math.Max(min, Math.Min(max, n));
        }

        private static string SeccionesManual(IDictionary<string, string> p)
        {
            // Lee el estado actual y solo togglea si hace falta llegar al pedido.
            bool quiero = LeerInt(p, "on", 1, 0, 1) == 1;
            try
            {
                string st = HttpGet(FlowxBase + "/api/aog/state");
                bool estaAhora = st.IndexOf("\"is_section_manual_on\":true", StringComparison.Ordinal) >= 0;
                if (estaAhora == quiero) return "maestro manual ya estaba en " + (quiero ? "ON" : "OFF");
                HttpPost(FlowxBase + "/api/aog/guidance/command", "{\"cmd\":\"sec_manual\"}");
                System.Threading.Thread.Sleep(500);
                string st2 = HttpGet(FlowxBase + "/api/aog/state");
                bool ahora = st2.IndexOf("\"is_section_manual_on\":true", StringComparison.Ordinal) >= 0;
                return "maestro manual -> " + (ahora ? "ON" : "OFF");
            }
            catch (Exception ex) { return "fallo: " + ex.GetBaseException().Message; }
        }

        // ---------------------------------------------------------------- //
        //  Nodos y red (solo lectura)                                        //
        // ---------------------------------------------------------------- //

        // Lee un campo string/num/bool de un objeto JSON plano por regex. Es
        // suficiente para resumir la salida del Engine sin arrastrar un parser.
        private static string CampoJson(string json, string clave)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                json, "\"" + clave + "\"\\s*:\\s*(\"([^\"]*)\"|[^,}\\]]+)");
            if (!m.Success) return "";
            return m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value.Trim();
        }

        private static string NodosLive(IDictionary<string, string> p)
        {
            string json;
            try { json = HttpGet(FlowxBase + "/api/nodos/unified"); }
            catch (Exception ex) { return "no pude leer /api/nodos/unified: " + ex.GetBaseException().Message; }

            var sb = new StringBuilder();
            sb.AppendLine("broker conectado: " + CampoJson(json, "broker_connected"));
            // Cada nodo es un objeto dentro de "nodos":[ {...}, {...} ]
            int ini = json.IndexOf("\"nodos\"", StringComparison.Ordinal);
            if (ini < 0) return sb.ToString() + json.Substring(0, Math.Min(400, json.Length));
            var objs = System.Text.RegularExpressions.Regex.Matches(json.Substring(ini), "\\{[^{}]*\\}");
            if (objs.Count == 0) sb.AppendLine("(sin nodos)");
            foreach (System.Text.RegularExpressions.Match o in objs)
            {
                string n = o.Value;
                string uid = CampoJson(n, "uid");
                if (string.IsNullOrEmpty(uid)) continue;
                sb.AppendLine(string.Format("{0,-18} {1,-8} {2,-16} online={3,-5} ip={4,-15} fw={5,-8} visto={6}",
                    uid, CampoJson(n, "tipo"), CampoJson(n, "alias"), CampoJson(n, "online"),
                    CampoJson(n, "ip"), CampoJson(n, "version"), CampoJson(n, "last_seen")));
            }
            return sb.ToString();
        }

        /// <summary>Solo lectura, tres GET fijos al Engine (loopback): la
        /// config completa del vehiculo/implemento (rumbo, anticipacion,
        /// secciones, enganche), la config de motores QuantiX y el estado en
        /// vivo de los nodos QuantiX. Sale JSON crudo, acotado por bloque.</summary>
        private static string CorteConfig(IDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            string[][] bloques =
            {
                new[] { "/api/aog/config",     "config del vehiculo e implemento" },
                new[] { "/api/quantix/motores", "motores QuantiX (config)" },
                new[] { "/api/quantix/live",    "nodos QuantiX (live)" },
            };
            foreach (var b in bloques)
            {
                sb.AppendLine("== " + b[0] + " — " + b[1] + " ==");
                try
                {
                    string json = HttpGet(FlowxBase + b[0]) ?? "";
                    if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                    if (json.Length > 60 * 1024) json = json.Substring(0, 60 * 1024) + " …(recortado)";
                    sb.AppendLine(json);
                }
                catch (Exception ex) { sb.AppendLine("error: " + ex.GetBaseException().Message); }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string NodoEstado(IDictionary<string, string> p)
        {
            string uid = UidValido(p);
            if (uid == null) return "falta 'uid' valido";
            try { return HttpGet(FlowxBase + "/api/nodos/" + uid + "/estado"); }
            catch (Exception ex) { return "no pude leer el estado de " + uid + ": " + ex.GetBaseException().Message; }
        }

        // Ping SOLO a direcciones privadas (10/8, 172.16/12, 192.168/16): es
        // para ver si un nodo de la LAN del tractor responde, no para tocar
        // nada fuera. Argumentos fijos, sin shell.
        private static string PingLan(IDictionary<string, string> p)
        {
            string ip = (Leer(p, "ip") ?? "").Trim();
            System.Net.IPAddress dir;
            if (!System.Net.IPAddress.TryParse(ip, out dir) ||
                dir.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return "ip invalida: " + ip;
            byte[] b = dir.GetAddressBytes();
            bool privada = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
            if (!privada) return "solo se permite ping a IPs privadas de la LAN";
            return CorrerProceso("ping.exe", new[] { "-n", "3", "-w", "1000", ip }, 15);
        }

        private static string SondearHttp(string url, string etiqueta)
        {
            try
            {
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    var sw = Stopwatch.StartNew();
                    var r = http.GetAsync(url).GetAwaiter().GetResult();
                    sw.Stop();
                    return string.Format("{0}: responde {1} en {2} ms", etiqueta, (int)r.StatusCode, sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                return etiqueta + ": NO responde (" + ex.GetBaseException().Message + ")";
            }
        }

        /// <summary>
        /// Corre un ejecutable del SISTEMA con argumentos FIJOS nuestros (nunca
        /// texto del cloud). Sin shell: ProcessStartInfo con lista de args.
        /// </summary>
        private static string CorrerProceso(string exe, string[] args, int timeoutSeg)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.Arguments = ArgsSeguros(args);

                using (var pr = Process.Start(psi))
                {
                    if (pr == null) return "(no se pudo lanzar " + exe + ")";
                    string outp = pr.StandardOutput.ReadToEnd();
                    string err = pr.StandardError.ReadToEnd();
                    if (!pr.WaitForExit(timeoutSeg * 1000))
                    {
                        try { pr.Kill(); } catch { }
                        return "(timeout de " + timeoutSeg + " s en " + exe + ")";
                    }
                    return string.IsNullOrWhiteSpace(err) ? outp : outp + "\n[stderr] " + err;
                }
            }
            catch (Exception ex) { return "(error corriendo " + exe + ": " + ex.Message + ")"; }
        }

        /// <summary>Cita argumentos con la regla de Windows. Los args son
        /// nuestros, pero se citan igual: si mañana alguno lleva un espacio,
        /// que no se parta en dos.</summary>
        internal static string ArgsSeguros(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (!string.IsNullOrEmpty(a) && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { sb.Append(a); continue; }
                sb.Append('"');
                foreach (char c in a ?? "")
                {
                    if (c == '"') sb.Append('\\');
                    sb.Append(c);
                }
                sb.Append('"');
            }
            return sb.ToString();
        }

        private static string Recortar(string s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));
    }
}
