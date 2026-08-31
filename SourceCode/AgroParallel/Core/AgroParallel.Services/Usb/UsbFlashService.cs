using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using AgroParallel.Models;

namespace AgroParallel.Usb
{
    // Envuelve esptool.exe (bundleado en <engineBaseDir>/tools/esptool/) para
    // flashear un nodo (QuantiX/VistaX/etc.) por USB desde la propia cabina,
    // sin PC externa ni internet. Un flasheo a la vez (lock). El progreso lo
    // parsea EsptoolOutputParser y se expone por Estado() para el polling
    // de la UI. El proceso en sí (Iniciar) se completa en la Task 6.
    public sealed class UsbFlashService
    {
        private readonly string _esptool;
        private readonly object _lock = new object();
        private UsbFlashEstadoDto _estado = new UsbFlashEstadoDto { Fase = "idle", Resultado = null };

        public UsbFlashService(string engineBaseDir)
        {
            _esptool = Path.Combine(engineBaseDir, "tools", "esptool", "esptool.exe");
        }

        public bool EsptoolPresente => File.Exists(_esptool);

        // Puertos COM visibles para elegir a qué nodo flashear.
        public IReadOnlyList<PuertoComDto> ListarPuertos()
        {
            var lista = new List<PuertoComDto>();
            foreach (var p in SerialPort.GetPortNames())
                lista.Add(new PuertoComDto { Port = p, Descripcion = DescribirPuerto(p) });
            return lista;
        }

        // Descripción del puerto: best-effort, sin depender del registro de
        // Windows (evita sumar Microsoft.Win32.Registry solo para esto). Si
        // el día de mañana hace falta un nombre lindo (ej. "CP2102 USB to
        // UART"), se agrega acá sin cambiar la firma pública.
        private static string DescribirPuerto(string port) => port;

        // Args para ProcessStartInfo.ArgumentList (NUNCA shell-concat). Puro,
        // sin IO: testeable en aislamiento.
        // NOTA: devuelve List<string> (no IReadOnlyList<string>) para que los
        // tests puedan usar IndexOf directo sobre el resultado; sigue siendo
        // asignable a IReadOnlyList<string> donde haga falta (Task 6).
        public static List<string> ArmarArgs(string puerto, string modo, string binPath, bool borrarAntes)
        {
            var a = new List<string> { "--chip", "auto", "--port", puerto, "--baud", "921600", "write_flash" };
            if (borrarAntes) a.Add("--erase-all");
            if (modo == "completo") { a.Add("0x0"); a.Add(binPath); }
            else { a.Add("0x10000"); a.Add(binPath); }
            return a;
        }

        // Snapshot del estado en curso, para el polling de la UI.
        public UsbFlashEstadoDto Estado()
        {
            lock (_lock)
                return new UsbFlashEstadoDto
                {
                    EnCurso = _estado.EnCurso,
                    Fase = _estado.Fase,
                    Pct = _estado.Pct,
                    Resultado = _estado.Resultado,
                    Codigo = _estado.Codigo,
                    Log = _estado.Log
                };
        }

        // Esqueleto de firma: correr esptool.exe con ArmarArgs() y parsear su
        // salida con EsptoolOutputParser queda para la Task 6.
        public bool Iniciar(string binPath, string modo, string puerto, bool borrarAntes, out string codigoError)
        {
            codigoError = null;
            return false; // reemplazado en Task 6
        }
    }
}
