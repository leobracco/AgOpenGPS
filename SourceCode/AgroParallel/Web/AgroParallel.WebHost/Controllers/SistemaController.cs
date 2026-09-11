// ============================================================================
// SistemaController.cs
// Endpoints REST del módulo Sistema:
//   GET  /api/sistema/brillo                  → { ok, value: 0..100 }
//   POST /api/sistema/brillo?value=N          → { ok, value: N }
//   POST /api/sistema/power?action=shutdown|restart|logoff|suspend|exitApp
//                                             → { ok }
//   GET  /api/sistema/pwa-info                → { ok, url, mdns_url, ips, port,
//                                                 qr_png_base64, candidatas[] }
//        Devuelve la URL recomendada para que el operario abra la PWA Field
//        desde su celular en la LAN, mas un PNG QR escaneable. Usado por
//        /pages/pwa-qr.html (pagina que el Hub muestra en la pantalla del tractor).
// Si no se inyectó ISistemaService, responde 503 desde cada endpoint.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using QRCoder;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SistemaController : AgpControllerBase
    {
        private readonly ISistemaService _sistema;
        private readonly int _port;

        public SistemaController(ISistemaService sistema, int port = 5180)
        {
            _sistema = sistema;
            _port = port;
        }

        [Route(HttpVerbs.Get, "/sistema/brillo")]
        public Task GetBrillo()
        {
            if (_sistema == null)
                return WriteJsonAsync(new { ok = false, value = -1, error = "service-unavailable" });
            int v = _sistema.GetBrightness();
            return WriteJsonAsync(new { ok = v >= 0, value = v });
        }

        [Route(HttpVerbs.Post, "/sistema/brillo")]
        public Task SetBrillo([QueryField] int value)
        {
            if (_sistema == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            bool ok = _sistema.SetBrightness(value);
            return WriteJsonAsync(new { ok, value });
        }

        [Route(HttpVerbs.Post, "/sistema/power")]
        public Task Power([QueryField] string action)
        {
            if (_sistema == null)
                return WriteJsonAsync(new { ok = false, error = "service-unavailable" });
            PowerAction pa;
            switch ((action ?? "").ToLowerInvariant())
            {
                case "shutdown": pa = PowerAction.Shutdown; break;
                case "restart": pa = PowerAction.Restart; break;
                case "logoff": pa = PowerAction.LogOff; break;
                case "suspend": pa = PowerAction.Suspend; break;
                case "exitapp": pa = PowerAction.ExitApp; break;
                default: return WriteJsonAsync(new { ok = false, error = "invalid-action" });
            }
            _sistema.ExecutePowerAction(pa);
            return WriteJsonAsync(new { ok = true });
        }

        // PWA Field — devuelve URL recomendada + QR para que el operario escanee
        // desde el celular. La pagina /pages/pwa-qr.html del Hub consume este endpoint
        // y lo muestra grande en la pantalla 11" del tractor.
        // Wire format: qr_png_base64, mdns_url (snake_case via AgpJson).
        [Route(HttpVerbs.Get, "/sistema/pwa-info")]
        public Task PwaInfo()
        {
            // IPs LAN candidatas. GetLanIPv4() ya filtra loopback, link-local y
            // adaptadores VIRTUALES (VirtualBox Host-Only, vEthernet/WSL, Hyper-V,
            // VPNs) — un QR con esas IPs no lleva a ningun lado — y viene ordenada:
            // NIC de la ruta por defecto primero, despues Ethernet fisica > Wi-Fi.
            var ipList = new List<string>();
            try { ipList = MdnsResponder.GetLanIPv4().Select(i => i.ToString()).ToList(); }
            catch { /* sin red util: ipList queda vacio y devolvemos solo mdns_url */ }

            // Encima de ese orden, la LAN fija de la pantalla del tractor
            // (192.168.5.*) va primera: en cabina es SIEMPRE la buena aunque no
            // tenga gateway. El resto conserva el orden de GetLanIPv4().
            ipList = ipList.OrderBy(i => i.StartsWith("192.168.5.") ? 0 : 1).ToList();

            string best = ipList.FirstOrDefault();
            string url = best != null ? "http://" + best + ":" + _port + "/m/" : null;
            string mdns_url = "http://agroparallel.local:" + _port + "/m/";

            // QR por candidata (IP), no del .local — porque hay celulares Android
            // que no resuelven .local sin la app de bonjour instalada. La IP siempre
            // funciona en cualquier sistema. Si quedan varias candidatas la UI las
            // muestra todas y el operario elige la de SU red.
            var candidatas = ipList.Take(4)
                .Select(ip =>
                {
                    string u = "http://" + ip + ":" + _port + "/m/";
                    return new { ip, url = u, qr_png_base64 = GenerarQr(u) };
                })
                .ToList();

            string qr_png_base64 = candidatas.Count > 0
                ? candidatas[0].qr_png_base64
                : GenerarQr(mdns_url);

            return WriteJsonAsync(new
            {
                ok = true,
                url,               // ej http://192.168.5.10:5180/m/ (la mejor)
                mdns_url,          // ej http://agroparallel.local:5180/m/
                ips = ipList,
                port = _port,
                qr_png_base64,     // QR de la mejor (compat con clientes viejos)
                candidatas         // [{ ip, url, qr_png_base64 }] una por red real
            });
        }

        // PNG QR en data-URI, o null si QRCoder falla (la UI muestra solo la URL).
        private static string GenerarQr(string texto)
        {
            try
            {
                using (var gen = new QRCodeGenerator())
                using (var data = gen.CreateQrCode(texto, QRCodeGenerator.ECCLevel.M))
                using (var png = new PngByteQRCode(data))
                {
                    byte[] bytes = png.GetGraphic(10);
                    return "data:image/png;base64," + Convert.ToBase64String(bytes);
                }
            }
            catch { return null; }
        }
    }
}
