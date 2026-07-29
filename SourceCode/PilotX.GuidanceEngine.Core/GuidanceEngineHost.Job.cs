// ============================================================================
// GuidanceEngineHost.Job.cs — abrir/cerrar lote headless (bloque 14). Mismo
// flujo de datos que FormGPS.FileOpenField/JobNew (SaveOpen.Designer.cs/
// FormGPS.cs) contra los mismos streamers portables (AgOpenGPS.IO, ya usados
// tal cual desde el lado FormGPS) — pero sin OpenFileDialog ni las ~30
// asignaciones de botones WinForms de JobNew() (irrelevantes sin UI).
//
// Sin esto, IsJobStarted nunca pasa a true en este proceso: ni el envío de
// PGN 239/229 (UpdateFixPosition) ni ningún test de guiado con AB/boundary
// real se puede ejercitar, solo el simulador en campo abierto.
//
// FieldBoundingBox/maxFieldDistance (CalculateMinMax en OpenGL.Designer.cs)
// se omiten a propósito: solo se usan para encuadrar la cámara (bloque 6),
// ninguna clase de guiado en Core los lee.
// ============================================================================

using System;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        public bool OpenField(string fieldName)
        {
            fieldName = (fieldName ?? "").Trim();
            if (fieldName.Length == 0) return false;

            string dir = Path.Combine(RegistrySettings.fieldsDirectory, fieldName);
            if (!Directory.Exists(dir))
            {
                Log.EventWriter("GuidanceEngine: lote no encontrado: " + dir);
                return false;
            }

            Wgs84 origin;
            try { origin = FieldPlaneFiles.LoadOrigin(dir); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: Field.txt invalido en " + fieldName + ": " + ex.Message);
                return false;
            }
            Pn.DefineLocalPlane(origin, true);

            AppModelField.Fields.OpenField(new DirectoryInfo(dir));
            currentFieldDirectory = fieldName;
            displayFieldName = fieldName;
            startCounter = 0;
            manualBtnState = btnStates.Off;
            autoBtnState = btnStates.Off;
            ABLineField.abHeading = 0.0;

            try
            {
                var tracks = TrackFiles.Load(dir);
                Trk.gArr.Clear();
                Trk.gArr.AddRange(tracks);
                Trk.idx = -1;
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: TrackLines.txt: " + ex.Message); }

            try
            {
                var boundaries = BoundaryFiles.Load(dir);
                Bnd.bndList.Clear();
                Bnd.bndList.AddRange(boundaries);
                Bnd.BuildTurnLines();
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: Boundary.txt: " + ex.Message); }

            string msg = $"GuidanceEngine: lote abierto: {fieldName} (tracks={Trk.gArr.Count}, boundaries={Bnd.bndList.Count}, IsJobStarted={IsJobStarted})";
            Log.EventWriter(msg);
            Console.WriteLine(msg);
            return true;
        }

        public void CloseField()
        {
            AppModelField.Fields.CloseField();
            Bnd.bndList.Clear();
            Trk.gArr.Clear();
            Trk.idx = -1;

            // Soltar la cobertura dibujada. Sin esto el lindero y las guías se
            // iban pero la pintura quedaba en pantalla: /api/aog/coverage seguía
            // devolviendo los parches del lote anterior y el mapa los mostraba
            // sobre un lote que ya no estaba abierto — y peor, se mezclaba con
            // lo del lote siguiente.
            //
            // Es SOLO memoria: no se toca ningún archivo ni se resetea el área
            // trabajada. Para borrar lo aplicado de verdad está el comando
            // dedicado, que además reescribe los archivos del lote.
            //
            // patchSaveList NO se limpia a propósito: ahí quedan los parches que
            // esperan bajar a disco, y vaciarlo perdería cobertura ya trabajada.
            for (int j = 0; j < TriStripField.Count; j++)
            {
                TriStripField[j]?.patchList?.Clear();
                TriStripField[j]?.triangleList?.Clear();
            }

            currentFieldDirectory = "";
            displayFieldName = "";
            string msg = $"GuidanceEngine: lote cerrado (IsJobStarted={IsJobStarted})";
            Log.EventWriter(msg);
            Console.WriteLine(msg);
        }
    }
}
