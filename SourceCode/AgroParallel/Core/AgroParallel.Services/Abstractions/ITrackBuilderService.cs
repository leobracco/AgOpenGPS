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

        // Guardar y salir: FileSaveTracks, elegir la guía seleccionada
        // (o la primera visible, o ninguna).
        void CloseUse();

        // Cancelar: restaurar gArr del backup.
        void CloseCancel();
    }
}
