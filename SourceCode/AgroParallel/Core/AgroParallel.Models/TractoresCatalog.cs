// ============================================================================
// TractoresCatalog.cs
// Catálogo estático de tractores comunes en Argentina. NO es una sección
// nueva: alimenta el selector Marca → Modelo dentro de Config → Vehículo
// (pages/config.html, pestaña "Tipo y marca") y lo único que hace es
// pre-llenar las medidas del PERFIL ACTIVO por los mismos endpoints de
// guardado de siempre (POST /aog/config/vehiculo · /dimensiones · /antena,
// bodies parciales). El perfil sigue siendo el de la base original.
//
// Las medidas son de folleto/ficha técnica (nivel catálogo, ±5 cm): sirven
// como punto de partida útil; el operario las verifica en el fierro. Donde
// no hay dato confiable va 0 = "sin dato, no tocar lo que hay".
//
// Igual que SembradorasCatalog/MaquinasCatalog: solo lectura, modelos
// curados; para agregar, editar este archivo.
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Plantilla de un modelo conocido de tractor.</summary>
    public sealed class TractorTemplate
    {
        [JsonPropertyName("marca")] public string Marca { get; set; } = "";
        [JsonPropertyName("modelo")] public string Modelo { get; set; } = "";

        /// <summary>0 = tractor (dirección delantera) · 2 = articulado.
        /// Mismo espacio que ConfigVehiculoSec.VehicleType.</summary>
        [JsonPropertyName("vehicle_type")] public int VehicleType { get; set; }

        /// <summary>Distancia entre ejes (m). En articulados es el total
        /// eje-a-eje: la posición del pivote se mide en el fierro.</summary>
        [JsonPropertyName("wheelbase_m")] public double WheelbaseM { get; set; }

        /// <summary>Trocha (m). 0 = sin dato (la trocha es ajustable en casi
        /// todos: se mide en el fierro, no se pisa lo configurado).</summary>
        [JsonPropertyName("track_width_m")] public double TrackWidthM { get; set; }

        /// <summary>Altura de antena sugerida sobre el techo de cabina (m).
        /// 0 = sin dato (depende del montaje).</summary>
        [JsonPropertyName("antenna_height_m")] public double AntennaHeightM { get; set; }

        [JsonPropertyName("descripcion")] public string Descripcion { get; set; } = "";
    }

    /// <summary>Bundle Marca → modelos para el select en cascada.</summary>
    public sealed class TractorMarcaDto
    {
        [JsonPropertyName("marca")] public string Marca { get; set; } = "";
        [JsonPropertyName("modelos")] public List<TractorTemplate> Modelos { get; set; }
            = new List<TractorTemplate>();
    }

    public static class TractoresCatalog
    {
        private static TractorTemplate T(string marca, string modelo, double wheelbase,
                                         int tipo = 0, double antena = 0, string desc = "")
        {
            return new TractorTemplate
            {
                Marca = marca,
                Modelo = modelo,
                VehicleType = tipo,
                WheelbaseM = wheelbase,
                AntennaHeightM = antena,
                Descripcion = desc,
            };
        }

        // Medidas de folleto — verificar en el fierro antes de salir al lote.
        private static readonly TractorTemplate[] _tractores = new[]
        {
            // ----- mecánica delantera (VehicleType 0) -----
            T("John Deere", "5090E",  2.05),
            T("John Deere", "6130J",  2.77),
            T("John Deere", "6145J",  2.77),
            T("John Deere", "6170J",  2.80),
            T("John Deere", "7230J",  2.93),
            T("New Holland", "T6.130", 2.64),
            T("New Holland", "T7040",  2.88),
            T("New Holland", "T7.240", 2.88),
            T("Case IH", "Farmall 120A", 2.42),
            T("Case IH", "Puma 185",     2.88),
            T("Case IH", "Puma 215",     2.88),
            T("Massey Ferguson", "MF 4707", 2.55),
            T("Massey Ferguson", "MF 6713", 2.67),
            T("Massey Ferguson", "MF 7719", 2.99),
            T("Valtra", "A134", 2.54),
            T("Valtra", "T190", 2.66),
            T("Deutz-Fahr", "Agrotron 6125", 2.77),

            // ----- articulados (VehicleType 2) -----
            // El "entre ejes" es eje a eje; la distancia del pivote al eje
            // trasero se mide en el fierro (config del articulado).
            T("Pauny", "250A", 2.90, tipo: 2, desc: "Articulado — medir pivote en el fierro."),
            T("Pauny", "280A", 3.05, tipo: 2, desc: "Articulado — medir pivote en el fierro."),
            T("Pauny", "540",  3.30, tipo: 2, desc: "Articulado — medir pivote en el fierro."),
            T("Pauny", "580",  3.44, tipo: 2, desc: "Articulado — medir pivote en el fierro."),
            T("Zanello (legado)", "417", 3.00, tipo: 2, desc: "Articulado — medir pivote en el fierro."),
            T("Zanello (legado)", "450", 3.20, tipo: 2, desc: "Articulado — medir pivote en el fierro."),
        };

        /// <summary>Marcas con sus modelos, para el select en cascada.</summary>
        public static List<TractorMarcaDto> Marcas()
        {
            return _tractores
                .GroupBy(t => t.Marca)
                .Select(g => new TractorMarcaDto { Marca = g.Key, Modelos = g.ToList() })
                .ToList();
        }

        /// <summary>Template exacto o null.</summary>
        public static TractorTemplate Buscar(string marca, string modelo)
        {
            return _tractores.FirstOrDefault(t =>
                string.Equals(t.Marca, marca, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Modelo, modelo, System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
