// ============================================================================
// FormGpsStateProvider.cs
// Ubicación: SourceCode/GPS/AgroParallel/Common/FormGpsStateProvider.cs
// Target: net48
//
// Implementación de IAogStateProvider que vive del lado PilotX (único proyecto
// que puede 'using AgOpenGPS'). Toma una referencia a FormGPS por constructor
// y expone una ventana read-only via AogStateSnapshot — los servicios
// AgroParallel (QuantiX/SectionX/OrbitX) leen el estado de PilotX por este
// adaptador en lugar de aferrarse a FormGPS directamente.
//
// Fase A · linchpin del decoupling. Después de esta clase, los bridges pueden
// migrar a netstandard2.0 (AgroParallel.Services) sin perder funcionalidad.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    /// <summary>
    /// Adapta el estado runtime de FormGPS a un snapshot inmutable consumible
    /// desde servicios netstandard2.0 (sin dependencia de WinForms).
    /// </summary>
    public sealed class FormGpsStateProvider : IAogStateProvider
    {
        private readonly FormGPS _form;

        public FormGpsStateProvider(FormGPS form)
        {
            _form = form;
        }

        public AogStateSnapshot GetSnapshot()
        {
            var snap = new AogStateSnapshot();
            if (_form == null) return snap;

            try
            {
                snap.IsJobStarted = _form.isJobStarted;
                snap.CurrentFieldDirectory = _form.currentFieldDirectory;
                snap.FieldsDirectory = RegistrySettings.fieldsDirectory;
                snap.AvgSpeed = _form.avgSpeed;
                snap.FixQuality = _form.pn != null ? _form.pn.fixQuality : 0;
                snap.PowerOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                    == System.Windows.Forms.PowerLineStatus.Online;
                snap.Heading = _form.pivotAxlePos.heading;
                snap.PivotEasting = _form.pivotAxlePos.easting;
                snap.PivotNorthing = _form.pivotAxlePos.northing;

                if (_form.AppModel != null)
                {
                    snap.Latitude = _form.AppModel.CurrentLatLon.Latitude;
                    snap.Longitude = _form.AppModel.CurrentLatLon.Longitude;
                }

                // Barra derecha HTML: estados de los botones de operación
                // (mismas variables que pinta GUI.Designer.cs / Sections.Designer.cs).
                snap.IsAutoSteerOn = _form.isBtnAutoSteerOn;
                snap.IsSectionAutoOn = _form.autoBtnState == btnStates.Auto;
                snap.IsSectionManualOn = _form.manualBtnState == btnStates.On;
                if (_form.trk != null)
                {
                    snap.IsAutoSnapToPivot = _form.trk.isAutoSnapToPivot;
                    snap.IsAutoTrackOn = _form.trk.isAutoTrack;
                    snap.TrackIdx = _form.trk.idx;
                    if (_form.trk.gArr != null)
                    {
                        snap.TracksTotal = _form.trk.gArr.Count;
                        int vis = 0;
                        for (int i = 0; i < _form.trk.gArr.Count; i++)
                            if (_form.trk.gArr[i].isVisible) vis++;
                        snap.TracksVisible = vis;
                    }
                }
                if (_form.yt != null) snap.IsYouTurnOn = _form.yt.isYouTurnBtnOn;
                if (_form.ct != null)
                {
                    snap.IsContourOn = _form.ct.isContourBtnOn;
                    snap.IsContourLocked = _form.ct.isLocked;
                }
                if (_form.isobus != null)
                {
                    snap.IsobusAlive = _form.isobus.IsAlive();
                    snap.IsobusOn = _form.isobus.SectionControlEnabled;
                }
                snap.HasBoundary = _form.bnd != null && _form.bnd.bndList != null
                    && _form.bnd.bndList.Count > 0;

                // Barra abajo HTML: estados del panelBottom nativo.
                snap.FlagColor = _form.flagColor;
                snap.IsNudgeOn = _form.isNudgeOn;
                if (_form.bnd != null)
                {
                    snap.HasHeadland = _form.bnd.bndList != null && _form.bnd.bndList.Count > 0
                        && _form.bnd.bndList[0].hdLine != null && _form.bnd.bndList[0].hdLine.Count > 0;
                    snap.IsHeadlandOn = _form.bnd.isHeadlandOn;
                    snap.IsSectionControlledByHeadland = _form.bnd.isSectionControlledByHeadland;
                }
                snap.HasHydLift = (AgOpenGPS.Properties.Settings.Default.setArdMac_setting0 & 2) == 2;
                if (_form.vehicle != null) snap.IsHydLiftOn = _form.vehicle.isHydLiftOn;
                if (_form.tram != null)
                {
                    snap.HasTram = (_form.tram.tramList != null ? _form.tram.tramList.Count : 0)
                        + (_form.tram.tramBndOuterArr != null ? _form.tram.tramBndOuterArr.Count : 0) > 0;
                    snap.TramDisplayMode = (int)_form.tram.displayMode;
                }
                if (_form.yt != null)
                {
                    snap.YouSkipMode = (int)_form.yt.skipMode;
                    snap.RowSkipsWidth = _form.yt.rowSkipsWidth;
                }

                snap.ToolEasting = _form.toolPos.easting;
                snap.ToolNorthing = _form.toolPos.northing;
                snap.ToolHeading = _form.toolPos.heading;

                if (_form.fd != null)
                {
                    snap.WorkedAreaTotalM2 = _form.fd.workedAreaTotal;
                    snap.ActualAreaCoveredM2 = _form.fd.actualAreaCovered;
                }

                if (_form.tool != null)
                {
                    int n = _form.tool.numOfSections;
                    snap.NumSections = n;
                    snap.ToolWidth = _form.tool.width;
                    snap.ToolOffset = _form.tool.offset;
                    if (n > 0 && _form.section != null)
                    {
                        var arr = new bool[n];
                        var pos = new List<SectionExtent>(n);
                        var speeds = new double[n];
                        for (int i = 0; i < n && i < _form.section.Length; i++)
                        {
                            var sec = _form.section[i];
                            arr[i] = sec != null && sec.sectionOnRequest;
                            if (sec != null)
                            {
                                pos.Add(new SectionExtent(i, sec.positionLeft, sec.positionRight));
                                // speedPixels = m/s * 10 (10 px = 1 m). *0.36 → km/h, signo preservado.
                                speeds[i] = sec.speedPixels * 0.36;
                            }
                        }
                        snap.SectionOnRequest = arr;
                        snap.SectionPositions = pos;
                        snap.SectionSpeedsKmh = speeds;
                    }
                    // Endpoints del implemento (ya en m/s, filtrados en PilotX).
                    snap.ToolFarLeftSpeedKmh = _form.tool.farLeftSpeed * 3.6;
                    snap.ToolFarRightSpeedKmh = _form.tool.farRightSpeed * 3.6;
                }

                var layer = _form.ShapefileLayerForAdapters;
                if (layer != null)
                {
                    snap.ShapeCurrentDose = layer.CurrentDose;
                    snap.ShapeIsInside = layer.CurrentInside;
                }

                // Geometría del lote: boundary + headland + track activo.
                // Se decimar a fpStep metros para que el JSON sea liviano.
                try
                {
                    if (_form.bnd != null && _form.bnd.bndList != null)
                    {
                        var bnds = new List<List<FieldPoint>>();
                        var hdls = new List<List<FieldPoint>>();
                        foreach (var b in _form.bnd.bndList)
                        {
                            if (b == null) continue;
                            bnds.Add(DecimateVec3(b.fenceLine, 0.5));
                            hdls.Add(DecimateVec3(b.hdLine, 0.5));
                        }
                        snap.Boundaries = bnds;
                        snap.Headlands = hdls;

                        // Área del lote por lindero: boundary[0] exterior menos
                        // los internos (exclusiones). Misma lógica que
                        // CFieldData.UpdateFieldBoundaryGUIAreas, computada acá
                        // para no depender de cuándo AOG la refrescó. m² → ha = ×1e-4.
                        if (_form.bnd.bndList.Count > 0)
                        {
                            double areaM2 = _form.bnd.bndList[0].area;
                            for (int i = 1; i < _form.bnd.bndList.Count; i++)
                                areaM2 -= _form.bnd.bndList[i].area;
                            snap.BoundaryAreaM2 = areaM2;
                        }
                    }

                    if (_form.trk != null && _form.trk.gArr != null && _form.trk.idx >= 0 && _form.trk.idx < _form.trk.gArr.Count)
                    {
                        var t = _form.trk.gArr[_form.trk.idx];
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

                // Vehículo seleccionado en Settings (tipo + marca) — para que
                // el mapa del Piloto (WebView2) renderice el sprite correcto.
                try
                {
                    int vt = AgOpenGPS.Properties.Settings.Default.setVehicle_vehicleType;
                    switch (vt)
                    {
                        case 0: snap.VehicleType = "Tractor"; snap.VehicleBrand = AgOpenGPS.Properties.Settings.Default.setBrand_TBrand.ToString(); break;
                        case 1: snap.VehicleType = "Harvester"; snap.VehicleBrand = AgOpenGPS.Properties.Settings.Default.setBrand_HBrand.ToString(); break;
                        case 2: snap.VehicleType = "Articulated"; snap.VehicleBrand = AgOpenGPS.Properties.Settings.Default.setBrand_WDBrand.ToString(); break;
                        default: snap.VehicleType = "Tractor"; snap.VehicleBrand = "AGOpenGPS"; break;
                    }
                }
                catch { snap.VehicleType = "Tractor"; snap.VehicleBrand = "AGOpenGPS"; }
            }
            catch
            {
                // Defensivo: jamás romper a un service por estado parcial de FormGPS.
            }

            return snap;
        }

        // Reduce densidad de polylines: descarta puntos a < minStepM metros del anterior.
        private static List<FieldPoint> DecimateVec3(System.Collections.Generic.List<vec3> src, double minStepM)
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

        public ShapeSnapshot GetShape()
        {
            if (_form == null) return null;
            try
            {
                var layer = _form.ShapefileLayerForAdapters;
                if (layer == null || layer.IsEmpty) return null;

                var polys = layer.ExportPolygonsLocal();
                if (polys == null) return null; // todavía no proyectado

                return new ShapeSnapshot
                {
                    SourceToken = layer.Source ?? string.Empty,
                    Count = polys.Count,
                    StyleField = layer.StyleField,
                    StyleMin = layer.StyleMin,
                    StyleMax = layer.StyleMax,
                    Polygons = polys
                };
            }
            catch
            {
                return null;
            }
        }

        public double GetShapeFieldDose(string fieldName)
        {
            if (_form == null || string.IsNullOrEmpty(fieldName)) return 0;
            try
            {
                var layer = _form.ShapefileLayerForAdapters;
                if (layer == null) return 0;
                int polyIdx = layer.CurrentPolygonIndex;
                if (polyIdx < 0) return 0;
                double val;
                if (layer.TryGetPolygonNumeric(polyIdx, fieldName, out val))
                    return val;
            }
            catch { }
            return 0;
        }

        public ShapeFieldsSnapshot GetShapeFields()
        {
            var snap = new ShapeFieldsSnapshot();
            if (_form == null) return snap;
            try
            {
                var layer = _form.ShapefileLayerForAdapters;
                if (layer == null || layer.IsEmpty) return snap;
                snap.SourceToken = layer.Source ?? string.Empty;
                var names = layer.FieldNames;
                if (names == null) return snap;
                for (int i = 0; i < names.Count; i++)
                {
                    var name = names[i];
                    var fi = new ShapeFieldInfo { Name = name };
                    double min, max; int count;
                    if (layer.TryGetFieldStats(name, out min, out max, out count))
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
