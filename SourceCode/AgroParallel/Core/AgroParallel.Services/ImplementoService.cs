// ============================================================================
// ImplementoService.cs — fuente única de verdad del implemento.
//
// Persistencia multi-implemento:
//   <BaseDir>/implementos/<slug>.json     — un archivo por implemento
//   <BaseDir>/implementos/_active.txt     — slug del implemento activo
//
// IMPORTANTE: este servicio NO escribe en la config nativa del piloto AOG.
// La página del Hub edita SOLO el implemento.json. Si AgValoniaGPS/AOG necesita
// el ancho/secciones/lookahead, los lee con su propia config nativa (Vehículo
// /Herramienta), independientemente de lo que vive acá.
//
// Migración legacy: si no existe el directorio pero sí implemento.json
// (formato viejo de un implemento único), se importa como "default".
// Si tampoco existe ese archivo, se siembra "default" desde VistaX y el
// tool actual (sólo lectura) para no arrancar en blanco.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class ImplementoService : IImplementoService
    {
        private const string DirName = "implementos";
        private const string LegacyFileName = "implemento.json";
        private const string ActiveFileName = "_active.txt";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        // Servicios opcionales usados sólo para la migración inicial.
        private readonly IVistaXConfigService _vistax;
        private readonly IVehicleToolService _vehicleTool;

        /// <summary>Cache en memoria del implemento activo. Se invalida al cambiar de activo o guardar.</summary>
        private ImplementoDto _cache;
        private string _cacheSlug;

        // Lock global de escrituras a disco. Sin esto, dos requests concurrentes
        // que hacen Load → modify → Save (típico cuando dos pestañas del Hub
        // editan el mismo implemento, o cuando NodosController.Aceptar() corre
        // contra ImplementoController.PutBySlug()) pisan los cambios del otro:
        // ambos leen el JSON viejo, ambos lo mutan, último Save gana.
        // El lock cubre solo escritura — las lecturas son del FS o del _cache.
        private readonly object _lock = new object();

        public ImplementoService(IVistaXConfigService vistax = null,
                                 IVehicleToolService vehicleTool = null,
                                 IQuantiXConfigService quantix = null,   // legacy, no usado
                                 ISectionXConfigService sectionx = null) // legacy, no usado
        {
            _vistax = vistax;
            _vehicleTool = vehicleTool;
        }

        // ----------------------------------------------------------------
        // Paths
        // ----------------------------------------------------------------

        public string GetPath()
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DirName);

        private string ActivePath() => Path.Combine(GetPath(), ActiveFileName);
        private string FilePath(string slug) => Path.Combine(GetPath(), slug + ".json");
        private string LegacyPath() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LegacyFileName);

        private void EnsureDir()
        {
            string p = GetPath();
            if (!Directory.Exists(p)) Directory.CreateDirectory(p);
        }

        private static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "implemento";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Trim().ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z') sb.Append(c);
                else if (c >= '0' && c <= '9') sb.Append(c);
                else if (c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            string slug = sb.ToString().Trim('-', '_');
            if (string.IsNullOrEmpty(slug)) slug = "implemento";
            // No permitir slugs reservados.
            if (slug.StartsWith("_")) slug = "x" + slug;
            return slug;
        }

        // ----------------------------------------------------------------
        // Bootstrap / migración
        // ----------------------------------------------------------------

        /// <summary>
        /// Garantiza que existe al menos un implemento ("default") y que hay
        /// un activo. Importa desde el viejo implemento.json si está.
        /// </summary>
        private void EnsureBootstrapped()
        {
            EnsureDir();
            string dir = GetPath();
            bool hasAny = Directory.GetFiles(dir, "*.json").Length > 0;

            if (!hasAny)
            {
                // ¿Hay un implemento.json legacy de la versión previa?
                if (File.Exists(LegacyPath()))
                {
                    try
                    {
                        var dto = JsonSerializer.Deserialize<ImplementoDto>(
                            File.ReadAllText(LegacyPath()), ReadOpts);
                        if (dto != null)
                        {
                            if (string.IsNullOrEmpty(dto.Nombre)) dto.Nombre = "default";
                            Save("default", dto);
                            SetActive("default");
                            return;
                        }
                    }
                    catch { /* fallthrough */ }
                }
                // Sin legacy: sembrar uno desde VistaX/Tool (snapshot, sin write-back).
                var seeded = SeedFromLegacyServices();
                Save("default", seeded);
                SetActive("default");
                return;
            }

            // Hay archivos pero quizás no hay activo.
            if (string.IsNullOrEmpty(GetActiveSlug()))
            {
                var first = List().FirstOrDefault();
                if (first != null) SetActive(first.Slug);
            }
        }

        private ImplementoDto SeedFromLegacyServices()
        {
            var nuevo = new ImplementoDto { Nombre = "default" };

            ToolConfigDto tool = null;
            try { tool = _vehicleTool?.GetTool(); } catch { }
            if (tool != null)
            {
                nuevo.AnchoTotalM = tool.Width;
                nuevo.OverlapM = tool.Overlap;
                nuevo.OffsetM = tool.Offset;
                nuevo.HitchLengthM = tool.HitchLength;
                nuevo.TrailingHitchLengthM = tool.TrailingHitchLength;
                nuevo.TrailingToolToPivotM = tool.TrailingToolToPivotLength;
                nuevo.LookaheadOnS = tool.LookAheadOn;
                nuevo.LookaheadOffS = tool.LookAheadOff;
                nuevo.TurnOffDelayS = tool.TurnOffDelay;
                nuevo.IsTrailing = tool.IsToolTrailing;
                nuevo.IsTBT = tool.IsToolTBT;
                nuevo.IsRearFixed = tool.IsToolRearFixed;
                nuevo.IsFrontFixed = tool.IsToolFrontFixed;
                nuevo.SectionOffWhenOut = tool.IsSectionOffWhenOut;
            }
            int numSeccionesAog = tool != null && tool.NumSections > 0 ? tool.NumSections : 0;

            VistaXImplementoDto vxImp = null;
            try { vxImp = _vistax?.GetImplemento(); } catch { }

            int totalSurcos = 0;
            if (vxImp != null && vxImp.Setup != null)
            {
                if (vxImp.Setup.DistanciaEntreSurcos > 0)
                    nuevo.DistanciaEntreSurcosM = vxImp.Setup.DistanciaEntreSurcos;
                if (vxImp.Setup.AnchoImplemento > 0 && nuevo.AnchoTotalM <= 0)
                    nuevo.AnchoTotalM = vxImp.Setup.AnchoImplemento;
                totalSurcos = vxImp.Setup.TotalSurcos;
                if (numSeccionesAog == 0 && vxImp.Setup.SeccionesAOG > 0)
                    numSeccionesAog = vxImp.Setup.SeccionesAOG;
            }
            if (vxImp != null && vxImp.Trenes != null)
            {
                foreach (var t in vxImp.Trenes)
                    nuevo.Trenes.Add(new TrenDto
                    {
                        Id = t.Id,
                        Nombre = t.Nombre ?? ("Tren " + t.Id),
                        DistanciaM = 0
                    });
            }
            if (nuevo.Trenes.Count == 0)
                nuevo.Trenes.Add(new TrenDto { Id = 1, Nombre = "Tren único", DistanciaM = 0 });

            int trenDefault = nuevo.Trenes[0].Id;
            int secs = numSeccionesAog > 0 ? numSeccionesAog : 1;
            nuevo.NumeroSurcos = totalSurcos > 0 ? totalSurcos : 0;
            for (int i = 1; i <= nuevo.NumeroSurcos; i++)
            {
                int seccion = secs > 0
                    ? Math.Min(secs, 1 + (i - 1) * secs / Math.Max(1, nuevo.NumeroSurcos))
                    : 0;
                nuevo.Surcos.Add(new SurcoDto { Numero = i, TrenId = trenDefault, SeccionPilotX = seccion });
            }
            for (int i = 1; i <= secs; i++)
                nuevo.Secciones.Add(new SeccionDto { Id = i, Nombre = "Sección " + i });
            return nuevo;
        }

        // ----------------------------------------------------------------
        // CRUD multi-implemento
        // ----------------------------------------------------------------

        public List<ImplementoListEntry> List()
        {
            EnsureDir();
            var dir = GetPath();
            string activo = GetActiveSlug();
            var list = new List<ImplementoListEntry>();
            foreach (var path in Directory.GetFiles(dir, "*.json"))
            {
                string slug = Path.GetFileNameWithoutExtension(path);
                if (slug.StartsWith("_")) continue;
                string nombre = slug;
                try
                {
                    var dto = JsonSerializer.Deserialize<ImplementoDto>(File.ReadAllText(path), ReadOpts);
                    if (dto != null && !string.IsNullOrWhiteSpace(dto.Nombre)) nombre = dto.Nombre;
                }
                catch { /* dejamos slug como nombre */ }
                list.Add(new ImplementoListEntry { Slug = slug, Nombre = nombre, Activo = slug == activo });
            }
            list.Sort((a, b) => string.Compare(a.Nombre, b.Nombre, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public string GetActiveSlug()
        {
            try
            {
                string p = ActivePath();
                if (!File.Exists(p)) return "";
                return File.ReadAllText(p).Trim();
            }
            catch { return ""; }
        }

        public ImplementoDto Load(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;
            string p = FilePath(slug);
            if (!File.Exists(p)) return null;
            try
            {
                var dto = JsonSerializer.Deserialize<ImplementoDto>(File.ReadAllText(p), ReadOpts);
                return dto != null ? Sanitize(dto) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[implemento] Load(" + slug + "): " + ex.Message);
                return null;
            }
        }

        public bool Save(string slug, ImplementoDto dto)
        {
            if (string.IsNullOrWhiteSpace(slug) || dto == null) return false;
            EnsureDir();
            lock (_lock)
            {
                try
                {
                    var clean = Sanitize(dto);
                    WriteAtomic(FilePath(slug), JsonSerializer.Serialize(clean, WriteOpts));
                    if (slug == _cacheSlug) _cache = clean;
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[implemento] Save(" + slug + "): " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>RMW atómico — usar desde controllers que necesitan
        /// Load → modify → Save sin que otro request pise el cambio. Devuelve
        /// el dto resultante (o null si no existía y mutate no lo creó).</summary>
        public ImplementoDto Update(string slug, Action<ImplementoDto> mutate)
        {
            if (string.IsNullOrWhiteSpace(slug) || mutate == null) return null;
            EnsureDir();
            lock (_lock)
            {
                try
                {
                    string p = FilePath(slug);
                    ImplementoDto dto = null;
                    if (File.Exists(p))
                    {
                        dto = JsonSerializer.Deserialize<ImplementoDto>(File.ReadAllText(p), ReadOpts);
                    }
                    if (dto == null) dto = new ImplementoDto();
                    mutate(dto);
                    var clean = Sanitize(dto);
                    WriteAtomic(p, JsonSerializer.Serialize(clean, WriteOpts));
                    if (slug == _cacheSlug) _cache = clean;
                    return clean;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[implemento] Update(" + slug + "): " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>Escritura atómica tmp+File.Replace. Evita JSON truncado si crashea
        /// entre WriteAllText y disk-flush (perderíamos config del operario).</summary>
        private static void WriteAtomic(string path, string contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        public bool SetActive(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return false;
            if (!File.Exists(FilePath(slug))) return false;
            EnsureDir();
            lock (_lock)
            {
                try
                {
                    WriteAtomic(ActivePath(), slug);
                    _cache = null;
                    _cacheSlug = null;
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[implemento] SetActive(" + slug + "): " + ex.Message);
                    return false;
                }
            }
        }

        public bool Delete(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return false;
            string p = FilePath(slug);
            if (!File.Exists(p)) return false;
            lock (_lock)
            {
                try
                {
                    File.Delete(p);
                    if (GetActiveSlug() == slug)
                    {
                        // Pasamos el activo al primero disponible.
                        var first = List().FirstOrDefault();
                        if (first != null) WriteAtomic(ActivePath(), first.Slug);
                        else WriteAtomic(ActivePath(), "");
                        // Invalidar cache porque cambia el activo
                        _cache = null;
                        _cacheSlug = null;
                    }
                    if (slug == _cacheSlug) { _cache = null; _cacheSlug = null; }
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[implemento] Delete(" + slug + "): " + ex.Message);
                    return false;
                }
            }
        }

        public bool Copy(string fromSlug, string toSlug, string nombre)
        {
            if (string.IsNullOrWhiteSpace(toSlug)) return false;
            var src = Load(fromSlug);
            if (src == null) return false;
            if (!string.IsNullOrWhiteSpace(nombre)) src.Nombre = nombre;
            else if (string.IsNullOrWhiteSpace(src.Nombre)) src.Nombre = toSlug;
            return Save(toSlug, src);
        }

        // ----------------------------------------------------------------
        // Compatibilidad legacy (consumidores VistaX/QuantiX/SectionX)
        // ----------------------------------------------------------------

        public ImplementoDto GetImplemento()
        {
            EnsureBootstrapped();
            string slug = GetActiveSlug();
            if (string.IsNullOrEmpty(slug)) return new ImplementoDto { Nombre = "vacío" };

            if (_cache != null && _cacheSlug == slug) return _cache;

            var dto = Load(slug) ?? new ImplementoDto { Nombre = slug };
            _cache = dto;
            _cacheSlug = slug;
            return _cache;
        }

        public bool SaveImplemento(ImplementoDto dto)
        {
            if (dto == null) return false;
            EnsureBootstrapped();
            string slug = GetActiveSlug();
            if (string.IsNullOrEmpty(slug)) slug = "default";
            return Save(slug, dto);
        }

        // ----------------------------------------------------------------
        // Sanitización
        // ----------------------------------------------------------------

        private static ImplementoDto Sanitize(ImplementoDto d)
        {
            if (d.Trenes == null) d.Trenes = new List<TrenDto>();
            if (d.Surcos == null) d.Surcos = new List<SurcoDto>();
            if (d.Secciones == null) d.Secciones = new List<SeccionDto>();
            if (d.NodosUids == null) d.NodosUids = new List<string>();
            // Normalizar UIDs: trim, sin vacíos, sin duplicados (case-insensitive).
            d.NodosUids = d.NodosUids
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (d.DistanciaEntreSurcosM <= 0) d.DistanciaEntreSurcosM = 0.525;
            if (d.NumeroSurcos < 0) d.NumeroSurcos = 0;

            var trenIds = new HashSet<int>(d.Trenes.Select(t => t.Id));
            int fallbackTren = d.Trenes.Count > 0 ? d.Trenes[0].Id : 0;
            foreach (var s in d.Surcos)
            {
                if (s.TrenId != 0 && !trenIds.Contains(s.TrenId))
                    s.TrenId = fallbackTren;
            }
            return d;
        }

        // Slugify expuesto para el controller (genera slug consistente desde nombre).
        public static string MakeSlug(string nombre) => Slugify(nombre);
    }
}
