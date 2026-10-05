// KmlBoundaryReader.cs — saca los polígonos de un KML como listas de Wgs84.
//
// Parseo por TEXTO, no XML, a propósito: es el mismo criterio del import
// nativo (btnLoadBoundaryFromGE_Click) y del import de lote del motor. Los KML
// de las apps de campo suelen venir con namespaces raros o mal formados y un
// parser XML estricto los rechaza; buscar los bloques <coordinates> a mano se
// banca todo eso. Ojo con el orden: KML es lon,lat — al revés de lo habitual.
//
// Compartido por los dos hosts (FormGPS y el motor headless): el que llama
// decide qué hacer con los anillos (primer polígono solo, o todos).

using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AgOpenGPS.IO
{
    public static class KmlBoundaryReader
    {
        /// <summary>
        /// Devuelve un anillo por cada bloque &lt;coordinates&gt; del KML, en
        /// orden de aparición. Los bloques con menos de 3 puntos (no llegan a
        /// polígono) se descartan. Texto ilegible → lista vacía, nunca tira.
        /// </summary>
        public static List<List<Wgs84>> ReadRings(string kmlContent)
        {
            var rings = new List<List<Wgs84>>();
            if (string.IsNullOrWhiteSpace(kmlContent)) return rings;

            int pos = 0;
            while (true)
            {
                int ini = kmlContent.IndexOf("<coordinates>", pos, StringComparison.OrdinalIgnoreCase);
                if (ini < 0) break;
                ini += "<coordinates>".Length;
                int fin = kmlContent.IndexOf("</coordinates>", ini, StringComparison.OrdinalIgnoreCase);
                if (fin < 0) break;
                pos = fin + "</coordinates>".Length;

                var ring = ParseRing(kmlContent.Substring(ini, fin - ini));
                if (ring.Count >= 3) rings.Add(ring);
            }
            return rings;
        }

        private static List<Wgs84> ParseRing(string bloque)
        {
            var ring = new List<Wgs84>();
            foreach (string item in bloque.Split(new[] { ' ', '\t', '\r', '\n' },
                                                 StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fix = item.Split(',');
                if (fix.Length < 2) continue;
                if (!double.TryParse(fix[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                if (!double.TryParse(fix[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                // Coordenada fuera de rango = KML roto; mejor descartar el punto
                // que meter un vértice en el polo y reventar el área.
                if (lat < -90 || lat > 90 || lon < -180 || lon > 180) continue;
                ring.Add(new Wgs84(lat, lon));
            }
            return ring;
        }
    }
}
