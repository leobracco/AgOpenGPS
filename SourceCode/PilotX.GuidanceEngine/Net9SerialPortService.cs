// ============================================================================
// Net9SerialPortService.cs — implementación de ISerialPortService para este
// proceso (net9.0). Igual a AgIO/Source/Classes/WindowsSerialPortService.cs
// (mismo wrapper 1:1 sobre System.IO.Ports.SerialPort) — se reimplementa acá
// en vez de referenciar el proyecto AgIO (que es un .exe WinForms net48
// completo) para no arrastrar esa dependencia a un proceso que debe quedar
// puro net9.0/portable.
// ============================================================================

using System;
using System.IO.Ports;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine
{
    public sealed class Net9SerialPortService : ISerialPortService
    {
        private SerialPort _port;

        private bool _dtrEnable;
        private bool _rtsEnable;
        private int _writeTimeout = SerialPort.InfiniteTimeout;

        public string PortName => _port?.PortName ?? "";
        public int BaudRate => _port?.BaudRate ?? 0;
        public bool IsOpen => _port?.IsOpen ?? false;

        public bool DtrEnable
        {
            get => _port?.DtrEnable ?? _dtrEnable;
            set { _dtrEnable = value; if (_port != null) _port.DtrEnable = value; }
        }

        public bool RtsEnable
        {
            get => _port?.RtsEnable ?? _rtsEnable;
            set { _rtsEnable = value; if (_port != null) _port.RtsEnable = value; }
        }

        public int WriteTimeout
        {
            get => _port?.WriteTimeout ?? _writeTimeout;
            set { _writeTimeout = value; if (_port != null) _port.WriteTimeout = value; }
        }

        public event Action<byte[]> OnDataReceived;

        public void Open(string portName, int baudRate)
        {
            Close();
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                DtrEnable = _dtrEnable,
                RtsEnable = _rtsEnable,
                WriteTimeout = _writeTimeout
            };
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

        public void DiscardInBuffer()
        {
            if (_port != null && _port.IsOpen) _port.DiscardInBuffer();
        }

        public void DiscardOutBuffer()
        {
            if (_port != null && _port.IsOpen) _port.DiscardOutBuffer();
        }

        public string[] GetAvailablePorts() => SerialPort.GetPortNames();

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
