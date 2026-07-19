// ============================================================================
// INtripClientService.cs — Cliente NTRIP portable (netstandard2.0).
// Extraído del NTRIPComm.Designer.cs de CoreX. Maneja la conexión TCP
// al caster NTRIP, autorización, envío periódico de GGA, recepción de
// datos RTCM. Sin WinForms.
// ============================================================================

using System;

namespace AgroParallel.Services.Abstractions
{
    public sealed class NtripConfig
    {
        public string CasterIp { get; set; }
        public int CasterPort { get; set; } = 2101;
        public string Mount { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int SendGgaIntervalSec { get; set; }
        public bool IsHttp10 { get; set; }
        public bool IsTcp { get; set; }
        // Si true, usa lat/lon manual en vez del GPS live.
        public bool IsGgaManual { get; set; }
        public double ManualLat { get; set; }
        public double ManualLon { get; set; }
    }

    public interface INtripClientService
    {
        bool IsConnected { get; }
        bool IsConnecting { get; }
        long TotalBytes { get; }
        string CasterIp { get; }

        // Conectar al caster (intento inmediato). gpsFeedback = callback que
        // devuelve lat/lon/alt/fix actuales para construir la GGA.
        // La reconexión/watchdog/GGA periódica las maneja SecondTick.
        void Connect(NtripConfig config, Func<NtripGpsData> gpsFeedback);

        void Disconnect();

        // Llamar cada segundo desde el loop principal.
        void SecondTick();

        // Datos RTCM recibidos del caster → el host los reenvía al GPS.
        event Action<byte[]> OnRtcmData;

        // Se envió una GGA periódica al caster (para feedback de UI).
        event Action OnGgaSent;
    }

    public sealed class NtripGpsData
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public int FixQuality { get; set; }
        public int Satellites { get; set; }
        public double Hdop { get; set; }
        public double Age { get; set; }
    }
}
