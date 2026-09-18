namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CISOBUS necesita del host (FormGPS en WinForms): el envío de
    /// PGN al loop UDP y el botón de section control ISOBUS de la UI.
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// </summary>
    public interface IIsobusHost
    {
        /// <summary>SendPgnToLoop — envía un PGN al loop UDP (CoreX).</summary>
        void SendPgnToLoop(byte[] data);

        /// <summary>btnIsobusSectionControl.Image — imagen on/off del botón.</summary>
        bool IsobusSectionControlImageOn { set; }

        /// <summary>btnIsobusSectionControl.Visible.</summary>
        bool IsobusButtonVisible { set; }
    }
}
