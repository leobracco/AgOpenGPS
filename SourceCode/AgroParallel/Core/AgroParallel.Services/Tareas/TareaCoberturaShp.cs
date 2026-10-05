// ============================================================================
// TareaCoberturaShp.cs — la cobertura (lo pintado) como shapefile de polígonos
// en WGS84, para abrir en cualquier GIS / el agrónomo.
//
// Reusa el mismo writer que ya exporta VistaX (NetTopologySuite.IO.Esri,
// Shapefile.WriteAllFeatures): cero dependencias nuevas.
//
// Geometría: la cobertura viaja como TIRAS DE TRIÁNGULOS (triangle strips de
// CPatches): a medida que la sección avanza agrega un par de vértices
// izquierda/derecha. El contorno de la tira es entonces "los pares hacia
// adelante + los impares hacia atrás" — un polígono por tira, en vez de un
// polígono por triángulo (que serían cientos de miles de features). El área
// del atributo se calcula sumando los triángulos en metros, no del polígono:
// es la misma cuenta que hace el motor para el contador de hectáreas.
//
// Coordenadas: el motor pinta en metros del plano local del lote; el que
// llama pasa la conversión a lat/lon (en el Engine es LocalPlane del lote).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using AgroParallel.Models;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;

namespace AgroParallel.Services.Tareas
{
    public static class TareaCoberturaShp
    {
        public const string PrjWgs84 =
            "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\","
            + "SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],"
            + "PRIMEM[\"Greenwich\",0.0],"
            + "UNIT[\"Degree\",0.0174532925199433]]";

        /// <summary>
        /// Escribe <paramref name="shpPath"/> (+ .shx, .dbf, .prj). Devuelve la
        /// cantidad de polígonos escritos; 0 = no había cobertura y NO se
        /// escribió ningún archivo.
        /// </summary>
        /// <param name="aLatLon">(easting, northing) locales → [lat, lon].</param>
        public static int Exportar(CoverageSnapshot cobertura, Func<double, double, double[]> aLatLon,
                                   string shpPath, string tareaId)
        {
            if (aLatLon == null) throw new ArgumentNullException(nameof(aLatLon));
            if (string.IsNullOrEmpty(shpPath)) throw new ArgumentException("Falta la ruta del .shp", nameof(shpPath));

            var factory = GeometryFactory.Default;
            var features = new List<IFeature>();

            if (cobertura?.Sections != null)
            {
                foreach (var sec in cobertura.Sections)
                {
                    if (sec?.Strips == null) continue;
                    foreach (var tira in sec.Strips)
                    {
                        var v = tira?.Vertices;
                        if (v == null || v.Count < 3) continue;

                        double areaM2 = AreaTiraM2(v);
                        if (!(areaM2 > 0)) continue;

                        var anillo = ContornoTira(v, aLatLon);
                        if (anillo == null) continue;

                        var attrs = new AttributesTable();
                        attrs.Add("tarea", tareaId ?? "");
                        attrs.Add("seccion", sec.Index);
                        attrs.Add("area_m2", Math.Round(areaM2, 2));
                        features.Add(new Feature(factory.CreatePolygon(anillo), attrs));
                    }
                }
            }

            if (features.Count == 0) return 0;

            string dir = Path.GetDirectoryName(Path.GetFullPath(shpPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Shapefile.WriteAllFeatures(features, shpPath);
            File.WriteAllText(Path.ChangeExtension(shpPath, ".prj"), PrjWgs84);
            return features.Count;
        }

        /// <summary>Suma de los triángulos de la tira (v[k], v[k+1], v[k+2]), en m².</summary>
        public static double AreaTiraM2(IList<CoverageVertex> v)
        {
            double area = 0;
            for (int k = 0; k + 2 < v.Count; k++)
            {
                double t = v[k].E * (v[k + 1].N - v[k + 2].N)
                         + v[k + 1].E * (v[k + 2].N - v[k].N)
                         + v[k + 2].E * (v[k].N - v[k + 1].N);
                area += Math.Abs(t * 0.5);
            }
            return area;
        }

        /// <summary>Anillo cerrado en grados (x = lon, y = lat), sentido horario
        /// como pide el formato shapefile para el contorno exterior.</summary>
        private static Coordinate[] ContornoTira(IList<CoverageVertex> v, Func<double, double, double[]> aLatLon)
        {
            var orden = new List<int>(v.Count + 1);
            for (int i = 0; i < v.Count; i += 2) orden.Add(i);
            int ultimoImpar = (v.Count - 1) % 2 == 1 ? v.Count - 1 : v.Count - 2;
            for (int i = ultimoImpar; i >= 1; i -= 2) orden.Add(i);

            var coords = new List<Coordinate>(orden.Count + 1);
            foreach (int i in orden)
            {
                var ll = aLatLon(v[i].E, v[i].N);
                if (ll == null || ll.Length < 2 || double.IsNaN(ll[0]) || double.IsNaN(ll[1])) return null;
                coords.Add(new Coordinate(ll[1], ll[0]));
            }
            if (coords.Count < 3) return null;
            coords.Add(coords[0].Copy());

            // Shoelace: positivo = antihorario → se invierte.
            double doble = 0;
            for (int i = 0; i + 1 < coords.Count; i++)
                doble += coords[i].X * coords[i + 1].Y - coords[i + 1].X * coords[i].Y;
            if (doble > 0) coords.Reverse();
            return coords.ToArray();
        }
    }
}
