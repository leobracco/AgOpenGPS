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

            CargarCobertura(dir);
            CargarRestoDelLote(dir);

            string msg = $"GuidanceEngine: lote abierto: {fieldName} (tracks={Trk.gArr.Count}, boundaries={Bnd.bndList.Count}, parches={ParchesCargados}, IsJobStarted={IsJobStarted})";
            Log.EventWriter(msg);
            Console.WriteLine(msg);
            return true;
        }

        public void CloseField()
        {
            // Cerrar el mapeo de las tiras que estan pintando ANTES de guardar.
            //
            // patchSaveList solo se llena cuando un parche se corta a los 61
            // triangulos; el parche que se esta dibujando en ese momento no esta
            // ahi. TurnMappingOff() es justo lo que lo empuja a la cola (ver
            // CPatches.TurnMappingOff). Sin esto, cerrar el lote guardaba los
            // parches viejos y perdia el ultimo tramo sembrado — verificado:
            // Sections.txt no se creaba con una sola pasada corta.
            for (int j = 0; j < TriStripField.Count; j++)
            {
                if (TriStripField[j] != null && TriStripField[j].isDrawing)
                    TriStripField[j].TurnMappingOff();
            }

            // PRIMERO a disco. Abajo se limpian los parches, asi que invertir el
            // orden perderia todo lo trabajado desde la ultima guardada.
            GuardarCoberturaPendiente();
            GuardarRestoDelLote();

            // Apagar el GUIADO, no solo soltar el lote.
            //
            // Cerrar limpiaba los datos del lote (guías, lindero, parches) pero
            // dejaba prendido el guiado ACTIVO, que es estado aparte. Quedaba:
            //   · el piloto ENGANCHADO sin lote — una máquina que sigue
            //     corrigiendo la dirección sola contra una línea de un lote que
            //     ya no está abierto;
            //   · el giro automático armado;
            //   · la línea AB/curva todavía válida, así que
            //     /api/aog/guidance/geometry la seguía sirviendo y el mapa la
            //     seguía dibujando aunque /api/aog/tracks devolviera vacío.
            //
            // Es la misma secuencia que usa ToggleContour al apagar contorno
            // (Commands.cs), que es donde ya estaba resuelto cómo se deja el
            // guiado en frío. El orden importa: primero soltar el piloto y
            // recién después invalidar las líneas.
            if (isBtnAutoSteerOn) ((IAutoSteerHost)this).PerformAutoSteerClick();
            Yt.isYouTurnBtnOn = false;
            Yt.ResetYouTurn();
            if (ABLineField != null) ABLineField.isABValid = false;
            if (CurveField != null) CurveField.isCurveValid = false;
            Ct.isContourBtnOn = false;
            Ct.isLocked = false;
            Trk.isAutoTrack = false;

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
