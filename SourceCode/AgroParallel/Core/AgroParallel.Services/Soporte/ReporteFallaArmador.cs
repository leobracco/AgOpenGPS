// ============================================================================
// ReporteFallaArmador.cs — arma el ZIP del "Reportar falla" en un toque.
//
// Por qué existe:
//   El soporte remoto era lento: pantallas de clientes que fallaban y nadie
//   podía leer los logs (Fran Barbero, Las Gringas). Con esto el operario toca
//   un botón, escribe qué pasó, y sale UN paquete con todo lo que hace falta
//   para entender la falla sin tener que dictar nada por teléfono.
//
// Qué lleva el ZIP:
//   reporte.json        manifiesto: código, fecha, versión, equipo, perfil,
//                       lote, qué se incluyó y qué se omitió (y por qué)
//   descripcion.txt     lo que escribió el operario
//   captura.png         la pantalla tal cual estaba antes de abrir el panel
//   config/…            configs (JSON/XML) SANITIZADAS — ver ReporteFallaSanitizador
//   logs/…, pantalla/…  colas de los logs (los últimos N KB), sanitizadas
//   lote/…              archivos chicos del lote abierto
//   diagnostico/…       textos generados (fuentes mudas, estado)
//
// Tope de tamaño: el server guarda el ZIP en base64 dentro de un doc de
// CouchDB, que tiene límite (8 MB por defecto). Si el primer armado se pasa
// del tope, se rearma más chico (nivel 1: logs y lote recortados; nivel 2: sin
// lote y logs mínimos). Todo lo que queda afuera se anota en "omitidos": un
// reporte que calla lo que no trae engaña a quien lo lee.
//
// Clase pura salvo lectura de los archivos que se le pasan: no sabe de rutas
// de PilotX. Quién junta las rutas es el Engine (ReporteFallaEngine).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgroParallel.Soporte
{
    public enum TipoArchivoReporte
    {
        /// <summary>Log: se toma la cola (últimos MaxBytes) y se sanitiza.</summary>
        Log,
        /// <summary>Config JSON/XML: se sanitiza por nombre de clave y aporta secretos.</summary>
        Config,
        /// <summary>Archivo del lote: entero si es chico, si no se omite.</summary>
        Lote,
        /// <summary>Texto ya generado (Contenido), se sanitiza.</summary>
        Texto,
    }

    public sealed class ArchivoReporte
    {
        public ArchivoReporte() { }

        public ArchivoReporte(string rutaZip, string rutaDisco, TipoArchivoReporte tipo)
        {
            RutaZip = rutaZip;
            RutaDisco = rutaDisco;
            Tipo = tipo;
        }

        public static ArchivoReporte DeTexto(string rutaZip, string contenido, TipoArchivoReporte tipo = TipoArchivoReporte.Texto)
        {
            return new ArchivoReporte { RutaZip = rutaZip, Contenido = contenido ?? "", Tipo = tipo };
        }

        /// <summary>Ruta dentro del ZIP (con "/").</summary>
        public string RutaZip { get; set; }
        /// <summary>Archivo en disco (o null si viene Contenido).</summary>
        public string RutaDisco { get; set; }
        /// <summary>Contenido ya cargado (alternativa a RutaDisco).</summary>
        public string Contenido { get; set; }
        public TipoArchivoReporte Tipo { get; set; }
        /// <summary>Para logs: cuánto de la cola tomar (0 = default del nivel).</summary>
        public int MaxBytes { get; set; }
    }

    public sealed class EntradaReporteFalla
    {
        public string Codigo { get; set; }
        public string Descripcion { get; set; }
        public string VersionPilotX { get; set; }
        public string DeviceId { get; set; }
        /// <summary>Token del equipo: NO viaja; se usa para taparlo donde aparezca.</summary>
        public string TokenEquipo { get; set; }
        public string PerfilActivo { get; set; }
        public string LoteNombre { get; set; }
        public DateTime Fecha { get; set; } = DateTime.Now;
        public byte[] CapturaPng { get; set; }
        public List<ArchivoReporte> Archivos { get; } = new List<ArchivoReporte>();
    }

    public sealed class ResultadoReporteFalla
    {
        public byte[] Zip { get; set; }
        public List<string> Incluidos { get; } = new List<string>();
        public List<string> Omitidos { get; } = new List<string>();
        /// <summary>0 = completo; 1 = recortado; 2 = mínimo.</summary>
        public int Nivel { get; set; }
    }

    public static class ReporteFallaArmador
    {
        /// <summary>Tope del ZIP: base64 (×4/3) + JSON tiene que entrar holgado
        /// en el límite por doc de CouchDB (8 MB por defecto).</summary>
        public const int TopeZipBytes = 4 * 1024 * 1024;

        /// <summary>Config más grande que esto no es config: se omite.</summary>
        private const int MaxConfigBytes = 512 * 1024;

        // Sin 0/O ni 1/I/L: el código se dicta por teléfono desde la cabina.
        private const string Alfabeto = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        private static readonly Regex FormatoCodigo = new Regex("^RF-[2-9A-HJKMNP-Z]{3}-[2-9A-HJKMNP-Z]{3}$", RegexOptions.Compiled);

        private sealed class Nivel
        {
            public int LogBytes;          // cola por log
            public int LoteArchivoMax;    // tope por archivo de lote
            public int LoteTotalMax;      // tope total de lote
            public int CapturaMax;        // tope de la captura
        }

        private static readonly Nivel[] Niveles =
        {
            new Nivel { LogBytes = 512 * 1024, LoteArchivoMax = 512 * 1024, LoteTotalMax = 2 * 1024 * 1024, CapturaMax = int.MaxValue },
            new Nivel { LogBytes = 128 * 1024, LoteArchivoMax = 128 * 1024, LoteTotalMax = 512 * 1024,      CapturaMax = int.MaxValue },
            new Nivel { LogBytes = 32 * 1024,  LoteArchivoMax = 0,          LoteTotalMax = 0,               CapturaMax = 1536 * 1024 },
        };

        /// <summary>Código para dictar por teléfono: "RF-K7M-4QX".</summary>
        public static string GenerarCodigo(Random rnd)
        {
            if (rnd == null) rnd = new Random();
            var sb = new StringBuilder("RF-");
            for (int i = 0; i < 6; i++)
            {
                if (i == 3) sb.Append('-');
                sb.Append(Alfabeto[rnd.Next(Alfabeto.Length)]);
            }
            return sb.ToString();
        }

        public static bool CodigoValido(string codigo)
        {
            return !string.IsNullOrEmpty(codigo) && FormatoCodigo.IsMatch(codigo);
        }

        /// <summary>Arma el ZIP. Si se pasa de <paramref name="topeBytes"/>,
        /// rearma en niveles más chicos; el último nivel se devuelve igual.</summary>
        public static ResultadoReporteFalla Armar(EntradaReporteFalla e, int topeBytes = TopeZipBytes)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            ResultadoReporteFalla r = null;
            for (int n = 0; n < Niveles.Length; n++)
            {
                r = ArmarNivel(e, n, Niveles[n]);
                if (r.Zip.Length <= topeBytes) return r;
            }
            return r;
        }

        private static ResultadoReporteFalla ArmarNivel(EntradaReporteFalla e, int nivel, Nivel cfg)
        {
            var r = new ResultadoReporteFalla { Nivel = nivel };
            var secretos = new HashSet<string>(StringComparer.Ordinal);
            var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entradas = new List<KeyValuePair<string, byte[]>>();
            int loteTotal = 0;

            // Configs PRIMERO: juntan los secretos que después se tapan en logs.
            var orden = e.Archivos.Where(a => a != null && !string.IsNullOrEmpty(a.RutaZip))
                                  .OrderBy(a => a.Tipo == TipoArchivoReporte.Config ? 0 : 1)
                                  .ToList();
            foreach (var a in orden)
            {
                string ruta = NormalizarRutaZip(a.RutaZip);
                if (ruta == null || !usados.Add(ruta))
                {
                    r.Omitidos.Add(a.RutaZip + " (nombre inválido o repetido)");
                    continue;
                }
                try
                {
                    switch (a.Tipo)
                    {
                        case TipoArchivoReporte.Config:
                        {
                            string txt = LeerTextoEntero(a, MaxConfigBytes, r, ruta);
                            if (txt == null) continue;
                            string ext = Path.GetExtension(ruta).ToLowerInvariant();
                            if (ext == ".json") txt = ReporteFallaSanitizador.SanitizarJson(txt, secretos);
                            else if (ext == ".xml") txt = ReporteFallaSanitizador.SanitizarXml(txt, secretos);
                            txt = ReporteFallaSanitizador.SanitizarTexto(txt, secretos, e.TokenEquipo);
                            entradas.Add(Par(ruta, txt));
                            break;
                        }
                        case TipoArchivoReporte.Log:
                        {
                            int max = a.MaxBytes > 0 ? Math.Min(a.MaxBytes, cfg.LogBytes) : cfg.LogBytes;
                            string txt = a.Contenido != null ? Cola(a.Contenido, max) : LeerCola(a.RutaDisco, max, r, ruta);
                            if (txt == null) continue;
                            entradas.Add(Par(ruta, ReporteFallaSanitizador.SanitizarTexto(txt, secretos, e.TokenEquipo)));
                            break;
                        }
                        case TipoArchivoReporte.Texto:
                        {
                            string txt = a.Contenido;
                            if (txt == null) txt = LeerTextoEntero(a, cfg.LogBytes, r, ruta);
                            if (txt == null) continue;
                            if (a.MaxBytes > 0) txt = Cola(txt, a.MaxBytes);
                            entradas.Add(Par(ruta, ReporteFallaSanitizador.SanitizarTexto(txt, secretos, e.TokenEquipo)));
                            break;
                        }
                        case TipoArchivoReporte.Lote:
                        {
                            byte[] datos = LeerLote(a, cfg, loteTotal, r, ruta);
                            if (datos == null) continue;
                            loteTotal += datos.Length;
                            if (EsTextoLote(ruta))
                            {
                                string txt = Encoding.UTF8.GetString(datos);
                                string limpio = ReporteFallaSanitizador.SanitizarTexto(txt, secretos, e.TokenEquipo);
                                if (!ReferenceEquals(limpio, txt) && limpio != txt) datos = Utf8(limpio);
                            }
                            entradas.Add(new KeyValuePair<string, byte[]>(ruta, datos));
                            break;
                        }
                    }
                    r.Incluidos.Add(ruta);
                }
                catch (Exception ex)
                {
                    r.Omitidos.Add(ruta + " (no se pudo leer: " + ex.Message + ")");
                }
            }

            bool conCaptura = e.CapturaPng != null && e.CapturaPng.Length > 0;
            if (conCaptura && e.CapturaPng.Length > cfg.CapturaMax)
            {
                r.Omitidos.Add("captura.png (" + Kb(e.CapturaPng.Length) + ", omitida por tamaño)");
                conCaptura = false;
            }
            else if (!conCaptura)
            {
                r.Omitidos.Add("captura.png (no se pudo tomar la captura)");
            }

            string descripcion = string.IsNullOrWhiteSpace(e.Descripcion) ? "(el operario no escribió nada)" : e.Descripcion.Trim();

            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Escribir(zip, "reporte.json", Utf8(Manifiesto(e, r, conCaptura, descripcion)), CompressionLevel.Optimal);
                    Escribir(zip, "descripcion.txt", Utf8(descripcion + Environment.NewLine), CompressionLevel.Optimal);
                    // PNG ya viene comprimido: guardarlo tal cual es más rápido.
                    if (conCaptura) Escribir(zip, "captura.png", e.CapturaPng, CompressionLevel.NoCompression);
                    foreach (var kv in entradas)
                        Escribir(zip, kv.Key, kv.Value, CompressionLevel.Optimal);
                }
                r.Zip = ms.ToArray();
            }
            return r;
        }

        private static string Manifiesto(EntradaReporteFalla e, ResultadoReporteFalla r, bool conCaptura, string descripcion)
        {
            var incluidos = new List<string> { "reporte.json", "descripcion.txt" };
            if (conCaptura) incluidos.Add("captura.png");
            incluidos.AddRange(r.Incluidos);

            var m = new Dictionary<string, object>
            {
                { "codigo", e.Codigo ?? "" },
                { "fecha", e.Fecha.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) },
                { "fecha_utc", e.Fecha.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                { "version_pilotx", e.VersionPilotX ?? "" },
                { "device_id", e.DeviceId ?? "" },
                { "equipo", SafeMachineName() },
                { "perfil_activo", e.PerfilActivo ?? "" },
                { "lote_abierto", e.LoteNombre ?? "" },
                { "descripcion", descripcion },
                { "nivel", r.Nivel },
                { "nivel_texto", r.Nivel == 0 ? "completo" : r.Nivel == 1 ? "recortado por tamaño" : "mínimo por tamaño" },
                { "incluidos", incluidos },
                { "omitidos", r.Omitidos },
                { "nota", "Configs y logs sanitizados: tokens, contraseñas y claves reemplazados por " + ReporteFallaSanitizador.Oculto + "." },
            };
            return JsonSerializer.Serialize(m, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }

        private static string SafeMachineName()
        {
            try { return Environment.MachineName; } catch { return ""; }
        }

        // ── lectura ─────────────────────────────────────────────────────────

        private static string LeerTextoEntero(ArchivoReporte a, int max, ResultadoReporteFalla r, string ruta)
        {
            if (a.Contenido != null) return a.Contenido;
            if (string.IsNullOrEmpty(a.RutaDisco) || !File.Exists(a.RutaDisco))
            {
                r.Omitidos.Add(ruta + " (no existe)");
                return null;
            }
            var fi = new FileInfo(a.RutaDisco);
            if (fi.Length > max)
            {
                r.Omitidos.Add(ruta + " (" + Kb(fi.Length) + ", omitido por tamaño)");
                return null;
            }
            return Encoding.UTF8.GetString(LeerCompartido(a.RutaDisco, 0, (int)fi.Length));
        }

        private static string LeerCola(string rutaDisco, int max, ResultadoReporteFalla r, string ruta)
        {
            if (string.IsNullOrEmpty(rutaDisco) || !File.Exists(rutaDisco))
            {
                r.Omitidos.Add(ruta + " (no existe)");
                return null;
            }
            long largo = new FileInfo(rutaDisco).Length;
            if (largo <= max) return Encoding.UTF8.GetString(LeerCompartido(rutaDisco, 0, (int)largo));
            byte[] cola = LeerCompartido(rutaDisco, largo - max, max);
            string txt = Encoding.UTF8.GetString(cola);
            int nl = txt.IndexOf('\n');
            if (nl >= 0 && nl < txt.Length - 1) txt = txt.Substring(nl + 1);   // arrancar en línea entera
            return "… (recortado: últimos " + Kb(max) + " de " + Kb(largo) + ") …" + Environment.NewLine + txt;
        }

        private static string Cola(string texto, int max)
        {
            if (texto == null) return null;
            if (Encoding.UTF8.GetByteCount(texto) <= max) return texto;
            // Aproximación por caracteres: alcanza para logs (casi todo ASCII).
            string cola = texto.Substring(Math.Max(0, texto.Length - max));
            int nl = cola.IndexOf('\n');
            if (nl >= 0 && nl < cola.Length - 1) cola = cola.Substring(nl + 1);
            return "… (recortado: últimos " + Kb(max) + ") …" + Environment.NewLine + cola;
        }

        private static byte[] LeerLote(ArchivoReporte a, Nivel cfg, int loteTotal, ResultadoReporteFalla r, string ruta)
        {
            if (a.Contenido != null) return Utf8(a.Contenido);
            if (string.IsNullOrEmpty(a.RutaDisco) || !File.Exists(a.RutaDisco))
            {
                r.Omitidos.Add(ruta + " (no existe)");
                return null;
            }
            long largo = new FileInfo(a.RutaDisco).Length;
            if (cfg.LoteArchivoMax <= 0)
            {
                r.Omitidos.Add(ruta + " (" + Kb(largo) + ", lote omitido para achicar el reporte)");
                return null;
            }
            if (largo > cfg.LoteArchivoMax)
            {
                r.Omitidos.Add(ruta + " (" + Kb(largo) + ", omitido por tamaño)");
                return null;
            }
            if (loteTotal + largo > cfg.LoteTotalMax)
            {
                r.Omitidos.Add(ruta + " (" + Kb(largo) + ", no entró en el tope del lote)");
                return null;
            }
            return LeerCompartido(a.RutaDisco, 0, (int)largo);
        }

        /// <summary>Lee aunque otro proceso lo tenga abierto escribiendo (los
        /// logs vivos: salida.log, el log de eventos del motor).</summary>
        private static byte[] LeerCompartido(string ruta, long desde, int cuanto)
        {
            using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (desde > 0) fs.Seek(desde, SeekOrigin.Begin);
                var buf = new byte[cuanto];
                int total = 0;
                while (total < cuanto)
                {
                    int n = fs.Read(buf, total, cuanto - total);
                    if (n <= 0) break;
                    total += n;
                }
                if (total == cuanto) return buf;
                var corto = new byte[total];
                Array.Copy(buf, corto, total);
                return corto;
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static bool EsTextoLote(string ruta)
        {
            string ext = Path.GetExtension(ruta).ToLowerInvariant();
            return ext == ".txt" || ext == ".kml" || ext == ".json" || ext == ".xml" || ext == ".csv";
        }

        /// <summary>Ruta segura dentro del ZIP: "/" como separador, sin "..",
        /// sin raíz, solo caracteres razonables. null = inválida.</summary>
        internal static string NormalizarRutaZip(string ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta)) return null;
            string s = ruta.Replace('\\', '/').Trim().TrimStart('/');
            if (s.Length == 0 || s.Length > 200) return null;
            var partes = s.Split('/');
            foreach (var p in partes)
            {
                if (p.Length == 0 || p == "." || p == "..") return null;
                if (p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                if (p.IndexOf(':') >= 0) return null;
            }
            return string.Join("/", partes);
        }

        private static void Escribir(ZipArchive zip, string ruta, byte[] datos, CompressionLevel nivel)
        {
            var entry = zip.CreateEntry(ruta, nivel);
            using (var s = entry.Open()) s.Write(datos, 0, datos.Length);
        }

        private static KeyValuePair<string, byte[]> Par(string ruta, string texto)
        {
            return new KeyValuePair<string, byte[]>(ruta, Utf8(texto));
        }

        private static byte[] Utf8(string s)
        {
            return new UTF8Encoding(false).GetBytes(s ?? "");
        }

        private static string Kb(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return Math.Max(1, (bytes + 512) / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        }
    }
}
