// ============================================================================
// WifiServiceWindows.cs — IWifiService sobre `netsh wlan`.
//
// Por qué netsh y no ms-settings: el panel de Windows mostraba las redes sin
// estado, pedía el teclado del SO y rompía el kiosko. Acá el escaneo, el
// estado y la conexión son datos que la página del Hub muestra con la UI y el
// teclado de PilotX.
//
// Trampa de localización: la SALIDA de netsh está traducida ("Señal",
// "Autenticación"). Solo se parsea lo estable entre idiomas: las líneas
// "SSID N : ..." (netsh no traduce la palabra SSID) y los porcentajes "NN%".
// Para leer UTF-8 de verdad (SSIDs con ñ/acentos) se corre netsh adentro de
// un cmd con `chcp 65001`.
//
// Conectar: perfil XML WPA2PSK (o abierto) + `netsh wlan add profile` +
// `netsh wlan connect`, y se confirma esperando a que la interfaz reporte el
// SSID pedido (el connect de netsh devuelve OK aunque la clave esté mal —
// la verdad la tiene el estado, no el comando).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class WifiServiceWindows : IWifiService
    {
        public List<WifiRedInfo> Escanear()
        {
            var redes = new List<WifiRedInfo>();
            string salida = RunNetsh("wlan show networks mode=bssid") ?? "";

            WifiRedInfo actual = null;
            foreach (var lnRaw in salida.Split('\n'))
            {
                string ln = lnRaw.TrimEnd('\r');
                // "SSID 1 : MiRed" — la palabra SSID no se traduce. El regex
                // exige el número para NO comer las líneas "BSSID 1 : ..".
                var mSsid = Regex.Match(ln, @"^SSID\s+\d+\s*:\s*(.*)$");
                if (mSsid.Success)
                {
                    string nombre = mSsid.Groups[1].Value.Trim();
                    if (nombre.Length == 0) nombre = "(red oculta)";
                    actual = redes.FirstOrDefault(r => r.Ssid == nombre);
                    if (actual == null)
                    {
                        actual = new WifiRedInfo { Ssid = nombre, Segura = true };
                        redes.Add(actual);
                    }
                    continue;
                }
                if (actual == null) continue;

                // Señal: cualquier línea del bloque con "NN%" (varios BSSID →
                // se queda con el mejor).
                var mPct = Regex.Match(ln, @"(\d{1,3})\s*%");
                if (mPct.Success)
                {
                    int pct = int.Parse(mPct.Groups[1].Value);
                    if (pct > actual.SenalPct) actual.SenalPct = pct;
                    continue;
                }

                // Autenticación: es la primera línea "clave : valor" después
                // del SSID; red abierta si dice Open/Abierta (es lo único que
                // se chequea del texto localizado, con fallback a "segura").
                if (Regex.IsMatch(ln, @"^\s{4}\S.*:\s*(Open|Abierta|Ninguna)\s*$", RegexOptions.IgnoreCase))
                    actual.Segura = false;
            }

            var estado = Estado();
            if (estado.Conectado && !string.IsNullOrEmpty(estado.Ssid))
            {
                var conectada = redes.FirstOrDefault(r => r.Ssid == estado.Ssid);
                if (conectada == null)
                {
                    conectada = new WifiRedInfo { Ssid = estado.Ssid, Segura = true, SenalPct = 100 };
                    redes.Insert(0, conectada);
                }
                conectada.Conectada = true;
            }

            return redes.OrderByDescending(r => r.Conectada)
                        .ThenByDescending(r => r.SenalPct).ToList();
        }

        public WifiEstado Estado()
        {
            string salida = RunNetsh("wlan show interfaces") ?? "";
            // Primera línea "    SSID : X" (sin B adelante). Si la interfaz
            // está desconectada, netsh no imprime SSID — eso ES el estado.
            string ssid = null;
            foreach (var lnRaw in salida.Split('\n'))
            {
                string ln = lnRaw.TrimEnd('\r');
                var m = Regex.Match(ln, @"^\s{2,}SSID\s*:\s*(.+)$");
                if (m.Success) { ssid = m.Groups[1].Value.Trim(); break; }
            }
            return new WifiEstado
            {
                Conectado = !string.IsNullOrEmpty(ssid),
                Ssid = ssid,
                Ip = IpDeWifi(),
            };
        }

        public bool Conectar(string ssid, string clave, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(ssid)) { error = "ssid-vacio"; return false; }

            string ssidXml = System.Security.SecurityElement.Escape(ssid);
            bool abierta = string.IsNullOrEmpty(clave);
            string seguridad = abierta
                ? "<authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX>"
                : "<authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX>";
            string sharedKey = abierta ? "" :
                "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>"
                + System.Security.SecurityElement.Escape(clave)
                + "</keyMaterial></sharedKey>";

            string xml =
                "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>" + ssidXml + "</name>" +
                "<SSIDConfig><SSID><name>" + ssidXml + "</name></SSID></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption>" + seguridad + "</authEncryption>" + sharedKey +
                "</security></MSM></WLANProfile>";

            string tmp = Path.Combine(Path.GetTempPath(), "pilotx-wifi.xml");
            try { File.WriteAllText(tmp, xml, new UTF8Encoding(false)); }
            catch (Exception ex) { error = "perfil: " + ex.Message; return false; }

            try
            {
                RunNetsh("wlan add profile filename=\"" + tmp + "\" user=all");
                RunNetsh("wlan connect name=\"" + ssid.Replace("\"", "") + "\"");
            }
            finally { try { File.Delete(tmp); } catch { } }

            // El connect devuelve OK con clave mala: confirmar contra el estado.
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
            RunNetsh("wlan disconnect");
            return true;
        }

        // Corre netsh en un cmd con chcp 65001 para que la salida venga UTF-8
        // (sin esto, los SSID con ñ/acentos llegan rotos por el codepage OEM).
        private static string RunNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/d /c chcp 65001 >nul & netsh " + args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };
                using (var p = Process.Start(psi))
                {
                    string salida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    return salida;
                }
            }
            catch { return null; }
        }

        private static string IpDeWifi()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) continue;
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return a.Address.ToString();
                }
            }
            catch { }
            return null;
        }
    }
}
