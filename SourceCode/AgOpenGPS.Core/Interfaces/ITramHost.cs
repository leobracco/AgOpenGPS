using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CTram necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// </summary>
    public interface ITramHost
    {
        /// <summary>Ancho de labor del implemento (m).</summary>
        double ToolWidth { get; }

        /// <summary>Distancia de cámara (negativa; umbral de grosor de línea GL).</summary>
        double CamSetDistance { get; }

        /// <summary>Límites del lote: [0] = exterior, resto = interiores.</summary>
        IReadOnlyList<CBoundaryList> BoundaryList { get; }
    }
}
