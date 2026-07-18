// ============================================================================
// FormGPS.HeadlandEdit.cs — geometría del editor de cabecera HTML (cabecera.html).
// Reemplaza el flujo "Build Around" de FormHeadLine. El algoritmo de offset es
// idéntico a FormHeadLine.btnBndLoop_Click; acá vive dentro de FormGPS para que
// el adapter IHeadlandEditService lo invoque sin abrir el form WinForms.
// Todos los métodos asumen que corren en el hilo UI (el adapter marshalea).
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // true si hay contorno externo con puntos.
        internal bool HeadlandEdit_HasBoundary()
        {
            return bnd != null && bnd.bndList.Count > 0
                   && bnd.bndList[0].fenceLine != null
                   && bnd.bndList[0].fenceLine.Count > 0;
        }

        internal double[][] HeadlandEdit_FenceEN()
        {
            if (!HeadlandEdit_HasBoundary()) return new double[0][];
            var src = bnd.bndList[0].fenceLine;
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        internal double[][] HeadlandEdit_HeadlandEN()
        {
            if (!HeadlandEdit_HasBoundary()) return new double[0][];
            var src = bnd.bndList[0].hdLine;
            if (src == null) return new double[0][];
            var outArr = new double[src.Count][];
            for (int i = 0; i < src.Count; i++)
                outArr[i] = new double[] { src[i].easting, src[i].northing };
            return outArr;
        }

        internal bool HeadlandEdit_IsHeadlandOn()
        {
            return bnd != null && bnd.bndList.Count > 0
                   && bnd.bndList[0].hdLine != null
                   && bnd.bndList[0].hdLine.Count > 0
                   && bnd.isHeadlandOn;
        }

        // Ancho útil de la herramienta en metros (para "usar ancho de herramienta").
        internal double HeadlandEdit_ToolWidthM()
        {
            return tool != null ? tool.width : 0.0;
        }

        // unitsFtM viene con espacio inicial (" m" / " ft"); lo normalizamos.
        internal string HeadlandEdit_Units()
        {
            return string.IsNullOrEmpty(unitsFtM) ? "m" : unitsFtM.Trim();
        }

        internal bool HeadlandEdit_IsSectionControlled()
        {
            return bnd != null && bnd.isSectionControlledByHeadland;
        }

        // Recalcula isHeadlandOn tras cualquier mutación de hdLine.
        private void HeadlandEdit_RefreshOnFlag()
        {
            bnd.isHeadlandOn = bnd.bndList.Count > 0
                               && bnd.bndList[0].hdLine != null
                               && bnd.bndList[0].hdLine.Count > 0;
        }

        // Offset idéntico a FormHeadLine.btnBndLoop_Click. distanceDisplay en
        // unidades display; ==0 copia contorno→cabecera. Devuelve false si el
        // offset colapsa (sin puntos válidos) — en ese caso NO toca hdLine.
        internal bool HeadlandEdit_BuildAround(double distanceDisplay)
        {
            if (!HeadlandEdit_HasBoundary()) return false;

            int ptCount = bnd.bndList[0].fenceLine.Count;

            if (distanceDisplay == 0)
            {
                hdl.desList.Clear();
                bnd.bndList[0].hdLine?.Clear();
                for (int i = 0; i < ptCount; i++)
                    bnd.bndList[0].hdLine.Add(new vec3(bnd.bndList[0].fenceLine[i]));
            }
            else
            {
                hdl.desList?.Clear();
                vec3 pt3 = new vec3();

                double moveDist = distanceDisplay * ftOrMtoM;
                double distSq = (moveDist) * (moveDist) * 0.999;

                for (int i = 0; i < ptCount; i++)
                {
                    pt3.easting = bnd.bndList[0].fenceLine[i].easting -
                        (Math.Sin(glm.PIBy2 + bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.northing = bnd.bndList[0].fenceLine[i].northing -
                        (Math.Cos(glm.PIBy2 + bnd.bndList[0].fenceLine[i].heading) * (moveDist));
                    pt3.heading = bnd.bndList[0].fenceLine[i].heading;

                    bool Add = true;
                    for (int j = 0; j < ptCount; j++)
                    {
                        double check = glm.DistanceSquared(pt3.northing, pt3.easting,
                                            bnd.bndList[0].fenceLine[j].northing, bnd.bndList[0].fenceLine[j].easting);
                        if (check < distSq) { Add = false; break; }
                    }

                    if (Add)
                    {
                        if (hdl.desList.Count > 0)
                        {
                            double dist = ((pt3.easting - hdl.desList[hdl.desList.Count - 1].easting) * (pt3.easting - hdl.desList[hdl.desList.Count - 1].easting))
                                + ((pt3.northing - hdl.desList[hdl.desList.Count - 1].northing) * (pt3.northing - hdl.desList[hdl.desList.Count - 1].northing));
                            if (dist > 1)
                                hdl.desList.Add(pt3);
                        }
                        else hdl.desList.Add(pt3);
                    }
                }

                if (hdl.desList.Count == 0)
                    return false;

                pt3 = new vec3(hdl.desList[0]);
                hdl.desList.Add(pt3);

                int cnt = hdl.desList.Count;
                if (cnt > 3)
                {
                    pt3 = new vec3(hdl.desList[0]);
                    hdl.desList.Add(pt3);

                    CABCurve.MakePointMinimumSpacing(ref hdl.desList, 1.2);
                    CABCurve.CalculateHeadings(ref hdl.desList);

                    bnd.bndList[0].hdLine.Clear();
                    foreach (vec3 item in hdl.desList)
                        bnd.bndList[0].hdLine.Add(item);
                }
            }

            FileSaveHeadland();
            HeadlandEdit_RefreshOnFlag();
            return true;
        }

        // Cabecera = copia del contorno.
        internal void HeadlandEdit_Reset()
        {
            if (!HeadlandEdit_HasBoundary()) return;
            hdl.desList.Clear();
            bnd.bndList[0].hdLine?.Clear();
            int ptCount = bnd.bndList[0].fenceLine.Count;
            for (int i = 0; i < ptCount; i++)
                bnd.bndList[0].hdLine.Add(new vec3(bnd.bndList[0].fenceLine[i]));
            FileSaveHeadland();
            HeadlandEdit_RefreshOnFlag();
        }

        // Apagar cabecera: limpiar hdLine + persistir.
        internal void HeadlandEdit_TurnOff()
        {
            if (bnd == null || bnd.bndList.Count == 0) return;
            bnd.bndList[0].hdLine?.Clear();
            FileSaveHeadland();
            bnd.isHeadlandOn = false;
        }

        internal void HeadlandEdit_SetSectionControlled(bool on)
        {
            if (bnd == null) return;
            bnd.isSectionControlledByHeadland = on;
            Properties.Settings.Default.setHeadland_isSectionControlled = on;
            Properties.Settings.Default.Save();
        }
    }
}
