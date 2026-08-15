// ============================================================================
// IWifiService.cs — manejo de WiFi PROPIO de PilotX (kiosko-friendly).
//
// El operario conecta la pantalla a una red desde la página del Hub, con el
// teclado de PilotX — nunca ve el panel del sistema operativo (el camino
// viejo abría ms-settings de Windows: mostraba las redes sin estado y pedía
// el teclado del SO, inservible en cabina táctil / modo kiosko).
//
// Implementaciones: WifiServiceWindows (netsh wlan) y WifiServiceLinux
// (nmcli / NetworkManager).
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Services.Abstractions
{
    public sealed class WifiRedInfo
    {
        public string Ssid { get; set; }
        /// <summary>Señal 0..100.</summary>
        public int SenalPct { get; set; }
        /// <summary>false = red abierta (sin clave).</summary>
        public bool Segura { get; set; }
        /// <summary>true = es la red actualmente conectada.</summary>
        public bool Conectada { get; set; }
    }

    public sealed class WifiEstado
    {
        public bool Conectado { get; set; }
        public string Ssid { get; set; }
        public string Ip { get; set; }
    }

    public interface IWifiService
    {
        /// <summary>Escanea las redes visibles. SIEMPRE marca cuál está
        /// conectada (merge con el estado — el listado del SO no lo trae).</summary>
        List<WifiRedInfo> Escanear();

        WifiEstado Estado();

        /// <summary>Conecta a una red. clave vacía/null = red abierta.
        /// Espera hasta confirmar (o timeout) y deja el motivo en error.</summary>
        bool Conectar(string ssid, string clave, out string error);

        bool Desconectar(out string error);
    }
}
