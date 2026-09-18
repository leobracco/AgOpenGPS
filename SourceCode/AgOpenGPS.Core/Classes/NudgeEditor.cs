// ============================================================================
// NudgeEditor.cs — "Mover guía" (reemplazo de FormNudge y FormRefNudge).
//
// Correr la guía activa de a pasos (esquivar, re-centrar sobre el pivote) o
// mover la línea de REFERENCIA, que corre el patrón entero del lote.
//
// Era `partial class FormGPS`: solo existía bajo WinForms y /api/nudge daba
// 404 — la pantalla "Mover guía" no hacía nada contra el motor. Quinto de
// docs/RETIRAR-WINFORMS.md, y el que destraba más íconos (13, de los que hoy
// solo andan 3 por comandos sueltos).
//
// Cero dependencias de WinForms; movido tal cual, host → campos y callbacks.
// FormGPS delega. No es thread-safe: cada host lo llama desde su propio hilo.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgOpenGPS
{
    public sealed class NudgeEditor
    {
        private readonly CTrack _trk;
        private readonly CABLine _abLine;
        private readonly CABCurve _curve;
        private readonly CTool _tool;

        private readonly Func<bool> _hayLoteFn;

        /// <summary>Persistir TrackLines.txt.</summary>
        private readonly Action _guardarGuias;

        // Los pasos viven en la config del host EN CENTIMETROS (igual que
        // FormNudge): get/set porque la pantalla los puede cambiar.
        private readonly Func<double> _snapGet;
        private readonly Action<double> _snapSet;
        private readonly Func<double> _snapRefGet;
        private readonly Action<double> _snapRefSet;

        public NudgeEditor(
            CTrack trk,
            CABLine abLine,
            CABCurve curve,
            CTool tool,
            Func<bool> hayLote,
            Action guardarGuias,
            Func<double> snapGet = null,
            Action<double> snapSet = null,
            Func<double> snapRefGet = null,
            Action<double> snapRefSet = null,
            Func<bool> esMetrico = null,
            Func<double> cm2Display = null,
            Func<double> m2CmDisplay = null,
            Func<double> display2m = null,
            Func<string> unidadesChicas = null)
        {
            _trk = trk;
            _abLine = abLine;
            _curve = curve;
            _tool = tool;
            _hayLoteFn = hayLote ?? (() => false);
            _guardarGuias = guardarGuias ?? (() => { });
            _snapGet = snapGet ?? (() => 20.0);
            _snapSet = snapSet ?? (_ => { });
            _snapRefGet = snapRefGet ?? (() => 5.0);
            _snapRefSet = snapRefSet ?? (_ => { });
            _esMetricoFn = esMetrico ?? (() => true);
            _cm2DisplayFn = cm2Display ?? (() => 1.0);
            _m2CmDisplayFn = m2CmDisplay ?? (() => 100.0);
            _display2mFn = display2m ?? (() => 0.01);
            _unidadesChicasFn = unidadesChicas ?? (() => "cm");
        }

        private bool _hayLote => _hayLoteFn();

        // Factores de unidades del host. Defaults metricos: el motor trabaja
        // en metros y la conversion a pulgadas es de la pantalla.
        private readonly Func<bool> _esMetricoFn;
        private readonly Func<double> _cm2DisplayFn;
        private readonly Func<double> _m2CmDisplayFn;
        private readonly Func<double> _display2mFn;
        private readonly Func<string> _unidadesChicasFn;
        private bool _esMetrico => _esMetricoFn();
        private double _cm2Display => _cm2DisplayFn();
        private double _m2CmDisplay => _m2CmDisplayFn();
        private double _display2m => _display2mFn();
        private string _unidadesChicas => _unidadesChicasFn();
        private double _snapCm { get => _snapGet(); set => _snapSet(value); }
        private double _snapRefCm { get => _snapRefGet(); set => _snapRefSet(value); }

        // Sesión de nudge de referencia: backup para Cancelar (gTemp del form nativo).
        private List<CTrk> nudgeRefBackup;
        private double nudgeRefMoved;

        public bool Nudge_HasTrack()
        {
            return _trk != null && _trk.gArr != null
                   && _trk.idx >= 0 && _trk.idx < _trk.gArr.Count;
        }

        // Paso en metros (los settings guardan cm, igual que FormNudge).
        private double Nudge_StepMeters() =>
            _snapCm * 0.01;

        private double NudgeRef_StepMeters() =>
            _snapRefCm * 0.01;

        // Setting (cm) → unidades display: cm entero o pulgadas con 1 decimal.
        private double Nudge_StepDisplay(double settingCm)
        {
            if (_esMetrico) return (int)settingCm;
            return Math.Round(settingCm * _cm2Display, 1);
        }

        public NudgeStateSnapshot Nudge_Snapshot()
        {
            double offset = Nudge_HasTrack() ? _trk.gArr[_trk.idx].nudgeDistance : 0;
            return new NudgeStateSnapshot
            {
                HasTrack = Nudge_HasTrack(),
                OffsetDisplay = (int)(offset * _m2CmDisplay),
                StepDisplay = Nudge_StepDisplay(_snapCm),
                StepRefDisplay = Nudge_StepDisplay(_snapRefCm),
                RefActive = nudgeRefBackup != null,
                RefMovedDisplay = (int)(nudgeRefMoved * _m2CmDisplay),
                Units = (_unidadesChicas ?? "cm").Trim()
            };
        }

        // --- Guía activa (réplica de FormNudge) ---

        public NudgeStateSnapshot Nudge_Move(int dir)
        {
            if (Nudge_HasTrack()) _trk.NudgeTrack(Math.Sign(dir) * Nudge_StepMeters());
            return Nudge_Snapshot();
        }

        public NudgeStateSnapshot Nudge_Half(int dir)
        {
            if (Nudge_HasTrack())
                _trk.NudgeTrack(Math.Sign(dir) * (_tool.width - _tool.overlap) * 0.5);
            return Nudge_Snapshot();
        }

        public NudgeStateSnapshot Nudge_Zero()
        {
            if (Nudge_HasTrack()) _trk.NudgeDistanceReset();
            return Nudge_Snapshot();
        }

        public NudgeStateSnapshot Nudge_ToPivot()
        {
            if (Nudge_HasTrack()) _trk.SnapToPivot();
            return Nudge_Snapshot();
        }

        // valueDisplay en cm (métrico) o in (imperial), igual que nudSnapDistance.
        public NudgeStateSnapshot Nudge_SetStep(double valueDisplay)
        {
            if (valueDisplay < 0) valueDisplay = 0;
            double meters = valueDisplay * _display2m;
            _snapCm = meters * 100;

            return Nudge_Snapshot();
        }

        // FormClosing de FormNudge: persistir las guías.
        public NudgeStateSnapshot Nudge_Close()
        {
            _guardarGuias();
            return Nudge_Snapshot();
        }

        // --- Guía de referencia (réplica de FormRefNudge) ---

        public NudgeStateSnapshot NudgeRef_Open()
        {
            if (!Nudge_HasTrack()) return Nudge_Snapshot();
            if (nudgeRefBackup == null)
            {
                nudgeRefBackup = new List<CTrk>();
                foreach (var item in _trk.gArr) nudgeRefBackup.Add(new CTrk(item));
                nudgeRefMoved = 0;
            }
            return Nudge_Snapshot();
        }

        public NudgeStateSnapshot NudgeRef_Move(int dir)
        {
            if (Nudge_HasTrack())
            {
                double d = Math.Sign(dir) * NudgeRef_StepMeters();
                _trk.NudgeRefTrack(d);
                nudgeRefMoved += d;
            }
            return Nudge_Snapshot();
        }

        public NudgeStateSnapshot NudgeRef_Half(int dir)
        {
            if (Nudge_HasTrack())
            {
                double d = Math.Sign(dir) * (_tool.width - _tool.overlap) * 0.5;
                _trk.NudgeRefTrack(d);
                nudgeRefMoved += d;
            }
            return Nudge_Snapshot();
        }

        public NudgeStateSnapshot NudgeRef_SetStep(double valueDisplay)
        {
            if (valueDisplay < 0) valueDisplay = 0;
            double meters = valueDisplay * _display2m;
            _snapRefCm = meters * 100;

            return Nudge_Snapshot();
        }

        public NudgeStateSnapshot NudgeRef_Save()
        {
            _guardarGuias();
            nudgeRefBackup = null;
            nudgeRefMoved = 0;
            return Nudge_Snapshot();
        }

        // btnCancelMain nativo: restaurar el backup e invalidar las líneas armadas.
        public NudgeStateSnapshot NudgeRef_Cancel()
        {
            if (nudgeRefBackup != null)
            {
                _trk.gArr.Clear();
                foreach (var item in nudgeRefBackup) _trk.gArr.Add(new CTrk(item));

                _abLine.isABValid = false;
                _curve.isCurveValid = false;

                nudgeRefBackup = null;
                nudgeRefMoved = 0;
            }
            return Nudge_Snapshot();
        }

        // POCO intermedio (assembly GPS) — el adapter lo copia al DTO de Models.
        public sealed class NudgeStateSnapshot
        {
            public bool HasTrack;
            public int OffsetDisplay;
            public double StepDisplay;
            public double StepRefDisplay;
            public bool RefActive;
            public int RefMovedDisplay;
            public string Units;
        }
    }
}
