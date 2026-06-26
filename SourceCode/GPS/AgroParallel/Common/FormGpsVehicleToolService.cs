// ============================================================================
// FormGpsVehicleToolService.cs
// Ubicación: SourceCode/GPS/AgroParallel/Common/FormGpsVehicleToolService.cs
// Target: net48
//
// Implementación PilotX-side de IVehicleToolService. Lee/escribe la config de
// Vehículo (CVehicle) y Herramienta (CTool) usando Properties.Settings.Default
// (mismo store que las pantallas legacy ConfigVehicleControl/ConfigToolControl).
//
// Al guardar:
//   1) Persiste setVehicle_*/setTool_* y llama Settings.Default.Save() (igual
//      que la pantalla original).
//   2) Marshal-Invoke al hilo de UI para reconstruir mf.vehicle = new CVehicle(mf)
//      o mf.tool = new CTool(mf), de modo que los cambios sean efectivos sin
//      reiniciar PilotX.
//   3) Tras reload de tool, dispara FixSectionLooks() para refrescar el strip
//      visual de secciones (mismo patrón que Controls.Designer).
//
// Phase D · primer reemplazo HTML del ConfigVehicle/ConfigTool clásico.
// ============================================================================

using System;
using System.Windows.Forms;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;
    using AgOpenGPS.Properties;

    public sealed class FormGpsVehicleToolService : IVehicleToolService
    {
        private readonly FormGPS _form;

        public FormGpsVehicleToolService(FormGPS form)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
        }

        public VehicleConfigDto GetVehicle()
        {
            var s = Settings.Default;
            return new VehicleConfigDto
            {
                VehicleType     = s.setVehicle_vehicleType,
                Wheelbase       = s.setVehicle_wheelbase,
                TrackWidth      = s.setVehicle_trackWidth,
                AntennaHeight   = s.setVehicle_antennaHeight,
                AntennaPivot    = s.setVehicle_antennaPivot,
                AntennaOffset   = s.setVehicle_antennaOffset,
                MaxSteerAngle   = s.setVehicle_maxSteerAngle,
                SlowSpeedCutoff = s.setVehicle_slowSpeedCutoff
            };
        }

        public ToolConfigDto GetTool()
        {
            var s = Settings.Default;
            // numOfSections en PilotX depende de setTool_isSectionsNotZones:
            // si secciones, viene de setVehicle_numSections; si zonas, de
            // setTool_numSectionsMulti. Para la UI HTML mostramos el efectivo.
            int numSec = s.setTool_isSectionsNotZones
                ? s.setVehicle_numSections
                : s.setTool_numSectionsMulti;

            return new ToolConfigDto
            {
                Width                     = s.setVehicle_toolWidth,
                Overlap                   = s.setVehicle_toolOverlap,
                Offset                    = s.setVehicle_toolOffset,
                NumSections               = numSec,
                HitchLength               = s.setVehicle_hitchLength,
                TrailingHitchLength       = s.setTool_toolTrailingHitchLength,
                TrailingToolToPivotLength = s.setTool_trailingToolToPivotLength,
                LookAheadOn               = s.setVehicle_toolLookAheadOn,
                LookAheadOff              = s.setVehicle_toolLookAheadOff,
                TurnOffDelay              = s.setVehicle_toolOffDelay,
                IsToolTrailing            = s.setTool_isToolTrailing,
                IsToolTBT                 = s.setTool_isToolTBT,
                IsToolRearFixed           = s.setTool_isToolRearFixed,
                IsToolFrontFixed          = s.setTool_isToolFront,
                IsSectionOffWhenOut       = s.setTool_isSectionOffWhenOut
            };
        }

        public VehicleToolBundleDto GetBundle()
        {
            return new VehicleToolBundleDto
            {
                Vehicle = GetVehicle(),
                Tool = GetTool()
            };
        }

        public bool SaveVehicle(VehicleConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = Settings.Default;
                s.setVehicle_vehicleType    = ClampInt(cfg.VehicleType, 0, 2);
                s.setVehicle_wheelbase      = ClampD(cfg.Wheelbase, 0.5, 20.0);
                s.setVehicle_trackWidth     = ClampD(cfg.TrackWidth, 0.5, 10.0);
                s.setVehicle_antennaHeight  = ClampD(cfg.AntennaHeight, 0.1, 10.0);
                s.setVehicle_antennaPivot   = ClampD(cfg.AntennaPivot, -10.0, 10.0);
                s.setVehicle_antennaOffset  = ClampD(cfg.AntennaOffset, -5.0, 5.0);
                s.setVehicle_maxSteerAngle  = ClampD(cfg.MaxSteerAngle, 5.0, 60.0);
                s.setVehicle_slowSpeedCutoff = ClampD(cfg.SlowSpeedCutoff, 0.0, 10.0);
                s.Save();

                // Reload CVehicle en hilo UI — el ctor recompone gains, lookahead, etc.
                InvokeOnUi(() =>
                {
                    try { _form.vehicle = new CVehicle(_form); } catch { }
                });
                return true;
            }
            catch { return false; }
        }

        public bool SaveTool(ToolConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = Settings.Default;
                s.setVehicle_toolWidth          = ClampD(cfg.Width, 0.1, 100.0);
                s.setVehicle_toolOverlap        = ClampD(cfg.Overlap, -2.0, 2.0);
                s.setVehicle_toolOffset         = ClampD(cfg.Offset, -10.0, 10.0);

                int n = ClampInt(cfg.NumSections, 1, 16);
                if (s.setTool_isSectionsNotZones) s.setVehicle_numSections = n;
                else s.setTool_numSectionsMulti = n;

                s.setVehicle_hitchLength            = ClampD(cfg.HitchLength, -10.0, 10.0);
                s.setTool_toolTrailingHitchLength   = ClampD(cfg.TrailingHitchLength, -20.0, 5.0);
                s.setTool_trailingToolToPivotLength = ClampD(cfg.TrailingToolToPivotLength, -20.0, 20.0);
                s.setVehicle_toolLookAheadOn        = ClampD(cfg.LookAheadOn, 0.0, 20.0);
                s.setVehicle_toolLookAheadOff      = ClampD(cfg.LookAheadOff, 0.0, 20.0);
                s.setVehicle_toolOffDelay          = ClampD(cfg.TurnOffDelay, 0.0, 10.0);

                // Mutex de "tipo de tool" — PilotX asume que solo uno está true a la vez.
                bool trailing = cfg.IsToolTrailing && !cfg.IsToolRearFixed && !cfg.IsToolFrontFixed;
                bool rear     = !trailing && cfg.IsToolRearFixed && !cfg.IsToolFrontFixed;
                bool front    = !trailing && !rear && cfg.IsToolFrontFixed;
                if (!trailing && !rear && !front) rear = true; // fallback razonable

                s.setTool_isToolTrailing    = trailing;
                s.setTool_isToolRearFixed   = rear;
                s.setTool_isToolFront       = front;
                s.setTool_isToolTBT         = cfg.IsToolTBT && trailing; // TBT solo con trailing
                s.setTool_isSectionOffWhenOut = cfg.IsSectionOffWhenOut;
                s.Save();

                // Reload CTool en hilo UI; mismo pattern que ConfigTool.Designer.cs:720+
                // que recompone sections (positions + widths) tras un cambio de tool.
                InvokeOnUi(() =>
                {
                    try
                    {
                        _form.tool = new CTool(_form);
                        try { _form.LineUpIndividualSectionBtns(); } catch { }
                        try { _form.SectionSetPosition(); } catch { }
                        try { _form.SectionCalcWidths(); } catch { }
                    }
                    catch { }
                });
                return true;
            }
            catch { return false; }
        }

        // --- helpers ------------------------------------------------------------

        private void InvokeOnUi(Action act)
        {
            if (act == null) return;
            try
            {
                if (_form.IsHandleCreated && _form.InvokeRequired)
                    _form.BeginInvoke((MethodInvoker)(() => act()));
                else
                    act();
            }
            catch { /* defensivo */ }
        }

        private static double ClampD(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
        private static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
