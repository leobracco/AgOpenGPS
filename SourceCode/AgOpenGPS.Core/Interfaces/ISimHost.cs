using AgOpenGPS.Core;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CSim necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// CModuleComm/CAHRS/ApplicationModel ya viven en Core y se exponen
    /// directo; de CNMEA (todavía en GPS) solo se exponen los campos que el
    /// simulador escribe.
    /// </summary>
    public interface ISimHost
    {
        CModuleComm Mc { get; }
        CAHRS Ahrs { get; }
        ApplicationModel AppModel { get; }

        /// <summary>pn.vtgSpeed — velocidad simulada (km/h).</summary>
        double VtgSpeed { set; }

        /// <summary>pn.AverageTheSpeed().</summary>
        void AverageTheSpeed();

        /// <summary>pn.fix.northing.</summary>
        double FixNorthing { set; }

        /// <summary>pn.fix.easting.</summary>
        double FixEasting { set; }

        /// <summary>pn.headingTrue y pn.headingTrueDual (grados).</summary>
        double HeadingTrueDegrees { set; }

        /// <summary>pn.hdop.</summary>
        double Hdop { set; }

        /// <summary>pn.altitude.</summary>
        double Altitude { set; }

        /// <summary>pn.satellitesTracked.</summary>
        int SatellitesTracked { set; }

        /// <summary>Contador de watchdog de sentencias GPS (0 = dato fresco).</summary>
        int SentenceCounter { set; }

        /// <summary>Pipeline principal de posición del host.</summary>
        void UpdateFixPosition();
    }
}
