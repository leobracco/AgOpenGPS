// ============================================================================
// FormGpsContornoService.cs — adapter IContornoService → PilotX.
// Envuelve FormGPS y corre la lógica del partial FormGPS.Contorno en el hilo
// UI (Invoke). Mapea los snapshots planos a los DTOs de contorno.html.
// Nunca tira: en error devuelve un DTO con Ok=false.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsContornoService : IContornoService
    {
        private readonly FormGPS _form;

        public FormGpsContornoService(FormGPS form) { _form = form; }

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

        private static ContornoStateDto Map(FormGPS.ContornoSnapshot s)
        {
            if (s == null) return new ContornoStateDto { Ok = false, Error = "no-state" };
            var dto = new ContornoStateDto
            {
                Ok = true,
                JobStarted = s.JobStarted,
                ToolWidth = s.ToolWidth,
                Recording = s.Recording,
                Error = s.Error
            };
            foreach (var it in s.Items)
            {
                dto.Boundaries.Add(new ContornoInfo
                {
                    Index = it.Index,
                    IsOuter = it.IsOuter,
                    AreaHa = it.AreaHa,
                    IsDriveThru = it.IsDriveThru,
                    Points = it.Points
                });
            }
            return dto;
        }

        private static ContornoRecordDto Map(FormGPS.ContornoRecSnapshot s)
        {
            if (s == null) return new ContornoRecordDto { Ok = false, Error = "no-state" };
            return new ContornoRecordDto
            {
                Ok = true,
                Active = s.Active,
                Paused = s.Paused,
                Points = s.Points,
                AreaHa = s.AreaHa,
                OffsetCm = s.OffsetCm,
                RightSide = s.RightSide,
                AtPivot = s.AtPivot,
                SectionRec = s.SectionRec,
                Error = s.Error
            };
        }

        private static ContornoStateDto FailState() =>
            new ContornoStateDto { Ok = false, Error = "ui-error" };

        private static ContornoRecordDto FailRec() =>
            new ContornoRecordDto { Ok = false, Error = "ui-error" };

        public ContornoStateDto GetState() => OnUi(() => Map(_form.Contorno_Snapshot()), FailState());
        public ContornoStateDto SetDriveThru(int index, bool value) => OnUi(() => Map(_form.Contorno_SetDriveThru(index, value)), FailState());
        public ContornoStateDto Delete(int index) => OnUi(() => Map(_form.Contorno_Delete(index)), FailState());
        public ContornoStateDto DeleteAll() => OnUi(() => Map(_form.Contorno_DeleteAll()), FailState());
        public ContornoStateDto ImportKml(bool multi) => OnUi(() => Map(_form.Contorno_ImportKml(multi)), FailState());
        public ContornoStateDto ImportKmlUpload(string kmlContenido, bool multi) =>
            OnUi(() => Map(_form.Contorno_ImportKmlTexto(kmlContenido, multi)), FailState());
        public ContornoStateDto OpenGoogleEarth() => OnUi(() => Map(_form.Contorno_OpenGoogleEarth()), FailState());
        public ContornoStateDto OpenMapa() => OnUi(() => Map(_form.Contorno_OpenMapa()), FailState());
        public ContornoStateDto BuildFromTracks() => OnUi(() => Map(_form.Contorno_BuildFromTracks()), FailState());

        public ContornoRecordDto RecordStart() => OnUi(() => Map(_form.ContornoRec_Start()), FailRec());
        public ContornoRecordDto RecordStatus() => OnUi(() => Map(_form.ContornoRec_Snapshot()), FailRec());
        public ContornoRecordDto RecordSet(double? offsetCm, bool? rightSide, bool? atPivot, bool? sectionRec) =>
            OnUi(() => Map(_form.ContornoRec_Set(offsetCm, rightSide, atPivot, sectionRec)), FailRec());
        public ContornoRecordDto RecordPause() => OnUi(() => Map(_form.ContornoRec_Pause()), FailRec());
        public ContornoRecordDto RecordAddPoint() => OnUi(() => Map(_form.ContornoRec_AddPoint()), FailRec());
        public ContornoRecordDto RecordUndo() => OnUi(() => Map(_form.ContornoRec_Undo()), FailRec());
        public ContornoRecordDto RecordRestart() => OnUi(() => Map(_form.ContornoRec_Restart()), FailRec());
        public ContornoRecordDto RecordSave() => OnUi(() => Map(_form.ContornoRec_Save()), FailRec());
        public ContornoRecordDto RecordCancel() => OnUi(() => Map(_form.ContornoRec_Cancel()), FailRec());
    }
}
