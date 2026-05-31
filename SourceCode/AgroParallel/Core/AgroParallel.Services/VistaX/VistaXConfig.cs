// ============================================================================
// VistaXConfig.cs - Configuración adaptada a la infraestructura real VistaX
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/VistaXConfig.cs
// Target: net48 (C# 7.3)
// ============================================================================

using System;
using System.IO;
using System.Text.Json;

namespace AgroParallel.VistaX
{
    public class VistaXConfig
    {
        public bool Enabled { get; set; }
        public string BrokerAddress { get; set; }
        public int BrokerPort { get; set; }
        public string ClientId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool UseTls { get; set; }
        public string TelemetriaTopic { get; set; }
        public string SpeedTopic { get; set; }
        public string SectionsTopic { get; set; }
        public string ImplementoJsonPath { get; set; }
        public int UiUpdateIntervalMs { get; set; }
        public int SensorTimeoutMs { get; set; }
        public bool LogToFieldRecord { get; set; }

        // Servidor de interfaz moderna (Node.js)
        public string ServerUrl { get; set; }

        // Método de inicio de monitoreo
        public string MetodoInicio { get; set; }
        public int UmbralSensoresActivos { get; set; }
        public int TiempoConfirmacionMs { get; set; }

        // Layout del panel embebido en PilotX — ajustables sin recompilar
        public int PanelHeight { get; set; }
        public int PanelWidthPercent { get; set; }
        public int PanelBottomMargin { get; set; }

        // Tamaños de popups (config, detalle surco) — sin bordes de Windows
        public int PopupConfigWidth { get; set; }
        public int PopupConfigHeight { get; set; }
        public int PopupDetalleWidth { get; set; }
        public int PopupDetalleHeight { get; set; }
        public int PopupDefaultWidth { get; set; }
        public int PopupDefaultHeight { get; set; }

        // Silencia la alarma sonora (beep cuando HasAlarm=true). El usuario
        // lo toggelea desde el boton del header del panel.
        public bool AlarmMuted { get; set; }

        // Path alternativo para guardar logs NDJSON y SHP (ej: "D:\\" o
        // "E:\\VistaX"). Si está vacío guarda en el campo actual de PilotX.
        // Útil para no llenar el disco del equipo.
        public string LogOutputDrive { get; set; }

        private static readonly string ConfigFileName = "vistaX.json";

        public VistaXConfig()
        {
            Enabled = false;
            BrokerAddress = "127.0.0.1";
            BrokerPort = 1883;
            ClientId = "PilotX_VistaX";
            Username = "";
            Password = "";
            UseTls = false;
            // Topic wildcard que cubre todos los nodos VistaX. El firmware
            // publica en `vistax/{UID}/telemetria` (un canal por nodo).
            // El legacy literal `vistax/nodos/telemetria` lo usaba el vistax-server
            // Node.js (deprecado) — ya no publica nadie ahí.
            TelemetriaTopic = "vistax/+/telemetria";
            SpeedTopic = "aog/machine/speed";
            SectionsTopic = "sections/state";
            ImplementoJsonPath = "";
            UiUpdateIntervalMs = 500;
            SensorTimeoutMs = 3000;
            LogToFieldRecord = true;

            ServerUrl = "http://localhost:3000/bar";

            MetodoInicio = "sensores";
            UmbralSensoresActivos = 3;
            TiempoConfirmacionMs = 500;

            // Defaults de layout del panel embebido (pegado al footer por
            // defecto; el user puede ajustar desde FormVistaXConfig).
            PanelHeight = 150;
            PanelWidthPercent = 70;
            PanelBottomMargin = 20;

            // Defaults de popups sin bordes
            PopupConfigWidth = 900;
            PopupConfigHeight = 700;
            PopupDetalleWidth = 380;
            PopupDetalleHeight = 520;
            PopupDefaultWidth = 500;
            PopupDefaultHeight = 400;
        }

        public static VistaXConfig Load()
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                var def = new VistaXConfig();
                def.Save();
                return def;
            }

            try
            {
                string json = File.ReadAllText(path);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var config = JsonSerializer.Deserialize<VistaXConfig>(json, opts);

                // Sanitizar valores de layout (por si el JSON tiene valores absurdos)
                if (config != null)
                {
                    if (config.PanelHeight <= 0) config.PanelHeight = 120;
                    if (config.PanelWidthPercent <= 0 || config.PanelWidthPercent > 100) config.PanelWidthPercent = 70;
                    if (config.PanelBottomMargin < 0) config.PanelBottomMargin = 60;

                    // Migración: configs viejas tienen el topic legacy del
                    // vistax-server Node.js (deprecado). El firmware ya publica
                    // por-UID — pasamos al wildcard sin pedirle al usuario que
                    // edite el JSON a mano.
                    if (config.TelemetriaTopic == "vistax/nodos/telemetria")
                    {
                        config.TelemetriaTopic = "vistax/+/telemetria";
                        config.Save();
                    }
                }

                return config ?? new VistaXConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] Error config: " + ex.Message);
                return new VistaXConfig();
            }
        }

        public void Save()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(GetConfigPath(), JsonSerializer.Serialize(this, opts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] Error guardando: " + ex.Message);
            }
        }

        public ImplementoConfig LoadImplemento()
        {
            if (!string.IsNullOrEmpty(ImplementoJsonPath) && File.Exists(ImplementoJsonPath))
            {
                return ParseImplemento(ImplementoJsonPath);
            }

            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "implementos");
            if (Directory.Exists(dataDir))
            {
                string[] files = Directory.GetFiles(dataDir, "*.json");
                if (files.Length > 0)
                {
                    return ParseImplemento(files[0]);
                }
            }

            System.Diagnostics.Debug.WriteLine("[VistaX] No se encontró JSON de implemento");
            return new ImplementoConfig();
        }

        // Persiste el implemento al mismo path desde el que se cargó. Devuelve
        // true si pudo escribir. Usado para guardar el estado de Muted por
        // sensor desde la UI del piloto/panel.
        public bool SaveImplemento(ImplementoConfig imp)
        {
            if (imp == null) return false;
            string path = ImplementoJsonPath;
            if (string.IsNullOrEmpty(path))
            {
                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "implementos");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                path = Path.Combine(dataDir, "implemento.json");
            }
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, JsonSerializer.Serialize(imp, opts));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] Error guardando implemento: " + ex.Message);
                return false;
            }
        }

        private static ImplementoConfig ParseImplemento(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var config = JsonSerializer.Deserialize<ImplementoConfig>(json, opts);
                System.Diagnostics.Debug.WriteLine("[VistaX] Implemento cargado: " + path
                    + " (" + (config.MapeoSensores != null ? config.MapeoSensores.Count : 0) + " sensores)");
                return config ?? new ImplementoConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] Error parseando implemento: " + ex.Message);
                return new ImplementoConfig();
            }
        }

        private static string GetConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        }
    }
}
