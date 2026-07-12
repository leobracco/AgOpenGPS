// ============================================================================
// FormGpsImuCalibracionService.cs
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
// Persiste con Properties.Settings.Default.Save(), igual que el form nativo.
// ============================================================================

using System;
using System.Windows.Forms;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;
    using AgOpenGPS.Properties;

    public sealed class FormGpsImuCalibracionService : IImuCalibracionService
    {
        private readonly FormGPS _form;

        public FormGpsImuCalibracionService(FormGPS form) { _form = form; }

        public ImuCalibracionSnapshot GetSnapshot()
        {
            var snap = new ImuCalibracionSnapshot();
            try
            {
                var ahrs = _form?.ahrs;
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
                case "remove_zero_offset": act = () => { _form.ahrs.rollZero = 0; SaveRollZero(); }; break;
                case "roll_offset_up": act = () => { _form.ahrs.rollZero += 0.1; SaveRollZero(); }; break;
                case "roll_offset_down": act = () => { _form.ahrs.rollZero -= 0.1; SaveRollZero(); }; break;
                case "toggle_invert": act = ToggleInvert; break;
                case "reset_imu": act = () => { _form.ahrs.imuHeading = 99999; _form.ahrs.imuRoll = 88888; }; break;
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
                _form.ahrs.rollFilter = v * 0.01;
                Settings.Default.setIMU_rollFilter = v * 0.01;
                Settings.Default.Save();
            });
            return true;
        }

        private void ZeroRoll()
        {
            if (_form?.ahrs == null || _form.ahrs.imuRoll == 88888) return;
            _form.ahrs.rollZero = _form.ahrs.imuRoll + _form.ahrs.rollZero;
            SaveRollZero();
        }

        private void ToggleInvert()
        {
            if (_form?.ahrs == null) return;
            _form.ahrs.isRollInvert = !_form.ahrs.isRollInvert;
            Settings.Default.setIMU_invertRoll = _form.ahrs.isRollInvert;
            Settings.Default.Save();
        }

        private void SaveRollZero()
        {
            Settings.Default.setIMU_rollZero = _form.ahrs.rollZero;
            Settings.Default.Save();
        }

        private void InvokeOnUi(Action act)
        {
            if (act == null || _form == null) return;
            try
            {
                if (_form.IsHandleCreated && _form.InvokeRequired)
                    _form.BeginInvoke((MethodInvoker)(() => act()));
                else
                    act();
            }
            catch { /* defensivo */ }
        }
    }
}
