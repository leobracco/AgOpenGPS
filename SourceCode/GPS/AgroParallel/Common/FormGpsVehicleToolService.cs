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
            bool sectionsMode = s.setTool_isSectionsNotZones;
            int numSec = sectionsMode
                ? s.setVehicle_numSections
                : s.setTool_numSectionsMulti;

            // Modo secciones: anchos individuales desde las posiciones
            // (widths[i] = |pos[i+1] - pos[i]|, mismo cálculo que ConfigTool).
            double[] widths = null;
            if (sectionsMode)
            {
                decimal[] pos = ReadSectionPositions(s);
                int n = Math.Max(1, Math.Min(16, numSec));
                widths = new double[n];
                for (int i = 0; i < n; i++)
                    widths[i] = Math.Abs((double)(pos[i + 1] - pos[i]));
            }

            // Modo zonas: setTool_zones = "cantZonas,hasta1,...,hasta8".
            int zones = 0; int[] zoneRanges = null;
            try
            {
                string[] words = (s.setTool_zones ?? "").Split(',');
                if (words.Length > 0) int.TryParse(words[0], out zones);
                if (zones > 0)
                {
                    zoneRanges = new int[zones];
                    for (int i = 1; i <= zones && i < words.Length; i++)
                        int.TryParse(words[i], out zoneRanges[i - 1]);
                }
            }
            catch { /* string corrupta: la UI cae a defaults */ }

            return new ToolConfigDto
            {
                Width                     = s.setVehicle_toolWidth,
                Overlap                   = s.setVehicle_toolOverlap,
                Offset                    = s.setVehicle_toolOffset,
                NumSections               = numSec,
                IsSectionsNotZones        = sectionsMode,
                SectionWidths             = widths,
                SectionWidthMulti         = s.setTool_sectionWidthMulti,
                Zones                     = zones,
                ZoneRanges                = zoneRanges,
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
                // OJO: Opacity/Color/IsImage NO viven en el ctor — los asigna
                // LoadSettings DESPUÉS de construir. Sin re-aplicarlos acá el
                // vehículo quedaba dibujado como triángulo transparente
                // ("no muestra ningún tractor") hasta reiniciar PilotX.
                InvokeOnUi(() =>
                {
                    try
                    {
                        _form.vehicle = new CVehicle(_form);
                        var s2 = Settings.Default;
                        _form.vehicle.VehicleConfig.Opacity = s2.setDisplay_vehicleOpacity * 0.01;
                        _form.vehicle.VehicleConfig.Color =
                            (AgOpenGPS.Core.Models.ColorRgba)s2.setDisplay_colorVehicle.CheckColorFor255();
                        _form.vehicle.VehicleConfig.IsImage = s2.setDisplay_isVehicleImage;
                    }
                    catch { }
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
                s.setVehicle_toolOverlap        = ClampD(cfg.Overlap, -2.0, 2.0);
                s.setVehicle_toolOffset         = ClampD(cfg.Offset, -10.0, 10.0);

                // ---- secciones vs zonas (réplica de tabTSections_Leave) ----
                s.setTool_isSectionsNotZones = cfg.IsSectionsNotZones;
                if (cfg.IsSectionsNotZones)
                {
                    // Modo secciones: ≤16, cada una con su ancho. El ancho total
                    // es la suma; las posiciones quedan simétricas respecto de 0
                    // (mismo cálculo que CalculateSectionPositions).
                    int n = ClampInt(cfg.NumSections, 1, 16);
                    s.setVehicle_numSections = n;

                    double defW = n > 0 && cfg.Width > 0 ? cfg.Width / n : 0.5;
                    decimal[] w = new decimal[16];
                    for (int i = 0; i < 16; i++)
                    {
                        double wi = (cfg.SectionWidths != null && i < cfg.SectionWidths.Length && cfg.SectionWidths[i] > 0)
                            ? cfg.SectionWidths[i] : defW;
                        w[i] = (decimal)ClampD(wi, 0.01, 10.0);
                    }

                    decimal total = 0;
                    for (int j = 0; j < n; j++) total += w[j];

                    decimal[] pos = new decimal[17];
                    pos[0] = total * -0.5m;
                    for (int j = 1; j < 17; j++)
                        pos[j] = j <= n ? pos[j - 1] + w[j - 1] : 0m;
                    WriteSectionPositions(s, pos);

                    s.setVehicle_toolWidth = (double)total;
                    // WinForms fuerza secciones monocolor en este modo
                    s.setColor_isMultiColorSections = false;
                }
                else
                {
                    // Modo zonas: ≤64 secciones iguales; total = n × anchoSección.
                    int n = ClampInt(cfg.NumSections, 1, 64);
                    s.setTool_numSectionsMulti = n;

                    double sw = cfg.SectionWidthMulti > 0
                        ? cfg.SectionWidthMulti
                        : (cfg.Width > 0 ? cfg.Width / n : 0.5);
                    sw = ClampD(sw, 0.01, 10.0);
                    s.setTool_sectionWidthMulti = sw;
                    s.setVehicle_toolWidth = n * sw;

                    // setTool_zones = "cantZonas,hasta1..hasta8" (9 valores SIEMPRE).
                    int z = ClampInt(cfg.Zones, 1, 8);
                    if (z > n) z = n;                   // no más zonas que secciones
                    int[] zr = new int[9];
                    zr[0] = z;
                    int prev = 0;
                    for (int i = 1; i <= z; i++)
                    {
                        int r = (cfg.ZoneRanges != null && i - 1 < cfg.ZoneRanges.Length)
                            ? cfg.ZoneRanges[i - 1] : n;
                        r = ClampInt(r, prev + 1, n);   // ascendente estricto
                        zr[i] = r;
                        prev = r;
                    }
                    zr[z] = n;                          // la última zona cierra en n
                    s.setTool_zones = string.Join(",", zr);
                }

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
                bool sectionsMode = cfg.IsSectionsNotZones;
                InvokeOnUi(() =>
                {
                    try
                    {
                        _form.tool = new CTool(_form);
                        if (sectionsMode)
                        {
                            try { _form.LineUpIndividualSectionBtns(); } catch { }
                            try { _form.SectionSetPosition(); } catch { }
                            try { _form.SectionCalcWidths(); } catch { }
                        }
                        else
                        {
                            try { _form.SectionCalcMulti(); } catch { }
                            try { _form.LineUpAllZoneButtons(); } catch { }
                        }
                    }
                    catch { }
                });
                return true;
            }
            catch { return false; }
        }

        public string GetVehiculoCustom()
        {
            try { return Settings.Default.setBrand_VehiculoCustom ?? ""; }
            catch { return ""; }
        }

        public bool SetVehiculoCustom(string archivo)
        {
            try
            {
                // solo nombre de archivo plano (nada de rutas / traversal)
                archivo = (archivo ?? "").Trim();
                if (archivo.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;

                Settings.Default.setBrand_VehiculoCustom = archivo;
                Settings.Default.Save();
                InvokeOnUi(() => _form.AplicarVehiculoCustom());
                return true;
            }
            catch { return false; }
        }

        // --- IMU / fuente de rumbo (espejo de tabDHeading + tabDRoll) ------------

        public ImuConfigDto GetImu()
        {
            var s = Settings.Default;
            return new ImuConfigDto
            {
                HeadingSource          = s.setGPS_headingFromWhichSource,
                FusionGpsPercent       = ClampInt((int)Math.Round(s.setIMU_fusionWeight2 * 500.0), 0, 100),
                MinGpsStep10Cm         = s.setF_minHeadingStepDistance == 1.0,
                DualHeadingOffset      = s.setGPS_dualHeadingOffset,
                DualReverseDistance    = s.setGPS_dualReverseDetectionDistance,
                IsRtkAlarm             = s.setGPS_isRTK,
                IsRtkKillAutosteer     = s.setGPS_isRTK_KillAutoSteer,
                FixJumpAlarmDistance   = s.setGPS_jumpFixAlarmDistance,
                IsReverseOn            = s.setIMU_isReverseOn,
                AutoSwitchDualFix      = s.setAutoSwitchDualFixOn,
                AutoSwitchDualFixSpeed = s.setAutoSwitchDualFixSpeed,
                RollFilterPercent      = ClampInt((int)Math.Round(s.setIMU_rollFilter * 100.0), 0, 100),
                InvertRoll             = s.setIMU_invertRoll
            };
        }

        public bool SaveImu(ImuConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = Settings.Default;

                string src = cfg.HeadingSource == "Dual" ? "Dual" : "Fix";
                s.setGPS_headingFromWhichSource = src;

                // Mismo rango que hsbarFusion de la WinForm (5..60 % GPS)
                double fusion = ClampInt(cfg.FusionGpsPercent, 5, 60) * 0.002;
                s.setIMU_fusionWeight2 = fusion;

                // Paso mínimo GPS: mismo par de valores que UpdateStepDistanceUI()
                s.setF_minHeadingStepDistance = cfg.MinGpsStep10Cm ? 1.0 : 0.5;
                s.setGPS_minimumStepLimit     = cfg.MinGpsStep10Cm ? 0.1 : 0.05;

                s.setGPS_dualHeadingOffset            = ClampD(cfg.DualHeadingOffset, -100.0, 100.0);
                s.setGPS_dualReverseDetectionDistance = ClampD(cfg.DualReverseDistance, 0.0, 10.0);
                s.setGPS_isRTK                = cfg.IsRtkAlarm;
                s.setGPS_isRTK_KillAutoSteer  = cfg.IsRtkKillAutosteer;
                s.setGPS_jumpFixAlarmDistance = ClampInt(cfg.FixJumpAlarmDistance, 0, 1000);
                s.setIMU_isReverseOn          = cfg.IsReverseOn;
                s.setAutoSwitchDualFixOn      = cfg.AutoSwitchDualFix;
                s.setAutoSwitchDualFixSpeed   = ClampD(cfg.AutoSwitchDualFixSpeed, 1.0, 10.0);

                // Mismo rango que hsbarRollFilter de la WinForm (0..98)
                double rollFilter = ClampInt(cfg.RollFilterPercent, 0, 98) * 0.01;
                s.setIMU_rollFilter = rollFilter;
                s.setIMU_invertRoll = cfg.InvertRoll;
                s.Save();

                // Aplicar en vivo (mismo efecto que tabDHeading_Leave/tabDRoll_Leave)
                InvokeOnUi(() =>
                {
                    try
                    {
                        _form.headingFromSource = src;
                        _form.ahrs.fusionWeight = fusion;
                        _form.minHeadingStepDist = s.setF_minHeadingStepDistance;
                        _form.gpsMinimumStepDistance = s.setGPS_minimumStepLimit;
                        _form.isFirstHeadingSet = false;
                        _form.pn.headingTrueDualOffset = s.setGPS_dualHeadingOffset;
                        _form.dualReverseDetectionDistance = s.setGPS_dualReverseDetectionDistance;
                        _form.isRTK_AlarmOn = s.setGPS_isRTK;
                        _form.isRTK_KillAutosteer = s.setGPS_isRTK_KillAutoSteer;
                        _form.ahrs.isReverseOn = s.setIMU_isReverseOn;
                        _form.ahrs.autoSwitchDualFixOn = s.setAutoSwitchDualFixOn;
                        _form.ahrs.autoSwitchDualFixSpeed = s.setAutoSwitchDualFixSpeed;
                        _form.ahrs.rollFilter = rollFilter;
                        _form.ahrs.isRollInvert = s.setIMU_invertRoll;
                    }
                    catch { }
                });
                return true;
            }
            catch { return false; }
        }

        public ImuLiveDto GetImuLive()
        {
            var a = _form.ahrs;
            bool hasRoll = a.imuRoll != 88888;
            return new ImuLiveDto
            {
                HasImuHeading = a.imuHeading != 99999,
                HasImuRoll    = hasRoll,
                ImuRoll       = hasRoll ? a.imuRoll : 0.0,
                RollZero      = a.rollZero
            };
        }

        public bool ZeroRoll()
        {
            if (_form.ahrs.imuRoll == 88888) return false;
            InvokeOnUi(() =>
            {
                try
                {
                    // Mismo cálculo que btnZeroRoll_Click: el roll crudo pasa a ser el cero.
                    _form.ahrs.imuRoll += _form.ahrs.rollZero;
                    _form.ahrs.rollZero = _form.ahrs.imuRoll;
                    PersistRollZero();
                }
                catch { }
            });
            return true;
        }

        public bool AdjustRollZero(double delta)
        {
            if (_form.ahrs.imuRoll == 88888) return false;
            if (double.IsNaN(delta) || double.IsInfinity(delta)) return false;
            double d = ClampD(delta, -5.0, 5.0);
            InvokeOnUi(() =>
            {
                try { _form.ahrs.rollZero += d; PersistRollZero(); } catch { }
            });
            return true;
        }

        public bool RemoveRollZero()
        {
            InvokeOnUi(() =>
            {
                try { _form.ahrs.rollZero = 0; PersistRollZero(); } catch { }
            });
            return true;
        }

        public bool ResetImu()
        {
            InvokeOnUi(() =>
            {
                try
                {
                    _form.ahrs.imuHeading = 99999;
                    _form.ahrs.imuRoll = 88888;
                }
                catch { }
            });
            return true;
        }

        private void PersistRollZero()
        {
            // La WinForm persiste en tabDRoll_Leave; acá persistimos de inmediato.
            Settings.Default.setIMU_rollZero = _form.ahrs.rollZero;
            Settings.Default.Save();
        }

        // --- helpers ------------------------------------------------------------

        private static decimal[] ReadSectionPositions(Settings s)
        {
            return new[]
            {
                s.setSection_position1,  s.setSection_position2,  s.setSection_position3,
                s.setSection_position4,  s.setSection_position5,  s.setSection_position6,
                s.setSection_position7,  s.setSection_position8,  s.setSection_position9,
                s.setSection_position10, s.setSection_position11, s.setSection_position12,
                s.setSection_position13, s.setSection_position14, s.setSection_position15,
                s.setSection_position16, s.setSection_position17
            };
        }

        private static void WriteSectionPositions(Settings s, decimal[] p)
        {
            s.setSection_position1 = p[0];   s.setSection_position2 = p[1];
            s.setSection_position3 = p[2];   s.setSection_position4 = p[3];
            s.setSection_position5 = p[4];   s.setSection_position6 = p[5];
            s.setSection_position7 = p[6];   s.setSection_position8 = p[7];
            s.setSection_position9 = p[8];   s.setSection_position10 = p[9];
            s.setSection_position11 = p[10]; s.setSection_position12 = p[11];
            s.setSection_position13 = p[12]; s.setSection_position14 = p[13];
            s.setSection_position15 = p[14]; s.setSection_position16 = p[15];
            s.setSection_position17 = p[16];
        }

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
