// ============================================================================
// FormGpsStateProvider.cs
// Ubicación: SourceCode/GPS/AgroParallel/Common/FormGpsStateProvider.cs
// Target: net48
//
// Implementación de IAogStateProvider que vive del lado AOG (único proyecto
// que puede 'using AgOpenGPS'). Toma una referencia a FormGPS por constructor
// y expone una ventana read-only via AogStateSnapshot — los servicios
// AgroParallel (QuantiX/SectionX/OrbitX) leen el estado de AOG por este
// adaptador en lugar de aferrarse a FormGPS directamente.
//
// Fase A · linchpin del decoupling. Después de esta clase, los bridges pueden
// migrar a netstandard2.0 (AgroParallel.Services) sin perder funcionalidad.
// ============================================================================

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
                snap.AvgSpeed = _form.avgSpeed;
                snap.Heading = _form.pivotAxlePos.heading;
                snap.PivotEasting = _form.pivotAxlePos.easting;
                snap.PivotNorthing = _form.pivotAxlePos.northing;

                if (_form.AppModel != null)
                {
                    snap.Latitude = _form.AppModel.CurrentLatLon.Latitude;
                    snap.Longitude = _form.AppModel.CurrentLatLon.Longitude;
                }

                if (_form.tool != null)
                {
                    int n = _form.tool.numOfSections;
                    snap.NumSections = n;
                    snap.ToolWidth = _form.tool.width;
                    if (n > 0 && _form.section != null)
                    {
                        var arr = new bool[n];
                        for (int i = 0; i < n && i < _form.section.Length; i++)
                        {
                            var sec = _form.section[i];
                            arr[i] = sec != null && sec.sectionOnRequest;
                        }
                        snap.SectionOnRequest = arr;
                    }
                }

                var layer = _form.ShapefileLayerForAdapters;
                if (layer != null)
                {
                    snap.ShapeCurrentDose = layer.CurrentDose;
                    snap.ShapeIsInside = layer.CurrentInside;
                }

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
    }
}
