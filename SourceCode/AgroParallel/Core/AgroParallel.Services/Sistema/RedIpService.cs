// ============================================================================
// RedIpService.cs — configuración de IP (DHCP / fija) de los adaptadores.
//
// PilotX corre como usuario LIMITADO y NO puede cambiar la IP (netsh/New-NetIP
// requieren admin). Este servicio solo LEE la config (no necesita permisos) y
// para APLICAR deja un pedido en C:\PilotX\netconfig\request.json; el helper
// NetApplyWatcher (tarea SYSTEM de ViewX TabletTools) lo aplica y responde en
// result.json. Así el cambio privilegiado lo hace SYSTEM, no PilotX.
//
// Autocontenido (sin dependencias) — se instancia inline en AgpWebHost.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace AgroParallel.Services
{
    public sealed class RedAdaptador
    {
        public int IfIndex { get; set; }
        public string Nombre { get; set; }
        public string Tipo { get; set; }      // "wifi" | "ethernet"
        public bool Dhcp { get; set; }
        public string Ip { get; set; }
        public int Prefix { get; set; }
        public string Gateway { get; set; }
        public List<string> Dns { get; set; } = new List<string>();
        public bool Up { get; set; }
        public int Metric { get; set; }   // menor metrica = salida a internet
    }

    public sealed class RedIpService
    {
        // Mismo directorio que usa NetApplyWatcher (helper SYSTEM).
        private const string Dir = @"C:\PilotX\netconfig";

        public List<RedAdaptador> Listar()
        {
            var lista = new List<RedAdaptador>();
            var metricas = LeerMetricas();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                bool wifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                bool eth = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
                if (!wifi && !eth) continue;
                // Descartar virtuales (Hyper-V, VMware, VirtualBox, WSL, VPN…).
                string desc = (ni.Description ?? "") + " " + (ni.Name ?? "");
                if (desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0
                    || desc.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0
                    || desc.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0
                    || desc.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0
                    || desc.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0
                    || desc.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var a = new RedAdaptador
                {
                    Nombre = ni.Name,
                    Tipo = wifi ? "wifi" : "ethernet",
                    Up = ni.OperationalStatus == OperationalStatus.Up,
                };
                try
                {
                    var props = ni.GetIPProperties();
                    var v4 = props.GetIPv4Properties();
                    a.IfIndex = v4 != null ? v4.Index : -1;
                    a.Dhcp = v4 != null && v4.IsDhcpEnabled;
                    var uni = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                                                        && !u.Address.ToString().StartsWith("169.254."));
                    if (uni != null) { a.Ip = uni.Address.ToString(); a.Prefix = uni.PrefixLength; }
                    var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (gw != null) a.Gateway = gw.Address.ToString();
                    a.Dns = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList();
                }
                catch { /* adaptador sin IPv4: queda con lo básico */ }
                if (a.IfIndex > 0)
                {
                    a.Metric = metricas.TryGetValue(a.IfIndex, out var mm) ? mm : -1;
                    lista.Add(a);
                }
            }
            return lista.OrderBy(a => a.Tipo).ThenBy(a => a.Nombre).ToList();
        }

        // Metrica de interfaz por ifIndex, parseando `netsh interface ipv4 show
        // interfaces` (solo lectura, sin admin). Columnas: Idx Met MTU Estado
        // Nombre — los datos son numeros (independiente del idioma).
        private static Dictionary<int, int> LeerMetricas()
        {
            var d = new Dictionary<int, int>();
            try
            {
                var psi = new ProcessStartInfo("netsh", "interface ipv4 show interfaces")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    foreach (var lnRaw in outp.Split('\n'))
                    {
                        var m = Regex.Match(lnRaw, @"^\s*(\d+)\s+(\d+)\s+\d+\s+");
                        if (m.Success)
                            d[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);
                    }
                }
            }
            catch { /* sin netsh: metricas vacias */ }
            return d;
        }

        /// <summary>
        /// Aplica config vía el helper SYSTEM. mode = "dhcp" | "static".
        /// Devuelve true si el helper reportó ok dentro del timeout.
        /// </summary>
        public bool Aplicar(int ifIndex, string mode, string ip, int prefix, string gateway, IEnumerable<string> dns, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Dir);
                string id = "r" + DateTime.UtcNow.Ticks.ToString();
                string req = JsonSerializer.Serialize(new
                {
                    id,
                    ifIndex,
                    mode,
                    ip,
                    prefix,
                    gateway,
                    dns = (dns ?? Enumerable.Empty<string>()).ToArray(),
                });
                string resPath = Path.Combine(Dir, "result.json");
                try { if (File.Exists(resPath)) File.Delete(resPath); } catch { }
                File.WriteAllText(Path.Combine(Dir, "request.json"), req);

                // Esperar el resultado (el helper aplica en ~5-8 s).
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(1000);
                    if (!File.Exists(resPath)) continue;
                    string txt;
                    try { txt = File.ReadAllText(resPath); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    using (var doc = JsonDocument.Parse(txt))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("id", out var idEl) && idEl.GetString() != id) continue; // resultado viejo
                        bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                        if (!ok && root.TryGetProperty("error", out var errEl)) error = errEl.GetString();
                        return ok;
                    }
                }
                error = "El helper de red no respondió (¿NetApplyWatcher no está corriendo?).";
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
