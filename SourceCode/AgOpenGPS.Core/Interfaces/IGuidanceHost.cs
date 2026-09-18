namespace AgOpenGPS
{
    /// <summary>
    /// Lo que CGuidance (Stanley) necesita del host (FormGPS en WinForms).
    /// Hereda de IABLineHost (Vehicle/Ahrs/escalares de guiado) y agrega las
    /// dos líneas activas, ya en Core. Inversión de dependencia para el
    /// traspaso de portabilidad (2026-07-17).
    /// </summary>
    public interface IGuidanceHost : IABLineHost
    {
        /// <summary>CABLine (ya en Core) — pivote/heading de la recta activa.</summary>
        CABLine ABLine { get; }

        /// <summary>CABCurve (ya en Core) — pivote/heading de la curva activa.</summary>
        CABCurve Curve { get; }
    }
}
