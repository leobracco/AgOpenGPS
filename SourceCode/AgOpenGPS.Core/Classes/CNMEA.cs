using AgOpenGPS.Core;
using AgOpenGPS.Core.Models;
using System;
using System.Globalization;
using System.Text;

namespace AgOpenGPS
{
    public class CNMEA
    {
        //our current fix
        public vec2 fix = new vec2(0, 0);

        //other GIS Info
        public double altitude, speed, vtgSpeed = float.MaxValue;

        public double headingTrueDual, headingTrue, hdop, age, headingTrueDualOffset;

        public int fixQuality, ageAlarm;
        public int satellitesTracked;

        private readonly INmeaHost mf;

        public CNMEA(INmeaHost f)
        {
            //constructor, grab the main form reference
            mf = f;
            mf.AppModel.LocalPlane = new LocalPlane(new Wgs84(0, 0), mf.AppModel.SharedFieldProperties);
            ageAlarm = Properties.Settings.Default.setGPS_ageAlarm;
        }

        public void AverageTheSpeed()
        {
            //average the speed
            //if (speed > 70) speed = 70;
            mf.AvgSpeed = (mf.AvgSpeed * 0.75) + (speed * 0.25);
        }

        public void DefineLocalPlane(Wgs84 origin, bool setSim)
        {
            mf.AppModel.LocalPlane = new LocalPlane(origin, mf.AppModel.SharedFieldProperties);
            if (setSim && mf.IsSimTimerEnabled)
            {
                mf.AppModel.CurrentLatLon = origin;
                mf.Sim.CurrentLatLon = origin;

                Properties.Settings.Default.setGPS_SimLatitude = mf.AppModel.LocalPlane.Origin.Latitude;
                Properties.Settings.Default.setGPS_SimLongitude = mf.AppModel.LocalPlane.Origin.Longitude;
                Properties.Settings.Default.Save();
            }
            GeoCoord geoCoord = mf.AppModel.LocalPlane.ConvertWgs84ToGeoCoord(mf.AppModel.CurrentLatLon);
            mf.WorldGrid.checkZoomWorldGrid(geoCoord);
        }

    }
}