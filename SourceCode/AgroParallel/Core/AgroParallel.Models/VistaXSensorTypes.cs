// ============================================================================
// VistaXSensorTypes.cs — tipos válidos de sensor que puede tomar un cable
// VistaX. El firmware ya distingue MODO_PULSE (pulsos por segundo, semilla /
// fertilizante / rotación / turbina) y MODO_STATE (on/off, bajada herramienta /
// tolva / presión / final de carrera). El nodo recibe la asignación por MQTT
// retenido en `vistax/nodos/{uid}/cables/config` con `modo: pulse|state`.
//
// La UI usa el slug ("semilla", "fertilizante", ...) en el JSON del implemento;
// el helper IsState() lo convierte a modo para publicar al firmware.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Models
{
    public static class VistaXSensorTypes
    {
        // ---- Pulse (cuentan eventos) ----
        public const string Semilla          = "semilla";
        public const string Fertilizante     = "fertilizante";
        public const string RotacionEje      = "rotacion_eje";
        public const string Turbina          = "turbina";

        // ---- State (on/off) ----
        public const string BajadaHerramienta = "bajada_herramienta";
        public const string TolvaVacia        = "tolva_vacia";
        public const string TolvaLlena        = "tolva_llena";
        public const string Presion           = "presion";
        public const string FinalCarrera      = "final_carrera";

        /// <summary>Slugs en el orden que conviene mostrar en la UI.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Semilla, Fertilizante, RotacionEje, Turbina,
            BajadaHerramienta, TolvaVacia, TolvaLlena, Presion, FinalCarrera
        };

        private static readonly HashSet<string> _stateSet = new HashSet<string>(
            new[] { BajadaHerramienta, TolvaVacia, TolvaLlena, Presion, FinalCarrera },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>True si el tipo es on/off (modo "state" en el firmware).</summary>
        public static bool IsState(string tipo) => !string.IsNullOrEmpty(tipo) && _stateSet.Contains(tipo);

        /// <summary>"pulse" o "state" según el slug. Lo consume el publisher al nodo.</summary>
        public static string ModoFirmware(string tipo) => IsState(tipo) ? "state" : "pulse";

        /// <summary>True si el tipo es contador de semilla (entra al cálculo de SPM / densidad).</summary>
        public static bool IsSemilla(string tipo) =>
            string.Equals(tipo, Semilla, StringComparison.OrdinalIgnoreCase);
    }
}
