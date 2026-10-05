using System;

namespace AgOpenGPS.Core.Models
{
    // An instance of LocalPlane defines the origin and the meaning of a local coordinate
    // system that uses Northing and Easting coordinates.
    public class LocalPlane
    {
        private SharedFieldProperties _sharedFieldProperties;
        private double _metersPerDegreeLat;

        public LocalPlane(Wgs84 origin, SharedFieldProperties sharedFieldProperties)
        {
            Origin = origin;
            _sharedFieldProperties = sharedFieldProperties;
            SetMetersPerDegreeLat();
        }

        public Wgs84 Origin { get; }

        public GeoCoord ConvertWgs84ToGeoCoord(Wgs84 latLon)
        {
            return new GeoCoord(
                (latLon.Latitude - Origin.Latitude) * _metersPerDegreeLat,
                (latLon.Longitude - Origin.Longitude) * MetersPerDegreeLon(latLon.Latitude)
            );
        }

        // Posición del GPS en el plano local CON la corrección de deriva
        // (DriftCompensation, la del panel "Corregir posición" y del punto de
        // referencia). Es lo que se usa para el FIX — el tractor se corre sobre
        // la geometría del lote. Geometría (linderos, banderas, KML/ISOXML) va
        // por ConvertWgs84ToGeoCoord, sin deriva. Convención: mapa = gps + deriva.
        public GeoCoord ConvertWgs84ToFixGeoCoord(Wgs84 latLon)
        {
            return ConvertWgs84ToGeoCoord(latLon) + _sharedFieldProperties.DriftCompensation;
        }

        // Geometría pura, inversa exacta de ConvertWgs84ToGeoCoord.
        //
        // Antes sumaba DriftCompensation acá (herencia de upstream, donde la
        // deriva SOLO se aplicaba al PGN de posición corregida y el mapa no se
        // movía). Ahora la deriva entra en el fix (ConvertWgs84ToFixGeoCoord):
        // sumarla también acá la contaría dos veces en el PGN de posición
        // corregida y corría las exportaciones. Hasta este cambio ningún
        // camino de PilotX escribía la deriva (siempre 0), así que quitarla de
        // acá no cambia nada de lo que ya andaba.
        public Wgs84 ConvertGeoCoordToWgs84(GeoCoord geoCoord)
        {
            double lat = Origin.Latitude + (geoCoord.Northing / _metersPerDegreeLat);
            double lon = Origin.Longitude + (geoCoord.Easting / MetersPerDegreeLon(lat));
            return new Wgs84(lat, lon);
        }

        // see https://en.wikipedia.org/wiki/Geographic_coordinate_system#Latitude_and_longitude
        private void SetMetersPerDegreeLat()
        {
            double originLatInRad = Units.DegreesToRadians(Origin.Latitude);
            _metersPerDegreeLat = 111132.92
                - 559.82 * Math.Cos(2.0 * originLatInRad)
                + 1.175 * Math.Cos(4.0 * originLatInRad)
                - 0.0023 * Math.Cos(6.0 * originLatInRad);
            // meters per degree longitude depends on latitude
            // so we must calculate it for each point separately in ConvertWgs84ToGeoCoord and ConvertGeoCoordToWgs84
        }

        private double MetersPerDegreeLon(double lat)
        {
            double latRad = Units.DegreesToRadians(lat);
            return
                111412.84 * Math.Cos(latRad)
                - 93.5 * Math.Cos(3.0 * latRad)
                + 0.118 * Math.Cos(5.0 * latRad);
        }

    }
}