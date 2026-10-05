// ============================================================================
// ReporteFallaSanitizador.cs — que el reporte de falla no se lleve secretos.
//
// El ZIP del reporte sale del equipo (a OrbitX o a un pendrive que anda de
// mano en mano). Nada de lo que hay acá adentro puede servir para entrar a
// ningún lado:
//
//   · JSON de config: toda clave con nombre de secreto (token, password,
//     clave, api key…) con valor TEXTO se reemplaza por «oculto». Recorre
//     objetos y listas anidados (el NTRIP vive dentro de "ntrip": {...}).
//   · XML del perfil de vehículo (formato AOG <setting name=..><value>):
//     mismo criterio (ahí vive AgShareApiKey).
//   · Credenciales dentro de URLs (rtsp://usuario:clave@ip) en cualquier lado.
//   · Logs y textos: el patrón clave:valor de AccionesSoporte.Sanitizar, el
//     token del equipo, y además CADA secreto que se encontró en la config,
//     esté donde esté. Por eso las configs se procesan ANTES que los logs: si
//     un log repite la contraseña del NTRIP (pasa), también sale tapada.
//
// Lo que NO es secreto se deja, aunque se parezca: "keya" (el motor Keya),
// "setTram_passes", "start_pass" con valor numérico. Un número nunca se tapa.
//
// Idea de redacción tomada de AgOpenWeb (DebugDumpService.RedactSecrets,
// © AgOpenWeb Contributors): allá es plano y por nombre; acá es recursivo,
// cubre XML/URLs y propaga los valores encontrados a los logs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgroParallel.Soporte
{
    public static class ReporteFallaSanitizador
    {
        public const string Oculto = "«oculto»";

        // Secretos más cortos que esto no se reemplazan "por valor" en los
        // logs: tapar cada "1234" o "true" del archivo lo volvería ilegible.
        // En la config se tapan igual (por nombre de clave).
        private const int LargoMinimoPorValor = 4;

        private static readonly string[] Contiene =
        {
            "password", "passwd", "contrasena", "contraseña", "token", "secret",
            "apikey", "api_key", "privatekey", "private_key", "clave", "pwd",
            "psk", "credential",
        };

        private static readonly Regex UrlConCredenciales = new Regex(
            @"(?<pre>[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s:/@""'<>]+:)(?<clave>[^\s@/""'<>]+)(?<arroba>@)",
            RegexOptions.Compiled);

        private static readonly Regex SettingXml = new Regex(
            @"(?<pre><setting\s+name=""(?<n>[^""]+)""[^>]*>\s*<value>)(?<v>[^<]*)(?<post></value>)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ElementoXml = new Regex(
            @"(?<pre><(?<n>[A-Za-z_][\w.\-]*)(\s[^>]*)?>)(?<v>[^<]+)(?<post></\k<n>>)",
            RegexOptions.Compiled);

        /// <summary>¿El nombre de esta clave/propiedad suena a secreto?</summary>
        public static bool EsClaveSecreta(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return false;
            string k = nombre.ToLowerInvariant().Replace('-', '_');
            foreach (var c in Contiene)
                if (k.Contains(c)) return true;
            // "pass" suelto o al final ("ntripPass", "wifi_pass"), pero no
            // "passes" (pasadas de tram) ni "bypass".
            if (k == "pass") return true;
            if (k.EndsWith("pass") && !k.EndsWith("bypass")) return true;
            return false;
        }

        /// <summary>Tapa secretos en un JSON. Si no parsea, cae al patrón de
        /// texto (nunca lo devuelve crudo). Agrega a <paramref name="secretos"/>
        /// cada valor tapado, para buscarlo después en los logs.</summary>
        public static string SanitizarJson(string json, ICollection<string> secretos)
        {
            if (string.IsNullOrEmpty(json)) return json ?? "";
            JsonNode raiz;
            try { raiz = JsonNode.Parse(json); }
            catch (Exception) { return SanitizarTexto(json, secretos, null); }
            if (raiz == null) return json;

            raiz = Recorrer(raiz, null, secretos);
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            return raiz.ToJsonString(opts);
        }

        private static JsonNode Recorrer(JsonNode nodo, string clave, ICollection<string> secretos)
        {
            var obj = nodo as JsonObject;
            if (obj != null)
            {
                foreach (var k in obj.Select(kv => kv.Key).ToList())
                {
                    var hijo = obj[k];
                    if (hijo == null) continue;
                    var nuevo = Recorrer(hijo, k, secretos);
                    if (!ReferenceEquals(nuevo, hijo)) obj[k] = nuevo;
                }
                return obj;
            }

            var arr = nodo as JsonArray;
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var hijo = arr[i];
                    if (hijo == null) continue;
                    var nuevo = Recorrer(hijo, clave, secretos);
                    if (!ReferenceEquals(nuevo, hijo)) arr[i] = nuevo;
                }
                return arr;
            }

            var val = nodo as JsonValue;
            string s;
            if (val == null || !val.TryGetValue(out s) || string.IsNullOrEmpty(s)) return nodo;

            if (EsClaveSecreta(clave))
            {
                Juntar(secretos, s);
                return JsonValue.Create(Oculto);
            }

            string limpio = TaparUrls(s, secretos);
            return limpio == s ? nodo : JsonValue.Create(limpio);
        }

        /// <summary>Tapa secretos en un XML de settings (perfil de vehículo) y
        /// en elementos simples con nombre de secreto.</summary>
        public static string SanitizarXml(string xml, ICollection<string> secretos)
        {
            if (string.IsNullOrEmpty(xml)) return xml ?? "";
            string s = SettingXml.Replace(xml, m => Tapar(m, secretos));
            s = ElementoXml.Replace(s, m => Tapar(m, secretos));
            return TaparUrls(s, secretos);
        }

        private static string Tapar(Match m, ICollection<string> secretos)
        {
            string v = m.Groups["v"].Value;
            if (!EsClaveSecreta(m.Groups["n"].Value) || string.IsNullOrWhiteSpace(v) || v == Oculto)
                return m.Value;
            Juntar(secretos, System.Net.WebUtility.HtmlDecode(v));
            Juntar(secretos, v);
            return m.Groups["pre"].Value + Oculto + m.Groups["post"].Value;
        }

        /// <summary>Tapa secretos en texto libre (logs): URLs con credenciales,
        /// patrón clave:valor, el token del equipo y cada secreto conocido.</summary>
        public static string SanitizarTexto(string texto, IEnumerable<string> secretos, string tokenEquipo)
        {
            if (string.IsNullOrEmpty(texto)) return texto ?? "";
            var junta = new List<string>();
            string s = TaparUrls(texto, junta);
            s = AccionesSoporte.Sanitizar(s, tokenEquipo);

            var todos = new List<string>(junta);
            if (secretos != null) todos.AddRange(secretos);
            if (!string.IsNullOrEmpty(tokenEquipo)) todos.Add(tokenEquipo);
            foreach (var sec in todos.Where(x => x != null && x.Length >= LargoMinimoPorValor && x != Oculto)
                                     .Distinct().OrderByDescending(x => x.Length))
                s = s.Replace(sec, Oculto);
            return s;
        }

        private static string TaparUrls(string s, ICollection<string> secretos)
        {
            return UrlConCredenciales.Replace(s, m =>
            {
                string clave = m.Groups["clave"].Value;
                if (clave == Oculto) return m.Value;
                Juntar(secretos, clave);
                return m.Groups["pre"].Value + Oculto + m.Groups["arroba"].Value;
            });
        }

        private static void Juntar(ICollection<string> secretos, string valor)
        {
            if (secretos == null || string.IsNullOrEmpty(valor) || valor == Oculto) return;
            if (!secretos.Contains(valor)) secretos.Add(valor);
        }
    }
}
