// ============================================================================
// FormGpsFlagsService.cs — adapter IFlagsService → PilotX.
// Envuelve FormGPS y corre la lógica del partial FormGPS.Flags en el hilo UI
// (Invoke). Mapea el snapshot plano al DTO de banderas.html. Nunca tira:
// en error devuelve un DTO con Ok=false.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsFlagsService : IFlagsService
    {
        private readonly FormGPS _form;

        public FormGpsFlagsService(FormGPS form) { _form = form; }

        private T OnUi<T>(Func<T> body, T fallback)
        {
            if (_form == null || _form.IsDisposed) return fallback;
            try
            {
                if (_form.InvokeRequired)
                    return (T)_form.Invoke(new Func<T>(body));
                return body();
            }
            catch { return fallback; }
        }

        private static FlagsStateDto Map(FormGPS.FlagsSnapshot s)
        {
            if (s == null) return new FlagsStateDto { Ok = false, Error = "no-state" };
            var dto = new FlagsStateDto
            {
                Ok = true,
                HasField = s.HasField,
                Picked = s.Picked,
                CurLat = s.CurLat,
                CurLon = s.CurLon,
                Error = s.Error,
                Flags = new List<FlagItemDto>()
            };
            foreach (var it in s.Items)
            {
                dto.Flags.Add(new FlagItemDto
                {
                    Number = it.Number,
                    Id = it.Id,
                    Color = it.Color,
                    Notes = it.Notes,
                    Lat = it.Lat,
                    Lon = it.Lon,
                    DistanceM = it.DistanceM
                });
            }
            return dto;
        }

        private static FlagsStateDto Fail() =>
            new FlagsStateDto { Ok = false, Error = "ui-error" };

        public FlagsStateDto GetState() => OnUi(() => Map(_form.Flags_Snapshot()), Fail());
        public FlagsStateDto Pick(int number) => OnUi(() => Map(_form.Flags_Pick(number)), Fail());
        public FlagsStateDto Delete() => OnUi(() => Map(_form.Flags_Delete()), Fail());
        public FlagsStateDto SetNotes(string notes) => OnUi(() => Map(_form.Flags_SetNotes(notes)), Fail());
        public FlagsStateDto Add(double lat, double lon, int color, bool useCurrent) =>
            OnUi(() => Map(_form.Flags_Add(lat, lon, color, useCurrent)), Fail());
        public FlagsStateDto CloseSession() => OnUi(() => Map(_form.Flags_Close()), Fail());
        public FlagsStateDto Import() => OnUi(() => Map(_form.Flags_Import()), Fail());
        public FlagsStateDto Export() => OnUi(() => Map(_form.Flags_Export()), Fail());
    }
}
