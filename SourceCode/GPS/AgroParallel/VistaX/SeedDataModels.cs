// ============================================================================
// SeedDataModels.cs - Modelos para payloads MQTT reales de VistaX
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/SeedDataModels.cs
// Target: net48 (C# 7.3)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.VistaX
{
    // =========================================================================
    // Tipos de sensor — definición central usada por todo el módulo VistaX.
    // =========================================================================

    public static class TipoSensor
    {
        public const string Semilla = "semilla";
        public const string FertiLinea = "ferti_linea";
        public const string FertiCostado = "ferti_costado";
        public const string BajadaHerramienta = "bajada_herramienta";
        public const string Turbina = "turbina";
        public const string Tolva = "tolva";

        // Todos los tipos disponibles (orden = prioridad en UI).
        public static readonly string[] Todos = new[]
        {
            Semilla, FertiLinea, FertiCostado, BajadaHerramienta, Turbina, Tolva
        };

        // Nombres amigables para UI.
        public static string NombreAmigable(string tipo)
        {
            if (tipo == null) return "?";
            switch (tipo)
            {
                case Semilla: return "Semilla";
                case FertiLinea: return "Fertilizante en l\u00EDnea";
                case FertiCostado: return "Fertilizante al costado";
                case BajadaHerramienta: return "Bajada de herramienta";
                case Turbina: return "Turbina";
                case Tolva: return "Tolva";
                default: return tipo;
            }
        }

        // Color asociado a cada tipo para UI.
        public static System.Drawing.Color GetColor(string tipo)
        {
            if (tipo == null) return System.Drawing.Color.FromArgb(120, 120, 120);
            switch (tipo)
            {
                case Semilla: return System.Drawing.Color.FromArgb(0, 230, 118);
                case FertiLinea: return System.Drawing.Color.FromArgb(0, 229, 255);
                case FertiCostado: return System.Drawing.Color.FromArgb(255, 234, 0);
                case BajadaHerramienta: return System.Drawing.Color.FromArgb(255, 160, 0);
                case Turbina: return System.Drawing.Color.FromArgb(156, 39, 176);
                case Tolva: return System.Drawing.Color.FromArgb(121, 85, 72);
                default: return System.Drawing.Color.FromArgb(120, 120, 120);
            }
        }

        // ¿Genera alarma de flujo cuando vale 0?
        public static bool GeneraAlarmaFlujo(string tipo)
        {
            return tipo == Semilla || tipo == FertiLinea || tipo == FertiCostado;
        }

        // ¿Es sensor por surco (se muestra en la barra principal)?
        public static bool EsPorSurco(string tipo)
        {
            return tipo == Semilla || tipo == FertiLinea || tipo == FertiCostado;
        }

        // ¿Es sensor global (no por surco, se muestra como indicador)?
        public static bool EsGlobal(string tipo)
        {
            return tipo == Turbina || tipo == Tolva || tipo == BajadaHerramienta;
        }
    }

    // =========================================================================
    // Payload crudo del ESP32: vistax/nodos/telemetria
    // { "uid": "VX-S3-A1", "sensores": [{ "cable": 1, "valor": 14.2, "raw": 5 }] }
    // =========================================================================

    public class EspTelemetriaPayload
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("sensores")]
        public List<EspSensorData> Sensores { get; set; }
    }

    public class EspSensorData
    {
        [JsonPropertyName("cable")]
        public int Cable { get; set; }

        [JsonPropertyName("valor")]
        public double Valor { get; set; }

        [JsonPropertyName("raw")]
        public int Raw { get; set; }
    }

    // =========================================================================
    // Payload de secciones AOG: sections/state
    // { "t1": [1,1,0,1,...], "t2": [1,1,1,...] }
    // =========================================================================

    public class SectionsStatePayload
    {
        [JsonPropertyName("t1")]
        public List<int> T1 { get; set; }

        [JsonPropertyName("t2")]
        public List<int> T2 { get; set; }
    }

    // =========================================================================
    // Configuración de un sensor (mapeo_sensores del JSON de implemento)
    // =========================================================================

    public class SensorConfig
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; }

        [JsonPropertyName("cable")]
        public int Cable { get; set; }

        [JsonPropertyName("pin")]
        public int Pin { get; set; }

        // Surco principal que monitorea este sensor.
        [JsonPropertyName("bajada")]
        public int Bajada { get; set; }

        // Rango de surcos cubiertos (para caso 96 surcos / 8 sensores).
        // Si ambos son 0, el sensor cubre solo Bajada (1:1).
        [JsonPropertyName("surco_desde")]
        public int SurcoDesde { get; set; }

        [JsonPropertyName("surco_hasta")]
        public int SurcoHasta { get; set; }

        [JsonPropertyName("tipo")]
        public string Tipo { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("tren")]
        public int Tren { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        // Sección AOG a la que pertenece (0 = auto-calcular por posición).
        [JsonPropertyName("seccion_aog")]
        public int SeccionAOG { get; set; }

        public SensorConfig()
        {
            Tren = 1;
            IsActive = true;
            Tipo = "semilla";
        }

        // Retorna true si este sensor cubre el surco dado.
        public bool CobreSurco(int surco)
        {
            if (SurcoDesde > 0 && SurcoHasta > 0)
                return surco >= SurcoDesde && surco <= SurcoHasta;
            return surco == Bajada;
        }

        // Cantidad de surcos que cubre este sensor.
        public int SurcosCubiertos
        {
            get
            {
                if (SurcoDesde > 0 && SurcoHasta > 0)
                    return SurcoHasta - SurcoDesde + 1;
                return 1;
            }
        }
    }

    // =========================================================================
    // Configuración del implemento (perfil JSON completo)
    // =========================================================================

    public class ImplementoSetup
    {
        [JsonPropertyName("densidad_objetivo")]
        public double DensidadObjetivo { get; set; }

        [JsonPropertyName("tolerancia_desvio")]
        public double ToleranciaDesvio { get; set; }

        [JsonPropertyName("distancia_entre_surcos")]
        public double DistanciaEntreSurcos { get; set; }

        [JsonPropertyName("factor_k_default")]
        public double FactorK { get; set; }

        [JsonPropertyName("objetivos_tren")]
        public Dictionary<string, double> ObjetivosTren { get; set; }

        // Total de surcos físicos del implemento (puede ser != cantidad de sensores).
        // Ej: 96 surcos con 8 sensores, o 43 surcos con 43 sensores.
        [JsonPropertyName("total_surcos")]
        public int TotalSurcos { get; set; }

        // Cantidad de secciones AOG configuradas para este implemento.
        // Puede ser diferente de surcos y de sensores.
        // Ej: 43 surcos, 43 sensores, 2 secciones AOG.
        [JsonPropertyName("secciones_aog")]
        public int SeccionesAOG { get; set; }

        // Ancho total del implemento en metros (leído de AOG o manual).
        [JsonPropertyName("ancho_implemento")]
        public double AnchoImplemento { get; set; }

        public ImplementoSetup()
        {
            DensidadObjetivo = 16;
            ToleranciaDesvio = 20;
            DistanciaEntreSurcos = 0.191;
            FactorK = 0.15;
        }

        // Calcula qué surcos cubre la sección AOG dada (1-based).
        // Distribución uniforme: sección 1 = surcos 1..N/S, etc.
        public void GetSurcosPorSeccion(int seccion, int totalSurcos, int totalSecciones,
            out int desde, out int hasta)
        {
            if (totalSecciones <= 0 || totalSurcos <= 0 || seccion <= 0)
            {
                desde = 0; hasta = 0; return;
            }
            int porSeccion = totalSurcos / totalSecciones;
            int resto = totalSurcos % totalSecciones;
            desde = (seccion - 1) * porSeccion + Math.Min(seccion - 1, resto) + 1;
            int extra = seccion <= resto ? 1 : 0;
            hasta = desde + porSeccion + extra - 1;
        }
    }

    // Definicion explicita de un tren dentro del implemento. Replica la
    // estructura del editor "Trenes de siembra" de VistaX-Core: cada tren
    // tiene un ID + un nombre amigable + una cantidad de surcos.
    public class TrenConfig
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("surcos")]
        public int Surcos { get; set; }

        public TrenConfig() { Nombre = ""; }
    }

    public class ImplementoConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("nombre")]
        public string Nombre { get; set; }

        [JsonPropertyName("setup")]
        public ImplementoSetup Setup { get; set; }

        // Lista ordenada de trenes (editable desde FormVistaXTrenes). Si el
        // JSON legacy no la tiene, se deriva de MapeoSensores al cargar.
        [JsonPropertyName("trenes")]
        public List<TrenConfig> Trenes { get; set; }

        [JsonPropertyName("mapeo_sensores")]
        public List<SensorConfig> MapeoSensores { get; set; }

        public ImplementoConfig()
        {
            Setup = new ImplementoSetup();
            Trenes = new List<TrenConfig>();
            MapeoSensores = new List<SensorConfig>();
        }
    }

    // =========================================================================
    // Método de inicio de monitoreo
    // =========================================================================

    public enum MetodoInicioMonitoreo
    {
        Sensores = 0,
        Herramienta = 1,
        Pintando = 2,
        Manual = 3
    }

    // =========================================================================
    // Estado de cada surco procesado
    // =========================================================================

    public enum RowState
    {
        Ok = 0,
        Failure = 1,
        NoData = 2,
        LowRate = 3,
        HighRate = 4
    }

    public class SurcoState
    {
        public int Bajada { get; set; }
        public string Tipo { get; set; }
        public int Tren { get; set; }
        public double Valor { get; set; }
        public double Spm { get; set; }
        public int NuevasSemillas { get; set; }
        public bool Alerta { get; set; }
        public bool SeccionCortada { get; set; }
        public DateTime LastUpdate { get; set; }

        public RowState State
        {
            get
            {
                if (SeccionCortada) return RowState.NoData;
                if (Alerta) return RowState.Failure;
                if (Spm <= 0) return RowState.NoData;
                return RowState.Ok;
            }
        }
    }

    // =========================================================================
    // Layout de un tren para centrado de sensores en la UI
    // =========================================================================

    public class TrenLayout
    {
        [JsonPropertyName("tren")]
        public int Tren { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("sensorWidthPx")]
        public int SensorWidthPx { get; set; }

        [JsonPropertyName("spacingPx")]
        public int SpacingPx { get; set; }

        [JsonPropertyName("totalWidthPx")]
        public int TotalWidthPx { get; set; }

        [JsonPropertyName("offsetXPx")]
        public int OffsetXPx { get; set; }

        // Objetivo de siembra (semillas/m) para este tren. Puede venir de
        // ImplementoSetup.ObjetivosTren["1"/"2"] o caer a DensidadObjetivo.
        [JsonPropertyName("objetivo")]
        public double Objetivo { get; set; }

        [JsonPropertyName("surcos")]
        public SurcoState[] Surcos { get; set; }

        public TrenLayout()
        {
            Surcos = new SurcoState[0];
        }
    }

    // =========================================================================
    // Snapshot para UI (thread-safe)
    // =========================================================================

    public class SeedMonitorSnapshot
    {
        public double Velocidad { get; set; }
        public double SpmPromedio { get; set; }
        public int FallasActivas { get; set; }
        public int SurcosActivos { get; set; }
        public SurcoState[] Surcos { get; set; }
        public TrenLayout[] Trenes { get; set; }
        public int ContainerWidthPx { get; set; }
        public DateTime LastUpdate { get; set; }
        public bool IsConnected { get; set; }
        public bool HasAlarm { get; set; }
        public string AlarmMessage { get; set; }
        public string NombreImplemento { get; set; }
        public bool MonitoreoActivo { get; set; }
        public MetodoInicioMonitoreo MetodoInicio { get; set; }

        // Tolerancia de desvio (en %) respecto al objetivo para decidir si un
        // surco esta "ok" o "con desvio" aunque no haya alerta critica.
        // Viene de ImplementoSetup.ToleranciaDesvio.
        public double ToleranciaDesvio { get; set; }

        // Posición GPS del tractor (WGS84).
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        // Heading del implemento (radianes) y geometría para calcular
        // posición de cada surco en el logger.
        public double ToolHeading { get; set; }
        public double ToolEasting { get; set; }
        public double ToolNorthing { get; set; }

        // Posición lateral de cada surco (metros desde centro del implemento).
        // Index = surco global (orden en Surcos[]).
        public double[] SurcoLateralOffsets { get; set; }

        public SeedMonitorSnapshot()
        {
            Surcos = new SurcoState[0];
            Trenes = new TrenLayout[0];
            AlarmMessage = "";
            NombreImplemento = "";
        }
    }
}
