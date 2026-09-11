// ============================================================================
// PrescripcionService.cs
// Implementación de IPrescripcionService. Lee .geojson de
// <BaseDir>/data/prescripciones/, parsea Polygon/MultiPolygon, expone lookup
// de dosis por Lat/Lon vía ray casting.
//
// Concurrencia: el QuantiXMotorBridge llama GetDoseAt() cada 200ms desde su
// timer; la UI puede llamar SetActive() en cualquier momento. Usamos un lock
// para reemplazar la referencia _active de forma atómica (swap-pointer). Las
// lecturas no toman lock (referencia inmutable una vez asignada).
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
    public sealed class PrescripcionService : IPrescripcionService
    {
        private const string DirName = "data";
        private const string SubDirName = "prescripciones";
        private const string StateFile = "prescripciones-state.json";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Cache de la prescripción activa. Estático porque dos instancias
        // distintas del service (una para el bridge QuantiX, otra para el
        // controller HTTP) deben ver la misma activa. La alternativa era pasar
        // un singleton vía bootstrap, pero el bridge se crea en FormGPS.cs
        // legacy fuera del WebHost — más simple compartir state acá.
        private static readonly object _swapLock = new object();
        private static PrescripcionDto _active;
        private static bool _bootstrapped;

        private sealed class StateFileDto
        {
            public string ActivoId { get; set; } = "";
            public string PropiedadDosis { get; set; } = "";
            public string Lote { get; set; } = "";
        }

        /// <summary>Quién sabe qué lote está abierto. Lo setea el host (engine
        /// o FormGPS) al arrancar; el service lo consulta al ACTIVAR (para atar
        /// la prescripción al lote) y en cada lookup (para no dosificar con el
        /// mapa de otro lote). null o "" = sin lote abierto. Estático por la
        /// misma razón que _active: todas las instancias ven el mismo lote.</summary>
        public static Func<string> LoteActualProvider;

        private static string LoteActual()
        {
            try { return LoteActualProvider?.Invoke() ?? ""; }
            catch { return ""; }
        }

        public PrescripcionService()
        {
            // Auto-cargar la activa al arranque (si quedó marcada en sesión
            // previa). El flag _bootstrapped evita re-cargar si una segunda
            // instancia se construye (el state estático ya está poblado).
            lock (_swapLock)
            {
                if (_bootstrapped) return;
                _bootstrapped = true;
            }
            try
            {
                var st = LoadState();
                // El lote de activación viene del state persistido, NO del
                // provider: al arranque no hay lote abierto todavía y re-atarla
                // acá la dejaría sin lote para siempre.
                if (!string.IsNullOrEmpty(st.ActivoId))
                    SetActiveCore(st.ActivoId, st.PropiedadDosis, st.Lote ?? "");
            }
            catch { /* file corrupto: arrancamos sin activa */ }
        }

        // -------------------- PATHS --------------------
        private static string BaseDir()
        {
            string d = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, DirName, SubDirName);
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }

        private static string StatePath()
            => Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, StateFile);

        // -------------------- STATE persist --------------------
        private static StateFileDto LoadState()
        {
            try
            {
                return AgroParallel.Common.AtomicJson.Read<StateFileDto>(StatePath(), JsonOpts)
                    ?? new StateFileDto();
            }
            catch { return new StateFileDto(); }
        }

        private static void SaveState(StateFileDto st)
        {
            try { AgroParallel.Common.AtomicJson.Write(StatePath(), JsonSerializer.Serialize(st)); }
            catch { /* permission denied: el lookup en memoria sigue OK */ }
        }

        // -------------------- ID / filename --------------------
        private static string IdFromFilename(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(filename) ?? "";
            // slug simple: lowercase + reemplazar espacios y caracteres no [a-z0-9-_]
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            string slug = sb.ToString().Trim('-');
            // colapsar dobles guiones
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug;
        }

        private static string FilenameFromId(string id)
        {
            // Buscar el archivo cuyo IdFromFilename matchea (no podemos asumir
            // que el slug→filename sea inverso exacto porque pueden venir con
            // mayúsculas/acentos del cloud).
            try
            {
                foreach (var f in Directory.GetFiles(BaseDir(), "*.geojson"))
                {
                    if (IdFromFilename(Path.GetFileName(f)) == id) return f;
                }
            }
            catch { }
            return null;
        }

        // -------------------- LIST --------------------
        public List<PrescripcionListItemDto> ListAvailable()
        {
            var list = new List<PrescripcionListItemDto>();
            string activeId;
            lock (_swapLock) { activeId = _active != null ? _active.Id : ""; }

            try
            {
                foreach (var f in Directory.GetFiles(BaseDir(), "*.geojson"))
                {
                    var info = new FileInfo(f);
                    string fn = Path.GetFileName(f);
                    string id = IdFromFilename(fn);
                    var item = new PrescripcionListItemDto
                    {
                        Id = id,
                        Nombre = Path.GetFileNameWithoutExtension(fn),
                        Archivo = fn,
                        Bytes = info.Length,
                        FechaModUtc = info.LastWriteTimeUtc.ToString("O"),
                        Activo = (id == activeId),
                        PropiedadesCandidatas = SniffCandidateProperties(f)
                    };
                    list.Add(item);
                }
            }
            catch { /* dir vacío o sin permisos */ }
            return list;
        }

        /// <summary>Lee el primer feature del archivo y devuelve los nombres
        /// de propiedades numéricas. La UI los muestra para que el operario
        /// elija cuál es "la dosis".</summary>
        private static List<string> SniffCandidateProperties(string path)
        {
            var props = new List<string>();
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (!doc.RootElement.TryGetProperty("features", out var features)) return props;
                    if (features.ValueKind != JsonValueKind.Array) return props;
                    foreach (var feat in features.EnumerateArray())
                    {
                        if (!feat.TryGetProperty("properties", out var p) ||
                            p.ValueKind != JsonValueKind.Object) continue;
                        foreach (var prop in p.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number)
                            {
                                if (!props.Contains(prop.Name)) props.Add(prop.Name);
                            }
                        }
                        if (props.Count > 0) break; // primer feature alcanza
                    }
                }
            }
            catch { }
            return props;
        }

        // -------------------- ACTIVE --------------------
        public PrescripcionDto GetActive()
        {
            lock (_swapLock) { return _active; }
        }

        public void ClearActive()
        {
            lock (_swapLock) { _active = null; }
            SaveState(new StateFileDto());
        }

        public bool SetActive(string id, string propiedadDosis)
            => SetActiveCore(id, propiedadDosis, LoteActual());

        private bool SetActiveCore(string id, string propiedadDosis, string lote)
        {
            if (string.IsNullOrEmpty(id)) { ClearActive(); return true; }
            string path = FilenameFromId(id);
            if (path == null || !File.Exists(path)) return false;

            // Auto-pick propiedadDosis si no viene: orden de preferencia.
            string propEff = propiedadDosis;
            if (string.IsNullOrEmpty(propEff))
            {
                var candidates = SniffCandidateProperties(path);
                var prefs = new[] { "dosis", "dose", "rate", "kgha", "lha", "tasa" };
                foreach (var p in prefs)
                {
                    foreach (var c in candidates)
                    {
                        if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase))
                        { propEff = c; break; }
                    }
                    if (!string.IsNullOrEmpty(propEff)) break;
                }
                // fallback: primera propiedad numérica
                if (string.IsNullOrEmpty(propEff) && candidates.Count > 0) propEff = candidates[0];
            }

            var parsed = ParseFile(path, id, propEff);
            if (parsed == null || parsed.Features.Count == 0) return false;
            parsed.Lote = lote ?? "";

            lock (_swapLock) { _active = parsed; }
            SaveState(new StateFileDto { ActivoId = id, PropiedadDosis = propEff ?? "", Lote = parsed.Lote });
            return true;
        }

        // -------------------- PARSE --------------------
        /// <summary>Parsea un GeoJSON FeatureCollection a PrescripcionDto.
        /// Soporta Polygon y MultiPolygon. Ignora otros tipos de geometría.</summary>
        private static PrescripcionDto ParseFile(string path, string id, string propiedadDosis)
        {
            PrescripcionDto dto;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    dto = new PrescripcionDto
                    {
                        Id = id,
                        Nombre = Path.GetFileNameWithoutExtension(path) ?? "",
                        PropiedadDosis = propiedadDosis ?? "",
                        LoadedUtc = DateTime.UtcNow.ToString("O")
                    };

                    double globalMinLon =  double.MaxValue, globalMinLat =  double.MaxValue;
                    double globalMaxLon = -double.MaxValue, globalMaxLat = -double.MaxValue;

                    if (!doc.RootElement.TryGetProperty("features", out var features)
                        || features.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    int fileIdx = -1;
                    foreach (var feat in features.EnumerateArray())
                    {
                        fileIdx++;
                        double dosis = 0;
                        string label = "";

                        if (feat.TryGetProperty("properties", out var props) &&
                            props.ValueKind == JsonValueKind.Object)
                        {
                            if (!string.IsNullOrEmpty(propiedadDosis) &&
                                props.TryGetProperty(propiedadDosis, out var dv) &&
                                dv.ValueKind == JsonValueKind.Number)
                            {
                                dosis = dv.GetDouble();
                            }
                            if (props.TryGetProperty("zona", out var lv) &&
                                lv.ValueKind == JsonValueKind.String)
                            {
                                label = lv.GetString() ?? "";
                            }
                            else if (props.TryGetProperty("name", out var nv) &&
                                     nv.ValueKind == JsonValueKind.String)
                            {
                                label = nv.GetString() ?? "";
                            }
                        }

                        if (!feat.TryGetProperty("geometry", out var geom) ||
                            geom.ValueKind != JsonValueKind.Object) continue;
                        if (!geom.TryGetProperty("type", out var tv) ||
                            tv.ValueKind != JsonValueKind.String) continue;
                        string type = tv.GetString() ?? "";
                        if (!geom.TryGetProperty("coordinates", out var coords)) continue;

                        if (string.Equals(type, "Polygon", StringComparison.OrdinalIgnoreCase))
                        {
                            var f = ParsePolygon(coords, dosis, label);
                            if (f != null)
                            {
                                f.FileIndex = fileIdx;
                                dto.Features.Add(f);
                                UpdateGlobalBBox(f, ref globalMinLon, ref globalMinLat, ref globalMaxLon, ref globalMaxLat);
                            }
                        }
                        else if (string.Equals(type, "MultiPolygon", StringComparison.OrdinalIgnoreCase))
                        {
                            // Cada elemento del MultiPolygon es un Polygon → un feature aparte.
                            foreach (var poly in coords.EnumerateArray())
                            {
                                var f = ParsePolygon(poly, dosis, label);
                                if (f != null)
                                {
                                    f.FileIndex = fileIdx;
                                    dto.Features.Add(f);
                                    UpdateGlobalBBox(f, ref globalMinLon, ref globalMinLat, ref globalMaxLon, ref globalMaxLat);
                                }
                            }
                        }
                        // Point/LineString: irrelevantes para variable-rate, los ignoramos.
                    }

                    dto.FeatureCount = dto.Features.Count;
                    if (dto.FeatureCount > 0)
                    {
                        dto.MinLon = globalMinLon; dto.MinLat = globalMinLat;
                        dto.MaxLon = globalMaxLon; dto.MaxLat = globalMaxLat;
                    }
                }
            }
            catch { return null; }
            return dto;
        }

        private static PrescripcionFeatureDto ParsePolygon(JsonElement coords, double dosis, string label)
        {
            if (coords.ValueKind != JsonValueKind.Array) return null;
            var feat = new PrescripcionFeatureDto { Dosis = dosis, Label = label };
            double minLon =  double.MaxValue, minLat =  double.MaxValue;
            double maxLon = -double.MaxValue, maxLat = -double.MaxValue;
            foreach (var ring in coords.EnumerateArray())
            {
                if (ring.ValueKind != JsonValueKind.Array) continue;
                var pts = new List<double[]>();
                foreach (var pt in ring.EnumerateArray())
                {
                    if (pt.ValueKind != JsonValueKind.Array) continue;
                    double lon = 0, lat = 0;
                    int i = 0;
                    foreach (var coord in pt.EnumerateArray())
                    {
                        if (coord.ValueKind != JsonValueKind.Number) break;
                        if (i == 0) lon = coord.GetDouble();
                        else if (i == 1) lat = coord.GetDouble();
                        i++;
                    }
                    if (i < 2) continue;
                    pts.Add(new[] { lon, lat });
                    if (lon < minLon) minLon = lon; if (lat < minLat) minLat = lat;
                    if (lon > maxLon) maxLon = lon; if (lat > maxLat) maxLat = lat;
                }
                if (pts.Count >= 3) feat.Rings.Add(pts);
            }
            if (feat.Rings.Count == 0) return null;
            feat.MinLon = minLon; feat.MinLat = minLat;
            feat.MaxLon = maxLon; feat.MaxLat = maxLat;
            return feat;
        }

        private static void UpdateGlobalBBox(PrescripcionFeatureDto f,
            ref double minLon, ref double minLat, ref double maxLon, ref double maxLat)
        {
            if (f.MinLon < minLon) minLon = f.MinLon;
            if (f.MinLat < minLat) minLat = f.MinLat;
            if (f.MaxLon > maxLon) maxLon = f.MaxLon;
            if (f.MaxLat > maxLat) maxLat = f.MaxLat;
        }

        /// <summary>true si la prescripción aplica en el lote abierto AHORA:
        /// tiene lote asociado y coincide (case-insensitive) con el actual.
        /// Sin lote asociado no aplica en ninguno — mejor no dosificar que
        /// dosificar con el mapa de otro lote.</summary>
        public static bool AplicaEnLoteActual(PrescripcionDto p)
        {
            if (p == null || string.IsNullOrEmpty(p.Lote)) return false;
            string lote = LoteActual();
            return !string.IsNullOrEmpty(lote)
                && string.Equals(p.Lote, lote, StringComparison.OrdinalIgnoreCase);
        }

        // -------------------- POINT-IN-POLYGON --------------------
        public double GetDoseAt(double lat, double lon)
        {
            PrescripcionDto active;
            lock (_swapLock) { active = _active; }
            if (active == null || active.Features.Count == 0) return 0;

            // Atada a SU lote: con otro lote abierto (o ninguno) no dosifica.
            if (!AplicaEnLoteActual(active)) return 0;

            // Fast reject por bounding box global.
            if (lon < active.MinLon || lon > active.MaxLon ||
                lat < active.MinLat || lat > active.MaxLat) return 0;

            // Buscar el primer feature que contiene el punto.
            // (En prescripciones bien formadas las zonas no se solapan;
            // si lo hicieran, ganaría la primera definida.)
            for (int i = 0; i < active.Features.Count; i++)
            {
                var f = active.Features[i];
                if (lon < f.MinLon || lon > f.MaxLon ||
                    lat < f.MinLat || lat > f.MaxLat) continue;
                if (PointInPolygon(lon, lat, f.Rings)) return f.Dosis;
            }
            return 0;
        }

        /// <summary>Ray casting clásico. Punto en polígono = punto en outer
        /// ring AND no en ningún hole. Comparación strict-less para evitar
        /// que un punto exacto en un borde se cuente dos veces.</summary>
        private static bool PointInPolygon(double x, double y, List<List<double[]>> rings)
        {
            if (rings == null || rings.Count == 0) return false;
            if (!PointInRing(x, y, rings[0])) return false;
            // Holes: si está dentro de alguno, no cuenta.
            for (int i = 1; i < rings.Count; i++)
            {
                if (PointInRing(x, y, rings[i])) return false;
            }
            return true;
        }

        private static bool PointInRing(double x, double y, List<double[]> ring)
        {
            // Algoritmo de W. Randolph Franklin (PNPOLY).
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = ring[i][0], yi = ring[i][1];
                double xj = ring[j][0], yj = ring[j][1];
                bool intersect = ((yi > y) != (yj > y)) &&
                                 (x < (xj - xi) * (y - yi) / (yj - yi + 1e-30) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        // -------------------- EDICIÓN --------------------

        public bool SetZoneDose(string id, int zoneIndex, double dose)
        {
            if (string.IsNullOrEmpty(id) || zoneIndex < 0) return false;
            if (double.IsNaN(dose) || double.IsInfinity(dose) || dose < 0) return false;

            string path = FilenameFromId(id);
            if (path == null || !File.Exists(path)) return false;

            // Qué propiedad tocar: si la que se está editando es la ACTIVA, su
            // propiedad de dosis ya está decidida; si no, misma heurística que
            // SetActive (para poder corregir una prescripción antes de usarla).
            var st = LoadState();
            string prop = string.Equals(st.ActivoId, id, StringComparison.OrdinalIgnoreCase)
                ? st.PropiedadDosis : null;
            if (string.IsNullOrEmpty(prop))
            {
                var candidates = SniffCandidateProperties(path);
                prop = candidates.Count > 0 ? candidates[0] : "DOSIS";
            }

            try
            {
                // JsonNode y no JsonDocument: hay que MUTAR una sola propiedad
                // preservando el resto del archivo tal cual vino (el cloud lo
                // puede haber generado con propiedades que este código no
                // conoce y que no se pueden perder en la vuelta).
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
                var features = root?["features"] as System.Text.Json.Nodes.JsonArray;
                if (features == null || zoneIndex >= features.Count) return false;

                var props = features[zoneIndex]?["properties"] as System.Text.Json.Nodes.JsonObject;
                if (props == null) return false;
                props[prop] = Math.Round(dose, 3);

                // Escritura atómica: archivo temporal + replace. Un corte de
                // energía a mitad de un File.WriteAllText dejaría el geojson
                // por la mitad y la prescripción entera inutilizable.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = false }));
                File.Copy(tmp, path, overwrite: true);
                File.Delete(tmp);
            }
            catch { return false; }

            // Si es la activa, recargarla ya: LoadedUtc nuevo → el piloto la
            // reproyecta (el shape del mapa cambia de color) y el bridge de
            // QuantiX dosifica con el valor nuevo en su próximo tick.
            // PRESERVANDO el lote de activación: editar una dosis desde el Hub
            // con otro lote abierto no puede re-atar la prescripción a ese lote.
            if (string.Equals(st.ActivoId, id, StringComparison.OrdinalIgnoreCase))
                SetActiveCore(id, st.PropiedadDosis, st.Lote ?? "");

            return true;
        }
    }
}
