// ============================================================================
// FormGpsHeadlandEditService.cs — adapter IHeadlandEditService → PilotX.
// Envuelve FormGPS y corre la geometría del partial FormGPS.HeadlandEdit en el
// hilo UI (Invoke). Devuelve DTOs E/N para cabecera.html. Nunca tira: en error
// devuelve DTO con Ok=false / HasField=false.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsHeadlandEditService : IHeadlandEditService
    {
        private readonly FormGPS _form;

        public FormGpsHeadlandEditService(FormGPS form) { _form = form; }

        // Marshalea func al hilo UI. Si el form no está listo, corre el fallback.
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

        // Snapshot completo (corre en el hilo UI).
        private HeadlandEditStateDto BuildState(string error)
        {
            return new HeadlandEditStateDto
            {
                HasField = _form.HeadlandEdit_HasBoundary(),
                HasBoundary = _form.HeadlandEdit_HasBoundary(),
                IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                IsSectionControlled = _form.HeadlandEdit_IsSectionControlled(),
                Units = _form.HeadlandEdit_Units(),
                ToolWidthM = _form.HeadlandEdit_ToolWidthM(),
                Fence = _form.HeadlandEdit_FenceEN(),
                Headland = _form.HeadlandEdit_HeadlandEN(),
                Fences = _form.HeadlandEdit_FencesEN(),
                BndSelect = _form.HeadlandEdit_BndSelect(),
                Slice = _form.HeadlandEdit_SliceEN(),
                SliceMode = _form.HeadlandEdit_SliceMode(),
                APoint = _form.HeadlandEdit_APointEN(),
                BPoint = _form.HeadlandEdit_BPointEN(),
                CanUndo = _form.HeadlandEdit_CanUndo(),
                Error = error
            };
        }

        public HeadlandEditStateDto GetState()
        {
            return OnUi(() => BuildState(null), new HeadlandEditStateDto());
        }

        public HeadlandEditStateDto Open()
        {
            return OnUi(() => BuildState(_form.HeadlandEdit_Open()),
                new HeadlandEditStateDto { Error = "ui-error" });
        }

        public HeadlandEditStateDto Tap(double easting, double northing, string mode, double distanceDisplay)
        {
            return OnUi(() => BuildState(_form.HeadlandEdit_Tap(easting, northing, mode, distanceDisplay)),
                new HeadlandEditStateDto { Error = "ui-error" });
        }

        public HeadlandEditStateDto CancelTouch()
        {
            return OnUi(() => { _form.HeadlandEdit_CancelTouch(); return BuildState(null); },
                new HeadlandEditStateDto { Error = "ui-error" });
        }

        public HeadlandEditStateDto Extend(string end, bool grow)
        {
            return OnUi(() => { _form.HeadlandEdit_Extend(end == "a" ? "a" : "b", grow); return BuildState(null); },
                new HeadlandEditStateDto { Error = "ui-error" });
        }

        public HeadlandEditStateDto Clip()
        {
            return OnUi(() => BuildState(_form.HeadlandEdit_Clip()),
                new HeadlandEditStateDto { Error = "ui-error" });
        }

        public HeadlandEditStateDto Undo()
        {
            return OnUi(() => { _form.HeadlandEdit_Undo(); return BuildState(null); },
                new HeadlandEditStateDto { Error = "ui-error" });
        }

        public void CloseSession()
        {
            OnUi<object>(() => { _form.HeadlandEdit_CloseSession(); return null; }, null);
        }

        public HeadlandEditResultDto BuildAround(double distanceDisplay)
        {
            return OnUi(() =>
            {
                bool ok = _form.HeadlandEdit_BuildAround(distanceDisplay);
                return new HeadlandEditResultDto
                {
                    Ok = ok,
                    IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                    Headland = _form.HeadlandEdit_HeadlandEN(),
                    Error = ok ? null : "offset-collapsed"
                };
            }, new HeadlandEditResultDto { Ok = false, Error = "ui-error" });
        }

        public HeadlandEditResultDto Reset()
        {
            return OnUi(() =>
            {
                _form.HeadlandEdit_Reset();
                return new HeadlandEditResultDto
                {
                    Ok = true,
                    IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                    Headland = _form.HeadlandEdit_HeadlandEN()
                };
            }, new HeadlandEditResultDto { Ok = false, Error = "ui-error" });
        }

        public HeadlandEditResultDto TurnOff()
        {
            return OnUi(() =>
            {
                _form.HeadlandEdit_TurnOff();
                return new HeadlandEditResultDto
                {
                    Ok = true,
                    IsHeadlandOn = _form.HeadlandEdit_IsHeadlandOn(),
                    Headland = _form.HeadlandEdit_HeadlandEN()
                };
            }, new HeadlandEditResultDto { Ok = false, Error = "ui-error" });
        }

        public bool SetSectionControlled(bool on)
        {
            return OnUi(() =>
            {
                _form.HeadlandEdit_SetSectionControlled(on);
                return _form.HeadlandEdit_IsSectionControlled();
            }, false);
        }
    }
}
