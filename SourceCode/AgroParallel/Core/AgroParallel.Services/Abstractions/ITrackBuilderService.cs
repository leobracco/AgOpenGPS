// ============================================================================
// ITrackBuilderService.cs — contrato del gestor de tracks HTML (tracks.html).
// Reemplazo de FormBuildTracks: CRUD completo de líneas de guiado + backup/
// restore para cancel. Sin poll: cada acción devuelve el estado completo.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ITrackBuilderService
    {
        // Iniciar sesión: copia backup de gArr + devuelve estado completo.
        TrackBuilderStateDto Open();

        // Estado completo (sin mutación).
        TrackBuilderStateDto GetState();

        // Toggle visibilidad de un track.
        TrackBuilderStateDto ToggleVisibility(int index);

        // Toggle visibilidad de TODOS (show all / hide all).
        TrackBuilderStateDto ToggleAll(bool visible);

        // Seleccionar un track (highlight, no activar guiado).
        TrackBuilderStateDto Select(int index);

        // Borrar el track seleccionado.
        TrackBuilderStateDto Delete();

        // Duplicar el track seleccionado (con nuevo nombre).
        TrackBuilderStateDto Duplicate(string newName);

        // Renombrar el track seleccionado.
        TrackBuilderStateDto Rename(string newName);

        // Mover el track seleccionado arriba/abajo en la lista.
        TrackBuilderStateDto MoveUp();
        TrackBuilderStateDto MoveDown();

        // Swap A↔B del track seleccionado (invierte heading, revierte curva).
        TrackBuilderStateDto SwapAB();

        // Crear AB desde posición actual del vehículo (A+ heading).
        TrackBuilderStateDto CreateABFromPivot(double headingDeg, string name);

        // ── Dibujo sobre contorno (ex FormABDraw) ──────────────────────
        // Tap A/B en el contorno: primer tap = punto A (todos los contornos),
        // segundo = punto B (mismo contorno). Tras el segundo tap se habilitan
        // MakeCurve y MakeABLine.
        TrackBuilderStateDto Tap(double easting, double northing);

        // Cancelar el tap A/B pendiente.
        TrackBuilderStateDto CancelTouch();

        // Crear curva desde los puntos A/B marcados en el contorno.
        TrackBuilderStateDto MakeCurve();

        // Crear AB Line desde los puntos A/B marcados en el contorno.
        TrackBuilderStateDto MakeABLine();

        // Crear Boundary Curve (copia del contorno como track bndCurve).
        TrackBuilderStateDto MakeBoundaryCurve();

        // Extender extremo A o B de la curva seleccionada (+49 m).
        TrackBuilderStateDto ExtendA();
        TrackBuilderStateDto ExtendB();

        // ── Creación interactiva (fase 2, ex FormBuildTracks record) ────
        // Marcar A de curva: arranca la grabación automática (isRecordingCurve).
        TrackBuilderStateDto RecordCurveA();

        // Pausar/resumir la grabación de curva.
        TrackBuilderStateDto RecordCurvePause();

        // Marcar B: cierra la curva y la agrega como track.
        TrackBuilderStateDto RecordCurveB(string name);

        // Cancelar la grabación de curva sin guardar.
        TrackBuilderStateDto RecordCurveCancel();

        // Estado de grabación (para poll de la UI).
        bool IsRecordingCurve();
        int RecordedPointCount();

        // Guardar y salir: FileSaveTracks, elegir la guía seleccionada
        // (o la primera visible, o ninguna).
        void CloseUse();

        // Cancelar: restaurar gArr del backup.
        void CloseCancel();
    }
}
