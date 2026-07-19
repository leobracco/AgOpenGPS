// ============================================================================
// WindowsSerialPortService.cs — Implementación Windows de ISerialPortService.
// Wrapper delgado de System.IO.Ports.SerialPort. En Android se reemplaza
// por UsbSerialPortService (USB-OTG con UsbSerialForAndroid).
// ============================================================================

using System;
using System.IO.Ports;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class WindowsSerialPortService : ISerialPortService
    {
        private SerialPort _port;

        public string PortName => _port?.PortName ?? "";
        public int BaudRate => _port?.BaudRate ?? 0;
        public bool IsOpen => _port?.IsOpen ?? false;

        public event Action<byte[]> OnDataReceived;

        public void Open(string portName, int baudRate)
        {
            Close();
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            _port.DataReceived += Port_DataReceived;
            _port.Open();
        }

        public void Close()
        {
            if (_port != null)
            {
                try { _port.DataReceived -= Port_DataReceived; } catch { }
                try { if (_port.IsOpen) _port.Close(); } catch { }
                try { _port.Dispose(); } catch { }
                _port = null;
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            if (_port != null && _port.IsOpen)
                _port.Write(data, offset, count);
        }

        public string[] GetAvailablePorts()
        {
            return SerialPort.GetPortNames();
        }

        public void Dispose() => Close();

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = (SerialPort)sender;
                int n = sp.BytesToRead;
                if (n <= 0) return;
                var buf = new byte[n];
                sp.Read(buf, 0, n);
                OnDataReceived?.Invoke(buf);
            }
            catch { }
        }
    }
}
