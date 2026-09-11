// ITrackListService — lectura/selección de las guías (AB/curvas) del lote
// activo. Mismo patrón que IGuidanceCalculator/IImuCalibracionService: la
// interfaz vive en netstandard2.0, la implementación real ("using AgOpenGPS")
// vive del lado GPS en AgroParallel.Adapters (FormGpsTrackListService).

using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ITrackListService
    {
        /// <summary>Todas las guías del lote activo, en el mismo orden que
        /// trk.gArr (el índice devuelto es el que espera SelectTrack).</summary>
        List<TrackItemDto> GetTracks();

        /// <summary>Activa la guía en esa posición (trk.idx = index). false si
        /// el índice está fuera de rango.</summary>
        bool SelectTrack(int index);
    }
}
