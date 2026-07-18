// ============================================================================
// FormGPS.Contorno.cs — lógica de la página "Contorno" (contorno.html).
// Reemplaza los WinForms FormBoundary (lista/drive-thru/borrar/KML/Google
// Earth/desde tracks) y FormBoundaryPlayer (grabación manejando). El estado
// vive donde siempre (bnd.* de CBoundary); el timer de 500 ms del player se
// replica con el poll del JS a GET /api/contorno/record-status.
// Confirmaciones ("¿borrar?", "¿terminado?") las hace el HTML — acá no hay
// FormDialog salvo los diálogos de archivo/forms nativos (KML, FormMap,
// FormBuildBoundaryFromTracks). Todos los métodos asumen hilo UI.
// ============================================================================

using AgLibrary.Logging;
using AgOpenGPS.Classes;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Forms.Field;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        internal ContornoSnapshot Contorno_Snapshot(string error = null)
        {
            var s = new ContornoSnapshot
            {
                JobStarted = isJobStarted,
                ToolWidth = tool.width,
                Recording = bnd.isBndBeingMade,
                Error = error
            };

            for (int i = 0; i < bnd.bndList.Count; i++)
            {
                // Igual que FormBoundary.UpdateChart: el exterior nunca es drive-thru.
                if (i == 0) bnd.bndList[i].isDriveThru = false;

                s.Items.Add(new ContornoSnapshotItem
                {
                    Index = i,
                    IsOuter = i == 0,
                    AreaHa = Math.Round(bnd.bndList[i].area * 0.0001, 3),
                    IsDriveThru = bnd.bndList[i].isDriveThru,
                    Points = bnd.bndList[i].fenceLine.Count
                });
            }
            return s;
        }

        internal ContornoSnapshot Contorno_SetDriveThru(int index, bool value)
        {
            // Réplica de DriveThru_Click (solo internos).
            if (index > 0 && index < bnd.bndList.Count)
            {
                bnd.bndList[index].isDriveThru = value;
                bnd.BuildTurnLines();
            }
            return Contorno_Snapshot();
        }

        // Réplica de btnDelete_Click. La confirmación la hizo el HTML.
        // El exterior (0) solo se puede borrar si es el único (regla B_Click).
        internal ContornoSnapshot Contorno_Delete(int index)
        {
            if (index < 0 || index >= bnd.bndList.Count)
                return Contorno_Snapshot("indice-invalido");
            if (index == 0 && bnd.bndList.Count > 1)
                return Contorno_Snapshot("borrar-internos-primero");

            bnd.bndList[index].hdLine?.Clear();
            bnd.bndList.RemoveAt(index);

            FileSaveBoundary();
            fd.UpdateFieldBoundaryGUIAreas();
            bnd.BuildTurnLines();
            return Contorno_Snapshot();
        }

        // Réplica de ResetAllBoundary.
        internal ContornoSnapshot Contorno_DeleteAll()
        {
            bnd.bndList.Clear();
            FileSaveBoundary();
            fd.UpdateFieldBoundaryGUIAreas();
            bnd.BuildTurnLines();
            return Contorno_Snapshot();
        }

        // Réplica de btnLoadBoundaryFromGE_Click / btnLoadMultiBoundaryFromGE.
        internal ContornoSnapshot Contorno_ImportKml(bool multi)
        {
            if (!isJobStarted) return Contorno_Snapshot("sin-lote");

            string fileAndDirectory;
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "KML files (*.KML)|*.KML",
                InitialDirectory = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory)
            })
            {
                if (ofd.ShowDialog(this) == DialogResult.Cancel) return Contorno_Snapshot();
                fileAndDirectory = ofd.FileName;
            }

            string coordinates = null;
            int startIndex;
            string error = null;

            using (StreamReader reader = new StreamReader(fileAndDirectory))
            {
                if (multi) bnd.bndList.Clear();

                try
                {
                    while (!reader.EndOfStream)
                    {
                        string line = reader.ReadLine();
                        startIndex = line.IndexOf("<coordinates>");

                        if (startIndex != -1)
                        {
                            while (true)
                            {
                                int endIndex = line.IndexOf("</coordinates>");

                                if (endIndex == -1)
                                {
                                    if (startIndex == -1) coordinates += line.Substring(0);
                                    else coordinates += line.Substring(startIndex + 13);
                                }
                                else
                                {
                                    if (startIndex == -1) coordinates += line.Substring(0, endIndex);
                                    else coordinates += line.Substring(startIndex + 13, endIndex - (startIndex + 13));
                                    break;
                                }
                                line = reader.ReadLine();
                                line = line.Trim();
                                startIndex = -1;
                            }

                            line = coordinates;
                            char[] delimiterChars = { ' ', '\t', '\r', '\n' };
                            string[] numberSets = line.Split(delimiterChars);

                            if (numberSets.Length > 2)
                            {
                                CBoundaryList New = new CBoundaryList();

                                foreach (string item in numberSets)
                                {
                                    string[] fix = item.Split(',');
                                    double.TryParse(fix[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lonK);
                                    double.TryParse(fix[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double latK);

                                    GeoCoord geoCoord = AppModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(latK, lonK));
                                    New.fenceLine.Add(new vec3(geoCoord));
                                }

                                New.CalculateFenceArea(bnd.bndList.Count);
                                New.FixFenceLine(bnd.bndList.Count);

                                bnd.bndList.Add(New);
                                btnABDraw.Visible = true;
                                coordinates = "";
                            }
                            else
                            {
                                error = "kml-invalido";
                                Log.EventWriter("KML Read Error importing boundary");
                            }

                            if (!multi) break;
                        }
                    }
                    FileSaveBoundary();
                    fd.UpdateFieldBoundaryGUIAreas();
                    bnd.BuildTurnLines();
                    btnABDraw.Visible = true;
                }
                catch (Exception ed)
                {
                    Log.EventWriter("Load Boundary from KML " + ed.ToString());
                    error = "kml-error: " + ed.Message;
                }
            }

            bnd.isOkToAddPoints = false;
            return Contorno_Snapshot(error);
        }

        // Réplica de btnOpenGoogleEarth_Click.
        internal ContornoSnapshot Contorno_OpenGoogleEarth()
        {
            if (!isJobStarted) return Contorno_Snapshot("sin-lote");
            try
            {
                FileMakeKMLFromCurrentPosition(AppModel.CurrentLatLon);
                System.Diagnostics.Process.Start(Path.Combine(
                    RegistrySettings.fieldsDirectory, currentFieldDirectory, "CurrentPosition.KML"));
            }
            catch (Exception ex)
            {
                Log.EventWriter("Contorno GoogleEarth " + ex);
                return Contorno_Snapshot("google-earth-error");
            }
            return Contorno_Snapshot();
        }

        // FormMap (dibujar sobre satelital) sigue nativo: réplica del camino
        // DialogResult.Yes de boundariesToolStripMenuItem_Click.
        internal ContornoSnapshot Contorno_OpenMapa()
        {
            if (!isJobStarted) return Contorno_Snapshot("sin-lote");
            new FormMap(this).Show(this);
            return Contorno_Snapshot();
        }

        // Réplica de btnBuildBoundaryFromTracks_Click. La confirmación de pisar
        // el contorno existente la hizo el HTML.
        internal ContornoSnapshot Contorno_BuildFromTracks()
        {
            if (!isJobStarted) return Contorno_Snapshot("sin-lote");

            bnd.bndList.Clear();
            using (var form = new FormBuildBoundaryFromTracks(this, null))
            {
                form.ShowDialog(this);
            }
            PanelUpdateRightAndBottom();
            return Contorno_Snapshot();
        }

        // ------------------------------------------------------------------
        // Grabación manejando (ex FormBoundaryPlayer)
        // ------------------------------------------------------------------

        internal ContornoRecSnapshot ContornoRec_Snapshot(string error = null)
        {
            // Área shoelace de los puntos en curso (réplica de timer1_Tick).
            int ptCount = bnd.bndBeingMadePts.Count;
            double area = 0;
            if (ptCount > 0)
            {
                int j = ptCount - 1;
                for (int i = 0; i < ptCount; j = i++)
                {
                    area += (bnd.bndBeingMadePts[j].easting + bnd.bndBeingMadePts[i].easting)
                          * (bnd.bndBeingMadePts[j].northing - bnd.bndBeingMadePts[i].northing);
                }
                area = Math.Abs(area / 2);
            }

            return new ContornoRecSnapshot
            {
                Active = bnd.isBndBeingMade,
                Paused = !bnd.isOkToAddPoints,
                Points = ptCount,
                AreaHa = Math.Round(area * 0.0001, 2),
                OffsetCm = Math.Round(bnd.createBndOffset * 100.0),
                RightSide = bnd.isDrawRightSide,
                AtPivot = bnd.isDrawAtPivot,
                SectionRec = bnd.isRecBoundaryWhenSectionOn,
                Error = error
            };
        }

        // Réplica de FormBoundaryPlayer_Load (+ guard de btnAdd_Click).
        internal ContornoRecSnapshot ContornoRec_Start()
        {
            if (!isJobStarted) return ContornoRec_Snapshot("sin-lote");
            if (tool.width < 0.2)
            {
                Log.EventWriter("Boundary, Tool is too narrow");
                return ContornoRec_Snapshot("herramienta-angosta");
            }

            bnd.bndBeingMadePts.Clear();
            bnd.createBndOffset = tool.width * 0.5;
            bnd.isDrawAtPivot = Properties.Settings.Default.setBnd_isDrawPivot;
            bnd.isBndBeingMade = true;
            bnd.isOkToAddPoints = false; // arranca en pausa, como el player
            return ContornoRec_Snapshot();
        }

        internal ContornoRecSnapshot ContornoRec_Set(
            double? offsetCm, bool? rightSide, bool? atPivot, bool? sectionRec)
        {
            if (offsetCm.HasValue)
            {
                double cm = offsetCm.Value;
                if (cm < 0) cm = 0;
                if (cm > 4999) cm = 4999; // tope del nudOffset métrico
                bnd.createBndOffset = cm * 0.01;
            }
            if (rightSide.HasValue) bnd.isDrawRightSide = rightSide.Value;
            if (atPivot.HasValue)
            {
                bnd.isDrawAtPivot = atPivot.Value;
                Properties.Settings.Default.setBnd_isDrawPivot = atPivot.Value;
            }
            if (sectionRec.HasValue) bnd.isRecBoundaryWhenSectionOn = sectionRec.Value;
            return ContornoRec_Snapshot();
        }

        // Réplica de btnPausePlay_Click (toggle grabar/pausa).
        internal ContornoRecSnapshot ContornoRec_Pause()
        {
            if (!bnd.isBndBeingMade) return ContornoRec_Snapshot("sin-grabacion");
            bnd.isOkToAddPoints = !bnd.isOkToAddPoints;
            return ContornoRec_Snapshot();
        }

        // Réplica de btnAddPoint_Click (solo en pausa, como el form nativo).
        internal ContornoRecSnapshot ContornoRec_AddPoint()
        {
            if (!bnd.isBndBeingMade) return ContornoRec_Snapshot("sin-grabacion");
            if (!bnd.isOkToAddPoints)
            {
                bnd.isOkToAddPoints = true;
                AddBoundaryPoint();
                bnd.isOkToAddPoints = false;
            }
            return ContornoRec_Snapshot();
        }

        // Réplica de btnDeleteLast_Click.
        internal ContornoRecSnapshot ContornoRec_Undo()
        {
            int ptCount = bnd.bndBeingMadePts.Count;
            if (ptCount > 0) bnd.bndBeingMadePts.RemoveAt(ptCount - 1);
            return ContornoRec_Snapshot();
        }

        // Réplica de btnRestart_Click (confirmación en el HTML).
        internal ContornoRecSnapshot ContornoRec_Restart()
        {
            bnd.bndBeingMadePts?.Clear();
            return ContornoRec_Snapshot();
        }

        // Réplica de btnStop_Click camino OK (confirmación "¿Terminado?" en HTML).
        internal ContornoRecSnapshot ContornoRec_Save()
        {
            if (!bnd.isBndBeingMade) return ContornoRec_Snapshot("sin-grabacion");

            string error = null;
            if (bnd.bndBeingMadePts.Count > 2)
            {
                CBoundaryList New = new CBoundaryList();
                for (int i = 0; i < bnd.bndBeingMadePts.Count; i++)
                {
                    New.fenceLine.Add(bnd.bndBeingMadePts[i]);
                }

                New.CalculateFenceArea(bnd.bndList.Count);
                New.FixFenceLine(bnd.bndList.Count);
                bnd.bndList.Add(New);

                fd.UpdateFieldBoundaryGUIAreas();
                CalculateMinMax();
                FileSaveBoundary();
                bnd.BuildTurnLines();
                btnABDraw.Visible = true;

                Log.EventWriter("Driven Boundary Created, Area Ha: "
                    + (New.area * 0.0001).ToString("0.00", CultureInfo.InvariantCulture));
            }
            else
            {
                error = "pocos-puntos";
            }

            bnd.isOkToAddPoints = false;
            bnd.isBndBeingMade = false;
            bnd.bndBeingMadePts.Clear();
            return ContornoRec_Snapshot(error);
        }

        // Cierre sin guardar (cierre de ventana del widget en modo grabación).
        internal ContornoRecSnapshot ContornoRec_Cancel()
        {
            bnd.isOkToAddPoints = false;
            bnd.isBndBeingMade = false;
            bnd.bndBeingMadePts.Clear();
            return ContornoRec_Snapshot();
        }

        internal sealed class ContornoSnapshotItem
        {
            public int Index;
            public bool IsOuter;
            public double AreaHa;
            public bool IsDriveThru;
            public int Points;
        }

        internal sealed class ContornoSnapshot
        {
            public bool JobStarted;
            public double ToolWidth;
            public bool Recording;
            public string Error;
            public List<ContornoSnapshotItem> Items = new List<ContornoSnapshotItem>();
        }

        internal sealed class ContornoRecSnapshot
        {
            public bool Active;
            public bool Paused;
            public int Points;
            public double AreaHa;
            public double OffsetCm;
            public bool RightSide;
            public bool AtPivot;
            public bool SectionRec;
            public string Error;
        }
    }
}
