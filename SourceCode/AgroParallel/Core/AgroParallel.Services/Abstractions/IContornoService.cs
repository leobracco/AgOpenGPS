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
