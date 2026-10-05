// ============================================================================
// ClienteHttp.cs — quién es el que llama a la API (para la autoridad de control).
//
// La CABINA se reconoce SOLO por loopback (127.0.0.1 / ::1): es la pantalla
// local (PilotX.Desktop, el WebView del Hub, el head Android que corre el
// mismo Engine en el equipo, el soporte remoto que entra por el motor). Un
// header jamás convierte a un equipo de la LAN en cabina.
//
// Un REMOTO se identifica por (X-PilotX-Cliente, IP). Sin header, por IP.
// X-PilotX-Nombre es lo que se muestra en el cartelito de la cabina.
// ============================================================================

using System.Net;
using System.Text;
using AgroParallel.Services.Control;
using EmbedIO;

namespace AgroParallel.WebHost.Autoridad
{
    public static class ClienteHttp
    {
        public const string HeaderCliente = "X-PilotX-Cliente";
        public const string HeaderNombre = "X-PilotX-Nombre";

        public static ClienteControl De(IHttpContext ctx)
        {
            IPAddress ip = null;
            try { ip = ctx?.Request?.RemoteEndPoint?.Address; } catch { }
            if (ip != null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

            string nombre = Limpiar(Header(ctx, HeaderNombre), 40, permitirEspacios: true);

            if (ip == null || IPAddress.IsLoopback(ip))
                return ClienteControl.Cabina(string.IsNullOrEmpty(nombre) ? null : nombre);

            string ipTxt = ip.ToString();
            string id = Limpiar(Header(ctx, HeaderCliente), 64, permitirEspacios: false);
            id = string.IsNullOrEmpty(id) ? "ip:" + ipTxt : "r:" + id;
            return ClienteControl.Remoto(id, string.IsNullOrEmpty(nombre) ? "Equipo " + ipTxt : nombre, ipTxt);
        }

        private static string Header(IHttpContext ctx, string nombre)
        {
            try { return ctx?.Request?.Headers?[nombre]; } catch { return null; }
        }

        // Sólo letras, dígitos y unos pocos signos: lo que viene del header va a
        // logs y a la pantalla de la cabina.
        private static string Limpiar(string s, int max, bool permitirEspacios)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (sb.Length >= max) break;
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || (permitirEspacios && c == ' '))
                    sb.Append(c);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}
