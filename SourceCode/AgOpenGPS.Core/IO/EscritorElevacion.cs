using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.IO
{
    /// <summary>
    /// Escribe Elevation.txt del lote abierto en APPEND con buffer: el motor
    /// llama a <see cref="Agregar"/> por cada punto aceptado y el disco se toca
    /// recién cada <see cref="FilasPorFlush"/> filas o <see cref="SegundosPorFlush"/>
    /// segundos (y siempre al cerrar el lote). Nunca tira excepciones: si el
    /// disco falla, la fila queda en el buffer, se anota <see cref="UltimoError"/>
    /// y se reintenta en el próximo flush — el pipeline de fix no se entera.
    ///
    /// Compatibilidad con AOG: si el archivo ya existe (lote migrado, que trae
    /// la cabecera de AOG sola, o un lote de PilotX con puntos) se CONTINÚA sin
    /// reescribir la cabecera. Si no existe o está vacío, se crea con la
    /// cabecera de AOG (<see cref="ElevationFiles.BuildHeader"/>) al primer
    /// flush: un lote donde nunca hubo RTK fijo no queda con un archivo vacío.
    ///
    /// Thread-safe: el fix entra por el hilo del pipeline y el cierre de lote
    /// por el del Hub.
    /// </summary>
    public sealed class EscritorElevacion
    {
        /// <summary>Si el disco no responde por mucho tiempo, no crecer sin
        /// techo en memoria: pasado esto se descarta lo pendiente (y se avisa).</summary>
        public const int MaxPendientes = 50000;

        public int FilasPorFlush { get; set; } = 200;
        public double SegundosPorFlush { get; set; } = 15;

        private readonly object _lock = new object();
        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly Stopwatch _desdeFlush = new Stopwatch();
        private Wgs84 _startFix;

        /// <summary>Carpeta del lote abierto (null = ninguno).</summary>
        public string Directorio { get; private set; }

        /// <summary>Puntos del lote: los que ya estaban en el archivo + los nuevos (aunque estén en el buffer).</summary>
        public int Puntos { get; private set; }

        /// <summary>Filas en el buffer, todavía no escritas.</summary>
        public int Pendientes { get; private set; }

        /// <summary>Último error de disco (null = el último flush anduvo).</summary>
        public string UltimoError { get; private set; }

        /// <summary>
        /// Apunta al lote. Si había otro abierto, lo flushea antes. Cuenta los
        /// puntos que ya tiene el archivo (lote continuado). No escribe nada.
        /// </summary>
        public void Abrir(string directorioLote, Wgs84 startFix)
        {
            lock (_lock)
            {
                if (Directorio != null) FlushLocked();
                _buffer.Clear();
                Pendientes = 0;
                UltimoError = null;
                Directorio = directorioLote;
                _startFix = startFix;
                int existentes = 0;
                try { existentes = ElevationFiles.ContarPuntos(Ruta); }
                catch (Exception ex) { UltimoError = ex.Message; }
                Puntos = existentes;
                _desdeFlush.Restart();
            }
        }

        /// <summary>Flushea y suelta el lote.</summary>
        public void Cerrar()
        {
            lock (_lock)
            {
                if (Directorio != null) FlushLocked();
                Directorio = null;
                _buffer.Clear();
                Pendientes = 0;
                Puntos = 0;
            }
        }

        public void Agregar(FilaElevacion fila)
        {
            lock (_lock)
            {
                if (Directorio == null) return;
                _buffer.Append(ElevationFiles.FormatearFila(fila)).Append("\r\n");
                Pendientes++;
                Puntos++;
                if (Pendientes >= FilasPorFlush || _desdeFlush.Elapsed.TotalSeconds >= SegundosPorFlush)
                    FlushLocked();
            }
        }

        /// <summary>Flush por tiempo sin punto nuevo (lo llama el tick del motor).</summary>
        public void FlushSiVencio()
        {
            lock (_lock)
            {
                if (Directorio == null || Pendientes == 0) return;
                if (_desdeFlush.Elapsed.TotalSeconds >= SegundosPorFlush) FlushLocked();
            }
        }

        public void Flush()
        {
            lock (_lock)
            {
                if (Directorio != null) FlushLocked();
            }
        }

        private string Ruta => Directorio == null ? null : Path.Combine(Directorio, ElevationFiles.FileName);

        private void FlushLocked()
        {
            _desdeFlush.Restart();
            if (Pendientes == 0) return;
            try
            {
                if (!Directory.Exists(Directorio)) Directory.CreateDirectory(Directorio);
                using (var fs = new FileStream(Ruta, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    string prefijo = "";
                    if (fs.Length == 0)
                    {
                        prefijo = ElevationFiles.BuildHeader(DateTime.Now, _startFix);
                    }
                    else
                    {
                        // Un archivo que no termina en salto de línea (escritura
                        // cortada, editado a mano) pegaría la fila nueva a la última.
                        fs.Seek(-1, SeekOrigin.End);
                        int ultimo = fs.ReadByte();
                        if (ultimo != '\n') prefijo = "\r\n";
                    }
                    fs.Seek(0, SeekOrigin.End);
                    byte[] bytes = new UTF8Encoding(false).GetBytes(prefijo + _buffer.ToString());
                    fs.Write(bytes, 0, bytes.Length);
                }
                _buffer.Clear();
                Pendientes = 0;
                UltimoError = null;
            }
            catch (Exception ex)
            {
                UltimoError = ex.Message;
                if (Pendientes > MaxPendientes)
                {
                    Puntos -= Pendientes;
                    _buffer.Clear();
                    Pendientes = 0;
                    UltimoError = "buffer descartado tras fallas de disco: " + ex.Message;
                }
            }
        }
    }
}
