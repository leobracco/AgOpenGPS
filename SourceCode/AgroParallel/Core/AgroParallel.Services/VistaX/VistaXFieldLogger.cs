// ============================================================================
// VistaXFieldLogger.cs - Registro NDJSON de monitoreo + export a Shapefile
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/VistaXFieldLogger.cs
// Target: net48 (C# 7.3)
//
// Graba una línea JSON por snapshot (~250ms) en NDJSON. Al detener exporta
// dos shapefiles:
//   1) Puntos por lectura (un punto por surco por lectura, con SPM y estado)
//   2) Grilla de calor: celdas de ~5m con densidad promedio coloreada
//
// Path configurable via LogOutputDrive para guardar en otra partición.
// ============================================================================

using AgroParallel.Services.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AgroParallel.VistaX
{
    public class VistaXFieldLogger : IDisposable
    {
        private readonly IAogStateProvider _state;
        private readonly VistaXConfig _config;

        private StreamWriter _writer;
        private string _ndjsonPath;
        private string _sessionDir;
        private int _lineCount;
        private bool _disposed;
        private DateTime _sessionStart;

        // Intervalo entre registros: 250ms = ~0.5m a 8km/h.
        private const int MinLogIntervalMs = 250;
        private int _lastLogTick;

        public bool IsLogging { get { return _writer != null; } }
        public string CurrentLogPath { get { return _ndjsonPath; } }

        public VistaXFieldLogger(IAogStateProvider state, VistaXConfig config)
        {
            _state = state;
            _config = config;
        }

        // =====================================================================
        // Start / Stop
        // =====================================================================

        public void Start()
        {
            if (_writer != null) return;

            try
            {
                _sessionDir = ResolveOutputDir();
                System.Diagnostics.Debug.WriteLine("[VistaX-Log] ResolveOutputDir => \""
                    + (_sessionDir ?? "NULL") + "\"");

                if (string.IsNullOrEmpty(_sessionDir))
                {
                    System.Diagnostics.Debug.WriteLine("[VistaX-Log] No se pudo resolver directorio de salida");
                    return;
                }

                if (!Directory.Exists(_sessionDir))
                {
                    Directory.CreateDirectory(_sessionDir);
                    System.Diagnostics.Debug.WriteLine("[VistaX-Log] Directorio creado: " + _sessionDir);
                }

                _sessionStart = DateTime.Now;
                string timestamp = _sessionStart.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                _ndjsonPath = Path.Combine(_sessionDir, "vistax_" + timestamp + ".ndjson");

                _writer = new StreamWriter(_ndjsonPath, false, Encoding.UTF8);
                _writer.AutoFlush = true;
                _lineCount = 0;

                System.Diagnostics.Debug.WriteLine("[VistaX-Log] INICIADO OK: " + _ndjsonPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX-Log] ERROR iniciando: " + ex.ToString());
                _writer = null;
            }
        }

        public void Stop()
        {
            if (_writer == null) return;

            try
            {
                _writer.Flush();
                _writer.Close();
                _writer.Dispose();
            }
            catch { }
            _writer = null;

            System.Diagnostics.Debug.WriteLine("[VistaX-Log] Detenido: " + _lineCount + " registros");

            // Exportar shapefiles en background.
            if (_lineCount > 0 && !string.IsNullOrEmpty(_ndjsonPath))
            {
                string path = _ndjsonPath;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        ExportPointsShapefile(path);
                        ExportHeatmapShapefile(path);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[VistaX-Log] Error SHP: " + ex.Message);
                    }
                });
            }
        }

        // =====================================================================
        // Write snapshot
        // =====================================================================

        public void WriteSnapshot(SeedMonitorSnapshot snap)
        {
            if (_writer == null || _disposed) return;
            if (snap == null) return;

            int now = Environment.TickCount;
            if (now - _lastLogTick < MinLogIntervalMs) return;
            _lastLogTick = now;

            try
            {
                // Lat/lon vienen del snapshot (capturadas en CreateSnapshot,
                // que corre en el mismo ciclo que el fix GPS).
                double lat = snap.Latitude;
                double lon = snap.Longitude;

                var sb = new StringBuilder(512);
                sb.Append('{');
                AppendStr(sb, "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.Append(','); AppendNum(sb, "lat", lat, 7);
                sb.Append(','); AppendNum(sb, "lon", lon, 7);
                sb.Append(','); AppendNum(sb, "vel", snap.Velocidad, 2);
                sb.Append(','); AppendNum(sb, "spm", snap.SpmPromedio, 2);
                sb.Append(','); AppendInt(sb, "fallas", snap.FallasActivas);
                sb.Append(','); AppendInt(sb, "activos", snap.SurcosActivos);
                sb.Append(','); AppendBool(sb, "alarma", snap.HasAlarm);
                sb.Append(','); AppendBool(sb, "mon", snap.MonitoreoActivo);
                // Geometría del implemento para reconstruir posiciones.
                sb.Append(','); AppendNum(sb, "tE", snap.ToolEasting, 4);
                sb.Append(','); AppendNum(sb, "tN", snap.ToolNorthing, 4);
                sb.Append(','); AppendNum(sb, "tH", snap.ToolHeading, 6);

                // Detalle por surco (solo semilla) con offset lateral.
                if (snap.Surcos != null && snap.Surcos.Length > 0)
                {
                    sb.Append(",\"surcos\":[");
                    bool first = true;
                    int surcoIdx = 0;
                    foreach (var s in snap.Surcos)
                    {
                        if (s == null) { surcoIdx++; continue; }
                        if (!string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase))
                        { surcoIdx++; continue; }
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('{');
                        AppendInt(sb, "t", s.Tren);
                        sb.Append(','); AppendInt(sb, "b", s.Bajada);
                        sb.Append(','); AppendNum(sb, "v", s.Valor, 2);
                        sb.Append(','); AppendNum(sb, "s", s.Spm, 2);
                        sb.Append(','); AppendBool(sb, "a", s.Alerta);
                        // Offset lateral en metros desde el centro del implemento.
                        if (snap.SurcoLateralOffsets != null && surcoIdx < snap.SurcoLateralOffsets.Length)
                        {
                            sb.Append(','); AppendNum(sb, "off", snap.SurcoLateralOffsets[surcoIdx], 3);
                        }
                        sb.Append('}');
                        surcoIdx++;
                    }
                    sb.Append(']');
                }

                sb.Append('}');

                _writer.WriteLine(sb.ToString());
                _lineCount++;

                if (_lineCount <= 3 || _lineCount % 50 == 0)
                    System.Diagnostics.Debug.WriteLine("[VistaX-Log] Linea " + _lineCount);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX-Log] Error escribiendo: " + ex.ToString());
            }
        }

        // =====================================================================
        // Output directory resolution
        // =====================================================================

        private string ResolveOutputDir()
        {
            string altDrive = _config != null ? _config.LogOutputDrive : null;
            if (!string.IsNullOrWhiteSpace(altDrive))
            {
                string fieldName = GetFieldName();
                return Path.Combine(altDrive.Trim(), "VistaX_Logs",
                    !string.IsNullOrEmpty(fieldName) ? fieldName : "sin_campo");
            }

            if (_state != null)
            {
                string fieldDir = GetFieldFullPath();
                if (!string.IsNullOrEmpty(fieldDir))
                    return Path.Combine(fieldDir, "VistaX");
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VistaX_Logs");
        }

        private string GetFieldName()
        {
            try { return _state != null ? _state.GetSnapshot().CurrentFieldDirectory : null; }
            catch { return null; }
        }

        private string GetFieldFullPath()
        {
            try
            {
                if (_state == null) return null;
                var snap = _state.GetSnapshot();
                string name = snap.CurrentFieldDirectory;
                if (string.IsNullOrEmpty(name)) return null;
                if (string.IsNullOrEmpty(snap.FieldsDirectory)) return null;
                return Path.Combine(snap.FieldsDirectory, name);
            }
            catch { return null; }
        }

        // =====================================================================
        // Export 1: Puntos — un punto por surco por lectura
        // =====================================================================

        private static void ExportPointsShapefile(string ndjsonPath)
        {
            if (!File.Exists(ndjsonPath)) return;

            var factory = GeometryFactory.Default;
            var features = new List<IFeature>();

            using (var reader = new StreamReader(ndjsonPath, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        double lat = ExtractDouble(line, "\"lat\":");
                        double lon = ExtractDouble(line, "\"lon\":");
                        if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) continue;

                        double vel = ExtractDouble(line, "\"vel\":");
                        double spmAvg = ExtractDouble(line, "\"spm\":");
                        string ts = ExtractString(line, "\"t\":\"");

                        // Heading del implemento para calcular posición de cada surco.
                        double toolH = ExtractDouble(line, "\"tH\":");

                        // Extraer surcos individuales.
                        int surcoStart = line.IndexOf("\"surcos\":[", StringComparison.Ordinal);
                        if (surcoStart < 0) continue;

                        int arrStart = line.IndexOf('[', surcoStart);
                        int arrEnd = line.IndexOf(']', arrStart);
                        if (arrStart < 0 || arrEnd < 0) continue;

                        string arrStr = line.Substring(arrStart, arrEnd - arrStart + 1);
                        int pos = 0;
                        while (pos < arrStr.Length)
                        {
                            int objStart = arrStr.IndexOf('{', pos);
                            if (objStart < 0) break;
                            int objEnd = arrStr.IndexOf('}', objStart);
                            if (objEnd < 0) break;

                            string obj = arrStr.Substring(objStart, objEnd - objStart + 1);
                            pos = objEnd + 1;

                            int tren = (int)ExtractDouble(obj, "\"t\":");
                            int bajada = (int)ExtractDouble(obj, "\"b\":");
                            double spm = ExtractDouble(obj, "\"s\":");
                            double valor = ExtractDouble(obj, "\"v\":");
                            bool alerta = obj.Contains("\"a\":true");
                            double offset = ExtractDouble(obj, "\"off\":");

                            // Calcular posición del surco: desplazamiento lateral
                            // perpendicular al heading del implemento.
                            double surcoLat = lat;
                            double surcoLon = lon;
                            if (Math.Abs(offset) > 0.001)
                            {
                                // Perpendicular al heading: heading - PI/2
                                double perpH = toolH - Math.PI / 2.0;
                                // Convertir metros a grados (aprox).
                                double dLat = (Math.Cos(perpH) * offset) / 111320.0;
                                double dLon = (Math.Sin(perpH) * offset) / (111320.0 * Math.Cos(lat * Math.PI / 180.0));
                                surcoLat = lat + dLat;
                                surcoLon = lon + dLon;
                            }

                            var point = factory.CreatePoint(new Coordinate(surcoLon, surcoLat));
                            var attrs = new AttributesTable();
                            attrs.Add("timestamp", ts);
                            attrs.Add("vel_kmh", Math.Round(vel, 1));
                            attrs.Add("tren", tren);
                            attrs.Add("surco", bajada);
                            attrs.Add("spm", Math.Round(spm, 1));
                            attrs.Add("valor", Math.Round(valor, 1));
                            attrs.Add("alerta", alerta ? 1 : 0);
                            attrs.Add("offset_m", Math.Round(offset, 2));

                            features.Add(new Feature(point, attrs));
                        }
                    }
                    catch { }
                }
            }

            if (features.Count == 0) return;

            string shpPath = Path.ChangeExtension(ndjsonPath, ".shp");
            Shapefile.WriteAllFeatures(features, shpPath);
            WritePrj(Path.ChangeExtension(ndjsonPath, ".prj"));

            System.Diagnostics.Debug.WriteLine("[VistaX-Log] SHP puntos: " + shpPath
                + " (" + features.Count + " puntos)");
        }

        // =====================================================================
        // Export 2: Mapa de calor — grilla de celdas con densidad promedio
        // =====================================================================

        private static void ExportHeatmapShapefile(string ndjsonPath)
        {
            if (!File.Exists(ndjsonPath)) return;

            // Paso 1: Leer todos los puntos con SPM.
            var points = new List<HeatPoint>();
            using (var reader = new StreamReader(ndjsonPath, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        double lat = ExtractDouble(line, "\"lat\":");
                        double lon = ExtractDouble(line, "\"lon\":");
                        if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) continue;

                        double spm = ExtractDouble(line, "\"spm\":");
                        int fallas = (int)ExtractDouble(line, "\"fallas\":");
                        int activos = (int)ExtractDouble(line, "\"activos\":");

                        points.Add(new HeatPoint { Lat = lat, Lon = lon, Spm = spm, Fallas = fallas, Activos = activos });
                    }
                    catch { }
                }
            }

            if (points.Count < 2) return;

            // Paso 2: Calcular bounding box.
            double minLat = double.MaxValue, maxLat = double.MinValue;
            double minLon = double.MaxValue, maxLon = double.MinValue;
            foreach (var p in points)
            {
                if (p.Lat < minLat) minLat = p.Lat;
                if (p.Lat > maxLat) maxLat = p.Lat;
                if (p.Lon < minLon) minLon = p.Lon;
                if (p.Lon > maxLon) maxLon = p.Lon;
            }

            // Tamaño de celda: ~5 metros en grados (aprox).
            double cellSizeLat = 5.0 / 111320.0;             // ~5m en latitud
            double cellSizeLon = 5.0 / (111320.0 * Math.Cos(minLat * Math.PI / 180.0)); // ~5m en longitud

            int cols = (int)Math.Ceiling((maxLon - minLon) / cellSizeLon) + 1;
            int rows = (int)Math.Ceiling((maxLat - minLat) / cellSizeLat) + 1;

            // Limitar tamaño de grilla.
            if (cols > 2000 || rows > 2000 || (long)cols * rows > 500000)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX-Log] Heatmap: grilla demasiado grande ("
                    + cols + "x" + rows + "), saltando");
                return;
            }

            // Paso 3: Acumular SPM, activos y fallas por celda.
            // sumActivos suma todos los surcos activos por punto (densidad de
            // siembra simultánea). countFallas cuenta lecturas con fallas>0
            // (no la suma de fallas) — usado para % de lecturas problemáticas.
            var sumSpm = new double[rows, cols];
            var countSpm = new int[rows, cols];
            var sumFallas = new int[rows, cols];
            var sumActivos = new int[rows, cols];
            var countConFalla = new int[rows, cols];

            foreach (var p in points)
            {
                int r = (int)((p.Lat - minLat) / cellSizeLat);
                int c = (int)((p.Lon - minLon) / cellSizeLon);
                if (r < 0) r = 0; if (r >= rows) r = rows - 1;
                if (c < 0) c = 0; if (c >= cols) c = cols - 1;

                sumSpm[r, c] += p.Spm;
                countSpm[r, c]++;
                sumFallas[r, c] += p.Fallas;
                sumActivos[r, c] += p.Activos;
                if (p.Fallas > 0) countConFalla[r, c]++;
            }

            // Paso 4: Generar polígonos (celdas con datos).
            var factory = GeometryFactory.Default;
            var features = new List<IFeature>();

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (countSpm[r, c] == 0) continue;

                    double avgSpm = Math.Round(sumSpm[r, c] / countSpm[r, c], 2);
                    double avgFallas = Math.Round((double)sumFallas[r, c] / countSpm[r, c], 2);

                    double y0 = minLat + r * cellSizeLat;
                    double y1 = y0 + cellSizeLat;
                    double x0 = minLon + c * cellSizeLon;
                    double x1 = x0 + cellSizeLon;

                    var ring = factory.CreateLinearRing(new[]
                    {
                        new Coordinate(x0, y0),
                        new Coordinate(x1, y0),
                        new Coordinate(x1, y1),
                        new Coordinate(x0, y1),
                        new Coordinate(x0, y0)
                    });
                    var polygon = factory.CreatePolygon(ring);

                    double avgActivos = Math.Round((double)sumActivos[r, c] / countSpm[r, c], 2);
                    double pctFallas  = Math.Round(100.0 * countConFalla[r, c] / countSpm[r, c], 1);

                    var attrs = new AttributesTable();
                    attrs.Add("spm_avg", avgSpm);
                    attrs.Add("fallas",  avgFallas);
                    attrs.Add("act_avg", avgActivos);   // surcos activos promedio
                    attrs.Add("pct_fall", pctFallas);   // % lecturas con falla
                    attrs.Add("lecturas", countSpm[r, c]);

                    // Clasificación para estilo:
                    // 0 = sin datos, 1 = bueno (>80% obj), 2 = medio, 3 = bajo, 4 = falla
                    int clase = avgSpm > 12 ? 1 : avgSpm > 8 ? 2 : avgSpm > 3 ? 3 : 4;
                    if (avgFallas > 0.5) clase = 4;
                    attrs.Add("clase", clase);

                    features.Add(new Feature(polygon, attrs));
                }
            }

            if (features.Count == 0) return;

            string heatPath = Path.Combine(
                Path.GetDirectoryName(ndjsonPath),
                Path.GetFileNameWithoutExtension(ndjsonPath) + "_heatmap.shp");

            Shapefile.WriteAllFeatures(features, heatPath);
            WritePrj(Path.ChangeExtension(heatPath, ".prj"));

            System.Diagnostics.Debug.WriteLine("[VistaX-Log] SHP heatmap: " + heatPath
                + " (" + features.Count + " celdas, " + cols + "x" + rows + " grilla)");
        }

        private struct HeatPoint
        {
            public double Lat, Lon, Spm;
            public int Fallas, Activos;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void WritePrj(string prjPath)
        {
            File.WriteAllText(prjPath,
                "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\","
                + "SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],"
                + "PRIMEM[\"Greenwich\",0.0],"
                + "UNIT[\"Degree\",0.0174532925199433]]");
        }

        private static double ExtractDouble(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += key.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
                end++;
            if (end == idx) return 0;
            double val;
            double.TryParse(json.Substring(idx, end - idx),
                NumberStyles.Any, CultureInfo.InvariantCulture, out val);
            return val;
        }

        private static string ExtractString(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return "";
            idx += key.Length;
            int end = json.IndexOf('"', idx);
            if (end < 0) return "";
            return json.Substring(idx, end - idx);
        }

        private static void AppendStr(StringBuilder sb, string key, string val)
        {
            sb.Append('"').Append(key).Append("\":\"").Append(val ?? "").Append('"');
        }

        private static void AppendNum(StringBuilder sb, string key, double val, int decimals)
        {
            sb.Append('"').Append(key).Append("\":")
              .Append(Math.Round(val, decimals).ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendInt(StringBuilder sb, string key, int val)
        {
            sb.Append('"').Append(key).Append("\":")
              .Append(val.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendBool(StringBuilder sb, string key, bool val)
        {
            sb.Append('"').Append(key).Append("\":")
              .Append(val ? "true" : "false");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
