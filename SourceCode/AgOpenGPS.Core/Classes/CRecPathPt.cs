using AgOpenGPS.Core.Models;

// Punto de path grabado (extraido de GPS/Classes/CRecordedPath.cs, traspaso
// portabilidad 2026-07-17): POCO sin FormGPS ni GL, usado por IO/RecPathFiles.
namespace AgOpenGPS
{
    public class CRecPathPt
    {
        public double easting { get; set; }
        public double northing { get; set; }
        public double heading { get; set; }
        public double speed { get; set; }
        public bool autoBtnState { get; set; }

        //constructor
        public CRecPathPt(double _easting, double _northing, double _heading, double _speed,
                            bool _autoBtnState)
        {
            easting = _easting;
            northing = _northing;
            heading = _heading;
            speed = _speed;
            autoBtnState = _autoBtnState;
        }

        public GeoCoord AsGeoCoord => new GeoCoord(northing, easting);
    }
}
