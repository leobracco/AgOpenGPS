// ============================================================================
// FormGps.SimHost.cs
// Implementación de ISimHost sobre FormGPS. CSim vive en AgOpenGPS.Core y
// consume el host a través de esta interfaz (inversión de dependencia —
// traspaso de portabilidad 2026-07-17).
// ============================================================================

using AgOpenGPS.Core;

namespace AgOpenGPS
{
    public partial class FormGPS : ISimHost
    {
        CModuleComm ISimHost.Mc => mc;
        CAHRS ISimHost.Ahrs => ahrs;
        ApplicationModel ISimHost.AppModel => AppModel;

        double ISimHost.VtgSpeed { set => pn.vtgSpeed = value; }
        void ISimHost.AverageTheSpeed() => pn.AverageTheSpeed();
        double ISimHost.FixNorthing { set => pn.fix.northing = value; }
        double ISimHost.FixEasting { set => pn.fix.easting = value; }
        double ISimHost.HeadingTrueDegrees { set => pn.headingTrue = pn.headingTrueDual = value; }
        double ISimHost.Hdop { set => pn.hdop = value; }
        double ISimHost.Altitude { set => pn.altitude = value; }
        int ISimHost.SatellitesTracked { set => pn.satellitesTracked = value; }
        int ISimHost.SentenceCounter { set => sentenceCounter = (uint)value; }
        void ISimHost.UpdateFixPosition() => UpdateFixPosition();
    }
}
