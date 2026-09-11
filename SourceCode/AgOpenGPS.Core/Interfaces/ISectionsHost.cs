namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CSectionCalculator (SectionSetPosition/SectionCalcWidths/
    /// SectionCalcMulti/BuildMachineByte, extraídos de Sections.Designer.cs)
    /// necesita del host (FormGPS en WinForms). Inversión de dependencia para
    /// el traspaso de portabilidad (bloque 9 matriz Android, 2026-07-19).
    /// Estos cuatro métodos son cálculo/protocolo puro, sin GL ni UI. El
    /// resto de Sections.Designer.cs son handlers de botones WinForms
    /// (bloque 10) y la decisión de on/off automático de cada sección, que
    /// vive junto al draw en OpenGL.Designer.cs — se solapa con el bloque 6
    /// (render) y no se toca en esta extracción.
    /// </summary>
    public interface ISectionsHost
    {
        CTool Tool { get; }
        CSection[] Section { get; }

        /// <summary>MAXSECTIONS — tamaño fijo del array de secciones (64).</summary>
        int MaxSections { get; }

        double AvgSpeed { get; }
        CTram Tram { get; }

        CPGN_FE P254 { get; }
        CPGN_EF P239 { get; }
        CPGN_E5 P229 { get; }
    }
}
