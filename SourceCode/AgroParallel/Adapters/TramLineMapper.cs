// ============================================================================
// TramLineMapper.cs — TramSnapshot (geometría) → TramLineStateDto (la API).
//
// Vive suelto y se LINKEA en los dos proyectos que lo necesitan (AgOpenGPS.csproj
// y PilotX.GuidanceEngine.csproj) en vez de duplicarse, que es la convención que
// ya usa el repo para NmeaParser y SteerConfigService.
//
// El motivo de no copiarlo: son ~90 líneas de mapeo con detalles que no se ven
// al leer (los puntos de corte se omiten con el centinela 9000000, los tracks AB
// mandan dos puntos y las curvas mandan la lista entera). Dos copias divergen y
// el sintoma seria que una pantalla dibuja las tramlines y la otra no — con
// nadie mirando cual de las dos esta bien.
//
// No puede vivir en AgroParallel.Services porque ese proyecto solo referencia
// AgroParallel.Models, y esto necesita los tipos de geometria de AgOpenGPS.Core.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;

namespace AgOpenGPS
{
    using AgOpenGPS.Core.Models;

    internal static class TramLineMapper
    {
        /// <summary>Centinela de "sin punto" que usa el editor.</summary>
        private const double SinPunto = 9000000;

        public static TramLineStateDto Map(TramLineEditor.TramSnapshot snap)
        {
            if (snap == null) return new TramLineStateDto { Ok = false, Error = "no-state" };

            var dto = new TramLineStateDto
            {
                Ok = true,
                HasBoundary = snap.HasBoundary,
                Units = snap.Units,
                TrackWidthDisplay = System.Math.Round(snap.TrackWidthDisplay, 2),
                TramWidthDisplay = System.Math.Round(snap.TramWidthDisplay, 2),
                ToolWidthDisplay = System.Math.Round(snap.ToolWidthDisplay, 2),
                SelIdx = snap.SelIdx,
                Passes = snap.Passes,
                StartPass = snap.StartPass,
                IsOuter = snap.IsOuter,
                Alpha = snap.Alpha,
                CutStep = snap.CutStep,
                Error = snap.Error,
            };

            foreach (var t in snap.Tracks)
            {
                var pts = new List<double[]>();
                // Una AB queda definida por sus dos extremos; una curva hay que
                // mandarla entera o la pantalla no la puede dibujar.
                if (t.Mode == "ab")
                {
                    pts.Add(new double[] { t.PtA.easting, t.PtA.northing });
                    pts.Add(new double[] { t.PtB.easting, t.PtB.northing });
                }
                else if (t.CurvePts != null)
                {
                    for (int i = 0; i < t.CurvePts.Count; i++)
                        pts.Add(new double[] { t.CurvePts[i].easting, t.CurvePts[i].northing });
                }

                dto.Tracks.Add(new TramLineTrackDto
                {
                    Index = t.Index,
                    Name = t.Name,
                    Mode = t.Mode,
                    Points = pts.ToArray(),
                });
            }

            dto.NewTrams = MapLista(snap.NewTrams);
            dto.SavedTrams = MapLista(snap.SavedTrams);

            foreach (var f in snap.Fences)
            {
                var pts = new double[f.Count][];
                for (int i = 0; i < f.Count; i++)
                    pts[i] = new double[] { f[i].easting, f[i].northing };
                dto.Fences.Add(pts);
            }

            dto.OuterBnd = MapVec2(snap.OuterBnd);
            dto.InnerBnd = MapVec2(snap.InnerBnd);

            // Los puntos de corte solo se mandan si el operario ya los marcó.
            if (snap.PtA.easting < SinPunto)
                dto.PtA = new double[] { snap.PtA.easting, snap.PtA.northing };
            if (snap.PtB.easting < SinPunto)
                dto.PtB = new double[] { snap.PtB.easting, snap.PtB.northing };

            return dto;
        }

        private static List<double[][]> MapLista(List<List<vec2>> src)
        {
            var list = new List<double[][]>();
            if (src == null) return list;
            foreach (var linea in src)
            {
                var pts = new double[linea.Count][];
                for (int i = 0; i < linea.Count; i++)
                    pts[i] = new double[] { linea[i].easting, linea[i].northing };
                list.Add(pts);
            }
            return list;
        }

        private static double[][] MapVec2(List<vec2> src)
        {
            if (src == null || src.Count == 0) return new double[0][];
            var pts = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                pts[i] = new double[] { src[i].easting, src[i].northing };
            return pts;
        }
    }
}
