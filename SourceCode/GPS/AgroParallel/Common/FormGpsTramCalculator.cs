// ============================================================================
// FormGpsTramCalculator.cs
// Adaptador ITramCalculator -> PilotX. Stage 4b de la migracion OpenGL del
// mapa: el render GL necesita las tramlines (wheel tracks generados adentro
// del lote) + outer/inner boundary tracks (pegados al borde) + displayMode
// para decidir que mostrar.
//
// Lectura plana de FormGPS:
//   . mf.tram.tramList          -> List<List<vec2>> (lineas internas)
//   . mf.tram.tramBndOuterArr   -> List<vec2>       (boundary outer cerrado)
//   . mf.tram.tramBndInnerArr   -> List<vec2>       (boundary inner cerrado)
//   . mf.tram.displayMode       -> TramMode enum    (None/All/FillTracks/Bnd)
//
// Revision: incrementa cuando cambia displayMode o las cuentas (lines.count,
// outer.count, inner.count). No comparamos puntos uno por uno - cualquier
// regeneracion (passes/ancho/alpha distintos) re-arma los arrays y las
// cuentas casi siempre cambian. Cuando no cambian (raro), el cliente igual
// tiene la geometria correcta porque el snapshot llega completo en cada
// poll - la revision solo sirve para saltar el upload al VBO.
// ============================================================================

using System;
using System.Collections.Generic;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsTramCalculator : ITramCalculator
    {
        private readonly FormGPS _form;

        private string _lastMode = "None";
        private int _lastLines = 0;
        private int _lastOuter = 0;
        private int _lastInner = 0;
        private long _revision = 0;

        public FormGpsTramCalculator(FormGPS form) { _form = form; }

        public TramGeometrySnapshot GetGeometry()
        {
            // Snapshot defensivo por defecto - sin job o sin tram el cliente
            // ve DisplayMode="None" y oculta la capa.
            var snap = new TramGeometrySnapshot
            {
                DisplayMode = "None",
                Lines = new List<TramLine>(),
                OuterBoundary = new List<FieldPoint>(),
                InnerBoundary = new List<FieldPoint>(),
                Revision = _revision
            };
            if (_form == null) return snap;

            try
            {
                if (_form.tram == null) return snap;

                // Map TramMode enum -> string. None bloquea todo el render
                // pero igual emitimos las cuentas para que la revision suba
                // cuando se apaga el tram (caso "ahora no quiero verlo").
                switch (_form.tram.displayMode)
                {
                    case TramMode.None:           snap.DisplayMode = "None"; break;
                    case TramMode.All:            snap.DisplayMode = "All"; break;
                    case TramMode.FillTracks:     snap.DisplayMode = "FillTracks"; break;
                    case TramMode.BoundaryTracks: snap.DisplayMode = "BoundaryTracks"; break;
                    default:                      snap.DisplayMode = "None"; break;
                }

                // Lineas internas
                var src = _form.tram.tramList;
                if (src != null)
                {
                    for (int i = 0; i < src.Count; i++)
                    {
                        var lst = src[i];
                        if (lst == null || lst.Count < 2) continue;
                        var line = new TramLine { Points = new List<FieldPoint>(lst.Count) };
                        for (int h = 0; h < lst.Count; h++)
                            line.Points.Add(new FieldPoint(lst[h].easting, lst[h].northing));
                        snap.Lines.Add(line);
                    }
                }

                // Outer
                var outer = _form.tram.tramBndOuterArr;
                if (outer != null)
                {
                    for (int i = 0; i < outer.Count; i++)
                        snap.OuterBoundary.Add(new FieldPoint(outer[i].easting, outer[i].northing));
                }

                // Inner
                var inner = _form.tram.tramBndInnerArr;
                if (inner != null)
                {
                    for (int i = 0; i < inner.Count; i++)
                        snap.InnerBoundary.Add(new FieldPoint(inner[i].easting, inner[i].northing));
                }

                // Bump revision solo cuando cambia mode o cuentas.
                if (snap.DisplayMode != _lastMode
                    || snap.Lines.Count != _lastLines
                    || snap.OuterBoundary.Count != _lastOuter
                    || snap.InnerBoundary.Count != _lastInner)
                {
                    _revision++;
                    _lastMode = snap.DisplayMode;
                    _lastLines = snap.Lines.Count;
                    _lastOuter = snap.OuterBoundary.Count;
                    _lastInner = snap.InnerBoundary.Count;
                }
                snap.Revision = _revision;
            }
            catch (Exception)
            {
                // Estado parcial (rebuild en otro thread, etc) - devolvemos
                // "None" seguro con la revision actual.
                snap.DisplayMode = "None";
                snap.Lines.Clear();
                snap.OuterBoundary.Clear();
                snap.InnerBoundary.Clear();
                snap.Revision = _revision;
            }

            return snap;
        }
    }
}
