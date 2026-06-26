// NodoStatus — DTO de un nodo AgroParallel descubierto en LAN vía MQTT.
// Producido por INodoRegistryService.

using System;
using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>
    /// Estado snapshot de un dispositivo ESP32 (QuantiX, VistaX, SectionX,
    /// FlowX, StormX, etc.) descubierto vía MQTT en la LAN del tractor.
    /// </summary>
    public sealed class NodoStatus
    {
        /// <summary>UID del nodo (ej. "QX-A1B2C3D4").</summary>
        public string Uid { get; set; }

        /// <summary>Tipo / producto ("QuantiX", "VistaX", "SectionX", ...).</summary>
        public string Type { get; set; }

        /// <summary>IP LAN del nodo.</summary>
        public string Ip { get; set; }

        /// <summary>Versión de firmware reportada en el announcement.</summary>
        public string Firmware { get; set; }

        /// <summary>Cantidad de motores (sólo QuantiX), 0 si no aplica.</summary>
        public int Motors { get; set; }

        /// <summary>Uptime del nodo en segundos al momento del announcement.</summary>
        public long Uptime { get; set; }

        /// <summary>UTC de la última vez que se vio tráfico de este nodo.</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>true si se vio tráfico en los últimos N segundos (default 15).</summary>
        public bool Online { get; set; }

        /// <summary>Razón del último reset reportada por el firmware en el announcement:
        /// "poweron", "task_wdt", "int_wdt", "brownout", "panic", "sw_reset", etc.
        /// null si el firmware no la reporta (versiones viejas). Útil para diagnóstico
        /// de cuelgues en campo sin enchufar USB serial.</summary>
        public string BootReason { get; set; }

        /// <summary>Tanda 2: true si el nodo está en safe-mode (≥3 crashes seguidos).
        /// El firmware sólo acepta ping/clear_safe_mode hasta que el operario lo
        /// resetee explícitamente desde la UI. false en firmwares legacy sin Tanda 2.</summary>
        public bool SafeMode { get; set; }

        /// <summary>Tanda 2: contador persistido en NVS de crashes consecutivos
        /// (task_wdt/int_wdt/panic/brownout/wdt). Boots limpios lo resetean a 0.
        /// 0 en firmwares legacy sin Tanda 2.</summary>
        public int CrashCount { get; set; }

        /// <summary>Telemetría en vivo de los motores (sólo QuantiX). Indexado por Id.
        /// Vacío si no es un nodo QuantiX o si todavía no llegó status_live.</summary>
        public List<MotorLive> MotorsLive { get; set; }
    }
}
