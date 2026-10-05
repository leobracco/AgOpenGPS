// AllSettingsSnapshot — DTO read-only del volcado "Todos los ajustes" de
// PilotX (reemplazo HTML de la vieja ventana WinForms FormAllSettings).
// Producido por IAogStateProvider.GetAllSettings(): agrupa los settings
// estáticos + la telemetría en vivo como pares etiqueta/valor ya formateados
// del lado PilotX, para que la página sea un renderer genérico sin lógica.

using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>Una fila del volcado: etiqueta legible + valor ya formateado.</summary>
    public sealed class SettingRow
    {
        public string Label { get; set; }
        public string Value { get; set; }
        public SettingRow() { }
        public SettingRow(string label, string value)
        {
            Label = label;
            Value = value;
        }
    }

    /// <summary>Grupo de filas bajo un título (Dirección, Vehículo, GPS, etc.).</summary>
    public sealed class SettingGroup
    {
        public string Title { get; set; }
        public List<SettingRow> Rows { get; set; } = new List<SettingRow>();

        public SettingGroup() { }
        public SettingGroup(string title)
        {
            Title = title;
        }

        public void Add(string label, string value)
        {
            Rows.Add(new SettingRow(label, value));
        }
    }

    /// <summary>
    /// Volcado completo de ajustes + telemetría. <see cref="Groups"/> son los
    /// settings estáticos (config del vehículo/implemento/GPS); <see cref="Live"/>
    /// es la telemetría que refresca a 1 Hz.
    /// </summary>
    public sealed class AllSettingsSnapshot
    {
        /// <summary>Versión de PilotX (SemVer), para mostrar en el encabezado.</summary>
        public string SemVer { get; set; }

        /// <summary>Ruta del perfil de vehículo activo.</summary>
        public string VehicleFile { get; set; }

        /// <summary>Grupos de ajustes estáticos.</summary>
        public List<SettingGroup> Groups { get; set; } = new List<SettingGroup>();

        /// <summary>Grupos de telemetría en vivo (refrescan cada segundo).</summary>
        public List<SettingGroup> Live { get; set; } = new List<SettingGroup>();
    }
}
