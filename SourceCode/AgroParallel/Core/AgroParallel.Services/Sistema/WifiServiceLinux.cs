// ============================================================================
// WifiServiceLinux.cs — IWifiService sobre nmcli (NetworkManager), la
// contracara de WifiServiceWindows para la pantalla kiosko Linux.
//
// Parsing: `nmcli -t` (terse, campos separados por ':'). Con IN-USE primero
// y SIGNAL/SECURITY al final, un SSID que contenga ':' se reconstruye
// uniendo los campos del medio — sin depender del escapado de nmcli.
//
// Permisos: NetworkManager deja manejar WiFi a usuarios de consola local via
// polkit; el instalador del kiosko agrega al usuario pilotx al grupo netdev.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class WifiServiceLinux : IWifiService
    {
        public List<WifiRedInfo> Escanear()
        {
            var redes = new List<WifiRedInfo>();
            string salida = Run("nmcli", "-t --escape no -f IN-USE,SSID,SIGNAL,SECURITY dev wifi list --rescan yes") ?? "";
            foreach (var lnRaw in salida.Split('\n'))
            {
                string ln = lnRaw.TrimEnd('\r');
                if (ln.Length == 0) continue;
                var partes = ln.Split(':');
                if (partes.Length < 4) continue;

                bool enUso = partes[0].Trim() == "*";
                string seguridad = partes[partes.Length - 1].Trim();
                string senalStr = partes[partes.Length - 2].Trim();
                string ssid = string.Join(":", partes.Skip(1).Take(partes.Length - 3)).Trim();
                if (ssid.Length == 0) ssid = "(red oculta)";

                int.TryParse(senalStr, out int senal);
                var existente = redes.FirstOrDefault(r => r.Ssid == ssid);
                if (existente == null)
                {
                    redes.Add(new WifiRedInfo
                    {
                        Ssid = ssid,
                        SenalPct = senal,
                        // "--" o vacío = red abierta.
                        Segura = seguridad.Length > 0 && seguridad != "--",
                        Conectada = enUso,
                    });
                }
                else
                {
                    if (senal > existente.SenalPct) existente.SenalPct = senal;
                    existente.Conectada |= enUso;
                }
            }
            return redes.OrderByDescending(r => r.Conectada)
                        .ThenByDescending(r => r.SenalPct).ToList();
        }

        public WifiEstado Estado()
        {
            var conectada = Escanear().FirstOrDefault(r => r.Conectada);
            string ip = null;
            string dev = WifiDevice();
            if (dev != null)
            {
                string salida = Run("nmcli", "-t --escape no -f IP4.ADDRESS dev show " + dev) ?? "";
                // "IP4.ADDRESS[1]:192.168.1.20/24"
                var ln = salida.Split('\n').FirstOrDefault(l => l.Contains(":"));
                if (ln != null)
                {
                    string v = ln.Substring(ln.IndexOf(':') + 1).Trim();
                    int barra = v.IndexOf('/');
                    ip = barra > 0 ? v.Substring(0, barra) : v;
                    if (ip.Length == 0) ip = null;
                }
            }
            return new WifiEstado
            {
                Conectado = conectada != null,
                Ssid = conectada?.Ssid,
                Ip = ip,
            };
        }

        public bool Conectar(string ssid, string clave, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(ssid)) { error = "ssid-vacio"; return false; }
            // Las comillas rompen el quoting del proceso: mejor rechazar que inyectar.
            if (ssid.Contains("\"") || (clave != null && clave.Contains("\"")))
            { error = "caracteres-no-soportados"; return false; }

            string args = string.IsNullOrEmpty(clave)
                ? "dev wifi connect \"" + ssid + "\""
                : "dev wifi connect \"" + ssid + "\" password \"" + clave + "\"";
            string salida = Run("nmcli", args, 30000);
            if (salida == null) { error = "nmcli-no-disponible"; return false; }

            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(1000);
                var e = Estado();
                if (e.Conectado && e.Ssid == ssid) return true;
            }
            error = "no-conecto (clave incorrecta o red fuera de alcance)";
            return false;
        }

        public bool Desconectar(out string error)
        {
            error = null;
            string dev = WifiDevice();
            if (dev == null) { error = "sin-interfaz-wifi"; return false; }
            Run("nmcli", "dev disconnect " + dev);
            return true;
        }

        private static string WifiDevice()
        {
            string salida = Run("nmcli", "-t --escape no -f DEVICE,TYPE dev") ?? "";
            foreach (var lnRaw in salida.Split('\n'))
            {
                var partes = lnRaw.TrimEnd('\r').Split(':');
                if (partes.Length >= 2 && partes[1].Trim() == "wifi")
                    return partes[0].Trim();
            }
            return null;
        }

        private static string Run(string exe, string args, int timeoutMs = 20000)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    string salida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    return salida;
                }
            }
            catch { return null; }
        }
    }
}
