// ============================================================================
// EngineImuCalibracionService.cs
// Adaptador IImuCalibracionService → PilotX. Envuelve FormGPS.ahrs (CAHRS) y
// replica la lógica de los handlers nativos de la solapa "Roll" del asistente
// de dirección (ConfigData.Designer.cs / FormSteerWiz.cs), pero invocable por
// HTTP sin depender de que esa ventana esté abierta — ahrs es estado vivo de
// FormGPS, no de FormConfig.
//   zero_roll          → btnZeroRoll_Click:      rollZero = imuRoll + rollZero
//   remove_zero_offset → btnRemoveZeroOffset_Click: rollZero = 0
//   roll_offset_up/dn  → btnRollOffsetUp/Down_Click: rollZero ± 0.1°
//   toggle_invert      → cboxDataInvertRoll:      isRollInvert = !isRollInvert
//   reset_imu          → btnResetIMU_Click:        sentinels (99999/88888)
// Persiste con Settings.Default.Save(), igual que el form nativo.
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;
    using AgOpenGPS.Properties;

    public sealed class EngineImuCalibracionService : IImuCalibracionService
    {
        private readonly GuidanceEngineHost _engine;

        public EngineImuCalibracionService(GuidanceEngineHost engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public ImuCalibracionSnapshot GetSnapshot()
        {
            var snap = new ImuCalibracionSnapshot();
            try
            {
                var ahrs = _engine?.Ahrs;
                if (ahrs != null)
                {
                    snap.Present = ahrs.imuRoll != 88888 && ahrs.imuHeading != 99999;
                    snap.ImuHeading = ahrs.imuHeading;
                    snap.ImuRoll = ahrs.imuRoll;
                    snap.RollZero = ahrs.rollZero;
                    snap.RollFilter = ahrs.rollFilter;
                    snap.IsRollInvert = ahrs.isRollInvert;
                }
            }
            catch { /* defensivo: jamás romper el snapshot por estado parcial */ }
            return snap;
        }

        public bool ExecuteCommand(string command)
        {
            Action act;
            switch ((command ?? "").Trim().ToLowerInvariant())
            {
                case "zero_roll": act = ZeroRoll; break;
                case "remove_zero_offset": act = () => { _engine.Ahrs.rollZero = 0; SaveRollZero(); }; break;
                case "roll_offset_up": act = () => { _engine.Ahrs.rollZero += 0.1; SaveRollZero(); }; break;
                case "roll_offset_down": act = () => { _engine.Ahrs.rollZero -= 0.1; SaveRollZero(); }; break;
                case "toggle_invert": act = ToggleInvert; break;
                case "reset_imu": act = () => { _engine.Ahrs.imuHeading = 99999; _engine.Ahrs.imuRoll = 88888; }; break;
                default: return false;
            }
            InvokeOnUi(act);
            return true;
        }

        public bool SetRollFilter(double percent0To100)
        {
            double v = percent0To100 < 0 ? 0 : (percent0To100 > 100 ? 100 : percent0To100);
            InvokeOnUi(() =>
            {
                _engine.Ahrs.rollFilter = v * 0.01;
                global::AgOpenGPS.Properties.Settings.Default.setIMU_rollFilter = v * 0.01;
                global::AgOpenGPS.Properties.Settings.Default.Save();
            });
            return true;
        }

        private void ZeroRoll()
        {
            if (_engine?.Ahrs == null || _engine.Ahrs.imuRoll == 88888) return;
            _engine.Ahrs.rollZero = _engine.Ahrs.imuRoll + _engine.Ahrs.rollZero;
            SaveRollZero();
        }

        private void ToggleInvert()
        {
            if (_engine?.Ahrs == null) return;
            _engine.Ahrs.isRollInvert = !_engine.Ahrs.isRollInvert;
            global::AgOpenGPS.Properties.Settings.Default.setIMU_invertRoll = _engine.Ahrs.isRollInvert;
            global::AgOpenGPS.Properties.Settings.Default.Save();
        }

        private void SaveRollZero()
        {
            global::AgOpenGPS.Properties.Settings.Default.setIMU_rollZero = _engine.Ahrs.rollZero;
            global::AgOpenGPS.Properties.Settings.Default.Save();
        }

        private void InvokeOnUi(Action act)
        {
            if (act == null || _engine == null) return;
            // Headless: no hay hilo de UI que marshalar, se ejecuta directo.
            // (en FormGPS acá iba un BeginInvoke si InvokeRequired)
            try { act(); }
            catch { /* defensivo */ }
        }
    }
}
