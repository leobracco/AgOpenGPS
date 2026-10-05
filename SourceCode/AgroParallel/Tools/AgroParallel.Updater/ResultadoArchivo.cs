// ============================================================================
// ResultadoArchivo.cs — el veredicto del Updater, para que PilotX lo lea.
//
// Archivo: <install>\AgroParallel\Updates\updater-resultado.json
//
// Antes PilotX lanzaba el Updater y se cerraba 1,5 s después sin saber qué
// pasaba. Si el Updater abortaba (parche rechazado, return 3), PilotX ya se
// había ido, el vigilante lo relanzaba en la MISMA versión y la pantalla
// quedaba en "Aplicando" — caso Francisco Barbero: 8 intentos, ninguno
// aplicó, nadie veía por qué. Ahora el Updater escribe acá en qué anda:
//
//   validando  -> arrancó, está mirando el paquete (PilotX espera)
//   aplicando  -> el paquete sirve; espera que PilotX cierre y lo instala
//   rechazado  -> no se tocó nada (motivo); PilotX NO se cierra
//   ok / fallo -> resultado final (lo lee PilotX al volver a arrancar)
//
// JSON chato escrito a mano: net48 no trae System.Text.Json. Lo lee
// AgroParallel.OrbitX.VeredictoUpdater (este archivo se compila linkeado en
// AgroParallel.Services.Tests para probar que los dos lados se entienden).
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AgroParallel.Updater
{
    internal static class ResultadoArchivo
    {
        public const string NombreArchivo = "updater-resultado.json";

        public static string Ruta(string install)
        {
            return Path.Combine(install, "AgroParallel", "Updates", NombreArchivo);
        }

        public static string Armar(string estado, string motivo, int codigo, string zip)
        {
            var sb = new StringBuilder();
            sb.Append("{\"estado\":\"").Append(Escapar(estado)).Append('"');
            sb.Append(",\"motivo\":\"").Append(Escapar(motivo)).Append('"');
            sb.Append(",\"codigo\":").Append(codigo.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"zip\":\"").Append(Escapar(zip)).Append('"');
            sb.Append(",\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Escribe el veredicto (atómico: .tmp + reemplazo). Nunca tira:
        /// si no se puede escribir, el Updater sigue igual que antes.</summary>
        public static void Escribir(string install, string estado, string motivo, int codigo, string zip, Action<string> log)
        {
            try
            {
                if (string.IsNullOrEmpty(install)) return;
                string ruta = Ruta(install);
                Directory.CreateDirectory(Path.GetDirectoryName(ruta));
                string tmp = ruta + ".tmp";
                File.WriteAllText(tmp, Armar(estado, motivo, codigo, zip), new UTF8Encoding(false));
                if (File.Exists(ruta)) File.Delete(ruta);
                File.Move(tmp, ruta);
                if (log != null) log("Veredicto: " + estado + (string.IsNullOrEmpty(motivo) ? "" : " (" + motivo + ")"));
            }
            catch (Exception ex)
            {
                if (log != null) log("No pude escribir el veredicto (" + estado + "): " + ex.Message);
            }
        }

        private static string Escapar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
