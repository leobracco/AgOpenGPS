// ============================================================================
// FormGPS.RecPath.cs — lógica de recorded paths para HTML (recpath.html).
// Port de FormRecordName + FormRecordPicker. Hilo UI.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgLibrary.Logging;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // ── List .rec files ─────────────────────────────────────────────
        internal List<string> RecPath_ListFiles()
        {
            var list = new List<string>();
            string fieldDir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
            if (!Directory.Exists(fieldDir)) return list;

            foreach (string file in Directory.GetFiles(fieldDir, "*.rec"))
                list.Add(Path.GetFileNameWithoutExtension(file));

            return list;
        }

        // ── Load a .rec file ────────────────────────────────────────────
        internal bool RecPath_Load(string name)
        {
            string fieldDir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
            string recFile = Path.Combine(fieldDir, name + ".rec");
            if (!File.Exists(recFile)) return false;

            try
            {
                // Copy to RecPath.txt (auto-load on next field open)
                File.Copy(recFile, Path.Combine(fieldDir, "RecPath.txt"), true);

                using (StreamReader reader = new StreamReader(recFile))
                {
                    reader.ReadLine(); // header
                    string line = reader.ReadLine();
                    int numPoints = int.Parse(line);
                    recPath.recList.Clear();

                    while (!reader.EndOfStream)
                    {
                        for (int v = 0; v < numPoints; v++)
                        {
                            line = reader.ReadLine();
                            string[] words = line.Split(',');
                            CRecPathPt point = new CRecPathPt(
                                double.Parse(words[0], CultureInfo.InvariantCulture),
                                double.Parse(words[1], CultureInfo.InvariantCulture),
                                double.Parse(words[2], CultureInfo.InvariantCulture),
                                double.Parse(words[3], CultureInfo.InvariantCulture),
                                bool.Parse(words[4]));
                            recPath.recList.Add(point);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.EventWriter("Load Recorded Path " + ex.ToString());
                return false;
            }
        }

        // ── Delete a .rec file ──────────────────────────────────────────
        internal bool RecPath_Delete(string name)
        {
            string fieldDir = Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory);
            string recFile = Path.Combine(fieldDir, name + ".rec");
            if (!File.Exists(recFile)) return false;
            try { File.Delete(recFile); return true; }
            catch { return false; }
        }

        // ── Turn off recorded path ──────────────────────────────────────
        internal void RecPath_TurnOff()
        {
            recPath.StopDrivingRecordedPath();
            recPath.recList.Clear();
            FileSaveRecPath();
            panelDrag.Visible = false;
        }

        // ── Save with name (after stop recording) ───────────────────────
        internal bool RecPath_SaveWithName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string filename = name.Trim() + ".rec";
            FileSaveRecPath();
            FileSaveRecPath(filename);
            return true;
        }

        // ── Discard recording ───────────────────────────────────────────
        internal void RecPath_Discard()
        {
            recPath.recList.Clear();
        }
    }
}
