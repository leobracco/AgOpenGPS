namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CSettingsSender (SendSettings/SendRelaySettingsToMachineModule,
    /// extraídos de FormGPS.cs) necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (bloque 9 matriz
    /// Android, 2026-07-19). Cálculo/armado de PGN puro, sin GL ni UI — el envío
    /// en sí (SendPgnToLoop, socket UDP a CoreX) se queda en FormGPS y cruza como
    /// método, mismo patrón que IIsobusHost.
    /// </summary>
    public interface ISettingsSenderHost
    {
        CPGN_FC P252 { get; }
        CPGN_FB P251 { get; }
        CPGN_EE P238 { get; }
        CPGN_EC P236 { get; }
        CPGN_EB P235 { get; }

        CSection[] Section { get; }
        CTool Tool { get; }

        /// <summary>Envía un PGN al loop UDP (CoreX).</summary>
        void SendPgnToLoop(byte[] data);
    }
}
