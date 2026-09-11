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

        // DTR/RTS: reset de placas Arduino al conectar. Asignables antes de
        // Open() (se cachean y aplican al abrir, igual que System.IO.Ports.SerialPort).
        bool DtrEnable { get; set; }
        bool RtsEnable { get; set; }

        // Timeout de escritura (ms). Asignable antes de Open().
        int WriteTimeout { get; set; }

        void Open(string portName, int baudRate);
        void Close();

        void Write(byte[] data, int offset, int count);

        // Vacía los buffers de entrada/salida — no-op si el puerto está cerrado.
        void DiscardInBuffer();
        void DiscardOutBuffer();

        // Datos recibidos del puerto serie.
        event Action<byte[]> OnDataReceived;

        // Lista de puertos disponibles (COM en Windows, USB devices en Android).
        string[] GetAvailablePorts();
    }
}
