// ============================================================================
// HikvisionIsapi.cs
//
// Cliente minimal de la API ISAPI de Hikvision. Solo lo que necesitamos:
//   - GET /ISAPI/System/deviceInfo  →  marca/modelo/firmware/serial
//
// La política del producto es "solo aceptamos cámaras Hikvision" — este
// probe es el que valida marca antes de empujar el RTSP al cloud. Si
// devuelve IsHikvision=false (no responde con el namespace de Hikvision,
// 401, timeout, etc.), CamarasRemoteRelay deja la cámara como inactiva
// en el reporte al cloud y OrbitX la pinta en gris con
// motivo_inactiva="marca_no_soportada".
//
// Auth: Hikvision usa Digest por default desde firmware moderno; las
// builds viejas aún aceptan Basic. HttpClientHandler con
// PreAuthenticate=true + Credentials cubre ambos casos (intenta primero,
// si recibe 401 negocia Digest).
// ============================================================================

using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgroParallel.Camaras
{
    public class IsapiDeviceInfo
    {
        public bool IsHikvision;
        public string Marca = "";       // "hikvision" si Ok, "" si no detectada
        public string Modelo = "";
        public string Firmware = "";
        public string Serial = "";
        public string Error = "";       // motivo de fallo si IsHikvision=false
    }

    public static class HikvisionIsapi
    {
        // El namespace XML de Hikvision es la firma más confiable (un dispositivo
        // de otra marca puede emitir un <DeviceInfo> con campos parecidos pero
        // no usar esta URI). Lo chequeamos como prueba positiva de marca.
        private const string HIK_XMLNS = "http://www.hikvision.com/ver";

        public static async Task<IsapiDeviceInfo> ProbeDeviceInfoAsync(
            string host, int port, string user, string pass, TimeSpan timeout, CancellationToken ct)
        {
            var info = new IsapiDeviceInfo();

            if (string.IsNullOrEmpty(host))
            {
                info.Error = "host vacío";
                return info;
            }

            int p = port > 0 ? port : 80;
            string url = "http://" + host + (p == 80 ? "" : (":" + p)) + "/ISAPI/System/deviceInfo";

            var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(user ?? "", pass ?? ""),
                PreAuthenticate = true,
                AllowAutoRedirect = false,
            };
            // Algunas builds rotas de Hikvision presentan cert HTTPS auto-firmado;
            // este probe va por HTTP plain para no negociar TLS contra la cámara.
            // Si en el futuro hay que ir HTTPS, agregar ServerCertificateCustomValidationCallback.

            using (handler)
            using (var http = new HttpClient(handler) { Timeout = timeout })
            {
                try
                {
                    using (var resp = await http.GetAsync(url, ct))
                    {
                        if (resp.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            info.Error = "401 credenciales inválidas";
                            return info;
                        }
                        if (!resp.IsSuccessStatusCode)
                        {
                            info.Error = "HTTP " + (int)resp.StatusCode;
                            return info;
                        }
                        string xml = await resp.Content.ReadAsStringAsync();
                        ParseDeviceInfo(xml, info);
                        return info;
                    }
                }
                catch (TaskCanceledException)
                {
                    info.Error = "timeout";
                    return info;
                }
                catch (HttpRequestException ex)
                {
                    info.Error = "net: " + ex.Message;
                    return info;
                }
                catch (Exception ex)
                {
                    info.Error = ex.GetType().Name + ": " + ex.Message;
                    return info;
                }
            }
        }

        // Parser regex — los XMLs ISAPI son chiquitos y planos; meter
        // System.Xml/XDocument por 4 campos era overkill y agregaba un
        // alloc innecesario en el hot-path de re-probe. Si en el futuro
        // necesitamos consumir respuestas anidadas, migrar a XDocument.
        private static readonly Regex RX_MODEL    = new Regex(@"<model[^>]*>([^<]+)</model>",            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RX_FIRMWARE = new Regex(@"<firmwareVersion[^>]*>([^<]+)</firmwareVersion>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RX_SERIAL   = new Regex(@"<serialNumber[^>]*>([^<]+)</serialNumber>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RX_BRAND    = new Regex(@"<deviceType[^>]*>([^<]+)</deviceType>",  RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void ParseDeviceInfo(string xml, IsapiDeviceInfo info)
        {
            if (string.IsNullOrEmpty(xml)) { info.Error = "respuesta vacía"; return; }

            // Firma positiva: namespace de Hikvision en el XML.
            bool nsOk = xml.IndexOf(HIK_XMLNS, StringComparison.OrdinalIgnoreCase) >= 0;

            var m = RX_MODEL.Match(xml);
            if (m.Success) info.Modelo = m.Groups[1].Value.Trim();
            m = RX_FIRMWARE.Match(xml);
            if (m.Success) info.Firmware = m.Groups[1].Value.Trim();
            m = RX_SERIAL.Match(xml);
            if (m.Success) info.Serial = m.Groups[1].Value.Trim();

            if (nsOk)
            {
                info.IsHikvision = true;
                info.Marca = "hikvision";
            }
            else
            {
                info.IsHikvision = false;
                info.Marca = "";
                // Si pudimos sacar deviceType, lo logueamos para diagnóstico — útil
                // cuando el cliente instaló una Dahua/Uniview por error.
                m = RX_BRAND.Match(xml);
                info.Error = "no es Hikvision (deviceType=" + (m.Success ? m.Groups[1].Value : "?") + ")";
            }
        }
    }
}
