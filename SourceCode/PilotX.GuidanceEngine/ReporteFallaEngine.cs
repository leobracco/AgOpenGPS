// ============================================================================
// ReporteFallaEngine.cs — qué aporta el MOTOR al "Reportar falla".
//
// ReporteFallaArmador (Services) no sabe de rutas de PilotX; acá se juntan:
//
//   logs/eventos.txt          registro de eventos del motor (AgOpenGPS_Events_Log.txt)
//   logs/eventos_sesion.txt   lo que todavía no bajó a disco (CrashLog baja cada 30 s)
//   logs/orbitx_sync.log      sync con OrbitX
//   logs/updater.log          <install>\AgroParallel\Updates\updater.log (si existe)
//   logs/vigilante.log        <install>\vigilante.log (relanzamientos de la pantalla)
//   config/*.json             configs del motor (ConfigRoot) — SANITIZADAS
//   config/GuidanceEngineData/…  tool.json, ultimo_lote.txt, etc.
//   config/perfil/<activo>.XML   perfil de vehículo activo — SANITIZADO
//   lote/…                    archivos chicos del lote abierto (los más chicos primero)
//   diagnostico/fuentes.txt   monitor de fuente muda (estado + últimos cortes)
//   diagnostico/estado.txt    estado del motor al momento del reporte + nodos
//   diagnostico/sistema.txt   Windows, RAM, disco (AccionesSoporte "sistema")
//
// Los logs de la PANTALLA (errores.log, salida-anterior.log) los manda el
// Desktop en el POST: los lee él, que sabe dónde está parado.
//
// Todo best-effort: si algo no se puede leer, el reporte sale igual y el
// armador lo anota en "omitidos".
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AgLibrary.Logging;
using AgroParallel.Common;
using AgroParallel.Diagnostico;
using AgroParallel.Soporte;

namespace AgOpenGPS
{
    internal static class ReporteFallaEngine
    {
        // Archivos del lote: los .txt de AOG son chicos salvo Sections.txt y
        // RecPath, que el armador omite por tamaño. Se ordena por tamaño para
        // que lo chico e importante (Field, Boundary, TrackLines) entre seguro.
        private static readonly HashSet<string> ExtLote = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".kml", ".json", ".xml", ".csv",
        };

        public static EntradaReporteFalla Contexto(GuidanceEngineHost host, MonitorFuentesMudas fuentes,
            Func<IReadOnlyList<AgroParallel.Models.NodoStatus>> nodos)
        {
            var e = new EntradaReporteFalla();
            try { e.VersionPilotX = AgroParallel.OrbitX.PilotXSelfUpdate.Snapshot().CurrentVersion; } catch { }
            e.PerfilActivo = RegistrySettings.vehicleFileName ?? "";

            string loteDir = null;
            try
            {
                if (host != null && host.IsJobStarted && !string.IsNullOrEmpty(host.currentFieldDirectory))
                {
                    e.LoteNombre = host.currentFieldDirectory;
                    loteDir = Path.Combine(RegistrySettings.fieldsDirectory ?? "", host.currentFieldDirectory);
                }
            }
            catch { }

            // ── logs del motor ──
            string logsDir = RegistrySettings.logsDirectory;
            if (!string.IsNullOrEmpty(logsDir))
                e.Archivos.Add(new ArchivoReporte("logs/eventos.txt",
                    Path.Combine(logsDir, "AgOpenGPS_Events_Log.txt"), TipoArchivoReporte.Log));
            try
            {
                // Log.sbEvents es un StringBuilder que escriben varios hilos: leerlo
                // puede fallar justo en una escritura. Si falla, no hay sesión.
                string sesion = Log.sbEvents.ToString().Replace("\r", Environment.NewLine);
                if (!string.IsNullOrEmpty(sesion))
                    e.Archivos.Add(ArchivoReporte.DeTexto("logs/eventos_sesion.txt", sesion, TipoArchivoReporte.Log));
            }
            catch { }

            string cfgRoot = AgpPaths.ConfigRoot;
            e.Archivos.Add(new ArchivoReporte("logs/orbitx_sync.log", Path.Combine(cfgRoot, "orbitx_sync.log"), TipoArchivoReporte.Log));

            string install = CarpetaInstalacion();
            e.Archivos.Add(new ArchivoReporte("logs/vigilante.log", Path.Combine(install, "vigilante.log"), TipoArchivoReporte.Log));
            string updater = Path.Combine(install, "AgroParallel", "Updates", "updater.log");
            if (File.Exists(updater))
                e.Archivos.Add(new ArchivoReporte("logs/updater.log", updater, TipoArchivoReporte.Log));

            // ── config (sanitizada por el armador) ──
            AgregarCarpeta(e, cfgRoot, "config", "*.json", TipoArchivoReporte.Config, EsConfigPropia);
            string ged = Path.Combine(cfgRoot, "GuidanceEngineData");
            AgregarCarpeta(e, ged, "config/GuidanceEngineData", "*.json", TipoArchivoReporte.Config, null);
            AgregarCarpeta(e, ged, "config/GuidanceEngineData", "*.txt", TipoArchivoReporte.Config, null);
            try
            {
                if (!string.IsNullOrEmpty(e.PerfilActivo) && !string.IsNullOrEmpty(RegistrySettings.vehiclesDirectory))
                {
                    string xml = Path.Combine(RegistrySettings.vehiclesDirectory, e.PerfilActivo + ".XML");
                    e.Archivos.Add(new ArchivoReporte("config/perfil/" + SinCaracteresRaros(e.PerfilActivo) + ".XML",
                        xml, TipoArchivoReporte.Config));
                }
            }
            catch { }

            // ── lote abierto ──
            if (loteDir != null && Directory.Exists(loteDir))
            {
                try
                {
                    var archivos = new DirectoryInfo(loteDir).GetFiles()
                        .Where(f => ExtLote.Contains(f.Extension))
                        .OrderBy(f => f.Length)
                        .Take(60);
                    foreach (var f in archivos)
                        e.Archivos.Add(new ArchivoReporte("lote/" + SinCaracteresRaros(f.Name), f.FullName, TipoArchivoReporte.Lote));
                }
                catch { }
            }

            // ── diagnósticos generados ──
            DateTime ahora = DateTime.UtcNow;
            if (fuentes != null)
                e.Archivos.Add(ArchivoReporte.DeTexto("diagnostico/fuentes.txt", fuentes.Resumen(ahora)));
            e.Archivos.Add(ArchivoReporte.DeTexto("diagnostico/estado.txt", EstadoMotor(host, nodos, ahora)));
            try
            {
                var sis = AccionesSoporte.Buscar("sistema");
                if (sis != null && sis.Ejecutar != null)
                    e.Archivos.Add(ArchivoReporte.DeTexto("diagnostico/sistema.txt",
                        sis.Ejecutar(new Dictionary<string, string>())));
            }
            catch { }

            return e;
        }

        /// <summary>&lt;install&gt; = la carpeta que contiene Engine\ y Desktop\.
        /// En desarrollo (bin\Debug\net9.0) no existe: se usa la del ejecutable
        /// y los logs de instalación simplemente figuran como "no existe".</summary>
        private static string CarpetaInstalacion()
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                if (string.Equals(Path.GetFileName(baseDir), "Engine", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(baseDir) ?? baseDir;
            }
            catch { }
            return baseDir;
        }

        private static bool EsConfigPropia(string nombre)
        {
            // Los .json del runtime de .NET no son config de PilotX.
            return !nombre.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                && !nombre.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
        }

        private static void AgregarCarpeta(EntradaReporteFalla e, string dir, string prefijo, string patron,
            TipoArchivoReporte tipo, Func<string, bool> filtro)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, patron).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(80))
                {
                    string nombre = Path.GetFileName(f);
                    if (filtro != null && !filtro(nombre)) continue;
                    e.Archivos.Add(new ArchivoReporte(prefijo + "/" + SinCaracteresRaros(nombre), f, tipo));
                }
            }
            catch { }
        }

        private static string SinCaracteresRaros(string nombre)
        {
            var sb = new StringBuilder(nombre.Length);
            foreach (char c in nombre)
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ' ' ? c : '_');
            return sb.ToString();
        }

        private static string EstadoMotor(GuidanceEngineHost host, Func<IReadOnlyList<AgroParallel.Models.NodoStatus>> nodos, DateTime ahora)
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            sb.AppendLine("ESTADO DEL MOTOR AL REPORTAR");
            sb.AppendLine("============================");
            sb.AppendLine("hora local : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv));
            try
            {
                if (host != null)
                {
                    sb.AppendLine("lote       : " + (host.IsJobStarted ? host.currentFieldDirectory : "(ninguno abierto)"));
                    bool gpsVivo = host.lastFixUtc != default(DateTime) && (ahora - host.lastFixUtc).TotalSeconds <= 3;
                    sb.AppendLine("gps        : " + (gpsVivo
                        ? "vivo, calidad " + (host.Pn != null ? host.Pn.fixQuality.ToString(inv) : "?")
                        : host.lastFixUtc == default(DateTime) ? "nunca hubo fix" : "SIN FIX hace "
                          + MonitorFuentesMudas.FormatearDuracion(ahora - host.lastFixUtc)));
                    sb.AppendLine("velocidad  : " + (gpsVivo ? host.avgSpeed.ToString("0.0", inv) + " km/h" : "0 (sin GPS)"));
                    sb.AppendLine("piloto     : " + (host.isBtnAutoSteerOn ? "ENCENDIDO" : "apagado"));
                    sb.AppendLine("secciones  : " + (host.autoBtnState == btnStates.Auto ? "auto"
                        : host.manualBtnState == btnStates.On ? "manual" : "apagadas"));
                    sb.AppendLine("simulador  : " + (host.isSimTimerEnabled ? "SÍ" : "no"));
                }
            }
            catch (Exception ex) { sb.AppendLine("(no se pudo leer el estado: " + ex.Message + ")"); }

            sb.AppendLine();
            sb.AppendLine("NODOS MQTT");
            sb.AppendLine("----------");
            try
            {
                var lista = nodos?.Invoke();
                if (lista == null || lista.Count == 0) sb.AppendLine("(ninguno registrado)");
                else
                    foreach (var n in lista)
                        sb.AppendLine(string.Format(inv, "· {0} {1}  {2}  fw {3}  ip {4}  último visto hace {5}",
                            n.Type, n.Uid, n.Online ? "ONLINE " : "offline", n.Firmware, n.Ip,
                            n.LastSeenUtc == default(DateTime) ? "—" : MonitorFuentesMudas.FormatearDuracion(ahora - n.LastSeenUtc)));
            }
            catch (Exception ex) { sb.AppendLine("(no se pudo leer: " + ex.Message + ")"); }
            return sb.ToString();
        }
    }
}
