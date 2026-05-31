// ============================================================================
// VistaXConfigService.cs — load/save vistaX.json + implemento JSON.
//
// Mismas convenciones que el legacy (AgroParallel.VistaX.VistaXConfig) para
// poder togglear UI Web ↔ WinForms sin perder configs:
//   - vistaX.json en AppDomain.BaseDirectory.
//   - Implemento: ImplementoJsonPath si está seteado, sino el primer
//     data\implementos\*.json relativo al BaseDirectory.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class VistaXConfigService : IVistaXConfigService
    {
        private const string ConfigFileName = "vistaX.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static string ConfigPath()
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

        public VistaXConfigDto GetConfig()
        {
            try
            {
                string p = ConfigPath();
                if (File.Exists(p))
                {
                    var dto = JsonSerializer.Deserialize<VistaXConfigDto>(
                        File.ReadAllText(p), ReadOpts);
                    if (dto != null) return Sanitize(dto);
                }
            }
            catch { }
            return new VistaXConfigDto();
        }

        public void SaveConfig(VistaXConfigDto dto)
        {
            if (dto == null) return;
            try
            {
                File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(Sanitize(dto), WriteOpts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[vistax] error guardando config: " + ex.Message);
            }
        }

        private static VistaXConfigDto Sanitize(VistaXConfigDto d)
        {
            if (d.BrokerPort <= 0) d.BrokerPort = 1883;
            if (d.UiUpdateIntervalMs <= 0) d.UiUpdateIntervalMs = 500;
            if (d.SensorTimeoutMs <= 0) d.SensorTimeoutMs = 3000;
            if (d.UmbralSensoresActivos <= 0) d.UmbralSensoresActivos = 3;
            if (d.TiempoConfirmacionMs <= 0) d.TiempoConfirmacionMs = 500;
            if (string.IsNullOrEmpty(d.BrokerAddress)) d.BrokerAddress = "127.0.0.1";
            if (string.IsNullOrEmpty(d.TelemetriaTopic)) d.TelemetriaTopic = "vistax/nodos/telemetria";
            if (string.IsNullOrEmpty(d.SpeedTopic)) d.SpeedTopic = "aog/machine/speed";
            if (string.IsNullOrEmpty(d.SectionsTopic)) d.SectionsTopic = "sections/state";
            if (string.IsNullOrEmpty(d.MetodoInicio)) d.MetodoInicio = "sensores";
            return d;
        }

        public string GetImplementoPath()
        {
            var cfg = GetConfig();
            if (!string.IsNullOrEmpty(cfg.ImplementoJsonPath) && File.Exists(cfg.ImplementoJsonPath))
                return cfg.ImplementoJsonPath;

            string dataDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "data", "implementos");
            if (Directory.Exists(dataDir))
            {
                string[] files = Directory.GetFiles(dataDir, "*.json");
                if (files.Length > 0) return files[0];
            }
            return null;
        }

        public VistaXImplementoDto GetImplemento()
        {
            string p = GetImplementoPath();
            if (p == null || !File.Exists(p)) return new VistaXImplementoDto();
            try
            {
                var dto = JsonSerializer.Deserialize<VistaXImplementoDto>(
                    File.ReadAllText(p), ReadOpts);
                return dto ?? new VistaXImplementoDto();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[vistax] error leyendo implemento: " + ex.Message);
                return new VistaXImplementoDto();
            }
        }

        public void SaveImplemento(VistaXImplementoDto dto)
        {
            if (dto == null) return;
            string p = GetImplementoPath();
            if (p == null)
            {
                // Si no hay path configurado, crear data/implementos/default.json.
                string dir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "data", "implementos");
                try { Directory.CreateDirectory(dir); } catch { }
                p = Path.Combine(dir, "default.json");
                // Y actualizar la config para futuras lecturas.
                var cfg = GetConfig();
                cfg.ImplementoJsonPath = p;
                SaveConfig(cfg);
            }
            try
            {
                File.WriteAllText(p, JsonSerializer.Serialize(dto, WriteOpts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[vistax] error guardando implemento: " + ex.Message);
            }
        }
    }
}
