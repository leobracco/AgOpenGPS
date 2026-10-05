// ============================================================================
// ColaReportesFalla.cs — los reportes de falla esperan en DISCO hasta subir.
//
// La cabina muchas veces no tiene internet justo cuando algo falla. El reporte
// se arma igual, queda en <ConfigRoot>\data\reportes_falla\pendientes\ y se
// sube cuando vuelve la conexión (aunque en el medio se reinicie PilotX). Los
// ya subidos pasan a enviados\ y se guardan los últimos 20, por si soporte
// pide "mandámelo por pendrive" un día después.
//
// El código del reporte es el nombre del archivo: se valida contra el formato
// RF-XXX-XXX antes de tocar el disco, así un código armado a mano no se
// convierte en una ruta ("..\..\algo").
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgroParallel.Soporte
{
    public sealed class ColaReportesFalla
    {
        public const string EnCola = "en_cola";
        public const string Subido = "subido";

        private const int MaxEnviados = 20;
        private const int MaxPendientes = 30;

        private readonly string _pendientes;
        private readonly string _enviados;
        private readonly object _lock = new object();

        public ColaReportesFalla(string carpetaBase)
        {
            if (string.IsNullOrEmpty(carpetaBase)) throw new ArgumentException("carpeta vacía", nameof(carpetaBase));
            _pendientes = Path.Combine(carpetaBase, "pendientes");
            _enviados = Path.Combine(carpetaBase, "enviados");
        }

        /// <summary>Guarda el ZIP en pendientes. Devuelve la ruta.</summary>
        public string Guardar(string codigo, byte[] zip)
        {
            if (!ReporteFallaArmador.CodigoValido(codigo)) throw new ArgumentException("código inválido: " + codigo, nameof(codigo));
            if (zip == null) throw new ArgumentNullException(nameof(zip));
            lock (_lock)
            {
                Directory.CreateDirectory(_pendientes);
                string ruta = Path.Combine(_pendientes, codigo + ".zip");
                string tmp = ruta + ".tmp";
                File.WriteAllBytes(tmp, zip);
                if (File.Exists(ruta)) File.Delete(ruta);
                File.Move(tmp, ruta);
                Podar(_pendientes, MaxPendientes);
                return ruta;
            }
        }

        /// <summary>Códigos pendientes de subir, el más viejo primero.</summary>
        public IReadOnlyList<string> Pendientes()
        {
            lock (_lock)
            {
                if (!Directory.Exists(_pendientes)) return new List<string>();
                return new DirectoryInfo(_pendientes).GetFiles("RF-*.zip")
                    .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                    .Where(ReporteFallaArmador.CodigoValido)
                    .ToList();
            }
        }

        /// <summary>Ruta del ZIP (pendiente o enviado); null si no existe o el código no es válido.</summary>
        public string RutaDe(string codigo)
        {
            if (!ReporteFallaArmador.CodigoValido(codigo)) return null;
            lock (_lock)
            {
                string p = Path.Combine(_pendientes, codigo + ".zip");
                if (File.Exists(p)) return p;
                string e = Path.Combine(_enviados, codigo + ".zip");
                if (File.Exists(e)) return e;
                return null;
            }
        }

        public byte[] Leer(string codigo)
        {
            string ruta = RutaDe(codigo);
            if (ruta == null) return null;
            try { return File.ReadAllBytes(ruta); }
            catch (IOException) { return null; }
        }

        /// <summary>"en_cola", "subido" o null si no existe.</summary>
        public string EstadoDe(string codigo)
        {
            if (!ReporteFallaArmador.CodigoValido(codigo)) return null;
            lock (_lock)
            {
                if (File.Exists(Path.Combine(_pendientes, codigo + ".zip"))) return EnCola;
                if (File.Exists(Path.Combine(_enviados, codigo + ".zip"))) return Subido;
                return null;
            }
        }

        public void MarcarEnviado(string codigo)
        {
            if (!ReporteFallaArmador.CodigoValido(codigo)) return;
            lock (_lock)
            {
                string origen = Path.Combine(_pendientes, codigo + ".zip");
                if (!File.Exists(origen)) return;
                Directory.CreateDirectory(_enviados);
                string destino = Path.Combine(_enviados, codigo + ".zip");
                if (File.Exists(destino)) File.Delete(destino);
                File.Move(origen, destino);
                // Que el orden de "enviados" sea el de envío, no el de armado.
                try { File.SetLastWriteTimeUtc(destino, DateTime.UtcNow); } catch (IOException) { }
                Podar(_enviados, MaxEnviados);
            }
        }

        private static void Podar(string dir, int max)
        {
            try
            {
                var viejos = new DirectoryInfo(dir).GetFiles("RF-*.zip")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(max)
                    .ToList();
                foreach (var f in viejos)
                {
                    try { f.Delete(); } catch (IOException) { } // uno trabado no frena la poda
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
