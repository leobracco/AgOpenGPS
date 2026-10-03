// ============================================================================
// GuidanceEngineHost.Planimetria.cs — planimetría fase 3: lo que el servicio
// de planimetría (EnginePlanimetriaService, en PilotX.GuidanceEngine) necesita
// del motor y no puede tocar de afuera sin el lock del pipeline de fix:
//
//   · PlanimetriaArchivoElevacion: vuelca a disco lo pendiente de
//     Elevation.txt (el escritor junta hasta 200 filas) y devuelve la ruta,
//     para que el mapa incluya lo último que se grabó.
//   · PlanimetriaPosicion: dónde está el tractor, en el plano del mapa y en
//     lat/lon del terreno (sin deriva, el mismo criterio que Elevation.txt).
//   · PlanimetriaInstalarGuiaCurva: agrega una guía curva con los puntos de
//     una curva de nivel y la activa. Misma receta que AB + Curva y SmartPath
//     (espaciado 1,6 m, rumbos, extensiones en las puntas) pero SIN el
//     corrimiento de medio ancho: la guía va SOBRE la curva de nivel.
//
// Nada de esto corre solo: lo llama el servicio cuando el operario toca algo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Ruta de Elevation.txt del lote abierto (con lo pendiente ya en
        /// disco), o null sin lote.</summary>
        public string PlanimetriaArchivoElevacion()
        {
            if (!IsJobStarted || string.IsNullOrEmpty(currentFieldDirectory)) return null;
            string dir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
            try
            {
                ProcesarFixSerializado(() =>
                {
                    if (string.Equals(_escritorElevacion.Directorio, dir, StringComparison.OrdinalIgnoreCase))
                        _escritorElevacion.Flush();
                });
            }
            catch (Exception ex)
            {
                Log.EventWriter("Planimetria: flush de Elevation.txt: " + ex.Message);
            }
            return Path.Combine(dir, "Elevation.txt");
        }

        /// <summary>
        /// Posición del tractor (pivote). <paramref name="e"/>/<paramref name="n"/>
        /// en el plano del mapa (con deriva, donde se dibuja el tractor);
        /// <paramref name="lat"/>/<paramref name="lon"/> del terreno (sin deriva).
        /// false sin lote o sin GPS vivo.
        /// </summary>
        public bool PlanimetriaPosicion(out double e, out double n, out double lat, out double lon)
        {
            e = n = lat = lon = double.NaN;
            if (!IsJobStarted || !HayGpsVivo()) return false;
            e = pivotAxlePos.easting;
            n = pivotAxlePos.northing;
            try
            {
                GeoCoord local = new GeoCoord(n, e) - AppModelField.SharedFieldProperties.DriftCompensation;
                Wgs84 ll = AppModelField.LocalPlane.ConvertGeoCoordToWgs84(local);
                lat = ll.Latitude;
                lon = ll.Longitude;
                return true;
            }
            catch { return false; }
        }

        /// <summary>lat/lon del terreno → plano local del lote (sin deriva: el
        /// mismo camino que el límite importado de KML).</summary>
        public void PlanimetriaALocal(double lat, double lon, out double e, out double n)
        {
            GeoCoord g = AppModelField.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
            e = g.Easting;
            n = g.Northing;
        }

        /// <summary>
        /// Agrega y ACTIVA una guía curva con estos puntos (plano local). Devuelve
        /// null si anduvo o el código de error: "sin_lote" | "pocos_puntos" |
        /// "smartpath" (la guía por última pasada la pisaría en la próxima cabecera).
        /// </summary>
        public string PlanimetriaInstalarGuiaCurva(List<vec3> puntos, string nombre)
        {
            if (!IsJobStarted) return "sin_lote";
            if (SmartPathActivo) return "smartpath";
            if (puntos == null || puntos.Count < 4) return "pocos_puntos";
            string error = null;
            ProcesarFixSerializado(() =>
            {
                var pts = new List<vec3>(puntos);
                CABCurve.MakePointMinimumSpacing(ref pts, 1.6);
                if (pts.Count < 4) { error = "pocos_puntos"; return; }
                CABCurve.CalculateHeadings(ref pts);

                double x = 0, y = 0;
                foreach (vec3 pt in pts) { x += Math.Cos(pt.heading); y += Math.Sin(pt.heading); }
                double aveH = Math.Atan2(y / pts.Count, x / pts.Count);
                if (aveH < 0) aveH += glm.twoPI;

                var trk = new CTrk
                {
                    mode = TrackMode.Curve,
                    ptA = new vec2(pts[0].easting, pts[0].northing),
                    ptB = new vec2(pts[pts.Count - 1].easting, pts[pts.Count - 1].northing),
                    heading = aveH,
                    name = string.IsNullOrWhiteSpace(nombre) ? "Curva de nivel" : nombre.Trim(),
                };
                CurveField.AddFirstLastPoints(ref pts);
                CABCurve.CalculateHeadings(ref pts);
                trk.curvePts.AddRange(pts);

                Trk.gArr.Add(trk);
                int idx = Trk.gArr.Count - 1;
                // Orden seguro (TrkBuilder_CloseUse): índice, invalidar, giro en U.
                Trk.idx = idx;
                CurveField.isCurveValid = false;
                ABLineField.isABValid = false;
                Yt.ResetYouTurn();
                SaveTracks();
                Log.EventWriter(string.Format(CultureInfo.InvariantCulture,
                    "Planimetria: guia por curva de nivel '{0}' ({1} puntos) activa [{2}]", trk.name, puntos.Count, idx));
            });
            return error;
        }
    }
}
