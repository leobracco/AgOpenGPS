// ============================================================================
// IdiomaService.cs — idioma de la interfaz (castellano / inglés / portugués).
//
// PilotX está ESCRITO en castellano: los textos viven tal cual en el XAML y en
// el HTML. Traducir no cambia esos textos — se traducen al vuelo contra un
// diccionario cuya CLAVE es el propio texto en castellano:
//
//     "Cabecera": { "en": "Headland", "pt": "Cabeceira" }
//
// Eso tiene tres consecuencias buscadas:
//   · Falta una traducción -> se ve en castellano. Nunca una clave cruda
//     ("lbl_headland_42") ni una pantalla rota.
//   · Una traducción mal elegida se corrige EDITANDO EL JSON en la cabina,
//     sin recompilar ni redeployar.
//   · No hubo que tocar 50 archivos XAML/HTML para reemplazar texto por
//     claves, que es donde se rompen las pantallas.
//
// El diccionario vive en wwwroot/idiomas.json (una sola fuente para la
// pantalla nativa y para las páginas del Hub). Acá solo se guarda CUÁL idioma
// eligió el operario; el archivo es idioma.json en el directorio del exe,
// mismo criterio que overlayPrefs.json.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.Services
{
    /// <summary>Idioma elegido para la interfaz.</summary>
    public sealed class IdiomaPrefs
    {
        // "es" | "en" | "pt". El castellano es el idioma en que está escrita
        // la UI, así que es el default y el fallback de todo lo que falte.
        [JsonPropertyName("idioma")] public string Idioma { get; set; } = "es";
    }

    public sealed class IdiomaService
    {
        public static readonly IdiomaService Instance = new IdiomaService();

        private const string FileName = "idioma.json";
        private readonly object _lock = new object();
        private IdiomaPrefs _cache;

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        // Documentos\AgOpenGPS y NO el directorio del exe: cada actualización
        // de PilotX borra y rehace la carpeta del programa, así que guardarlo
        // al lado del .exe significaba que un cliente en Brasil volvía a ver
        // la pantalla en castellano después de cada update. Es el mismo lugar
        // donde ya viven camaras.json y los lotes.
        private static string Ruta
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgOpenGPS");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, FileName);
            }
        }

        /// <summary>Los tres idiomas soportados. Cualquier otro cae a "es".</summary>
        public static bool EsValido(string codigo) =>
            codigo == "es" || codigo == "en" || codigo == "pt";

        /// <summary>Código de idioma actual ("es" si nunca se eligió o el archivo está roto).</summary>
        public string Actual()
        {
            lock (_lock)
            {
                if (_cache != null) return _cache.Idioma;
                try
                {
                    string p = Ruta;
                    if (File.Exists(p))
                    {
                        var leido = JsonSerializer.Deserialize<IdiomaPrefs>(File.ReadAllText(p), ReadOpts);
                        if (leido != null && EsValido(leido.Idioma))
                        {
                            _cache = leido;
                            return _cache.Idioma;
                        }
                    }
                }
                catch { /* archivo corrupto o sin permisos: se sigue en castellano */ }

                _cache = new IdiomaPrefs();
                return _cache.Idioma;
            }
        }

        /// <summary>
        /// Guarda el idioma. Devuelve el que quedó efectivamente aplicado, que
        /// puede no ser el pedido si vino un código que no soportamos.
        /// </summary>
        public string Guardar(string codigo)
        {
            string quiere = (codigo ?? "").Trim().ToLowerInvariant();
            if (!EsValido(quiere)) quiere = "es";

            lock (_lock)
            {
                _cache = new IdiomaPrefs { Idioma = quiere };
                try { File.WriteAllText(Ruta, JsonSerializer.Serialize(_cache, WriteOpts)); }
                catch { /* que no se persista no puede impedir el cambio en pantalla */ }
                return quiere;
            }
        }
    }
}
