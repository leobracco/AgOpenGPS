// ============================================================================
// FormGpsConfigService.cs
// Adaptador IConfigVehiculoService → PilotX. Réplica del guardado de FormConfig
// (WinForms) para la página HTML pages/config.html. Cada Guardar(seccion) copia
// la lógica del Leave nativo de la pestaña correspondiente:
//   settings + side-effects runtime + Settings.Default.Save() + LoadSettings().
// LoadSettings() NO recrea tool/vehicle/yt, por eso los campos runtime se setean
// explícitos igual que en los handlers originales. Unidades del wire: SIEMPRE
// las persistidas (metros / km/h / segundos) — el JS convierte para mostrar.
// ============================================================================

using System;
using System.Globalization;
using System.Reflection;
using AgLibrary.Logging;
using AgLibrary.Settings;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;
    using AgOpenGPS.Core.Models;
    using AgOpenGPS.Properties;

    public sealed class FormGpsConfigService : IConfigVehiculoService
    {
        private readonly FormGPS _form;

        public FormGpsConfigService(FormGPS form) { _form = form; }

        // ------------------------------------------------------------------
        // Infra
        // ------------------------------------------------------------------

        private T OnUi<T>(Func<T> fn)
        {
            if (_form.InvokeRequired) return (T)_form.Invoke(fn);
            return fn();
        }

        private static decimal GetSectionPosition(int n) // n = 1..17
        {
            System.Reflection.FieldInfo fi = typeof(Settings).GetField("setSection_position" + n);
            return (decimal)fi.GetValue(Settings.Default);
        }

        private static void SetSectionPosition(int n, decimal valor)
        {
            System.Reflection.FieldInfo fi = typeof(Settings).GetField("setSection_position" + n);
            fi.SetValue(Settings.Default, valor);
        }

        private static double Clamp(double v, double min, double max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        // ------------------------------------------------------------------
        // Snapshot
        // ------------------------------------------------------------------

        public ConfigSnapshotDto GetSnapshot()
        {
            return OnUi(BuildSnapshot);
        }

        private ConfigSnapshotDto BuildSnapshot()
        {
            var s = Settings.Default;
            var snap = new ConfigSnapshotDto
            {
                // setting persistido (no _form.isMetric): el runtime recién se
                // sincroniza cuando corre LoadSettings post-arranque.
                IsMetric = s.setMenu_isMetric,
                IsJobStarted = _form.isJobStarted,
                PerfilActivo = RegistrySettings.vehicleFileName,

                Vehiculo = new ConfigVehiculoSec
                {
                    VehicleType = s.setVehicle_vehicleType,
                    TractorBrand = s.setBrand_TBrand.ToString(),
                    HarvesterBrand = s.setBrand_HBrand.ToString(),
                    ArticulatedBrand = s.setBrand_WDBrand.ToString(),
                    IsVehicleImage = s.setDisplay_isVehicleImage,
                    Opacity = s.setDisplay_vehicleOpacity
                },
                Dimensiones = new ConfigDimensionesSec
                {
                    Wheelbase = s.setVehicle_wheelbase,
                    TrackWidth = s.setVehicle_trackWidth,
                    HitchLength = s.setVehicle_hitchLength
                },
                Antena = new ConfigAntenaSec
                {
                    AntennaHeight = s.setVehicle_antennaHeight,
                    AntennaPivot = s.setVehicle_antennaPivot,
                    AntennaOffset = s.setVehicle_antennaOffset
                },
                Enganche = new ConfigEngancheSec
                {
                    // Prioridad de carga original: Front → TBT → Trailing → RearFixed
                    Estilo = s.setTool_isToolFront ? "front"
                           : s.setTool_isToolTBT ? "tbt"
                           : s.setTool_isToolTrailing ? "trailing"
                           : "rear",
                    HitchLength = s.setVehicle_hitchLength,
                    TrailingHitchLength = s.setTool_toolTrailingHitchLength,
                    TankTrailingHitchLength = s.setVehicle_tankTrailingHitchLength
                },
                Offset = new ConfigOffsetSec
                {
                    ToolOffset = s.setVehicle_toolOffset,
                    ToolOverlap = s.setVehicle_toolOverlap,
                    TrailingToolToPivotLength = s.setTool_trailingToolToPivotLength
                },
                Timing = new ConfigTimingSec
                {
                    LookAheadOn = s.setVehicle_toolLookAheadOn,
                    LookAheadOff = s.setVehicle_toolLookAheadOff,
                    TurnOffDelay = s.setVehicle_toolOffDelay
                },
                Secciones = BuildSecciones(),
                Switches = new ConfigSwitchesSec
                {
                    WorkEnabled = _form.mc.isWorkSwitchEnabled,
                    WorkActiveLow = s.setF_isWorkSwitchActiveLow,
                    WorkManualSections = s.setF_isWorkSwitchManualSections,
                    SteerEnabled = _form.mc.isSteerWorkSwitchEnabled,
                    SteerManualSections = s.setF_isSteerWorkSwitchManualSections
                },
                Relay = new ConfigRelaySec { Pins = ParseRelayPins(s.setRelay_pinConfig) },
                Maquina = new ConfigMaquinaSec
                {
                    InvertRelays = (s.setArdMac_setting0 & 1) != 0,
                    HydOn = (s.setArdMac_setting0 & 2) != 0,
                    RaiseTime = s.setArdMac_hydRaiseTime,
                    LowerTime = s.setArdMac_hydLowerTime,
                    HydLiftLookAhead = s.setVehicle_hydraulicLiftLookAhead,
                    User1 = s.setArdMac_user1,
                    User2 = s.setArdMac_user2,
                    User3 = s.setArdMac_user3,
                    User4 = s.setArdMac_user4
                },
                Rumbo = new ConfigRumboSec
                {
                    HeadingSource = s.setGPS_headingFromWhichSource,
                    MinGpsStep = Math.Abs(s.setF_minHeadingStepDistance - 1.0) < 0.01,
                    Fusion = (int)(s.setIMU_fusionWeight2 * 500.0),
                    IsRtk = s.setGPS_isRTK,
                    IsRtkKillAutosteer = s.setGPS_isRTK_KillAutoSteer,
                    JumpFixDistance = s.setGPS_jumpFixAlarmDistance,
                    DualHeadingOffset = s.setGPS_dualHeadingOffset,
                    DualReverseDistance = s.setGPS_dualReverseDetectionDistance,
                    ReverseOn = s.setIMU_isReverseOn,
                    AutoSwitchDualFix = s.setAutoSwitchDualFixOn,
                    AutoSwitchSpeed = s.setAutoSwitchDualFixSpeed,
                    ImuPresent = _form.ahrs.imuHeading != 99999
                },
                Rolido = new ConfigRolidoSec
                {
                    RollZero = s.setIMU_rollZero,
                    RollFilter = (int)(s.setIMU_rollFilter * 100.0),
                    InvertRoll = s.setIMU_invertRoll,
                    ImuPresent = _form.ahrs.imuRoll != 88888,
                    ImuRoll = _form.ahrs.imuRoll
                },
                Uturn = new ConfigUturnSec
                {
                    Radius = s.set_youTurnRadius,
                    DistanceFromBoundary = s.set_youTurnDistanceFromBoundary,
                    ExtensionLength = s.set_youTurnExtensionLength,
                    Smoothing = s.setAS_uTurnSmoothing
                },
                Tram = new ConfigTramSec
                {
                    TramWidth = s.setTram_tramWidth,
                    DisplayTramControl = s.setTool_isDisplayTramControl,
                    OuterInverted = s.setTool_isTramOuterInverted
                },
                Display = new ConfigDisplaySec
                {
                    IsMetric = s.setMenu_isMetric,
                    Brightness = s.setDisplay_isBrightnessOn,
                    Floor = s.setDisplay_isTextureOn,
                    Grid = s.setMenu_isGridOn,
                    Speedo = s.setMenu_isSpeedoOn,
                    StartFullScreen = s.setDisplay_isStartFullScreen,
                    SvennArrow = s.setDisplay_isSvennArrowOn,
                    ExtraGuides = s.setMenu_isSideGuideLines,
                    Polygons = _form.isDrawPolygons,
                    Keyboard = s.setDisplay_isKeyboardOn,
                    LogElevation = s.setDisplay_isLogElevation,
                    DirectionMarkers = s.setTool_isDirectionMarkers,
                    SectionLines = s.setDisplay_isSectionLinesOn,
                    LineSmooth = s.setDisplay_isLineSmooth,
                    HeadlandDistance = s.isHeadlandDistanceOn,
                    NumGuideLines = _form.ABLine.numGuideLines
                },
                Botones = new ConfigBotonesSec
                {
                    FeatureTram = s.setFeatures.isTramOn,
                    FeatureHeadland = s.setFeatures.isHeadlandOn,
                    FeatureBoundary = s.setFeatures.isBoundaryOn,
                    FeatureRecPath = s.setFeatures.isRecPathOn,
                    FeatureAbSmooth = s.setFeatures.isABSmoothOn,
                    FeatureHideContour = s.setFeatures.isHideContourOn,
                    FeatureWebcam = s.setFeatures.isWebCamOn,
                    FeatureOffsetFix = s.setFeatures.isOffsetFixOn,
                    FeatureUturn = s.setFeatures.isUTurnOn,
                    FeatureLateral = s.setFeatures.isLateralOn,
                    FeatureNudge = s.setFeatures.isABLineOn,
                    SoundSteer = s.setSound_isAutoSteerOn,
                    SoundTurn = s.setSound_isUturnOn,
                    SoundHydLift = s.setSound_isHydLiftOn,
                    SoundSections = s.setSound_isSectionsOn,
                    AutoStartCorex = s.setDisplay_isAutoStartAgIO,
                    AutoOffCorex = s.setDisplay_isAutoOffAgIO,
                    ShutdownNoPower = s.setDisplay_isShutdownWhenNoPower,
                    HardwareMessages = s.setDisplay_isHardwareMessages
                }
            };
            return snap;
        }

        private ConfigSeccionesSec BuildSecciones()
        {
            var s = Settings.Default;
            var sec = new ConfigSeccionesSec
            {
                IsSectionsNotZones = s.setTool_isSectionsNotZones,
                MaxSections = FormGPS.MAXSECTIONS,
                NumSections = s.setVehicle_numSections,
                DefaultSectionWidth = s.setTool_defaultSectionWidth,
                NumSectionsMulti = s.setTool_numSectionsMulti,
                SectionWidthMulti = s.setTool_sectionWidthMulti,
                IsSectionOffWhenOut = s.setTool_isSectionOffWhenOut,
                SlowSpeedCutoff = s.setVehicle_slowSpeedCutoff,
                MinCoverage = s.setVehicle_minCoverage,
                ToolWidth = _form.tool.width,
                SectionWidths = new double[16],
                ZoneRanges = new int[8],
                Zones = 2
            };

            // Anchos individuales = diferencia de posiciones consecutivas
            for (int i = 1; i <= 16; i++)
            {
                sec.SectionWidths[i - 1] =
                    Math.Abs((double)(GetSectionPosition(i + 1) - GetSectionPosition(i)));
            }

            // setTool_zones = CSV de 9 ints: "zones,fin1..fin8"
            try
            {
                string[] w = (s.setTool_zones ?? "").Split(',');
                if (w.Length >= 9)
                {
                    sec.Zones = int.Parse(w[0], CultureInfo.InvariantCulture);
                    for (int i = 0; i < 8; i++)
                        sec.ZoneRanges[i] = int.Parse(w[i + 1], CultureInfo.InvariantCulture);
                }
            }
            catch { /* CSV corrupto: defaults */ }

            return sec;
        }

        private static int[] ParseRelayPins(string csv)
        {
            var pins = new int[24];
            try
            {
                string[] w = (csv ?? "").Split(',');
                for (int i = 0; i < 24 && i < w.Length; i++)
                    pins[i] = int.Parse(w[i], CultureInfo.InvariantCulture);
            }
            catch { }
            return pins;
        }

        // ------------------------------------------------------------------
        // Guardar
        // ------------------------------------------------------------------

        public ConfigResultDto Guardar(string seccion, ConfigGuardarBody body)
        {
            if (body == null) return ConfigResultDto.Falla("empty-body");
            string sec = (seccion ?? "").Trim().ToLowerInvariant();
            return OnUi(() =>
            {
                try
                {
                    ConfigResultDto r;
                    switch (sec)
                    {
                        case "vehiculo": r = GuardarVehiculo(body); break;
                        case "dimensiones": r = GuardarDimensiones(body); break;
                        case "antena": r = GuardarAntena(body); break;
                        case "enganche_estilo": r = GuardarEngancheEstilo(body); break;
                        case "enganche_dist": r = GuardarEngancheDist(body); break;
                        case "offset_implemento": r = GuardarOffsetImplemento(body); break;
                        case "pivote": r = GuardarPivote(body); break;
                        case "timing": r = GuardarTiming(body); break;
                        case "secciones": r = GuardarSecciones(body); break;
                        case "switches": r = GuardarSwitches(body); break;
                        case "relay": r = GuardarRelay(body); break;
                        case "maquina": r = GuardarMaquina(body); break;
                        case "rumbo": r = GuardarRumbo(body); break;
                        case "rolido": r = GuardarRolido(body); break;
                        case "uturn": r = GuardarUturn(body); break;
                        case "tram": r = GuardarTram(body); break;
                        case "display": r = GuardarDisplay(body); break;
                        case "botones": r = GuardarBotones(body); break;
                        default: return ConfigResultDto.Falla("seccion-desconocida");
                    }
                    if (r.Ok)
                    {
                        Settings.Default.Save();
                        _form.LoadSettings();
                        Log.EventWriter("Config HTML: seccion '" + sec + "' guardada");
                    }
                    return r;
                }
                catch (Exception ex)
                {
                    Log.EventWriter("Config HTML: error guardando '" + sec + "' (" + ex.Message + ")");
                    return ConfigResultDto.Falla(ex.Message);
                }
            });
        }

        // --- tabVConfig -----------------------------------------------------

        private ConfigResultDto GuardarVehiculo(ConfigGuardarBody b)
        {
            var s = Settings.Default;

            if (b.VehicleType.HasValue)
                s.setVehicle_vehicleType = Clamp(b.VehicleType.Value, 0, 2);

            TractorBrand tb = s.setBrand_TBrand;
            HarvesterBrand hb = s.setBrand_HBrand;
            ArticulatedBrand ab = s.setBrand_WDBrand;
            if (!string.IsNullOrEmpty(b.TractorBrand) && Enum.TryParse(b.TractorBrand, true, out TractorBrand tbNew)) tb = tbNew;
            if (!string.IsNullOrEmpty(b.HarvesterBrand) && Enum.TryParse(b.HarvesterBrand, true, out HarvesterBrand hbNew)) hb = hbNew;
            if (!string.IsNullOrEmpty(b.ArticulatedBrand) && Enum.TryParse(b.ArticulatedBrand, true, out ArticulatedBrand abNew)) ab = abNew;
            s.setBrand_TBrand = tb;
            s.setBrand_HBrand = hb;
            s.setBrand_WDBrand = ab;

            if (b.IsVehicleImage.HasValue) s.setDisplay_isVehicleImage = b.IsVehicleImage.Value;
            if (b.Opacity.HasValue) s.setDisplay_vehicleOpacity = Clamp(b.Opacity.Value, 20, 100);

            // El Enter nativo siempre resetea el color a (254,254,254) y lo persiste
            s.setDisplay_colorVehicle = System.Drawing.Color.FromArgb(254, 254, 254);

            // Caso especial Harvester: fuerza herramienta FRONTAL + hitch positivo
            if (s.setVehicle_vehicleType == (int)VehicleType.Harvester)
            {
                if (_form.tool.hitchLength < 0)
                {
                    _form.tool.hitchLength *= -1;
                    s.setVehicle_hitchLength = _form.tool.hitchLength;
                }
                s.setTool_isToolFront = true;
                s.setTool_isToolTBT = false;
                s.setTool_isToolTrailing = false;
                s.setTool_isToolRearFixed = false;
            }

            // Aplicar el tipo al runtime: el original lo hace vía
            // configVehicleControl.UpdateSettings() sobre mf.vehicle.VehicleConfig.
            // LoadSettings NO relee Type (solo Opacity/IsImage/Color), así que
            // sin esto el mapa sigue dibujando el vehículo viejo hasta reiniciar.
            _form.vehicle.VehicleConfig.Type = (VehicleType)s.setVehicle_vehicleType;

            // Rebind de texturas OpenGL (LoadSettings NO lo hace)
            switch ((VehicleType)s.setVehicle_vehicleType)
            {
                case VehicleType.Tractor:
                    _form.VehicleTextures.Tractor.SetBitmap(TractorBitmaps.GetBitmap(tb));
                    break;
                case VehicleType.Harvester:
                    _form.VehicleTextures.Harvester.SetBitmap(HarvesterBitmaps.GetBitmap(hb));
                    break;
                case VehicleType.Articulated:
                    _form.VehicleTextures.ArticulatedFront.SetBitmap(ArticulatedBitmaps.GetFrontBitmap(ab));
                    _form.VehicleTextures.ArticulatedRear.SetBitmap(ArticulatedBitmaps.GetRearBitmap(ab));
                    break;
            }
            return ConfigResultDto.Exito();
        }

        // --- tabVDimensions -------------------------------------------------

        private ConfigResultDto GuardarDimensiones(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.Wheelbase.HasValue)
            {
                s.setVehicle_wheelbase = Math.Abs(b.Wheelbase.Value);
                _form.vehicle.VehicleConfig.Wheelbase = s.setVehicle_wheelbase;
            }
            if (b.TrackWidth.HasValue)
            {
                s.setVehicle_trackWidth = Math.Abs(b.TrackWidth.Value);
                _form.vehicle.VehicleConfig.TrackWidth = s.setVehicle_trackWidth;
                _form.tram.halfWheelTrack = s.setVehicle_trackWidth * 0.5;
            }
            if (b.HitchLength.HasValue)
            {
                // El wire manda magnitud; el signo lo decide el estilo de enganche
                double hitch = Math.Abs(b.HitchLength.Value);
                if (!s.setTool_isToolFront) hitch *= -1;
                _form.tool.hitchLength = hitch;
                s.setVehicle_hitchLength = hitch;
            }
            return ConfigResultDto.Exito();
        }

        // --- tabVAntenna ----------------------------------------------------

        private ConfigResultDto GuardarAntena(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.AntennaHeight.HasValue)
            {
                s.setVehicle_antennaHeight = Math.Abs(b.AntennaHeight.Value);
                _form.vehicle.VehicleConfig.AntennaHeight = s.setVehicle_antennaHeight;
            }
            if (b.AntennaPivot.HasValue)
            {
                s.setVehicle_antennaPivot = b.AntennaPivot.Value; // puede ser negativo
                _form.vehicle.VehicleConfig.AntennaPivot = s.setVehicle_antennaPivot;
            }
            if (b.AntennaOffset.HasValue)
            {
                s.setVehicle_antennaOffset = b.AntennaOffset.Value; // + izquierda / − derecha
                _form.vehicle.VehicleConfig.AntennaOffset = s.setVehicle_antennaOffset;
            }
            return ConfigResultDto.Exito();
        }

        // --- tabTConfig -----------------------------------------------------

        private ConfigResultDto GuardarEngancheEstilo(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            string estilo = (b.Estilo ?? "").Trim().ToLowerInvariant();

            if (s.setVehicle_vehicleType == (int)VehicleType.Harvester)
            {
                estilo = "front"; // cosechadora obliga frontal
            }

            switch (estilo)
            {
                case "front":
                    s.setTool_isToolFront = true; s.setTool_isToolTBT = false;
                    s.setTool_isToolTrailing = false; s.setTool_isToolRearFixed = false;
                    break;
                case "tbt": // TBT implica trailing (quirk original)
                    s.setTool_isToolFront = false; s.setTool_isToolTBT = true;
                    s.setTool_isToolTrailing = true; s.setTool_isToolRearFixed = false;
                    break;
                case "trailing":
                    s.setTool_isToolFront = false; s.setTool_isToolTBT = false;
                    s.setTool_isToolTrailing = true; s.setTool_isToolRearFixed = false;
                    break;
                case "rear":
                    s.setTool_isToolFront = false; s.setTool_isToolTBT = false;
                    s.setTool_isToolTrailing = false; s.setTool_isToolRearFixed = true;
                    break;
                default:
                    return ConfigResultDto.Falla("estilo-invalido");
            }

            // Copia a runtime + corrección de signo del hitch (Leave original)
            _form.tool.isToolRearFixed = s.setTool_isToolRearFixed;
            _form.tool.isToolTrailing = s.setTool_isToolTrailing;
            _form.tool.isToolTBT = s.setTool_isToolTBT;
            _form.tool.isToolFrontFixed = s.setTool_isToolFront;

            if (s.setTool_isToolFront && _form.tool.hitchLength < 0) _form.tool.hitchLength *= -1;
            if (!s.setTool_isToolFront && _form.tool.hitchLength > 0) _form.tool.hitchLength *= -1;
            s.setVehicle_hitchLength = _form.tool.hitchLength;

            return ConfigResultDto.Exito();
        }

        // --- tabTHitch ------------------------------------------------------

        private ConfigResultDto GuardarEngancheDist(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.HitchLength.HasValue)
            {
                double hitch = Math.Abs(b.HitchLength.Value);
                if (!s.setTool_isToolFront) hitch *= -1; // trasero = negativo
                _form.tool.hitchLength = hitch;
                s.setVehicle_hitchLength = hitch;
            }
            if (b.TrailingHitchLength.HasValue)
            {
                double v = -Math.Abs(b.TrailingHitchLength.Value); // siempre ≤ 0
                _form.tool.trailingHitchLength = v;
                s.setTool_toolTrailingHitchLength = v;
            }
            if (b.TankTrailingHitchLength.HasValue)
            {
                double v = -Math.Abs(b.TankTrailingHitchLength.Value); // siempre ≤ 0
                _form.tool.tankTrailingHitchLength = v;
                s.setVehicle_tankTrailingHitchLength = v;
            }
            return ConfigResultDto.Exito();
        }

        // --- tabToolOffset --------------------------------------------------

        private ConfigResultDto GuardarOffsetImplemento(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.ToolOffset.HasValue)
            {
                _form.tool.offset = b.ToolOffset.Value; // + derecha / − izquierda
                s.setVehicle_toolOffset = b.ToolOffset.Value;
            }
            if (b.ToolOverlap.HasValue)
            {
                _form.tool.overlap = b.ToolOverlap.Value; // + overlap / − gap
                s.setVehicle_toolOverlap = b.ToolOverlap.Value;
            }
            return ConfigResultDto.Exito();
        }

        // --- tabToolPivot ---------------------------------------------------

        private ConfigResultDto GuardarPivote(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.TrailingToolToPivotLength.HasValue)
            {
                _form.tool.trailingToolToPivotLength = b.TrailingToolToPivotLength.Value; // + detrás
                s.setTool_trailingToolToPivotLength = b.TrailingToolToPivotLength.Value;
            }
            return ConfigResultDto.Exito();
        }

        // --- tabTSettings ---------------------------------------------------

        private ConfigResultDto GuardarTiming(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            double on = Clamp(b.LookAheadOn ?? s.setVehicle_toolLookAheadOn, 0.2, 22.0);
            double off = Clamp(b.LookAheadOff ?? s.setVehicle_toolLookAheadOff, 0.0, 20.0);
            double delay = Clamp(b.TurnOffDelay ?? s.setVehicle_toolOffDelay, 0.0, 10.0);

            if (off > 0 && delay > 0)
                return ConfigResultDto.Falla("off-y-delay-excluyentes");
            if (off > on * 0.8) off = on * 0.8; // clamp del original

            _form.tool.lookAheadOnSetting = on;
            _form.tool.lookAheadOffSetting = off;
            _form.tool.turnOffDelay = delay;
            s.setVehicle_toolLookAheadOn = on;
            s.setVehicle_toolLookAheadOff = off;
            s.setVehicle_toolOffDelay = delay;
            return ConfigResultDto.Exito();
        }

        // --- tabTSections ---------------------------------------------------

        private ConfigResultDto GuardarSecciones(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            bool esSecciones = b.IsSectionsNotZones ?? s.setTool_isSectionsNotZones;
            double capMetros = _form.isMetric ? 50.0 : 1900 * 0.0254; // 5000 cm / 1900 in

            // Comunes
            if (b.IsSectionOffWhenOut.HasValue)
            {
                s.setTool_isSectionOffWhenOut = b.IsSectionOffWhenOut.Value;
                _form.tool.isSectionOffWhenOut = b.IsSectionOffWhenOut.Value;
            }
            if (b.SlowSpeedCutoff.HasValue)
            {
                double kmh = Clamp(b.SlowSpeedCutoff.Value, 0.0, 30.0); // SIEMPRE km/h
                s.setVehicle_slowSpeedCutoff = kmh;
                _form.vehicle.slowSpeedCutoff = kmh;
            }
            if (b.MinCoverage.HasValue)
            {
                int cov = Clamp(b.MinCoverage.Value, 0, 100);
                s.setVehicle_minCoverage = cov;
                _form.tool.minCoverage = cov;
            }

            s.setTool_isSectionsNotZones = esSecciones;
            _form.tool.isSectionsNotZones = esSecciones;

            if (esSecciones)
            {
                int num = Clamp(b.NumSections ?? s.setVehicle_numSections, 1, 16);

                // Anchos: los que vengan pisan; el resto conserva el actual
                var anchos = new double[16];
                for (int i = 1; i <= 16; i++)
                    anchos[i - 1] = Math.Abs((double)(GetSectionPosition(i + 1) - GetSectionPosition(i)));
                if (b.SectionWidths != null)
                {
                    for (int i = 0; i < 16 && i < b.SectionWidths.Length; i++)
                        anchos[i] = Math.Abs(b.SectionWidths[i]);
                }

                double total = 0;
                for (int i = 0; i < num; i++) total += anchos[i];
                if (total > capMetros) return ConfigResultDto.Falla("ancho-total-excedido");
                if (total <= 0) return ConfigResultDto.Falla("ancho-total-cero");

                // Posiciones centradas: pos1 = −total/2 (réplica CalculateSectionPositions)
                var pos = new double[17];
                pos[0] = -total / 2.0;
                for (int j = 1; j <= num; j++) pos[j] = pos[j - 1] + anchos[j - 1];
                for (int j = num + 1; j <= 16; j++) pos[j] = 0;
                for (int j = 0; j < 17; j++) SetSectionPosition(j + 1, (decimal)pos[j]);

                if (b.DefaultSectionWidth.HasValue)
                    s.setTool_defaultSectionWidth = Math.Abs(b.DefaultSectionWidth.Value);

                _form.tool.numOfSections = num;
                s.setVehicle_numSections = num;

                _form.LineUpIndividualSectionBtns();
                _form.SectionSetPosition();
                _form.SectionCalcWidths(); // recalcula tool.width
                _form.tram.IsTramOuterOrInner();
                s.setVehicle_toolWidth = _form.tool.width;
                _form.SendRelaySettingsToMachineModule();

                // Secciones individuales no soportan multicolor
                s.setColor_isMultiColorSections = false;
                _form.tool.isMultiColoredSections = false;
            }
            else
            {
                int num = Clamp(b.NumSectionsMulti ?? s.setTool_numSectionsMulti, 1, FormGPS.MAXSECTIONS);
                int zonas = Clamp(b.Zones ?? _form.tool.zones, 2, 8);
                if (zonas > num) return ConfigResultDto.Falla("mas-zonas-que-secciones");

                double ancho = Math.Abs(b.SectionWidthMulti ?? s.setTool_sectionWidthMulti);
                if (ancho <= 0) return ConfigResultDto.Falla("ancho-seccion-cero");
                if (num * ancho > capMetros) return ConfigResultDto.Falla("ancho-total-excedido");

                _form.tool.numOfSections = num;
                s.setTool_numSectionsMulti = num;
                s.setTool_sectionWidthMulti = ancho;

                _form.tool.width = num * ancho;
                s.setVehicle_toolWidth = _form.tool.width;
                _form.tram.IsTramOuterOrInner();
                _form.SectionCalcMulti();

                // zoneRanges: slot 0 = cantidad de zonas; 1..zonas = fin de cada zona
                var ranges = new int[9];
                ranges[0] = zonas;
                if (b.ZoneRanges != null)
                {
                    for (int k = 1; k <= 8 && k <= b.ZoneRanges.Length; k++)
                        ranges[k] = Clamp(b.ZoneRanges[k - 1], 0, num);
                }
                else
                {
                    // Reparto default: división entera, resto a la última
                    int defa = num / zonas;
                    for (int k = 1; k < zonas; k++) ranges[k] = k * defa;
                }
                ranges[zonas] = num; // la última zona SIEMPRE termina en la última sección
                for (int k = zonas + 1; k <= 8; k++) ranges[k] = 0;

                // Validar orden creciente (mejora consciente sobre el original)
                for (int k = 2; k <= zonas; k++)
                    if (ranges[k] <= ranges[k - 1]) return ConfigResultDto.Falla("zonas-desordenadas");

                _form.tool.zones = zonas;
                for (int k = 0; k < 9; k++) _form.tool.zoneRanges[k] = ranges[k];
                s.setTool_zones = string.Join(",", ranges);

                _form.LineUpAllZoneButtons();
            }
            return ConfigResultDto.Exito();
        }

        // --- tabTSwitches ---------------------------------------------------

        private ConfigResultDto GuardarSwitches(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            bool workEnabled = b.WorkEnabled ?? _form.mc.isWorkSwitchEnabled;
            bool steerEnabled = b.SteerEnabled ?? _form.mc.isSteerWorkSwitchEnabled;

            if (b.WorkActiveLow.HasValue)
                _form.mc.isWorkSwitchActiveLow = s.setF_isWorkSwitchActiveLow = b.WorkActiveLow.Value;
            _form.mc.isWorkSwitchEnabled = s.setF_isWorkSwitchEnabled = workEnabled;
            if (b.WorkManualSections.HasValue)
                _form.mc.isWorkSwitchManualSections = s.setF_isWorkSwitchManualSections = b.WorkManualSections.Value;
            _form.mc.isSteerWorkSwitchEnabled = s.setF_isSteerWorkSwitchEnabled = steerEnabled;
            if (b.SteerManualSections.HasValue)
                _form.mc.isSteerWorkSwitchManualSections = s.setF_isSteerWorkSwitchManualSections = b.SteerManualSections.Value;

            // Derivado (Leave original)
            _form.mc.isRemoteWorkSystemOn = s.setF_isRemoteWorkSystemOn = (workEnabled || steerEnabled);
            return ConfigResultDto.Exito();
        }

        // --- tabRelay (Send + Save → PGN 236) --------------------------------

        private ConfigResultDto GuardarRelay(ConfigGuardarBody b)
        {
            if (b.Pins == null || b.Pins.Length != 24)
                return ConfigResultDto.Falla("pins-invalidos");
            var vals = new string[24];
            for (int i = 0; i < 24; i++)
            {
                if (b.Pins[i] < 0 || b.Pins[i] > 21) return ConfigResultDto.Falla("pin-fuera-de-rango");
                vals[i] = b.Pins[i].ToString(CultureInfo.InvariantCulture);
            }
            Settings.Default.setRelay_pinConfig = string.Join(",", vals);
            Settings.Default.Save();
            _form.SendRelaySettingsToMachineModule(); // PGN 236 → módulo
            return ConfigResultDto.Exito();
        }

        // --- tabAMachine (Send + Save → PGN 238) ------------------------------

        private ConfigResultDto GuardarMaquina(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            int sett = 0;
            bool invert = b.InvertRelays ?? ((s.setArdMac_setting0 & 1) != 0);
            bool hyd = b.HydOn ?? ((s.setArdMac_setting0 & 2) != 0);
            if (invert) sett |= 1;
            if (hyd) sett |= 2;
            s.setArdMac_setting0 = (byte)sett;

            if (b.RaiseTime.HasValue) s.setArdMac_hydRaiseTime = (byte)Clamp(b.RaiseTime.Value, 1, 255);
            if (b.LowerTime.HasValue) s.setArdMac_hydLowerTime = (byte)Clamp(b.LowerTime.Value, 1, 255);
            if (b.User1.HasValue) s.setArdMac_user1 = (byte)Clamp(b.User1.Value, 0, 255);
            if (b.User2.HasValue) s.setArdMac_user2 = (byte)Clamp(b.User2.Value, 0, 255);
            if (b.User3.HasValue) s.setArdMac_user3 = (byte)Clamp(b.User3.Value, 0, 255);
            if (b.User4.HasValue) s.setArdMac_user4 = (byte)Clamp(b.User4.Value, 0, 255);
            if (b.HydLiftLookAhead.HasValue)
            {
                double la = Clamp(b.HydLiftLookAhead.Value, 1.0, 20.0);
                s.setVehicle_hydraulicLiftLookAhead = la; // no viaja en el PGN
                _form.vehicle.hydLiftLookAheadTime = la;
            }

            Settings.Default.Save();

            // PGN 238 (réplica SaveSettingsMachine)
            _form.p_238.pgn[_form.p_238.set0] = s.setArdMac_setting0;
            _form.p_238.pgn[_form.p_238.raiseTime] = s.setArdMac_hydRaiseTime;
            _form.p_238.pgn[_form.p_238.lowerTime] = s.setArdMac_hydLowerTime;
            _form.p_238.pgn[_form.p_238.user1] = s.setArdMac_user1;
            _form.p_238.pgn[_form.p_238.user2] = s.setArdMac_user2;
            _form.p_238.pgn[_form.p_238.user3] = s.setArdMac_user3;
            _form.p_238.pgn[_form.p_238.user4] = s.setArdMac_user4;
            _form.SendPgnToLoop(_form.p_238.pgn);
            return ConfigResultDto.Exito();
        }

        // --- tabDHeading ------------------------------------------------------

        private ConfigResultDto GuardarRumbo(ConfigGuardarBody b)
        {
            var s = Settings.Default;

            if (!string.IsNullOrEmpty(b.HeadingSource))
            {
                string src = b.HeadingSource.Trim();
                if (src != "Fix" && src != "Dual") return ConfigResultDto.Falla("heading-source-invalido");
                s.setGPS_headingFromWhichSource = src;
                _form.headingFromSource = src;
            }
            if (b.MinGpsStep.HasValue)
            {
                s.setF_minHeadingStepDistance = b.MinGpsStep.Value ? 1.0 : 0.5;
                s.setGPS_minimumStepLimit = b.MinGpsStep.Value ? 0.1 : 0.05;
                _form.isFirstHeadingSet = false; // fuerza recálculo (original)
            }
            if (b.Fusion.HasValue)
            {
                int barra = Clamp(b.Fusion.Value, 5, 60);
                s.setIMU_fusionWeight2 = barra * 0.002;
                _form.ahrs.fusionWeight = s.setIMU_fusionWeight2;
            }
            if (b.IsRtk.HasValue)
                _form.isRTK_AlarmOn = s.setGPS_isRTK = b.IsRtk.Value;
            if (b.IsRtkKillAutosteer.HasValue)
                _form.isRTK_KillAutosteer = s.setGPS_isRTK_KillAutoSteer = b.IsRtkKillAutosteer.Value;
            if (b.JumpFixDistance.HasValue)
                s.setGPS_jumpFixAlarmDistance = Clamp(b.JumpFixDistance.Value, 0, 1000);
            if (b.DualHeadingOffset.HasValue)
            {
                s.setGPS_dualHeadingOffset = Clamp(b.DualHeadingOffset.Value, -100.0, 100.0);
                _form.pn.headingTrueDualOffset = s.setGPS_dualHeadingOffset;
            }
            if (b.DualReverseDistance.HasValue)
            {
                s.setGPS_dualReverseDetectionDistance = Clamp(b.DualReverseDistance.Value, 0.1, 0.9);
                _form.dualReverseDetectionDistance = s.setGPS_dualReverseDetectionDistance;
            }
            if (b.ReverseOn.HasValue)
                _form.ahrs.isReverseOn = s.setIMU_isReverseOn = b.ReverseOn.Value;
            if (b.AutoSwitchDualFix.HasValue)
                _form.ahrs.autoSwitchDualFixOn = s.setAutoSwitchDualFixOn = b.AutoSwitchDualFix.Value;
            if (b.AutoSwitchSpeed.HasValue)
            {
                double kmh = Clamp(b.AutoSwitchSpeed.Value, 1.0, 10.0); // SIEMPRE km/h
                s.setAutoSwitchDualFixSpeed = kmh;
                _form.ahrs.autoSwitchDualFixSpeed = kmh;
            }
            return ConfigResultDto.Exito();
        }

        // --- tabDRoll ---------------------------------------------------------

        private ConfigResultDto GuardarRolido(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.RollFilter.HasValue)
            {
                int barra = Clamp(b.RollFilter.Value, 0, 98);
                s.setIMU_rollFilter = barra * 0.01;
                _form.ahrs.rollFilter = s.setIMU_rollFilter;
            }
            if (b.InvertRoll.HasValue)
                _form.ahrs.isRollInvert = s.setIMU_invertRoll = b.InvertRoll.Value;

            // El Leave original persiste el rollZero vivo (las acciones lo mueven)
            s.setIMU_rollZero = _form.ahrs.rollZero;
            return ConfigResultDto.Exito();
        }

        public ConfigRolidoResultDto AccionRolido(string accion)
        {
            string acc = (accion ?? "").Trim().ToLowerInvariant();
            return OnUi(() =>
            {
                var r = new ConfigRolidoResultDto { Ok = true };
                var ahrs = _form.ahrs;
                bool sinImu = ahrs.imuRoll == 88888;
                switch (acc)
                {
                    case "zero":
                        if (sinImu) { r.Ok = false; r.Error = "sin-imu"; break; }
                        ahrs.imuRoll += ahrs.rollZero;
                        ahrs.rollZero = ahrs.imuRoll;
                        Log.EventWriter("Config HTML: Roll Zeroed con " + ahrs.rollZero.ToString("N2"));
                        break;
                    case "quitar":
                        ahrs.rollZero = 0;
                        break;
                    case "subir":
                        if (sinImu) { r.Ok = false; r.Error = "sin-imu"; break; }
                        ahrs.rollZero += 0.1;
                        break;
                    case "bajar":
                        if (sinImu) { r.Ok = false; r.Error = "sin-imu"; break; }
                        ahrs.rollZero -= 0.1;
                        break;
                    case "reset_imu":
                        ahrs.imuHeading = 99999;
                        ahrs.imuRoll = 88888;
                        break;
                    default:
                        r.Ok = false; r.Error = "accion-desconocida";
                        break;
                }
                if (r.Ok)
                {
                    Settings.Default.setIMU_rollZero = ahrs.rollZero;
                    Settings.Default.Save();
                }
                r.RollZero = ahrs.rollZero;
                r.ImuRoll = ahrs.imuRoll;
                r.ImuPresent = ahrs.imuRoll != 88888;
                return r;
            });
        }

        // --- tabUTurn -----------------------------------------------------------

        private ConfigResultDto GuardarUturn(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.Radius.HasValue)
            {
                double v = Math.Max(2.0, b.Radius.Value);
                _form.yt.youTurnRadius = v;
                s.set_youTurnRadius = v;
            }
            if (b.DistanceFromBoundary.HasValue)
            {
                double v = Math.Max(0.2, b.DistanceFromBoundary.Value);
                _form.yt.uturnDistanceFromBoundary = v;
                s.set_youTurnDistanceFromBoundary = v;
            }
            if (b.ExtensionLength.HasValue)
            {
                int v = Clamp(b.ExtensionLength.Value, 3, 50);
                _form.yt.youTurnStartOffset = v;
                s.set_youTurnExtensionLength = v;
            }
            if (b.Smoothing.HasValue)
            {
                int v = Clamp(b.Smoothing.Value, 8, 50);
                _form.yt.uTurnSmoothing = v;
                s.setAS_uTurnSmoothing = v;
            }
            // Leave original: reconstruir líneas de giro y descartar el U-turn creado
            _form.bnd.BuildTurnLines();
            _form.yt.ResetCreatedYouTurn();
            return ConfigResultDto.Exito();
        }

        // --- tabTram ---------------------------------------------------------

        private ConfigResultDto GuardarTram(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            if (b.TramWidth.HasValue)
            {
                double v = Math.Abs(b.TramWidth.Value);
                _form.tram.tramWidth = v;
                s.setTram_tramWidth = v;
            }
            if (b.OuterInverted.HasValue)
                s.setTool_isTramOuterInverted = b.OuterInverted.Value;
            if (b.DisplayTramControl.HasValue)
            {
                s.setTool_isDisplayTramControl = b.DisplayTramControl.Value;
                _form.tool.isDisplayTramControl = b.DisplayTramControl.Value;
            }
            _form.tram.IsTramOuterOrInner();
            return ConfigResultDto.Exito();
        }

        // --- tabDisplay (réplica SaveDisplaySettings) ---------------------------

        private ConfigResultDto GuardarDisplay(ConfigGuardarBody b)
        {
            var s = Settings.Default;

            if (b.Brightness.HasValue) { _form.isBrightnessOn = b.Brightness.Value; s.setDisplay_isBrightnessOn = b.Brightness.Value; }
            if (b.Floor.HasValue) { _form.isTextureOn = b.Floor.Value; s.setDisplay_isTextureOn = b.Floor.Value; }
            if (b.Grid.HasValue) { _form.isGridOn = b.Grid.Value; s.setMenu_isGridOn = b.Grid.Value; }
            if (b.SvennArrow.HasValue) { _form.isSvennArrowOn = b.SvennArrow.Value; s.setDisplay_isSvennArrowOn = b.SvennArrow.Value; }
            if (b.Speedo.HasValue) { _form.isSpeedoOn = b.Speedo.Value; s.setMenu_isSpeedoOn = b.Speedo.Value; }
            if (b.StartFullScreen.HasValue) s.setDisplay_isStartFullScreen = b.StartFullScreen.Value;
            if (b.ExtraGuides.HasValue) { _form.isSideGuideLines = b.ExtraGuides.Value; s.setMenu_isSideGuideLines = b.ExtraGuides.Value; }
            if (b.Polygons.HasValue) _form.isDrawPolygons = b.Polygons.Value; // solo runtime (original)
            if (b.Keyboard.HasValue) { _form.isKeyboardOn = b.Keyboard.Value; s.setDisplay_isKeyboardOn = b.Keyboard.Value; }
            if (b.LogElevation.HasValue) { _form.isLogElevation = b.LogElevation.Value; s.setDisplay_isLogElevation = b.LogElevation.Value; }
            if (b.DirectionMarkers.HasValue) { _form.isDirectionMarkers = b.DirectionMarkers.Value; s.setTool_isDirectionMarkers = b.DirectionMarkers.Value; }
            if (b.SectionLines.HasValue) { _form.isSectionlinesOn = b.SectionLines.Value; s.setDisplay_isSectionLinesOn = b.SectionLines.Value; }
            if (b.LineSmooth.HasValue) { _form.isLineSmooth = b.LineSmooth.Value; s.setDisplay_isLineSmooth = b.LineSmooth.Value; }
            if (b.HeadlandDistance.HasValue) { _form.isHeadlandDistanceOn = b.HeadlandDistance.Value; s.isHeadlandDistanceOn = b.HeadlandDistance.Value; }
            if (b.NumGuideLines.HasValue)
            {
                int v = Clamp(b.NumGuideLines.Value, 1, 5000);
                _form.ABLine.numGuideLines = v;
                s.setAS_numGuideLines = v;
            }
            if (b.IsMetric.HasValue)
            {
                _form.isMetric = b.IsMetric.Value;
                s.setMenu_isMetric = b.IsMetric.Value;
            }

            // El original re-persiste estos dos desde runtime aunque no hay control
            s.setMenu_isPureOn = _form.isPureDisplayOn;
            s.setMenu_isLightbarOn = _form.isLightbarOn;
            return ConfigResultDto.Exito();
        }

        // --- tabBtns -----------------------------------------------------------

        private ConfigResultDto GuardarBotones(ConfigGuardarBody b)
        {
            var s = Settings.Default;
            var f = s.setFeatures;

            if (b.FeatureTram.HasValue) f.isTramOn = b.FeatureTram.Value;
            if (b.FeatureHeadland.HasValue) f.isHeadlandOn = b.FeatureHeadland.Value;
            if (b.FeatureBoundary.HasValue) f.isBoundaryOn = b.FeatureBoundary.Value;
            if (b.FeatureRecPath.HasValue) f.isRecPathOn = b.FeatureRecPath.Value;
            if (b.FeatureAbSmooth.HasValue) f.isABSmoothOn = b.FeatureAbSmooth.Value;
            if (b.FeatureHideContour.HasValue) f.isHideContourOn = b.FeatureHideContour.Value;
            if (b.FeatureWebcam.HasValue) f.isWebCamOn = b.FeatureWebcam.Value;
            if (b.FeatureOffsetFix.HasValue) f.isOffsetFixOn = b.FeatureOffsetFix.Value;
            if (b.FeatureUturn.HasValue) f.isUTurnOn = b.FeatureUturn.Value;
            if (b.FeatureLateral.HasValue) f.isLateralOn = b.FeatureLateral.Value;
            if (b.FeatureNudge.HasValue) f.isABLineOn = b.FeatureNudge.Value; // Nudge = isABLineOn

            if (b.SoundSteer.HasValue) { s.setSound_isAutoSteerOn = b.SoundSteer.Value; _form.sounds.isSteerSoundOn = b.SoundSteer.Value; }
            if (b.SoundTurn.HasValue) { s.setSound_isUturnOn = b.SoundTurn.Value; _form.sounds.isTurnSoundOn = b.SoundTurn.Value; }
            if (b.SoundHydLift.HasValue) { s.setSound_isHydLiftOn = b.SoundHydLift.Value; _form.sounds.isHydLiftSoundOn = b.SoundHydLift.Value; }
            if (b.SoundSections.HasValue) { s.setSound_isSectionsOn = b.SoundSections.Value; _form.sounds.isSectionsSoundOn = b.SoundSections.Value; }

            if (b.AutoStartCorex.HasValue) s.setDisplay_isAutoStartAgIO = b.AutoStartCorex.Value;
            if (b.AutoOffCorex.HasValue) s.setDisplay_isAutoOffAgIO = b.AutoOffCorex.Value;
            if (b.ShutdownNoPower.HasValue) s.setDisplay_isShutdownWhenNoPower = b.ShutdownNoPower.Value;
            if (b.HardwareMessages.HasValue) s.setDisplay_isHardwareMessages = b.HardwareMessages.Value;
            return ConfigResultDto.Exito();
        }

        // ------------------------------------------------------------------
        // PrepararSecciones — réplica del Enter nativo de tabTSections
        // ------------------------------------------------------------------

        public ConfigResultDto PrepararSecciones()
        {
            return OnUi(() =>
            {
                try
                {
                    if (_form.isJobStarted)
                    {
                        if (_form.autoBtnState == btnStates.Auto)
                            _form.btnSectionMasterAuto.PerformClick();
                        if (_form.manualBtnState == btnStates.On)
                            _form.btnSectionMasterManual.PerformClick();
                    }
                    return ConfigResultDto.Exito();
                }
                catch (Exception ex)
                {
                    return ConfigResultDto.Falla(ex.Message);
                }
            });
        }
    }
}
