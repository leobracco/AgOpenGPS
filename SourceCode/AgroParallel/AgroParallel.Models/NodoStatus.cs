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

        /// <summary>true si se vio tráfico en los últimos N segundos (default 30).</summary>
        public bool Online { get; set; }

        /// <summary>Telemetría en vivo de los motores (sólo QuantiX). Indexado por Id.
        /// Vacío si no es un nodo QuantiX o si todavía no llegó status_live.</summary>
        public List<MotorLive> MotorsLive { get; set; }
    }
}
