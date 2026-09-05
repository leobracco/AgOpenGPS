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
