// ============================================================================
// SistemaController.cs
// Endpoints REST del módulo Sistema:
//   GET  /api/sistema/brillo                  → { value: 0..100, ok: bool }
//   POST /api/sistema/brillo?value=N          → { ok: bool, value: N }
//   POST /api/sistema/power?action=shutdown|restart|logoff|suspend|exitApp
//                                             → { ok: bool }
//   GET  /api/sistema/pwa-info                → { url, mdnsUrl, ips, qrPngBase64, port }
//        Devuelve la URL recomendada para que el operario abra la PWA Field
//        desde su celular en la LAN, mas un PNG QR escaneable. Usado por
//        /m/qr.html (pagina que el Hub muestra en la pantalla del tractor).
// Si no se inyectó ISistemaService, responde 503 desde cada endpoint.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgroParallel.Services.Abstractions;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using QRCoder;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class SistemaController : WebApiController
    {
        private readonly ISistemaService _sistema;
        private readonly int _port;

        public SistemaController(ISistemaService sistema, int port = 5180)
        {
            _sistema = sistema;
            _port = port;
        }

        [Route(HttpVerbs.Get, "/sistema/brillo")]
        public object GetBrillo()
        {
            if (_sistema == null)
                return new { ok = false, value = -1, error = "service-unavailable" };
            int v = _sistema.GetBrightness();
            return new { ok = v >= 0, value = v };
        }

        [Route(HttpVerbs.Post, "/sistema/brillo")]
        public object SetBrillo([QueryField] int value)
        {
            if (_sistema == null)
                return new { ok = false, error = "service-unavailable" };
            bool ok = _sistema.SetBrightness(value);
            return new { ok, value };
        }

        [Route(HttpVerbs.Post, "/sistema/power")]
        public object Power([QueryField] string action)
        {
            if (_sistema == null)
                return new { ok = false, error = "service-unavailable" };
            PowerAction pa;
            switch ((action ?? "").ToLowerInvariant())
            {
                case "shutdown": pa = PowerAction.Shutdown; break;
                case "restart": pa = PowerAction.Restart; break;
                case "logoff": pa = PowerAction.LogOff; break;
                case "suspend": pa = PowerAction.Suspend; break;
                case "exitapp": pa = PowerAction.ExitApp; break;
                default: return new { ok = false, error = "invalid-action" };
            }
            _sistema.ExecutePowerAction(pa);
            return new { ok = true };
        }

        // PWA Field — devuelve URL recomendada + QR para que el operario escanee
        // desde el celular. La pagina /m/qr.html del Hub consume este endpoint
        // y lo muestra grande en la pantalla 11" del tractor.
        [Route(HttpVerbs.Get, "/sistema/pwa-info")]
        public object PwaInfo()
        {
            // IPs LAN candidatas (IPv4, UP, no loopback, no link-local).
            var ipList = new List<string>();
            try { ipList = MdnsResponder.GetLanIPv4().Select(i => i.ToString()).ToList(); }
            catch { /* sin red util: ipList queda vacio y devolvemos solo mdnsUrl */ }

            // Heuristica de mejor IP: priorizamos 192.168.5.* (LAN tipica del tractor),
            // despues cualquier 192.168.*, despues 10.*, y por ultimo la primera.
            string best = ipList.FirstOrDefault(i => i.StartsWith("192.168.5."))
                       ?? ipList.FirstOrDefault(i => i.StartsWith("192.168."))
                       ?? ipList.FirstOrDefault(i => i.StartsWith("10."))
                       ?? ipList.FirstOrDefault();

            string url = best != null ? "http://" + best + ":" + _port + "/m/" : null;
            string mdnsUrl = "http://agroparallel.local:" + _port + "/m/";

            // QR del URL preferido (IP), no del .local — porque hay celulares Android
            // que no resuelven .local sin la app de bonjour instalada. La IP siempre
            // funciona en cualquier sistema.
            string qr = null;
            string textForQr = url ?? mdnsUrl;
            try
            {
                using (var gen = new QRCodeGenerator())
                using (var data = gen.CreateQrCode(textForQr, QRCodeGenerator.ECCLevel.M))
                using (var png = new PngByteQRCode(data))
                {
                    byte[] bytes = png.GetGraphic(10);
                    qr = "data:image/png;base64," + Convert.ToBase64String(bytes);
                }
            }
            catch { qr = null; }

            return new
            {
                ok = true,
                url,            // ej http://192.168.5.10:5180/m/
                mdnsUrl,        // ej http://agroparallel.local:5180/m/
                ips = ipList,
                port = _port,
                qrPngBase64 = qr
            };
        }
    }
}
