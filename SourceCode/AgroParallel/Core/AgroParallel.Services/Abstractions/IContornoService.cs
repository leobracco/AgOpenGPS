// ============================================================================
// IContornoService.cs — contrato de la página Contorno (contorno.html).
// Reemplaza FormBoundary (lista, drive-thru, borrar, KML, Google Earth) y
// FormBoundaryPlayer (grabación manejando). Implementado por PilotX
// (FormGpsContornoService) en el hilo UI; nunca tira, en error Ok=false.
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IContornoService
    {
        ContornoStateDto GetState();

        ContornoStateDto SetDriveThru(int index, bool value);
        ContornoStateDto Delete(int index);
        ContornoStateDto DeleteAll();

        // Diálogos/forms nativos (bloquean en hilo UI).
        ContornoStateDto ImportKml(bool multi);

        /// <summary>Import de KML SIN diálogo: la pantalla sube el contenido
        /// del archivo (texto KML crudo). multi=false agrega el primer polígono
        /// a la lista (p.ej. sumar una exclusión); multi=true reemplaza TODO
        /// por los polígonos del KML — la confirmación de pisar lo existente
        /// la pone la pantalla antes de llamar.</summary>
        ContornoStateDto ImportKmlUpload(string kmlContenido, bool multi);
        ContornoStateDto OpenGoogleEarth();
        ContornoStateDto OpenMapa();        // FormMap: dibujar sobre satelital
        ContornoStateDto BuildFromTracks(); // FormBuildBoundaryFromTracks

        // Grabación manejando (ex FormBoundaryPlayer).
        ContornoRecordDto RecordStart();
        ContornoRecordDto RecordStatus();
        ContornoRecordDto RecordSet(double? offsetCm, bool? rightSide, bool? atPivot, bool? sectionRec);
        ContornoRecordDto RecordPause();
        ContornoRecordDto RecordAddPoint();
        ContornoRecordDto RecordUndo();
        ContornoRecordDto RecordRestart();
        ContornoRecordDto RecordSave();
        ContornoRecordDto RecordCancel();
    }
}
