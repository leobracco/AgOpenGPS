// ============================================================================
// FormGpsPathsCalculator.cs
// Adaptador IPathsGeometryCalculator -> PilotX. Stage 5 de la migracion OpenGL
// del mapa: el render GL necesita dos polilineas de "caminos":
//   . YouTurn  -> el giro (Dubins/pattern) generado en cabecera.
//   . Recorded -> el camino grabado manejando (record path).
//
// Lectura plana de FormGPS:
//   . mf.yt.ytList          -> List<vec3>        (giro de cabecera activo)
//   . mf.recPath.recList    -> List<CRecPathPt>  (camino grabado)
//
// Cada punto tiene .easting/.northing (idem tram). "Activo" = >= 2 puntos.
//
// Revision: incrementa cuando cambian las cuentas (ytList.Count /
// recList.Count). No comparamos puntos uno por uno — cualquier regeneracion
// (nuevo giro, nueva grabacion, borrado) cambia las cuentas casi siempre.
// Cuando no cambian (raro), el cliente igual tiene la geometria correcta
// porque el snapshot llega completo en cada poll — la revision solo sirve
// para saltar el upload al VBO.
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsPathsCalculator : IPathsGeometryCalculator
    {
        private readonly FormGPS _form;

        private int _lastYouTurn = 0;
        private int _lastRecorded = 0;
        private long _revision = 0;

        public FormGpsPathsCalculator(FormGPS form) { _form = form; }

        public PathsGeometrySnapshot GetGeometry()
        {
            // Snapshot defensivo por defecto — sin job / sin giro / sin
            // grabacion el cliente ve listas vacias y oculta la capa.
            var snap = new PathsGeometrySnapshot
            {
                YouTurn = new List<FieldPoint>(),
                Recorded = new List<FieldPoint>(),
                Revision = _revision
            };
            if (_form == null) return snap;

            try
            {
                // YouTurn (giro de cabecera): mf.yt.ytList — List<vec3>.
                // "Activo" cuando tiene >= 2 puntos (una polilinea real).
                var yt = _form.yt;
                if (yt != null)
                {
                    var ytList = yt.ytList;
                    if (ytList != null && ytList.Count >= 2)
                    {
                        for (int i = 0; i < ytList.Count; i++)
                            snap.YouTurn.Add(new FieldPoint(ytList[i].easting, ytList[i].northing));
                    }
                }

                // Recorded (camino grabado): mf.recPath.recList — List<CRecPathPt>.
                var rec = _form.recPath;
                if (rec != null)
                {
                    var recList = rec.recList;
                    if (recList != null && recList.Count >= 2)
                    {
                        for (int i = 0; i < recList.Count; i++)
                            snap.Recorded.Add(new FieldPoint(recList[i].easting, recList[i].northing));
                    }
                }

                // Bump revision solo cuando cambian las cuentas.
                if (snap.YouTurn.Count != _lastYouTurn
                    || snap.Recorded.Count != _lastRecorded)
                {
                    _revision++;
                    _lastYouTurn = snap.YouTurn.Count;
                    _lastRecorded = snap.Recorded.Count;
                }
                snap.Revision = _revision;
            }
            catch (Exception)
            {
                // Estado parcial (rebuild del giro en otro thread, etc) —
                // devolvemos listas vacias seguras con la revision actual.
                snap.YouTurn.Clear();
                snap.Recorded.Clear();
                snap.Revision = _revision;
            }

            return snap;
        }
    }
}
