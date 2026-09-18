using System.Collections.Generic;

namespace AgOpenGPS
{
    /// <summary>
    /// Lo mínimo que CPatches necesita del host (FormGPS en WinForms).
    /// Inversión de dependencia para el traspaso de portabilidad (2026-07-17).
    /// CFieldData/CSection ya viven en Core y se exponen directos; los colores
    /// (System.Drawing.Color queda en GPS) se exponen como vec3 RGB 0..255.
    /// </summary>
    public interface IPatchesHost
    {
        CFieldData Fd { get; }
        CSection[] Section { get; }

        /// <summary>tool.isMultiColoredSections.</summary>
        bool ToolIsMultiColoredSections { get; }

        /// <summary>tool.isSectionsNotZones.</summary>
        bool ToolIsSectionsNotZones { get; }

        /// <summary>sectionColorDay como vec3 (R,G,B en 0..255).</summary>
        vec3 SectionColorDayVec { get; }

        /// <summary>tool.secColors[j] como vec3 (R,G,B en 0..255).</summary>
        vec3 SecColorVec(int j);

        /// <summary>patchCounter++ (contador de patches para el render).</summary>
        void IncrementPatchCounter();

        /// <summary>patchSaveList — patches cerrados pendientes de guardar a disco.</summary>
        List<List<vec3>> PatchSaveList { get; }

        /// <summary>
        /// La barra acaba de pintar el cuadrilatero (izq/der anterior, izq/der
        /// actual). El host lo marca en su grilla de cobertura para llevar el
        /// area NETA (Fd.actualAreaCovered), que en el AOG original salia de
        /// contar pixeles en OpenGL y en el motor headless no existia.
        /// </summary>
        void QuadPintado(vec3 izqAnt, vec3 derAnt, vec3 izq, vec3 der);
    }
}
