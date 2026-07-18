// ============================================================================
// FormGPS.Flags.cs — lógica del widget "Banderas" (banderas.html).
// Reemplaza los WinForms FormFlags (lista/selección/borrado/notas/distancia)
// y FormEnterFlag (alta por lat/lon + import/export CSV). El estado vive donde
// siempre (flagPts / flagNumberPicked de FormGPS); el timer de 500 ms del form
// nativo se replica con el poll del JS a GET /api/flags/state.
// Todos los métodos asumen hilo UI (el adapter marshalea).
// ============================================================================

using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        internal FlagsSnapshot Flags_Snapshot(string error = null)
        {
            // Igual que FormFlags.UpdateLabels: clamp del seleccionado.
            if (flagNumberPicked > flagPts.Count) flagNumberPicked = flagPts.Count;
            if (flagNumberPicked < 0) flagNumberPicked = 0;

            var s = new FlagsSnapshot
            {
                HasField = isJobStarted,
                Picked = flagNumberPicked,
                CurLat = AppModel.CurrentLatLon.Latitude,
                CurLon = AppModel.CurrentLatLon.Longitude,
                Error = error
            };

            for (int i = 0; i < flagPts.Count; i++)
            {
                var f = flagPts[i];
                s.Items.Add(new FlagsSnapshotItem
                {
                    Number = i + 1,
                    Id = f.ID,
                    Color = f.color,
                    Notes = f.notes ?? "",
                    Lat = f.latitude,
                    Lon = f.longitude,
                    DistanceM = Math.Round(glm.Distance(pn.fix, f.easting, f.northing), 2)
                });
            }
            return s;
        }

        internal FlagsSnapshot Flags_Pick(int number)
        {
            if (flagPts.Count > 0 && number >= 1 && number <= flagPts.Count)
                flagNumberPicked = number;
            return Flags_Snapshot();
        }

        // Réplica de btnDeleteFlag_Click: borrar la seleccionada, reseleccionar
        // la más cercana y persistir.
        internal FlagsSnapshot Flags_Delete()
        {
            int flag = flagNumberPicked;
            if (flagPts.Count > 0 && flag > 0) DeleteSelectedFlag();
            if (flagPts.Count > 0)
            {
                flagNumberPicked = flag > flagPts.Count ? flagPts.Count : flag;
            }
            FileSaveFlags();
            return Flags_Snapshot();
        }

        internal FlagsSnapshot Flags_SetNotes(string notes)
        {
            if (flagNumberPicked > 0 && flagNumberPicked <= flagPts.Count)
            {
                flagPts[flagNumberPicked - 1].notes = notes ?? "";
                FileSaveFlags();
            }
            return Flags_Snapshot();
        }

        // Alta: réplica de btnFlag_Click (posición actual, con rumbo) o de
        // FormEnterFlag.btnRed_Click (lat/lon manual, rumbo 0).
        internal FlagsSnapshot Flags_Add(double lat, double lon, int color, bool useCurrent)
        {
            if (!isJobStarted) return Flags_Snapshot("sin-lote");
            if (color < 0 || color > 2) color = 0;

            int nextflag = flagPts.Count + 1;
            CFlag flagPt;
            if (useCurrent)
            {
                flagPt = new CFlag(
                    AppModel.CurrentLatLon.Latitude, AppModel.CurrentLatLon.Longitude,
                    pn.fix.easting, pn.fix.northing,
                    fixHeading, color, nextflag, nextflag.ToString());
            }
            else
            {
                if (double.IsNaN(lat) || double.IsNaN(lon) ||
                    lat < -90 || lat > 90 || lon < -180 || lon > 180)
                    return Flags_Snapshot("latlon-invalido");

                GeoCoord geoCoord = AppModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
                flagPt = new CFlag(
                    lat, lon, geoCoord.Easting, geoCoord.Northing,
                    0, color, nextflag, nextflag.ToString());
            }

            flagPts.Add(flagPt);
            flagPts = FlagsFiles.DeduplicateFlags(flagPts);
            FileSaveFlags();
            flagNumberPicked = flagPts.Count < nextflag ? flagPts.Count : nextflag;
            return Flags_Snapshot();
        }

        // Cierre del widget (ex btnExit): deseleccionar y persistir.
        internal FlagsSnapshot Flags_Close()
        {
            flagNumberPicked = 0;
            FileSaveFlags();
            return Flags_Snapshot();
        }

        // Réplica de FormEnterFlag.btnImportFlags_Click (sin popups nativos:
        // el error vuelve en el snapshot y lo muestra el HTML).
        internal FlagsSnapshot Flags_Import()
        {
            if (!isJobStarted) return Flags_Snapshot("sin-lote");

            using (OpenFileDialog fileDialog = new OpenFileDialog
            {
                Filter = "Text Document | *.txt| CSV Document | *.csv",
                Title = "Elegí el archivo de banderas",
                Multiselect = false,
                RestoreDirectory = true
            })
            {
                if (fileDialog.ShowDialog(this) != DialogResult.OK) return Flags_Snapshot();

                try
                {
                    string[] lines = File.ReadAllLines(fileDialog.FileName);
                    foreach (string line in lines.Skip(1))
                    {
                        string[] parts = line.Split(',');
                        if (parts.Length >= 4 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude) &&
                            int.TryParse(parts[2], out int color))
                        {
                            string flagName = !string.IsNullOrWhiteSpace(parts[3])
                                ? parts[3].Trim() : $"{flagPts.Count + 1}";
                            GeoCoord geoCoord = AppModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(latitude, longitude));
                            int nextflag = flagPts.Count + 1;
                            flagPts.Add(new CFlag(
                                latitude, longitude,
                                geoCoord.Easting, geoCoord.Northing,
                                0, color, nextflag, flagName));
                            flagPts = FlagsFiles.DeduplicateFlags(flagPts);
                        }
                        else
                        {
                            FileSaveFlags();
                            return Flags_Snapshot("linea-invalida: " + line);
                        }
                    }
                    FileSaveFlags();
                    return Flags_Snapshot();
                }
                catch (Exception ex)
                {
                    AgLibrary.Logging.Log.EventWriter("Flags import " + ex);
                    return Flags_Snapshot("import-error: " + ex.Message);
                }
            }
        }

        // Réplica de FormEnterFlag.btnExportFlags_Click.
        internal FlagsSnapshot Flags_Export()
        {
            using (SaveFileDialog fileDialog = new SaveFileDialog
            {
                DefaultExt = "txt",
                Filter = "Text Document | *.txt| CSV Document | *.csv| All files| *.*",
                Title = "Exportar banderas",
                CheckFileExists = false,
                RestoreDirectory = true
            })
            {
                if (fileDialog.ShowDialog(this) != DialogResult.OK) return Flags_Snapshot();

                try
                {
                    using (StreamWriter writer = new StreamWriter(fileDialog.FileName))
                    {
                        writer.WriteLine("Latitude,Longitude,Color,Notes");
                        foreach (CFlag flag in flagPts)
                        {
                            writer.WriteLine(
                                flag.latitude.ToString(CultureInfo.InvariantCulture) + "," +
                                flag.longitude.ToString(CultureInfo.InvariantCulture) + "," +
                                flag.color.ToString(CultureInfo.InvariantCulture) + "," +
                                flag.notes);
                        }
                    }
                    return Flags_Snapshot();
                }
                catch (Exception ex)
                {
                    AgLibrary.Logging.Log.EventWriter("Flags export " + ex);
                    return Flags_Snapshot("export-error: " + ex.Message);
                }
            }
        }

        internal sealed class FlagsSnapshotItem
        {
            public int Number;
            public int Id;
            public int Color;
            public string Notes;
            public double Lat;
            public double Lon;
            public double DistanceM;
        }

        internal sealed class FlagsSnapshot
        {
            public bool HasField;
            public int Picked;
            public double CurLat;
            public double CurLon;
            public string Error;
            public List<FlagsSnapshotItem> Items = new List<FlagsSnapshotItem>();
        }
    }
}
