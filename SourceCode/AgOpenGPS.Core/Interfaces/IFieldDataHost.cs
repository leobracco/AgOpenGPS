using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CFieldData necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17):
    /// permite que CFieldData viva en AgOpenGPS.Core sin conocer la UI.
    /// </summary>
    public interface IFieldDataHost
    {
        /// <summary>Velocidad promedio actual (km/h).</summary>
        double AvgSpeed { get; }

        /// <summary>Nombre del lote activo para mostrar.</summary>
        string DisplayFieldName { get; }

        /// <summary>Ancho de labor del implemento (m).</summary>
        double ToolWidth { get; }

        /// <summary>Cantidad de secciones del implemento.</summary>
        int ToolNumOfSections { get; }

        /// <summary>Solape configurado entre pasadas (m).</summary>
        double ToolOverlap { get; }

        /// <summary>Límites del lote: [0] = exterior, resto = interiores.</summary>
        IReadOnlyList<CBoundaryList> BoundaryList { get; }
    }
}
