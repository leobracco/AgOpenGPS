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

        // Cacheados hasta que haya un puerto real (igual semántica que
        // System.IO.Ports.SerialPort: asignables con el puerto cerrado).
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
