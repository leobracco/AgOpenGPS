// IQuantiXService — control del bridge MQTT a los ESP32 QuantiX
// (PID de motores de siembra). Vive sobre IAogStateProvider para
// leer velocidad/secciones/posición sin acoplar a FormGPS.

namespace AgroParallel.Services.Abstractions
{
    public interface IQuantiXService
    {
        bool IsRunning { get; }
        int MessagesSent { get; }

        void Start();
        void Stop();

        /// <summary>Recargar la config de motores desde disco.</summary>
        void ReloadConfig();
    }
}
