namespace AgIO
{
    /// <summary>
    /// Lo que CNmeaParser necesita del host (FormLoop en WinForms). Inversión
    /// de dependencia para el traspaso de portabilidad (2026-07-17): el parsing
    /// NMEA queda en una clase sin UI y el host provee flags de captura,
    /// log monitor y el envío del PGN 0xD6 resultante.
    /// </summary>
    public interface INmeaParserHost
    {
        /// <summary>isGPSSentencesOn — capturar la última sentencia cruda de cada tipo.</summary>
        bool IsGpsSentencesOn { get; }

        /// <summary>isLogMonitorOn — monitor serie/UDP activo.</summary>
        bool IsLogMonitorOn { get; }

        /// <summary>logMonitorSentence.Append — volcar texto crudo al monitor.</summary>
        void AppendLogMonitor(string text);

        /// <summary>
        /// Enviar el PGN NMEA (0xD6) armado: loopback a PilotX y, si corresponde,
        /// UDP al módulo de autosteer (decisión del host).
        /// </summary>
        void SendNmeaPgn(byte[] pgn);
    }
}
