// ============================================================================
// FormGPS.Lotes.cs — lógica de gestión de lotes para la UI HTML (lote.html).
// Reemplaza los WinForms FormJob (abrir/reanudar/cerrar), FormFieldExisting
// (crear desde existente) y despacha los imports nativos KML / ISO-XML.
// Todos los métodos asumen hilo UI (FormGpsLotesService marshalea).
// ============================================================================

using AgLibrary.Logging;
using AgOpenGPS.Properties;
using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        // Réplica del bloque post-diálogo de btnJobMenu_Click: al abrir/cerrar
        // un lote desde HTML hay que apagar los section masters, avisar si el
        // inicio del lote queda lejos, loguear "** Opened **", persistir
        // setF_CurrentDir y refrescar la toolbar. Sin esto los botones de la
        // pantalla principal quedan desactualizados.
        internal void Lotes_PostOpenFixup(bool wasJobStarted, string prevFieldDir)
        {
            if (isJobStarted)
            {
                if (autoBtnState == btnStates.Auto) btnSectionMasterAuto.PerformClick();
                if (manualBtnState == btnStates.On) btnSectionMasterManual.PerformClick();
            }

            bool openedNewOrChanged =
                isJobStarted &&
                (!wasJobStarted ||
                 !string.Equals(currentFieldDirectory, prevFieldDir, StringComparison.OrdinalIgnoreCase));

            if (openedNewOrChanged)
            {
                double distance = AppModel.CurrentLatLon.DistanceInKiloMeters(AppModel.LocalPlane.Origin);
                if (distance > 10)
                {
                    TimedMessageBox(2500, "High Field Start Distance Warning",
                        "Field Start is " + distance.ToString("N1") + " km From current position");
                    Log.EventWriter("High Field Start Distance Warning");
                }

                Log.EventWriter("** Opened **  " + currentFieldDirectory + "   " +
                    DateTime.Now.ToString("f", CultureInfo.InvariantCulture));

                Settings.Default.setF_CurrentDir = currentFieldDirectory;
                Settings.Default.Save();
            }

            FieldMenuButtonEnableDisable(isJobStarted);
            toolStripBtnFieldTools.Enabled = isJobStarted;
            bnd.isHeadlandOn = (bnd.bndList.Count > 0 && bnd.bndList[0].hdLine.Count > 0);
            trk.idx = -1;
            PanelUpdateRightAndBottom();
        }

        // Réplica de FormFieldExisting.btnSave_Click: clona el template a un
        // directorio nuevo y abre el lote clonado. Devuelve false ante template
        // roto / nombre duplicado (el JS muestra el error genérico).
        internal bool Lotes_CreateFromExisting(string templateName, string newName,
                                               bool copyApplied, bool copyFlags,
                                               bool copyGuidance, bool copyHeadland)
        {
            string root = RegistrySettings.fieldsDirectory;
            string templateDir = Path.Combine(root, templateName);
            string templateField = Path.Combine(templateDir, "Field.txt");
            if (!File.Exists(templateField)) return false;

            string newDir = Path.Combine(root, newName);
            if (Directory.Exists(newDir)) return false;

            string offsets, convergence, startFix;
            try
            {
                using (var reader = new StreamReader(templateField))
                {
                    reader.ReadLine(); reader.ReadLine(); reader.ReadLine(); reader.ReadLine();
                    offsets = reader.ReadLine();
                    reader.ReadLine();
                    convergence = reader.ReadLine();
                    reader.ReadLine();
                    startFix = reader.ReadLine();
                }
                if (offsets == null || convergence == null || startFix == null) return false;
            }
            catch (Exception ex)
            {
                Log.EventWriter("Lotes_CreateFromExisting: template roto " + templateName + "\n" + ex);
                return false;
            }

            try
            {
                Directory.CreateDirectory(newDir);

                using (var writer = new StreamWriter(Path.Combine(newDir, "Field.txt")))
                {
                    writer.WriteLine(DateTime.Now.ToString("yyyy-MMMM-dd hh:mm:ss tt", CultureInfo.InvariantCulture));
                    writer.WriteLine("$FieldDir");
                    writer.WriteLine("FromExisting");
                    writer.WriteLine("$Offsets");
                    writer.WriteLine(offsets);
                    writer.WriteLine("$Convergence");
                    writer.WriteLine(convergence);
                    writer.WriteLine("StartFix");
                    writer.WriteLine(startFix);
                }

                void CopyIfExists(string file)
                {
                    string src = Path.Combine(templateDir, file);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(newDir, file));
                }

                if (copyApplied)
                {
                    CopyIfExists("Contour.txt");
                    CopyIfExists("Sections.txt");
                }
                else
                {
                    File.WriteAllText(Path.Combine(newDir, "Sections.txt"), "");
                    File.WriteAllLines(Path.Combine(newDir, "Contour.txt"), new[] { "$Contour" });
                }

                CopyIfExists("BackPic.txt");
                CopyIfExists("BackPic.png");
                CopyIfExists("Boundary.txt");
                CopyIfExists("Elevation.txt");

                if (File.Exists(Path.Combine(templateDir, "Headlines.txt")))
                    CopyIfExists("Headlines.txt");
                else
                    File.WriteAllLines(Path.Combine(newDir, "Headlines.txt"), new[] { "$Headlines" });

                if (copyFlags) CopyIfExists("Flags.txt");
                else File.WriteAllLines(Path.Combine(newDir, "Flags.txt"), new[] { "$Flags", "0" });

                if (copyGuidance)
                {
                    CopyIfExists("ABLines.txt");
                    CopyIfExists("RecPath.txt");
                    CopyIfExists("CurveLines.txt");
                    CopyIfExists("Tram.txt");
                    CopyIfExists("TrackLines.txt");
                }
                else
                {
                    File.WriteAllLines(Path.Combine(newDir, "RecPath.txt"), new[] { "$RecPath", "0" });
                }

                if (copyHeadland) CopyIfExists("Headland.txt");

                FileOpenField(Path.Combine(newDir, "Field.txt"));
                return isJobStarted;
            }
            catch (Exception ex)
            {
                Log.EventWriter("Lotes_CreateFromExisting: " + ex);
                return false;
            }
        }

        // Imports nativos — mismos diálogos WinForms que despachaba FormJob
        // (DialogResult.No → KML, DialogResult.Abort → ISO-XML).
        internal bool Lotes_ImportKml()
        {
            using (var form = new FormFieldKML(this)) { form.ShowDialog(this); }
            return isJobStarted;
        }

        internal bool Lotes_ImportIsoXml()
        {
            using (var form = new FormFieldIsoXml(this)) { form.ShowDialog(this); }
            return isJobStarted;
        }
    }
}
