// ============================================================================
// FormGPS.TramSimple.cs — geometría del editor "Tramlines simples" (tramline.html).
// Reemplaza el WinForms FormTram: ajusta pasadas / modo / alpha / swap AB y
// reconstruye los tramlines con ABLine.BuildTram() o curve.BuildTram(). La lógica
// es idéntica a FormTram; acá vive dentro de FormGPS para que el adapter
// ITramSimpleService la invoque sin abrir el form. Todos los métodos asumen que
// corren en el hilo UI (el adapter marshalea).
// ============================================================================

using System;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // Guía activa válida (hay al menos una y el índice apunta a ella).
        internal bool TramSimple_HasTrack()
        {
            return trk != null && trk.gArr != null
                   && trk.idx >= 0 && trk.idx < trk.gArr.Count;
        }

        internal bool TramSimple_HasBoundary()
        {
            return bnd != null && bnd.bndList.Count > 0;
        }

        // Curva vs AB: define qué builder usar (igual que el ctor de FormTram).
        internal bool TramSimple_IsCurve()
        {
            return TramSimple_HasTrack() && trk.gArr[trk.idx].mode != TrackMode.AB;
        }

        // Reconstruye el tram en memoria y deja la preview visible (displayMode=All),
        // exactamente como FormTram.MoveBuildTramLine(0). Sin guía activa no hay nada
        // que construir (el launcher garantiza trk.idx != -1, pero /state se puede
        // pedir sin lote cargado: no reventamos).
        private void TramSimple_Rebuild()
        {
            if (!TramSimple_HasTrack()) return;
            tram.displayMode = TramMode.All;
            if (TramSimple_IsCurve()) curve.BuildTram();
            else ABLine.BuildTram();
        }

        // Réplica de FormTram_Load: elige generateMode según lo que ya exista,
        // fuerza FillTracks sin contorno, y construye si todavía no hay tram.
        internal TramSimpleStateSnapshot TramSimple_Open()
        {
            tool.halfWidth = (tool.width - tool.overlap) / 2.0;

            tram.generateMode = TramMode.All;
            if (tram.tramList.Count > 0 && tram.tramBndOuterArr.Count > 0)
                tram.generateMode = TramMode.All;
            else if (tram.tramBndOuterArr.Count == 0)
                tram.generateMode = TramMode.FillTracks;
            else if (tram.tramList.Count == 0)
                tram.generateMode = TramMode.BoundaryTracks;
            else
                tram.generateMode = TramMode.All;

            if (bnd.bndList.Count == 0) tram.generateMode = TramMode.FillTracks;

            CloseTopMosts();

            if (tram.tramList.Count > 0 || tram.tramBndOuterArr.Count > 0)
            {
                // Ya hay tram: solo asegurar que la preview esté prendida.
                tram.displayMode = TramMode.All;
            }
            else
            {
                TramSimple_Rebuild();
            }

            return TramSimple_Snapshot();
        }

        internal TramSimpleStateSnapshot TramSimple_SetPasses(int passes)
        {
            if (passes < 1) passes = 1;
            tram.passes = passes;
            Properties.Settings.Default.setTram_passes = passes;
            Properties.Settings.Default.Save();
            TramSimple_Rebuild();
            return TramSimple_Snapshot();
        }

        internal TramSimpleStateSnapshot TramSimple_SetAlpha(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            tram.alpha = percent * 0.01;
            return TramSimple_Snapshot();
        }

        internal TramSimpleStateSnapshot TramSimple_SetMode(string mode)
        {
            TramMode m;
            switch (mode)
            {
                case "FillTracks":     m = TramMode.FillTracks; break;
                case "BoundaryTracks": m = TramMode.BoundaryTracks; break;
                case "All":            m = TramMode.All; break;
                default:               m = tram.generateMode; break;
            }
            // Sin contorno no hay tracks de borde: FormTram deshabilita el botón.
            if (bnd.bndList.Count == 0) m = TramMode.FillTracks;
            tram.generateMode = m;
            TramSimple_Rebuild();
            return TramSimple_Snapshot();
        }

        // Invierte la dirección de la guía activa (idéntico a FormTram.btnSwapAB_Click).
        internal TramSimpleStateSnapshot TramSimple_SwapAB()
        {
            if (!TramSimple_HasTrack()) return TramSimple_Snapshot();

            if (trk.gArr[trk.idx].mode == TrackMode.AB)
            {
                vec2 bob = trk.gArr[trk.idx].ptA;
                trk.gArr[trk.idx].ptA = trk.gArr[trk.idx].ptB;
                trk.gArr[trk.idx].ptB = new vec2(bob);

                trk.gArr[trk.idx].heading += Math.PI;
                if (trk.gArr[trk.idx].heading < 0) trk.gArr[trk.idx].heading += glm.twoPI;
                if (trk.gArr[trk.idx].heading > glm.twoPI) trk.gArr[trk.idx].heading -= glm.twoPI;

                double abHeading = trk.gArr[trk.idx].heading;
                trk.gArr[trk.idx].endPtA.easting = trk.gArr[trk.idx].ptA.easting - (Math.Sin(abHeading) * ABLine.abLength);
                trk.gArr[trk.idx].endPtA.northing = trk.gArr[trk.idx].ptA.northing - (Math.Cos(abHeading) * ABLine.abLength);

                trk.gArr[trk.idx].endPtB.easting = trk.gArr[trk.idx].ptB.easting + (Math.Sin(abHeading) * ABLine.abLength);
                trk.gArr[trk.idx].endPtB.northing = trk.gArr[trk.idx].ptB.northing + (Math.Cos(abHeading) * ABLine.abLength);
            }
            else
            {
                int cnt = trk.gArr[trk.idx].curvePts.Count;
                if (cnt > 0)
                {
                    trk.gArr[trk.idx].curvePts.Reverse();

                    vec3[] arr = new vec3[cnt];
                    cnt--;
                    trk.gArr[trk.idx].curvePts.CopyTo(arr);
                    trk.gArr[trk.idx].curvePts.Clear();

                    trk.gArr[trk.idx].heading += Math.PI;
                    if (trk.gArr[trk.idx].heading < 0) trk.gArr[trk.idx].heading += glm.twoPI;
                    if (trk.gArr[trk.idx].heading > glm.twoPI) trk.gArr[trk.idx].heading -= glm.twoPI;

                    for (int i = 1; i < cnt; i++)
                    {
                        vec3 pt3 = arr[i];
                        pt3.heading += Math.PI;
                        if (pt3.heading > glm.twoPI) pt3.heading -= glm.twoPI;
                        if (pt3.heading < 0) pt3.heading += glm.twoPI;
                        trk.gArr[trk.idx].curvePts.Add(pt3);
                    }

                    vec2 temp = new vec2(trk.gArr[trk.idx].ptA);
                    trk.gArr[trk.idx].ptA = new vec2(trk.gArr[trk.idx].ptB);
                    trk.gArr[trk.idx].ptB = new vec2(temp);
                }
            }

            FileSaveTracks();

            tram.tramArr?.Clear();
            tram.tramList?.Clear();
            tram.tramBndOuterArr?.Clear();
            tram.tramBndInnerArr?.Clear();

            TramSimple_Rebuild();
            return TramSimple_Snapshot();
        }

        // Cierra el editor (equivalente al FormClosing de FormTram).
        internal void TramSimple_Commit(bool save)
        {
            if (!save)
            {
                tram.tramArr?.Clear();
                tram.tramList?.Clear();
                tram.tramBndOuterArr?.Clear();
                tram.tramBndInnerArr?.Clear();
                tram.displayMode = 0;
            }

            FileSaveTram();
            PanelUpdateRightAndBottom();
            FixTramModeButton();

            Properties.Settings.Default.setTram_alpha = tram.alpha;
            Properties.Settings.Default.setTram_passes = tram.passes;
            Properties.Settings.Default.Save();
        }

        // Snapshot plano del estado (lo mapea el adapter al DTO).
        internal TramSimpleStateSnapshot TramSimple_Snapshot()
        {
            return new TramSimpleStateSnapshot
            {
                HasTrack = TramSimple_HasTrack(),
                HasBoundary = TramSimple_HasBoundary(),
                IsCurve = TramSimple_IsCurve(),
                Passes = tram.passes,
                AlphaPercent = (int)Math.Round(tram.alpha * 100.0),
                Mode = tram.generateMode.ToString(),
                ToolWidthDisplay = tool.width * m2FtOrM,
                TramWidthDisplay = tram.tramWidth * m2FtOrM,
                TrackWidthDisplay = vehicle.VehicleConfig.TrackWidth * m2FtOrM,
                Units = (unitsFtM ?? "m").Trim()
            };
        }

        // POCO intermedio para no acoplar el partial (assembly GPS) al DTO de Models
        // por conversión implícita; el adapter lo copia campo a campo.
        internal sealed class TramSimpleStateSnapshot
        {
            public bool HasTrack;
            public bool HasBoundary;
            public bool IsCurve;
            public int Passes;
            public int AlphaPercent;
            public string Mode;
            public double ToolWidthDisplay;
            public double TramWidthDisplay;
            public double TrackWidthDisplay;
            public string Units;
        }
    }
}
