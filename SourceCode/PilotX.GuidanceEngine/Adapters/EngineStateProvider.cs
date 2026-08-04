// ============================================================================
// EngineStateProvider.cs — adaptador IAogStateProvider sobre GuidanceEngineHost.
// Gemelo headless de FormGpsStateProvider (GPS/AgroParallel/Common).
//
// GetSnapshot() porta 1:1 desde FormGPS con renombrado de campos (lowercase ->
// PascalCase del host). Lo único que NO existe headless se stubbea:
//   · PowerStatus (WinForms SystemInformation)      -> PowerOnline = false
//   · flagColor / isNudgeOn (estado de UI nativa)    -> 0 / false
//   · Properties.Settings (proyecto GPS net48)       -> VehicleType/Brand fijos, HasHydLift=false
//   · ShapefileLayerForAdapters (capa del mapa AOG)  -> sin dosis de shape
//
// Los ~14 métodos satélite (graphs, sim-coords, display-colors, event-log,
// section-colors, shape) espejaban ventanas WinForms de PilotX que este
// proceso no tiene y que el render del mapa (PilotX.Desktop) no consume:
// devuelven snapshots vacíos/seguros. Si algún día el Hub corre contra el
// engine headless, se completan con la fuente real (Settings portados a Core,
// buffers de gráfico propios, etc.).
// ============================================================================

using System.Collections.Generic;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineStateProvider : IAogStateProvider
    {
        private readonly GuidanceEngineHost _host;

        public EngineStateProvider(GuidanceEngineHost host) { _host = host; }

        public AogStateSnapshot GetSnapshot()
        {
            var snap = new AogStateSnapshot();
            if (_host == null) return snap;

            try
            {
                snap.IsJobStarted = _host.IsJobStarted;
                snap.CurrentFieldDirectory = _host.currentFieldDirectory;
                snap.FieldsDirectory = RegistrySettings.fieldsDirectory;
                // GPS cortado (>3 s sin fix): velocidad CERO, no el ultimo valor
                // retenido. Sin esto el HUD quedaba clavado en la velocidad vieja
                // y los lazos que dosifican por velocidad seguian con un numero
                // mentiroso. Fail-safe: sin GPS no se dosifica.
                bool gpsVivo = _host.lastFixUtc != default(System.DateTime)
                    && (System.DateTime.UtcNow - _host.lastFixUtc).TotalSeconds <= 3;
                snap.AvgSpeed = gpsVivo ? _host.avgSpeed : 0;
                snap.DistanciaCabeceraM = _host.distancePivotToTurnLine;
                snap.FixQuality = _host.Pn != null ? _host.Pn.fixQuality : 0;
                snap.PowerOnline = false; // sin WinForms SystemInformation headless.
                snap.Heading = _host.pivotAxlePos.heading;
                snap.PivotEasting = _host.pivotAxlePos.easting;
                snap.PivotNorthing = _host.pivotAxlePos.northing;

                if (_host.AppModelField != null)
                {
                    snap.Latitude = _host.AppModelField.CurrentLatLon.Latitude;
                    snap.Longitude = _host.AppModelField.CurrentLatLon.Longitude;
                }

                snap.IsAutoSteerOn = _host.isBtnAutoSteerOn;
                snap.IsSectionAutoOn = _host.autoBtnState == btnStates.Auto;
                snap.IsSectionManualOn = _host.manualBtnState == btnStates.On;
                if (_host.Trk != null)
                {
                    snap.IsAutoSnapToPivot = _host.Trk.isAutoSnapToPivot;
                    snap.IsAutoTrackOn = _host.Trk.isAutoTrack;
                    snap.TrackIdx = _host.Trk.idx;
                    if (_host.Trk.gArr != null)
                    {
                        snap.TracksTotal = _host.Trk.gArr.Count;
                        int vis = 0;
                        for (int i = 0; i < _host.Trk.gArr.Count; i++)
                            if (_host.Trk.gArr[i].isVisible) vis++;
                        snap.TracksVisible = vis;
                    }
                }
                if (_host.Yt != null)
                {
                    snap.IsYouTurnOn = _host.Yt.isYouTurnBtnOn;
                    snap.YouTurnPhase = _host.Yt.youTurnPhase;
                    snap.IsYouTurnTriggered = _host.Yt.isYouTurnTriggered;
                    snap.YouTurnSkipWidth = _host.Yt.rowSkipsWidth;
                    snap.YouTurnSkipMode =
                        _host.Yt.skipMode == SkipMode.Alternative ? "alternado"
                        : _host.Yt.skipMode == SkipMode.IgnoreWorkedTracks ? "ignora_trabajadas"
                        : "normal";
                }
                // Prescripción: dosis en la posición actual. Se muestrea acá
                // porque el state se lee a ~10 Hz — el mismo ritmo al que la
                // dosis tiene sentido — y así QuantiX/FlowX la ven sin que el
                // host tenga que conocer la capa.
                if (Shape != null)
                {
                    Shape.MuestrearPosicion(_host.pivotAxlePos.easting, _host.pivotAxlePos.northing);
                    var capa = Shape.Capa;
                    if (capa != null && !capa.IsEmpty)
                    {
                        snap.ShapeCurrentDose = capa.CurrentDose;
                        snap.ShapeIsInside = capa.CurrentInside;
                    }
                }

                // Diagnostico del giro: sin esto, "no gira" se ve igual esté el
                // tractor fuera del lote, desviado, o con el lote roto.
                if (_host.Mc != null) snap.IsOutOfBounds = _host.Mc.isOutOfBounds;
                // crossTrackError viene en MILIMETROS en el host (int).
                snap.CrossTrackErrorM = _host.crossTrackError / 1000.0;
                if (_host.Ct != null)
                {
                    snap.IsContourOn = _host.Ct.isContourBtnOn;
                    snap.IsContourLocked = _host.Ct.isLocked;
                }
                if (_host.Isobus != null)
                {
                    snap.IsobusAlive = _host.Isobus.IsAlive();
                    snap.IsobusOn = _host.Isobus.SectionControlEnabled;
                }
                snap.HasBoundary = _host.Bnd != null && _host.Bnd.bndList != null
                    && _host.Bnd.bndList.Count > 0;

                // Barra abajo: flag/nudge son estado de UI nativa — no headless.
                snap.FlagColor = 0;
                snap.IsNudgeOn = false;
                if (_host.Bnd != null)
                {
                    snap.HasHeadland = _host.Bnd.bndList != null && _host.Bnd.bndList.Count > 0
                        && _host.Bnd.bndList[0].hdLine != null && _host.Bnd.bndList[0].hdLine.Count > 0;
                    snap.IsHeadlandOn = _host.Bnd.isHeadlandOn;
                    snap.IsSectionControlledByHeadland = _host.Bnd.isSectionControlledByHeadland;
                }
                snap.HasHydLift = false; // Properties.Settings vive en el proyecto GPS net48.
                if (_host.Vehicle != null) snap.IsHydLiftOn = _host.Vehicle.isHydLiftOn;
                if (_host.Tram != null)
                {
                    snap.HasTram = (_host.Tram.tramList != null ? _host.Tram.tramList.Count : 0)
                        + (_host.Tram.tramBndOuterArr != null ? _host.Tram.tramBndOuterArr.Count : 0) > 0;
                    snap.TramDisplayMode = (int)_host.Tram.displayMode;
                }
                if (_host.Yt != null)
                {
                    snap.YouSkipMode = (int)_host.Yt.skipMode;
                    snap.RowSkipsWidth = _host.Yt.rowSkipsWidth;
                }

                // Geometría del vehículo + dirección: el mapa dibuja el cuerpo a
                // escala real y las ruedas delanteras giradas aparte (el arte del
                // tractor no las trae, justamente porque giran).
                if (_host.Vehicle != null)
                {
                    snap.Wheelbase = _host.Vehicle.VehicleConfig.Wheelbase;
                    snap.TrackWidth = _host.Vehicle.VehicleConfig.TrackWidth;
                }
                if (_host.Mc != null) snap.SteerAngleDeg = _host.Mc.actualSteerAngleDegrees;

                snap.ToolEasting = _host.toolPos.easting;
                snap.ToolNorthing = _host.toolPos.northing;
                snap.ToolHeading = _host.toolPos.heading;

                if (_host.Fd != null)
                {
                    snap.WorkedAreaTotalM2 = _host.Fd.workedAreaTotal;
                    snap.ActualAreaCoveredM2 = _host.Fd.actualAreaCovered;
                }

                if (_host.Tool != null)
                {
                    int n = _host.Tool.numOfSections;
                    snap.NumSections = n;
                    snap.ToolWidth = _host.Tool.width;
                    snap.ToolOffset = _host.Tool.offset;
                    snap.IsSectionsNotZones = _host.Tool.isSectionsNotZones;
                    if (!_host.Tool.isSectionsNotZones && _host.Tool.zoneRanges != null)
                    {
                        // zoneRanges[0] no se usa; se expone 1..8 tal cual.
                        var z = new int[8];
                        for (int i = 1; i <= 8 && i < _host.Tool.zoneRanges.Length; i++) z[i - 1] = _host.Tool.zoneRanges[i];
                        snap.ZoneRanges = z;
                    }
                    if (n > 0 && _host.Sections != null)
                    {
                        var arr = new bool[n];
                        var estados = new int[n];
                        var pos = new List<SectionExtent>(n);
                        var speeds = new double[n];
                        for (int i = 0; i < n && i < _host.Sections.Length; i++)
                        {
                            var sec = _host.Sections[i];
                            arr[i] = sec != null && sec.sectionOnRequest;
                            // 0=Off, 1=Auto, 2=On: lo que eligió el operario, que
                            // es distinto de si la sección aplica ahora.
                            estados[i] = sec == null ? 0 : (int)sec.sectionBtnState;
                            if (sec != null)
                            {
                                pos.Add(new SectionExtent(i, sec.positionLeft, sec.positionRight));
                                speeds[i] = sec.speedPixels * 0.36;
                            }
                        }
                        snap.SectionOnRequest = arr;
                        snap.SectionStates = estados;
                        snap.SectionPositions = pos;
                        snap.SectionSpeedsKmh = speeds;
                    }
                    snap.ToolFarLeftSpeedKmh = _host.Tool.farLeftSpeed * 3.6;
                    snap.ToolFarRightSpeedKmh = _host.Tool.farRightSpeed * 3.6;
                }

                // Geometría del lote: boundary + headland + track activo.
                try
                {
                    if (_host.Bnd != null && _host.Bnd.bndList != null)
                    {
                        var bnds = new List<List<FieldPoint>>();
                        var hdls = new List<List<FieldPoint>>();
                        foreach (var b in _host.Bnd.bndList)
                        {
                            if (b == null) continue;
                            bnds.Add(DecimateVec3(b.fenceLine, 0.5));
                            hdls.Add(DecimateVec3(b.hdLine, 0.5));
                        }
                        snap.Boundaries = bnds;
                        snap.Headlands = hdls;

                        // Lindero en curso: se manda SIN decimar. Los puntos ya
                        // vienen cada ~1 m del updater, y el operario los mira
                        // justamente para saber que esta grabando — decimarlos
                        // seria borrar la evidencia que pidio ver.
                        snap.BoundaryBeingMade =
                            (_host.Bnd.isBndBeingMade && _host.Bnd.bndBeingMadePts != null
                                                      && _host.Bnd.bndBeingMadePts.Count > 0)
                            ? DecimateVec3(_host.Bnd.bndBeingMadePts, 0.0)
                            : null;

                        if (_host.Bnd.bndList.Count > 0)
                        {
                            double areaM2 = _host.Bnd.bndList[0].area;
                            for (int i = 1; i < _host.Bnd.bndList.Count; i++)
                                areaM2 -= _host.Bnd.bndList[i].area;
                            snap.BoundaryAreaM2 = areaM2;

                            // Area neta negativa = hay linderos "internos" mas
                            // grandes que el exterior. Es geometricamente
                            // imposible como isla, y deja el giro en cabecera y
                            // el corte por lindero sin poder funcionar: el
                            // pivote queda "dentro de una isla" en casi todo el
                            // lote. Se detecta aca porque es lo unico que mira
                            // TODOS los linderos juntos.
                            snap.BoundaryGeometryOk = areaM2 > 0;
                        }
                        else snap.BoundaryGeometryOk = true;
                    }

                    if (_host.Trk != null && _host.Trk.gArr != null && _host.Trk.idx >= 0 && _host.Trk.idx < _host.Trk.gArr.Count)
                    {
                        var t = _host.Trk.gArr[_host.Trk.idx];
                        if (t != null)
                        {
                            var ti = new TrackInfo
                            {
                                Name = t.name,
                                Mode = t.mode.ToString(),
                                Heading = t.heading,
                                A = new FieldPoint(t.ptA.easting, t.ptA.northing),
                                B = new FieldPoint(t.ptB.easting, t.ptB.northing),
                                CurvePts = DecimateVec3(t.curvePts, 1.0)
                            };
                            snap.ActiveTrack = ti;
                        }
                    }
                }
                catch { /* no romper snapshot por geometría */ }

                // Sin Properties.Settings headless: sprite default.
                snap.VehicleType = "Tractor";
                snap.VehicleBrand = "AGOpenGPS";
            }
            catch
            {
                // Defensivo: jamás romper a un consumidor por estado parcial.
            }

            return snap;
        }

        // Reduce densidad de polylines: descarta puntos a < minStepM metros del anterior.
        private static List<FieldPoint> DecimateVec3(List<vec3> src, double minStepM)
        {
            var dst = new List<FieldPoint>();
            if (src == null || src.Count == 0) return dst;
            var last = src[0];
            dst.Add(new FieldPoint(last.easting, last.northing));
            double step2 = minStepM * minStepM;
            for (int i = 1; i < src.Count; i++)
            {
                var p = src[i];
                double dx = p.easting - last.easting;
                double dy = p.northing - last.northing;
                if (dx * dx + dy * dy >= step2)
                {
                    dst.Add(new FieldPoint(p.easting, p.northing));
                    last = p;
                }
            }
            return dst;
        }

        // ---- Métodos satélite: sin fuente headless (ventanas WinForms de PilotX
        //      que este proceso no tiene). Snapshots vacíos/seguros. ----

        public AllSettingsSnapshot GetAllSettings() => new AllSettingsSnapshot();

        public EventLogSnapshot GetEventLog()
            => new EventLogSnapshot { File = "", History = "", Session = "" };

        // ---- Gráficos de diagnóstico en vivo ----
        // Ya NO son stubs: los 4 valores viven en el modelo Core que el motor
        // orquesta (CVehicle / CModuleComm / CAHRS / los campos de rumbo del
        // host), así que se leen igual que en FormGpsStateProvider. Son las
        // herramientas para calibrar la dirección: sin esto, las páginas
        // grafico-*.html dibujan una línea plana en cero.

        public XteGraphSample GetXteGraphSample()
        {
            var s = new XteGraphSample();
            try
            {
                if (_host.Vehicle != null)
                {
                    // Mismos valores que graficaba FormGraphXTE.DrawChart().
                    s.HeadingErrorDeg = System.Math.Round(_host.Vehicle.modeActualHeadingError, 1);
                    s.XteCm = System.Math.Round(_host.Vehicle.modeActualXTE * 100.0, 0);
                }
            }
            catch { /* defensivo: 0 si el guiado no está listo */ }
            return s;
        }

        public HeadingGraphSample GetHeadingGraphSample()
        {
            var s = new HeadingGraphSample();
            try
            {
                // Rumbo en radianes → grados, igual que FormGraphHeading.
                s.GpsHeadingDeg = System.Math.Round(glm.toDegrees(_host.gpsHeading), 1);
                s.ImuHeadingDeg = System.Math.Round(glm.toDegrees(_host.imuCorrected), 1);
            }
            catch { /* defensivo: 0 si la fusión de rumbo no está lista */ }
            return s;
        }

        public SteerGraphSample GetSteerGraphSample()
        {
            var s = new SteerGraphSample();
            try
            {
                if (_host.Mc != null)
                {
                    // Unidades de chart (×100) → grados, igual que FormGraphSteer.
                    s.ActualSteerDeg = System.Math.Round(_host.Mc.actualSteerAngleChart * 0.01, 1);
                    s.SetSteerDeg = System.Math.Round(_host.guidanceLineSteerAngle * 0.01, 1);
                }
            }
            catch { /* defensivo: 0 si la dirección no está lista */ }
            return s;
        }

        public CorrectionGraphSample GetCorrectionGraphSample()
        {
            var s = new CorrectionGraphSample();
            try
            {
                // Mismos valores crudos que mostraba FormCorrection (m).
                s.CorrectionDistance = System.Math.Round(_host.correctionDistanceGraph, 3);
                s.UncorrectedEasting = System.Math.Round(_host.uncorrectedEastingGraph, 3);
                if (_host.Pn != null)
                    s.Easting = System.Math.Round(_host.Pn.fix.easting, 3);

                // Roll del IMU: 88888 = sin IMU (mismo criterio que RollInDegrees).
                if (_host.Ahrs != null && _host.Ahrs.imuRoll != 88888)
                {
                    s.RollPresent = true;
                    s.RollDegrees = System.Math.Round(_host.Ahrs.imuRoll, 1);
                }
            }
            catch { /* defensivo: 0 / sin-roll si el GPS/IMU no está listo */ }
            return s;
        }

        public ShiftPosSnapshot GetShiftPos()
        {
            var s = new ShiftPosSnapshot();
            try
            {
                var props = _host.AppModelField?.SharedFieldProperties;
                if (props != null)
                {
                    // GeoDelta es struct: nunca null. Igual que FormShiftPos (m → cm).
                    var d = props.DriftCompensation;
                    s.NorthCm = System.Math.Round(d.NorthingDelta * 100.0, 0);
                    s.EastCm = System.Math.Round(d.EastingDelta * 100.0, 0);
                    // OffsetsOn queda en false: el flag lo togglean los comandos
                    // "offsets_on"/"offsets_off", que todavía no están en el
                    // ExecuteCommand del motor. La deriva (los 2 valores de
                    // arriba) sí es real y es lo que muestra la pantalla.
                    s.OffsetsOn = false;
                }
            }
            catch { /* defensivo: 0/off si el modelo de campo no está listo */ }
            return s;
        }

        public SimCoordsSnapshot GetSimCoords()
        {
            var s = new SimCoordsSnapshot();
            try
            {
                // Mismo origen que FormSimCoords_Load: la lat/lon guardada del sim.
                var cfg = global::AgOpenGPS.Properties.Settings.Default;
                s.Latitude = cfg.setGPS_SimLatitude;
                s.Longitude = cfg.setGPS_SimLongitude;
                s.SimOn = _host.isSimTimerEnabled;
                s.JobStarted = _host.IsJobStarted;
            }
            catch { /* defensivo: snapshot vacío si los settings no cargaron */ }
            return s;
        }

        public SectionColorsSnapshot GetSectionColors()
        {
            var s = new SectionColorsSnapshot();
            var colors = new string[16];
            for (int i = 0; i < 16; i++) colors[i] = "#000000";
            s.Colors = colors;
            return s;
        }

        public DisplayColorsSnapshot GetDisplayColors()
        {
            return new DisplayColorsSnapshot
            {
                FrameDay = "#000000",
                FrameNight = "#000000",
                FieldDay = "#000000",
                FieldNight = "#000000",
                TextDay = "#000000",
                TextNight = "#000000",
            };
        }

        // ---- prescripción (.shp) -------------------------------------------
        //
        // Antes esto eran stubs (dose 0, shape null): QuantiX y FlowX contra el
        // motor dosificaban con CERO y el mapa no tenía qué dibujar. La capa la
        // administra EngineShapeService; acá solo se consulta.

        /// <summary>Lo setea EngineWebHost al armar los servicios: comparten la
        /// MISMA capa que el upload y la carga automática.</summary>
        public EngineShapeService Shape { get; set; }

        public double GetShapeFieldDose(string fieldName)
        {
            try
            {
                var layer = Shape?.Capa;
                if (layer == null || layer.IsEmpty || layer.CurrentPolygonIndex < 0) return 0;
                double v;
                return layer.TryGetPolygonNumeric(layer.CurrentPolygonIndex, fieldName, out v) ? v : 0;
            }
            catch { return 0; }
        }

        // El snapshot del shape se CACHEA por instancia de capa: el poller del
        // mapa pega cada 1 s, y armar ExportPolygonsLocal (todas las listas de
        // vértices) en cada GET era CPU constante — y parte de los segundos que
        // tardaba el shape en aparecer al abrir el lote.
        private AgroParallel.Common.ShapefileLayer _shapeSnapCapa;
        private ShapeSnapshot _shapeSnapCache;
        // Origen del plano local con el que se proyectó el snapshot cacheado.
        // Sin esto, al abrir otro lote la capa se reproyecta (EnsureProjected
        // compara el origen y rehace las coordenadas) pero el snapshot seguía
        // siendo el viejo: el mapa dibujaba las zonas corridas justo la
        // diferencia entre los dos orígenes.
        private double _shapeSnapOriLat = double.NaN;
        private double _shapeSnapOriLon = double.NaN;

        public ShapeSnapshot GetShape()
        {
            try
            {
                var layer = Shape?.Capa;
                if (layer == null || layer.IsEmpty) { _shapeSnapCapa = null; _shapeSnapCache = null; return null; }

                // SIN LOTE ABIERTO no hay shape que mostrar: las coordenadas de la
                // capa se calculan contra el origen del lote, así que sin lote no
                // hay contra qué referirlas. Aparecía dibujada sobre un mapa vacío
                // y después, al abrir el lote, saltaba a su lugar.
                if (string.IsNullOrEmpty(_host?.currentFieldDirectory))
                { _shapeSnapCache = null; return null; }

                var plano = _host.AppModelField.LocalPlane;
                var ori = plano.Origin;
                if (ReferenceEquals(layer, _shapeSnapCapa) && _shapeSnapCache != null
                    && ori.Latitude == _shapeSnapOriLat && ori.Longitude == _shapeSnapOriLon)
                    return _shapeSnapCache;

                layer.EnsureProjected(plano);
                var polys = layer.ExportPolygonsLocal();
                if (polys == null) return null;

                _shapeSnapCapa = layer;
                _shapeSnapOriLat = ori.Latitude;
                _shapeSnapOriLon = ori.Longitude;
                _shapeSnapCache = new ShapeSnapshot
                {
                    SourceToken = layer.Source ?? string.Empty,
                    // Identifica la proyección: cambia sola al abrir otro lote.
                    GeomRev = ori.Latitude.ToString("F7", System.Globalization.CultureInfo.InvariantCulture)
                        + "," + ori.Longitude.ToString("F7", System.Globalization.CultureInfo.InvariantCulture),
                    Count = polys.Count,
                    StyleField = layer.StyleField,
                    StyleMin = layer.StyleMin,
                    StyleMax = layer.StyleMax,
                    Polygons = polys,
                };
                return _shapeSnapCache;
            }
            catch { return null; }
        }

        public ShapeFieldsSnapshot GetShapeFields()
        {
            var snap = new ShapeFieldsSnapshot();
            try
            {
                var layer = Shape?.Capa;
                if (layer == null || layer.IsEmpty) return snap;
                snap.SourceToken = layer.Source ?? string.Empty;
                var names = layer.FieldNames;
                if (names == null) return snap;
                for (int i = 0; i < names.Count; i++)
                {
                    var fi = new ShapeFieldInfo { Name = names[i] };
                    double min, max;
                    int count;
                    if (layer.TryGetFieldStats(names[i], out min, out max, out count))
                    {
                        fi.Numeric = true;
                        fi.Min = min;
                        fi.Max = max;
                        fi.Count = count;
                    }
                    snap.Fields.Add(fi);
                }
            }
            catch { }
            return snap;
        }
    }
}
