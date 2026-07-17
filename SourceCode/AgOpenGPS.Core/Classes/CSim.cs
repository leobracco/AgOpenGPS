using AgOpenGPS.Core.Models;
using System;

namespace AgOpenGPS
{
    public class CSim
    {
        // Host invertido (FormGPS implementa ISimHost) — traspaso 2026-07-17
        private readonly ISimHost mf;

        #region properties sim

        public Wgs84 CurrentLatLon { get; set; }

        public double headingTrue, stepDistance = 0.0, steerAngle, steerangleAve = 0.0;
        public double steerAngleScrollBar = 0;

        public bool isAccelForward, isAccelBack;

        #endregion properties sim

        public CSim(ISimHost _f)
        {
            mf = _f;
            CurrentLatLon = new Wgs84(
                Properties.Settings.Default.setGPS_SimLatitude,
                Properties.Settings.Default.setGPS_SimLongitude);
        }

        public void DoSimTick(double _st)
        {
            steerAngle = _st;

            double diff = Math.Abs(steerAngle - steerangleAve);

            if (diff > 11)
            {
                if (steerangleAve >= steerAngle)
                {
                    steerangleAve -= 6;
                }
                else steerangleAve += 6;
            }
            else if (diff > 5)
            {
                if (steerangleAve >= steerAngle)
                {
                    steerangleAve -= 2;
                }
                else steerangleAve += 2;
            }
            else if (diff > 1)
            {
                if (steerangleAve >= steerAngle)
                {
                    steerangleAve -= 0.5;
                }
                else steerangleAve += 0.5;
            }
            else
            {
                steerangleAve = steerAngle;
            }

            mf.Mc.actualSteerAngleDegrees = steerangleAve;

            double temp = stepDistance * Math.Tan(steerangleAve * 0.0165329252) / 2;
            headingTrue += temp;
            if (headingTrue > glm.twoPI) headingTrue -= glm.twoPI;
            if (headingTrue < 0) headingTrue += glm.twoPI;

            mf.VtgSpeed = Math.Abs(Math.Round(4 * stepDistance * 10, 2));
            mf.AverageTheSpeed();

            //Calculate the next Lat Long based on heading and distance
            CurrentLatLon = CurrentLatLon.CalculateNewPostionFromBearingDistance(headingTrue, stepDistance);

            GeoCoord fixCoord = mf.AppModel.LocalPlane.ConvertWgs84ToGeoCoord(CurrentLatLon);
            mf.FixNorthing = fixCoord.Northing;
            mf.FixEasting = fixCoord.Easting;
            double headingDeg = glm.toDegrees(headingTrue);
            mf.HeadingTrueDegrees = headingDeg;
            if (headingDeg >= 360) headingDeg -= 360;
            mf.Ahrs.imuHeading = headingDeg;

            mf.AppModel.CurrentLatLon = CurrentLatLon;

            mf.Hdop = 0.7;

            mf.Altitude = SimulateAltitude(mf.AppModel.CurrentLatLon);

            mf.SatellitesTracked = 12;

            mf.SentenceCounter = 0;

            mf.UpdateFixPosition();

            if (isAccelForward)
            {
                isAccelBack = false;
                stepDistance += 0.02;
                if (stepDistance > 0.12) isAccelForward = false;
            }
            if (isAccelBack)
            {
                isAccelForward = false;
                stepDistance -= 0.01;
                if (stepDistance < -0.06) isAccelBack = false;
            }
        }

        private double SimulateAltitude(Wgs84 latLon)
        {
            double temp = Math.Abs(latLon.Latitude * 100);
            temp -= ((int)(temp));
            temp *= 100;
            double altitude = temp + 200;

            temp = Math.Abs(latLon.Longitude * 100);
            temp -= ((int)(temp));
            temp *= 100;
            altitude += temp;
            return altitude;
        }

    }
}