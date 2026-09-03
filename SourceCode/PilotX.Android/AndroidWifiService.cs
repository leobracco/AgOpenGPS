// ============================================================================
// AndroidWifiService.cs — IWifiService para la tablet (página wifi.html del
// Hub). Gemelo de WifiServiceWindows (netsh) / WifiServiceLinux (nmcli).
//
// Lo que Android permite hoy:
//   · Estado (SSID + IP) y escaneo: WifiManager. El escaneo exige ubicación
//     (ACCESS_FINE_LOCATION concedida en runtime); sin permiso devuelve la
//     red actual sola.
//   · Conectar / olvidar: en API < 29 con la API clásica (WifiConfiguration);
//     en API 29+ Google la cerró: se usa WifiNetworkSuggestion (la tablet
//     muestra una notificación "¿conectar a X?" que el operario acepta una
//     vez) y se abre el panel de WiFi del sistema como respaldo.
//   · Desconectar: WifiManager.Disconnect (obsoleto pero funcional).
// ============================================================================

#nullable enable
using System;
using System.Collections.Generic;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using AgroParallel.Services.Abstractions;

#pragma warning disable CA1422 // APIs de WiFi obsoletas en API 29+: rama explícita por versión
#pragma warning disable CS0618

namespace PilotX.Droid
{
    internal sealed class AndroidWifiService : IWifiService
    {
        private readonly Context _ctx;

        public AndroidWifiService(Context ctx) { _ctx = ctx; }

        private WifiManager? Wifi => (WifiManager?)_ctx.ApplicationContext?.GetSystemService(Context.WifiService);

        private static string Limpiar(string? ssid)
        {
            if (string.IsNullOrEmpty(ssid)) return "";
            var s = ssid!.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return s == "<unknown ssid>" ? "" : s;
        }

        public WifiEstado Estado()
        {
            var e = new WifiEstado { Conectado = false, Ssid = "", Ip = "" };
            try
            {
                var w = Wifi;
                var info = w?.ConnectionInfo;
                if (info != null && info.NetworkId != -1)
                {
                    e.Ssid = Limpiar(info.SSID);
                    int ip = w!.DhcpInfo?.IpAddress ?? 0;
                    if (ip != 0)
                        e.Ip = string.Format("{0}.{1}.{2}.{3}", ip & 0xff, (ip >> 8) & 0xff, (ip >> 16) & 0xff, (ip >> 24) & 0xff);
                    e.Conectado = e.Ssid.Length > 0;
                }
            }
            catch (Exception ex) { Android.Util.Log.Warn("PilotX", "Wifi estado: " + ex.Message); }
            return e;
        }

        public List<WifiRedInfo> Escanear()
        {
            var list = new List<WifiRedInfo>();
            var actual = Estado();
            try
            {
                var w = Wifi;
                if (w == null) return list;
                try { w.StartScan(); } catch { /* throttled en API 28+: se usa el último resultado */ }
                var guardadas = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var cfgs = w.ConfiguredNetworks;
                    if (cfgs != null) foreach (var c in cfgs) guardadas.Add(Limpiar(c.Ssid));
                }
                catch { /* API 29+ devuelve vacío para apps normales */ }

                var vistos = new HashSet<string>(StringComparer.Ordinal);
                var res = w.ScanResults;
                if (res != null)
                {
                    foreach (var r in res)
                    {
                        var ssid = Limpiar(r.Ssid);
                        if (ssid.Length == 0 || !vistos.Add(ssid)) continue;
                        list.Add(new WifiRedInfo
                        {
                            Ssid = ssid,
                            SenalPct = Math.Max(0, Math.Min(100, 2 * (r.Level + 100))), // -100..-50 dBm → 0..100
                            Segura = !string.IsNullOrEmpty(r.Capabilities) &&
                                     (r.Capabilities.Contains("WPA") || r.Capabilities.Contains("WEP") || r.Capabilities.Contains("SAE")),
                            Conectada = actual.Conectado && ssid == actual.Ssid,
                            Guardada = guardadas.Contains(ssid),
                        });
                    }
                }
            }
            catch (Exception ex) { Android.Util.Log.Warn("PilotX", "Wifi escanear: " + ex.Message); }

            // Sin permiso de ubicación el scan viene vacío: al menos la actual.
            if (list.Count == 0 && actual.Conectado)
                list.Add(new WifiRedInfo { Ssid = actual.Ssid, SenalPct = 100, Segura = true, Conectada = true, Guardada = true });

            list.Sort((a, b) => b.SenalPct.CompareTo(a.SenalPct));
            return list;
        }

        public bool Conectar(string ssid, string clave, out string error)
        {
            error = "";
            ssid = Limpiar(ssid);
            if (ssid.Length == 0) { error = "SSID vacío"; return false; }
            var w = Wifi;
            if (w == null) { error = "Sin WiFi"; return false; }
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
                {
                    var cfg = new WifiConfiguration { Ssid = "\"" + ssid + "\"" };
                    if (string.IsNullOrEmpty(clave))
                        cfg.AllowedKeyManagement?.Set((int)KeyManagementType.None);
                    else
                        cfg.PreSharedKey = "\"" + clave + "\"";
                    int id = w.AddNetwork(cfg);
                    if (id < 0) { error = "No se pudo agregar la red"; return false; }
                    w.Disconnect();
                    bool ok = w.EnableNetwork(id, true);
                    w.Reconnect();
                    if (!ok) error = "No se pudo habilitar la red";
                    return ok;
                }

                // API 29+: sugerencia (el sistema pide confirmación una vez) +
                // panel del sistema como respaldo visible.
                var b = new WifiNetworkSuggestion.Builder().SetSsid(ssid);
                if (!string.IsNullOrEmpty(clave)) b = b.SetWpa2Passphrase(clave);
                var sug = b.Build();
                var status = w.AddNetworkSuggestions(new List<WifiNetworkSuggestion> { sug });
                if (status != NetworkStatus.SuggestionsSuccess && status != NetworkStatus.SuggestionsErrorAddDuplicate)
                {
                    error = "Sugerencia rechazada (" + status + ")";
                    return false;
                }
                try
                {
                    var i = new Intent(Android.Provider.Settings.ActionWifiSettings);
                    i.AddFlags(ActivityFlags.NewTask);
                    _ctx.StartActivity(i);
                }
                catch { /* best-effort */ }
                error = "En Android 10+ confirmá la red en el panel que se abrió";
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public bool Desconectar(out string error)
        {
            error = "";
            try { return Wifi?.Disconnect() ?? false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public bool Olvidar(string ssid, out string error)
        {
            error = "";
            ssid = Limpiar(ssid);
            var w = Wifi;
            if (w == null) { error = "Sin WiFi"; return false; }
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    // Sólo se pueden quitar las sugerencias propias.
                    var sug = new WifiNetworkSuggestion.Builder().SetSsid(ssid).Build();
                    w.RemoveNetworkSuggestions(new List<WifiNetworkSuggestion> { sug });
                    error = "Para olvidar una red del sistema usá el panel de WiFi de Android";
                    return true;
                }
                var cfgs = w.ConfiguredNetworks;
                if (cfgs == null) return false;
                bool alguna = false;
                foreach (var c in cfgs)
                    if (Limpiar(c.Ssid) == ssid) { w.RemoveNetwork(c.NetworkId); alguna = true; }
                if (alguna) w.SaveConfiguration();
                return alguna;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
