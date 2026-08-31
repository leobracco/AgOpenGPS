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
    // de la UI. Iniciar() corre esptool como proceso hijo en background.
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

        // Args para el proceso de esptool (NUNCA shell-concat: se citan uno por
        // uno en ArmarLineaDeComando). Puro, sin IO: testeable en aislamiento.
        // NOTA: devuelve List<string> (no IReadOnlyList<string>) para que los
        // tests puedan usar IndexOf directo sobre el resultado; sigue siendo
        // asignable a IReadOnlyList<string> donde haga falta (la usa Iniciar()).
        public static List<string> ArmarArgs(string puerto, string modo, string binPath, bool borrarAntes)
        {
            var a = new List<string> { "--chip", "auto", "--port", puerto, "--baud", "921600", "write_flash" };
            if (borrarAntes) a.Add("--erase-all");
            if (modo == "completo") { a.Add("0x0"); a.Add(binPath); }
            else { a.Add("0x10000"); a.Add(binPath); }
            return a;
        }

        // Equivalente a mano de ProcessStartInfo.ArgumentList (ver comentario en
        // Iniciar). Replica el algoritmo de citado de argumentos de Windows
        // (PasteArguments del propio runtime .NET) para que paths con espacios
        // o comillas no rompan la línea de comando de esptool.
        // internal (no private): testeado directo desde AgroParallel.Services.Tests
        // (InternalsVisibleTo en el csproj) sin pasar por Iniciar()/proceso real.
        internal static string ArmarLineaDeComando(IEnumerable<string> args)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var arg in args) AppendArgumentoCitado(sb, arg);
            return sb.ToString();
        }

        private static void AppendArgumentoCitado(System.Text.StringBuilder sb, string argumento)
        {
            if (sb.Length != 0) sb.Append(' ');

            if (argumento.Length != 0 && argumento.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                sb.Append(argumento);
                return;
            }

            sb.Append('"');
            int idx = 0;
            while (idx < argumento.Length)
            {
                char c = argumento[idx++];
                if (c == '\\')
                {
                    int numBackslash = 1;
                    while (idx < argumento.Length && argumento[idx] == '\\') { idx++; numBackslash++; }

                    if (idx == argumento.Length)
                        sb.Append('\\', numBackslash * 2); // van seguidas de la comilla de cierre
                    else if (argumento[idx] == '"')
                    {
                        sb.Append('\\', numBackslash * 2 + 1);
                        sb.Append('"');
                        idx++;
                    }
                    else
                        sb.Append('\\', numBackslash);
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\').Append('"');
                    continue;
                }

                sb.Append(c);
            }
            sb.Append('"');
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

        // Lanza esptool.exe como proceso hijo (ArgumentList, nunca shell-concat),
        // lee stdout/stderr línea a línea en background y actualiza _estado con
        // EsptoolOutputParser hasta que termina (o revienta) el flasheo.
        public bool Iniciar(string binPath, string modo, string puerto, bool borrarAntes, out string codigoError)
        {
            codigoError = null;
            if (!EsptoolPresente) { codigoError = "AGP-USB-005"; return false; }
            if (!File.Exists(binPath)) { codigoError = "AGP-USB-004"; return false; }

            lock (_lock)
            {
                if (_estado.EnCurso) { codigoError = "AGP-USB-007"; return false; }
                _estado = new UsbFlashEstadoDto { EnCurso = true, Fase = "conectando", Pct = 0, Resultado = null, Log = "" };
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _esptool,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // netstandard2.0 no expone ProcessStartInfo.ArgumentList (llegó en
            // .NET Core 2.1). UseShellExecute=false igual manda Arguments directo
            // a CreateProcess sin pasar por cmd.exe (no hay shell de por medio),
            // así que citamos cada argumento a mano con el mismo algoritmo que
            // usa ArgumentList puertas adentro — nunca un shell-concat ingenuo.
            psi.Arguments = ArmarLineaDeComando(ArmarArgs(puerto, modo, binPath, borrarAntes));

            var sbLog = new System.Text.StringBuilder();
            void OnLinea(string linea)
            {
                if (linea == null) return;
                lock (_lock)
                {
                    sbLog.AppendLine(linea);
                    string fase = _estado.Fase; int pct = _estado.Pct;
                    EsptoolOutputParser.Parse(linea, ref fase, ref pct);
                    _estado.Fase = fase; _estado.Pct = pct; _estado.Log = sbLog.ToString();
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true })
                    {
                        proc.OutputDataReceived += (_, e) => OnLinea(e.Data);
                        proc.ErrorDataReceived  += (_, e) => OnLinea(e.Data);
                        proc.Start();
                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();
                        proc.WaitForExit();
                        lock (_lock)
                        {
                            string log = sbLog.ToString();
                            string cod = EsptoolOutputParser.ClasificarError(log);
                            bool ok = proc.ExitCode == 0 && cod == null;
                            _estado.EnCurso = false;
                            _estado.Resultado = ok ? "ok" : "fail";
                            _estado.Codigo = ok ? null : (cod ?? "AGP-USB-003");
                            _estado.Pct = ok ? 100 : _estado.Pct;
                            _estado.Fase = ok ? "listo" : "error";
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    lock (_lock)
                    {
                        _estado.EnCurso = false; _estado.Resultado = "fail";
                        _estado.Codigo = "AGP-USB-003"; _estado.Fase = "error";
                        _estado.Log = sbLog.ToString() + "\n" + ex.Message;
                    }
                }
            });
            return true;
        }
    }
}
