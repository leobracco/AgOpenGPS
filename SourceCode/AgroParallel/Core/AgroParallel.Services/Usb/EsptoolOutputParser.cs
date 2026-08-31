using System.Text.RegularExpressions;

namespace AgroParallel.Usb
{
    // Parser puro (sin estado, sin IO) del stdout de esptool v5.x. Traduce las
    // líneas a una fase en castellano + un porcentaje 0..100 para la barra de
    // progreso de la UI, y clasifica las fallas conocidas a códigos AGP-USB.
    public static class EsptoolOutputParser
    {
        private static readonly Regex RxPct = new Regex(@"\((\d+)\s*%\)", RegexOptions.Compiled);

        public static void Parse(string linea, ref string fase, ref int pct)
        {
            if (string.IsNullOrEmpty(linea)) return;
            if (linea.Contains("Connecting")) { fase = "conectando"; }
            else if (linea.Contains("Erasing") || linea.Contains("Erase")) { fase = "borrando"; }
            else if (linea.Contains("Writing at") || linea.StartsWith("Wrote")) { fase = "escribiendo"; }
            else if (linea.Contains("Hash of data verified")) { fase = "verificando"; pct = 100; }
            else if (linea.Contains("Hard resetting") || linea.Contains("Leaving")) { fase = "reset"; pct = 100; }

            var m = RxPct.Match(linea);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int p)) pct = p;
        }

        public static string ClasificarError(string log)
        {
            if (string.IsNullOrEmpty(log)) return null;
            if (log.Contains("Access is denied") || log.Contains("Could not open") ||
                log.Contains("the port doesn't exist") || log.Contains("PermissionError"))
                return "AGP-USB-001";
            if (log.Contains("Failed to connect") || log.Contains("No serial data received") ||
                log.Contains("Timed out waiting for packet"))
                return "AGP-USB-002";
            return null;
        }
    }
}
