// SectionColorsSnapshot — estado para la pantalla de colores de secciones de
// PilotX (reemplazo HTML del WinForms FormColorSection). Expone los 16 colores de
// sección (hex "#RRGGBB") y si el modo multicolor está activo. Editar y aplicar
// escribe los colores + el flag multicolor vía POST /api/aog/guidance/command
// (sec_colors_<hex1>_..._<hex16>_<0|1>).

namespace AgroParallel.Models
{
    public sealed class SectionColorsSnapshot
    {
        /// <summary>Los 16 colores de sección en formato hex "#RRGGBB".</summary>
        public string[] Colors { get; set; }

        /// <summary>true si el modo multicolor de secciones está activo.</summary>
        public bool MultiColor { get; set; }
    }
}
