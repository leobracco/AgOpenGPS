// DisplayColorsSnapshot — estado para la pantalla de colores de display de PilotX
// (reemplazo HTML del WinForms FormColor). Expone los colores de marco, campo y
// texto para modo día y noche (hex "#RRGGBB"), el suavizado de cámara (0..100) y
// si está en modo día. Aplicar escribe todo por POST /api/aog/guidance/command
// (display_colors_<frameDay>_<frameNight>_<fieldDay>_<fieldNight>_<textDay>_<textNight>_<camSmooth>_<0|1>).

namespace AgroParallel.Models
{
    public sealed class DisplayColorsSnapshot
    {
        /// <summary>Color de marco/fondo en modo día (hex "#RRGGBB").</summary>
        public string FrameDay { get; set; }

        /// <summary>Color de marco/fondo en modo noche (hex "#RRGGBB").</summary>
        public string FrameNight { get; set; }

        /// <summary>Color del campo en modo día (hex "#RRGGBB").</summary>
        public string FieldDay { get; set; }

        /// <summary>Color del campo en modo noche (hex "#RRGGBB").</summary>
        public string FieldNight { get; set; }

        /// <summary>Color del texto en modo día (hex "#RRGGBB").</summary>
        public string TextDay { get; set; }

        /// <summary>Color del texto en modo noche (hex "#RRGGBB").</summary>
        public string TextNight { get; set; }

        /// <summary>Suavizado de la cámara (0..100 %).</summary>
        public int CamSmooth { get; set; }

        /// <summary>true si actualmente está en modo día.</summary>
        public bool IsDay { get; set; }
    }
}
