// ============================================================================
// EngineVehicleToolService.cs — adaptador IVehicleToolService sobre
// GuidanceEngineHost. Es lo que hace funcionar la pantalla de Configuración
// (config.html: vehículo, enganche, secciones/zonas, IMU) contra el motor
// headless: sin esto, GET/PUT /api/tool devolvían 404 y la pantalla no podía
// leer ni grabar nada.
//
// Port directo de GuidanceEngineVehicleToolService (PilotX.Android/
// GuidanceEngineStateServices.cs), que ya era 100% portable — mismo criterio
// que se usó con EngineLotesService. Cambia el namespace y, al guardar la
// herramienta, se delega el recálculo de geometría en
// GuidanceEngineHost.AplicarGeometriaDeSecciones() para no duplicar esa lógica.
//
// OJO: recalcular la geometría después de guardar NO es opcional. Sin eso las
// secciones quedan con las posiciones viejas y la huella de cobertura se dibuja
// mal (o de ancho cero) hasta reiniciar el motor.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineVehicleToolService : IVehicleToolService
    {
        private readonly GuidanceEngineHost _engine;

        public EngineVehicleToolService(GuidanceEngineHost engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public VehicleConfigDto GetVehicle()
        {
            var s = global::AgOpenGPS.Properties.Settings.Default;
            return new VehicleConfigDto
            {
                VehicleType = s.setVehicle_vehicleType,
                Wheelbase = s.setVehicle_wheelbase,
                TrackWidth = s.setVehicle_trackWidth,
                AntennaHeight = s.setVehicle_antennaHeight,
                AntennaPivot = s.setVehicle_antennaPivot,
                AntennaOffset = s.setVehicle_antennaOffset,
                MaxSteerAngle = s.setVehicle_maxSteerAngle,
                SlowSpeedCutoff = s.setVehicle_slowSpeedCutoff
            };
        }

        public ToolConfigDto GetTool()
        {
            var s = global::AgOpenGPS.Properties.Settings.Default;
            bool sectionsMode = s.setTool_isSectionsNotZones;
            int numSec = sectionsMode ? s.setVehicle_numSections : s.setTool_numSectionsMulti;

            double[] widths = null;
            if (sectionsMode)
            {
                decimal[] pos = ReadSectionPositions(s);
                int n = Math.Max(1, Math.Min(16, numSec));
                widths = new double[n];
                for (int i = 0; i < n; i++)
                    widths[i] = Math.Abs((double)(pos[i + 1] - pos[i]));
            }

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
                Width = s.setVehicle_toolWidth,
                Overlap = s.setVehicle_toolOverlap,
                Offset = s.setVehicle_toolOffset,
                NumSections = numSec,
                IsSectionsNotZones = sectionsMode,
                SectionWidths = widths,
                SectionWidthMulti = s.setTool_sectionWidthMulti,
                Zones = zones,
                ZoneRanges = zoneRanges,
                HitchLength = s.setVehicle_hitchLength,
                TrailingHitchLength = s.setTool_toolTrailingHitchLength,
                TrailingToolToPivotLength = s.setTool_trailingToolToPivotLength,
                LookAheadOn = s.setVehicle_toolLookAheadOn,
                LookAheadOff = s.setVehicle_toolLookAheadOff,
                TurnOffDelay = s.setVehicle_toolOffDelay,
                IsToolTrailing = s.setTool_isToolTrailing,
                IsToolTBT = s.setTool_isToolTBT,
                IsToolRearFixed = s.setTool_isToolRearFixed,
                IsToolFrontFixed = s.setTool_isToolFront,
                IsSectionOffWhenOut = s.setTool_isSectionOffWhenOut
            };
        }

        public VehicleToolBundleDto GetBundle()
            => new VehicleToolBundleDto { Vehicle = GetVehicle(), Tool = GetTool() };

        public bool SaveVehicle(VehicleConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = global::AgOpenGPS.Properties.Settings.Default;
                s.setVehicle_vehicleType = ClampInt(cfg.VehicleType, 0, 2);
                s.setVehicle_wheelbase = ClampD(cfg.Wheelbase, 0.5, 20.0);
                s.setVehicle_trackWidth = ClampD(cfg.TrackWidth, 0.5, 10.0);
                s.setVehicle_antennaHeight = ClampD(cfg.AntennaHeight, 0.1, 10.0);
                s.setVehicle_antennaPivot = ClampD(cfg.AntennaPivot, -10.0, 10.0);
                s.setVehicle_antennaOffset = ClampD(cfg.AntennaOffset, -5.0, 5.0);
                s.setVehicle_maxSteerAngle = ClampD(cfg.MaxSteerAngle, 5.0, 60.0);
                s.setVehicle_slowSpeedCutoff = ClampD(cfg.SlowSpeedCutoff, 0.0, 10.0);
                s.Save();
                // slowSpeedCutoff viaja en el archivo del motor (mismo motivo
                // que en SaveTool: s.Save() no persiste sin perfil elegido).
                ToolGeometryStore.Guardar();

                // Sin hilo de UI que marshalar: reload directo. Opacity/Color/
                // IsImage no viven en el ctor (mismo gotcha que FormGPS) — se
                // reaplican despues de construir.
                try
                {
                    _engine.Vehicle = new CVehicle(_engine);
                    _engine.Vehicle.VehicleConfig.Opacity = s.setDisplay_vehicleOpacity * 0.01;
                    _engine.Vehicle.VehicleConfig.Color =
                        (AgOpenGPS.Core.Models.ColorRgba)ColorExtensions.CheckColorFor255(s.setDisplay_colorVehicle);
                    _engine.Vehicle.VehicleConfig.IsImage = s.setDisplay_isVehicleImage;
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public bool SaveTool(ToolConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = global::AgOpenGPS.Properties.Settings.Default;
                s.setVehicle_toolOverlap = ClampD(cfg.Overlap, -2.0, 2.0);
                s.setVehicle_toolOffset = ClampD(cfg.Offset, -10.0, 10.0);

                s.setTool_isSectionsNotZones = cfg.IsSectionsNotZones;
                if (cfg.IsSectionsNotZones)
                {
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
                    s.setColor_isMultiColorSections = false;
                }
                else
                {
                    int n = ClampInt(cfg.NumSections, 1, 64);
                    s.setTool_numSectionsMulti = n;

                    double sw = cfg.SectionWidthMulti > 0
                        ? cfg.SectionWidthMulti
                        : (cfg.Width > 0 ? cfg.Width / n : 0.5);
                    sw = ClampD(sw, 0.01, 10.0);
                    s.setTool_sectionWidthMulti = sw;
                    s.setVehicle_toolWidth = n * sw;

                    int z = ClampInt(cfg.Zones, 1, 8);
                    if (z > n) z = n;
                    int[] zr = new int[9];
                    zr[0] = z;
                    int prev = 0;
                    for (int i = 1; i <= z; i++)
                    {
                        int r = (cfg.ZoneRanges != null && i - 1 < cfg.ZoneRanges.Length)
                            ? cfg.ZoneRanges[i - 1] : n;
                        r = ClampInt(r, prev + 1, n);
                        zr[i] = r;
                        prev = r;
                    }
                    zr[z] = n;
                    s.setTool_zones = string.Join(",", zr);
                }

                s.setVehicle_hitchLength = ClampD(cfg.HitchLength, -10.0, 10.0);
                s.setTool_toolTrailingHitchLength = ClampD(cfg.TrailingHitchLength, -20.0, 5.0);
                s.setTool_trailingToolToPivotLength = ClampD(cfg.TrailingToolToPivotLength, -20.0, 20.0);
                s.setVehicle_toolLookAheadOn = ClampD(cfg.LookAheadOn, 0.0, 20.0);
                s.setVehicle_toolLookAheadOff = ClampD(cfg.LookAheadOff, 0.0, 20.0);
                s.setVehicle_toolOffDelay = ClampD(cfg.TurnOffDelay, 0.0, 10.0);

                bool trailing = cfg.IsToolTrailing && !cfg.IsToolRearFixed && !cfg.IsToolFrontFixed;
                bool rear = !trailing && cfg.IsToolRearFixed && !cfg.IsToolFrontFixed;
                bool front = !trailing && !rear && cfg.IsToolFrontFixed;
                if (!trailing && !rear && !front) rear = true;

                s.setTool_isToolTrailing = trailing;
                s.setTool_isToolRearFixed = rear;
                s.setTool_isToolFront = front;
                s.setTool_isToolTBT = cfg.IsToolTBT && trailing;
                s.setTool_isSectionOffWhenOut = cfg.IsSectionOffWhenOut;
                s.Save();
                // s.Save() es un NO-OP si no hay perfil de vehículo elegido
                // (vehicle_file_name vacío = caso normal del motor headless).
                // Sin esta línea la geometría del implemento que llega por
                // PUT /api/tool — o por el write-back del implemento activo,
                // ImplementoService.SyncToolIfChanged — vivía solo en RAM y se
                // perdía al reiniciar el proceso.
                ToolGeometryStore.Guardar();

                // Sin las funciones de layout de botones (LineUpIndividualSectionBtns/
                // LineUpAllZoneButtons, WinForms) ni FixSectionLooks: solo reload
                // de CTool + recalculo de posiciones/anchos (logica pura, ya
                // portada en bloque 9 como SectionCalculator).
                try
                {
                    // Releer CTool (ancho, cantidad de secciones, modo) y
                    // repartir de nuevo la geometría lateral. El orden importa:
                    // AplicarGeometriaDeSecciones decide secciones vs zonas
                    // leyendo el Tool recién cargado.
                    _engine.Tool = new CTool(_engine);
                    _engine.AplicarGeometriaDeSecciones();
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public string GetVehiculoCustom()
        {
            try { return global::AgOpenGPS.Properties.Settings.Default.setBrand_VehiculoCustom ?? ""; }
            catch { return ""; }
        }

        public bool SetVehiculoCustom(string archivo)
        {
            try
            {
                archivo = (archivo ?? "").Trim();
                if (archivo.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;

                global::AgOpenGPS.Properties.Settings.Default.setBrand_VehiculoCustom = archivo;
                global::AgOpenGPS.Properties.Settings.Default.Save();
                // Sin AplicarVehiculoCustom (carga el sprite en el render GL —
                // GuidanceEngineHost no tiene render, eso vive en PilotX.Desktop/
                // el render Android, bloque 6). Persistir el setting ya alcanza
                // para que el siguiente render lo lea.
                return true;
            }
            catch { return false; }
        }

        public ImuConfigDto GetImu()
        {
            var s = global::AgOpenGPS.Properties.Settings.Default;
            return new ImuConfigDto
            {
                HeadingSource = s.setGPS_headingFromWhichSource,
                FusionGpsPercent = ClampInt((int)Math.Round(s.setIMU_fusionWeight2 * 500.0), 0, 100),
                MinGpsStep10Cm = s.setF_minHeadingStepDistance == 1.0,
                DualHeadingOffset = s.setGPS_dualHeadingOffset,
                DualReverseDistance = s.setGPS_dualReverseDetectionDistance,
                IsRtkAlarm = s.setGPS_isRTK,
                IsRtkKillAutosteer = s.setGPS_isRTK_KillAutoSteer,
                FixJumpAlarmDistance = s.setGPS_jumpFixAlarmDistance,
                IsReverseOn = s.setIMU_isReverseOn,
                AutoSwitchDualFix = s.setAutoSwitchDualFixOn,
                AutoSwitchDualFixSpeed = s.setAutoSwitchDualFixSpeed,
                RollFilterPercent = ClampInt((int)Math.Round(s.setIMU_rollFilter * 100.0), 0, 100),
                InvertRoll = s.setIMU_invertRoll
            };
        }

        public bool SaveImu(ImuConfigDto cfg)
        {
            if (cfg == null) return false;
            try
            {
                var s = global::AgOpenGPS.Properties.Settings.Default;

                string src = cfg.HeadingSource == "Dual" ? "Dual" : "Fix";
                s.setGPS_headingFromWhichSource = src;

                double fusion = ClampInt(cfg.FusionGpsPercent, 5, 60) * 0.002;
                s.setIMU_fusionWeight2 = fusion;

                s.setF_minHeadingStepDistance = cfg.MinGpsStep10Cm ? 1.0 : 0.5;
                s.setGPS_minimumStepLimit = cfg.MinGpsStep10Cm ? 0.1 : 0.05;

                s.setGPS_dualHeadingOffset = ClampD(cfg.DualHeadingOffset, -100.0, 100.0);
                s.setGPS_dualReverseDetectionDistance = ClampD(cfg.DualReverseDistance, 0.0, 10.0);
                s.setGPS_isRTK = cfg.IsRtkAlarm;
                s.setGPS_isRTK_KillAutoSteer = cfg.IsRtkKillAutosteer;
                s.setGPS_jumpFixAlarmDistance = ClampInt(cfg.FixJumpAlarmDistance, 0, 1000);
                s.setIMU_isReverseOn = cfg.IsReverseOn;
                s.setAutoSwitchDualFixOn = cfg.AutoSwitchDualFix;
                s.setAutoSwitchDualFixSpeed = ClampD(cfg.AutoSwitchDualFixSpeed, 1.0, 10.0);

                double rollFilter = ClampInt(cfg.RollFilterPercent, 0, 98) * 0.01;
                s.setIMU_rollFilter = rollFilter;
                s.setIMU_invertRoll = cfg.InvertRoll;
                s.Save();

                try
                {
                    _engine.headingFromSource = src;
                    _engine.Ahrs.fusionWeight = fusion;
                    _engine.minHeadingStepDist = s.setF_minHeadingStepDistance;
                    _engine.gpsMinimumStepDistance = s.setGPS_minimumStepLimit;
                    _engine.isFirstHeadingSet = false;
                    _engine.Pn.headingTrueDualOffset = s.setGPS_dualHeadingOffset;
                    _engine.dualReverseDetectionDistance = s.setGPS_dualReverseDetectionDistance;
                    _engine.Ahrs.isReverseOn = s.setIMU_isReverseOn;
                    _engine.Ahrs.autoSwitchDualFixOn = s.setAutoSwitchDualFixOn;
                    _engine.Ahrs.autoSwitchDualFixSpeed = s.setAutoSwitchDualFixSpeed;
                    _engine.Ahrs.rollFilter = rollFilter;
                    _engine.Ahrs.isRollInvert = s.setIMU_invertRoll;
                    // Alarma RTK en caliente: el motor la consume por fix en
                    // ComprobarAlarmaRtk (port de OpenGL.Designer.cs:505-561);
                    // sin esto el cambio recién aplicaba al reiniciar.
                    _engine.isRTK_AlarmOn = s.setGPS_isRTK;
                    _engine.isRTK_KillAutosteer = s.setGPS_isRTK_KillAutoSteer;
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        public ImuLiveDto GetImuLive()
        {
            var a = _engine.Ahrs;
            bool hasRoll = a.imuRoll != 88888;
            return new ImuLiveDto
            {
                HasImuHeading = a.imuHeading != 99999,
                HasImuRoll = hasRoll,
                ImuRoll = hasRoll ? a.imuRoll : 0.0,
                RollZero = a.rollZero
            };
        }

        public bool ZeroRoll()
        {
            if (_engine.Ahrs.imuRoll == 88888) return false;
            try
            {
                _engine.Ahrs.imuRoll += _engine.Ahrs.rollZero;
                _engine.Ahrs.rollZero = _engine.Ahrs.imuRoll;
                PersistRollZero();
            }
            catch { }
            return true;
        }

        public bool AdjustRollZero(double delta)
        {
            if (_engine.Ahrs.imuRoll == 88888) return false;
            if (double.IsNaN(delta) || double.IsInfinity(delta)) return false;
            double d = ClampD(delta, -5.0, 5.0);
            try { _engine.Ahrs.rollZero += d; PersistRollZero(); } catch { }
            return true;
        }

        public bool RemoveRollZero()
        {
            try { _engine.Ahrs.rollZero = 0; PersistRollZero(); } catch { }
            return true;
        }

        public bool ResetImu()
        {
            try
            {
                _engine.Ahrs.imuHeading = 99999;
                _engine.Ahrs.imuRoll = 88888;
            }
            catch { }
            return true;
        }

        private void PersistRollZero()
        {
            global::AgOpenGPS.Properties.Settings.Default.setIMU_rollZero = _engine.Ahrs.rollZero;
            global::AgOpenGPS.Properties.Settings.Default.Save();
        }

        private static decimal[] ReadSectionPositions(global::AgOpenGPS.Properties.Settings s)
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

        private static void WriteSectionPositions(global::AgOpenGPS.Properties.Settings s, decimal[] p)
        {
            s.setSection_position1 = p[0]; s.setSection_position2 = p[1];
            s.setSection_position3 = p[2]; s.setSection_position4 = p[3];
            s.setSection_position5 = p[4]; s.setSection_position6 = p[5];
            s.setSection_position7 = p[6]; s.setSection_position8 = p[7];
            s.setSection_position9 = p[8]; s.setSection_position10 = p[9];
            s.setSection_position11 = p[10]; s.setSection_position12 = p[11];
            s.setSection_position13 = p[12]; s.setSection_position14 = p[13];
            s.setSection_position15 = p[14]; s.setSection_position16 = p[15];
            s.setSection_position17 = p[16];
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
