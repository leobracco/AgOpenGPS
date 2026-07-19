// ============================================================================
// ISerialPortService.cs — Interfaz portable para puertos serie.
// En Windows: System.IO.Ports.SerialPort (ya funciona).
// En Android: UsbSerialForAndroid (CDC/FTDI sobre USB-OTG).
// CoreX tiene 5 puertos: GPS, IMU, Steer, Machine, RTCM (radio/pass).
// Esta interfaz abstrae UN puerto; el host instancia uno por dispositivo.
// ============================================================================

using System;

namespace AgroParallel.Services.Abstractions
{
    public interface ISerialPortService : IDisposable
    {
        string PortName { get; }
        int BaudRate { get; }
        bool IsOpen { get; }

        void Open(string portName, int baudRate);
        void Close();

        void Write(byte[] data, int offset, int count);

        // Datos recibidos del puerto serie.
        event Action<byte[]> OnDataReceived;

        // Lista de puertos disponibles (COM en Windows, USB devices en Android).
        string[] GetAvailablePorts();
    }
}
