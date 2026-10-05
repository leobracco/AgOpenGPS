// ============================================================================
// VistaXSurcosSync.cs — qué archivos del registro VistaX por lote
// (<lote>/VistaX/Surcos/, ver VxRegistroArchivo) suben a OrbitX y cuándo.
//
//   vistax_surcos_NNNN.ndjson → subtipo "vistax_surcos"
//   resumen.json              → subtipo "vistax_resumen"
//   producto "vistax", es_lote = FALSE con lote_nombre (igual que
//   elevation_points): en OrbitX varias consultas traen TODOS los docs
//   es_lote de un lote con su contenido; decenas de partes de 250 KB las
//   inflarían. El panel los junta por subtipo + lote_nombre.
//   ruta_rel = vistax/surcos/<lote>/<archivo>
//
// Reglas (mismo criterio que Elevation.txt):
//   · una parte COMPLETA (ya hay otra después) no cambia más: se encola una
//     vez y no se vuelve a leer mientras el server la tenga confirmada con
//     ese largo;
//   · la parte que CRECE y el resumen se encolan como mucho cada 5 min, para
//     no mandar —ni archivar en el server— una copia nueva cada 30 s;
//   · al cerrar el lote, OrbitXSync llama con forzar=true para que lo último
//     (la parte parcial y el resumen final) suba sin esperar.
// Lógica pura sobre el disco: VistaXSurcosSyncTests.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using AgroParallel.Services.VistaX;

namespace AgroParallel.Services.OrbitX
{
    public sealed class VistaXSurcosSync
    {
        public const string SubtipoTramos = "vistax_surcos";
        public const string SubtipoResumen = "vistax_resumen";
        public const string Producto = "vistax";
        public static readonly TimeSpan IntervaloParcial = TimeSpan.FromMinutes(5);

        public sealed class Envio
        {
            public string Path;
            public string RutaRel;
            public string Subtipo;
        }

        private readonly Dictionary<string, long> _largoEncolado = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _ultimaParcial = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static string RutaRel(string lote, string archivo)
        {
            return "vistax/surcos/" + lote + "/" + archivo;
        }

        /// <summary>
        /// Archivos del registro de <paramref name="loteDir"/> que hay que
        /// encolar ahora. <paramref name="confirmado"/>: true si el server ya
        /// confirmó ese path (OrbitXSync._lastHashes). <paramref name="forzar"/>
        /// saltea la espera de 5 min (cierre de lote).
        /// </summary>
        public List<Envio> Planificar(string loteDir, string lote, Func<string, bool> confirmado,
            DateTime ahora, bool forzar = false)
        {
            var r = new List<Envio>();
            if (string.IsNullOrEmpty(loteDir) || string.IsNullOrEmpty(lote)) return r;
            string dir = VxRegistroArchivo.DirDeLote(loteDir);
            if (!Directory.Exists(dir)) return r;
            if (confirmado == null) confirmado = _ => false;

            var partes = VxRegistroArchivo.Partes(dir);
            for (int i = 0; i < partes.Count; i++)
            {
                string p = partes[i];
                long largo;
                try { largo = new FileInfo(p).Length; } catch { continue; }
                long previo;
                bool mismoLargo = _largoEncolado.TryGetValue(p, out previo) && previo == largo;
                bool creciendo = i == partes.Count - 1;
                if (mismoLargo && confirmado(p)) continue;
                if (creciendo)
                {
                    if (!forzar && !TocaParcial(p, ahora)) continue;
                    _ultimaParcial[p] = ahora;
                }
                _largoEncolado[p] = largo;
                r.Add(new Envio { Path = p, RutaRel = RutaRel(lote, Path.GetFileName(p)), Subtipo = SubtipoTramos });
            }

            string res = Path.Combine(dir, VxRegistroArchivo.NombreResumen);
            if (File.Exists(res) && (forzar || TocaParcial(res, ahora)))
            {
                _ultimaParcial[res] = ahora;
                r.Add(new Envio { Path = res, RutaRel = RutaRel(lote, VxRegistroArchivo.NombreResumen), Subtipo = SubtipoResumen });
            }
            return r;
        }

        private bool TocaParcial(string path, DateTime ahora)
        {
            DateTime u;
            return !_ultimaParcial.TryGetValue(path, out u) || ahora - u >= IntervaloParcial;
        }
    }
}
