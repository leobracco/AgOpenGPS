// ============================================================================
// MaquinasCatalog.cs
// Catálogo estático de OTRA maquinaria (no sembradoras): cosechadoras,
// pulverizadoras y fertilizadoras (foco mercado argentino).
//
// El operario selecciona Tipo → Marca → Modelo en herramienta.html y aplica
// la plantilla, que pre-llena en el ImplementoDto:
//   · AnchoTotalM      (cabezal / botalón / ancho de labor del modelo)
//   · Secciones        (numeroSecciones del modelo; cosechadora/sólido = 1)
//   · Categoria/Marca/Modelo
// y limpia la estructura propia de sembradora (surcos/trenes/torres), que no
// aplica a estas máquinas.
//
// Igual que SembradorasCatalog: solo lectura, modelos curados. Para agregar
// modelos, editar este archivo (no hay UI de edición).
//
// Snake_case en JsonPropertyName por consistencia con el resto del Core.
// ============================================================================

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Una plantilla pre-armada para un modelo conocido de máquina.</summary>
    public sealed class MaquinaTemplate
    {
        /// <summary>"cosechadora" | "pulverizadora" | "fertilizadora".</summary>
        [JsonPropertyName("categoria")] public string Categoria { get; set; } = "";
        [JsonPropertyName("marca")] public string Marca { get; set; } = "";
        [JsonPropertyName("modelo")] public string Modelo { get; set; } = "";

        /// <summary>Ancho de labor (m): cabezal en cosechadora, botalón en
        /// pulverizadora, ancho en fertilizadora.</summary>
        [JsonPropertyName("ancho_labor_m")] public double AnchoLaborM { get; set; }

        /// <summary>Cantidad de secciones de corte. 1 si la máquina no
        /// secciona (cosechadora, fertilizadora de disco).</summary>
        [JsonPropertyName("numero_secciones")] public int NumeroSecciones { get; set; } = 1;

        /// <summary>Solo fertilizadoras: "solido" | "liquido".</summary>
        [JsonPropertyName("subtipo")] public string Subtipo { get; set; } = "";

        /// <summary>Solo fertilizadoras: "neumatico" | "doble_disco" | "botalon".</summary>
        [JsonPropertyName("sistema")] public string Sistema { get; set; } = "";

        /// <summary>Texto descriptivo corto para mostrar como ayuda en la UI.</summary>
        [JsonPropertyName("descripcion")] public string Descripcion { get; set; } = "";
    }

    /// <summary>Bundle Marca → modelos para el UI (select cascada).</summary>
    public sealed class MaquinaMarcaDto
    {
        [JsonPropertyName("marca")] public string Marca { get; set; } = "";
        [JsonPropertyName("modelos")] public List<MaquinaTemplate> Modelos { get; set; }
            = new List<MaquinaTemplate>();
    }

    /// <summary>Bundle Tipo (categoría) → marcas para el UI.</summary>
    public sealed class MaquinaTipoDto
    {
        [JsonPropertyName("categoria")] public string Categoria { get; set; } = "";
        [JsonPropertyName("etiqueta")] public string Etiqueta { get; set; } = "";
        [JsonPropertyName("marcas")] public List<MaquinaMarcaDto> Marcas { get; set; }
            = new List<MaquinaMarcaDto>();
    }

    public static class MaquinasCatalog
    {
        // ----- COSECHADORAS (ancho = cabezal, 1 sección) -----
        private static readonly MaquinaTemplate[] _cosechadoras = new[]
        {
            Cosechadora("John Deere", "S550", 7.6),
            Cosechadora("John Deere", "S660", 9.1),
            Cosechadora("John Deere", "S670", 10.7),
            Cosechadora("John Deere", "S760", 10.7),
            Cosechadora("John Deere", "S780", 12.2),
            Cosechadora("John Deere", "X9", 13.7),
            Cosechadora("Case IH", "Axial-Flow 4150", 7.6),
            Cosechadora("Case IH", "Axial-Flow 5150", 9.1),
            Cosechadora("Case IH", "Axial-Flow 7150", 10.7),
            Cosechadora("Case IH", "Axial-Flow 8250", 12.2),
            Cosechadora("Case IH", "Axial-Flow 9250", 13.7),
            Cosechadora("New Holland", "TC5090", 6.1),
            Cosechadora("New Holland", "CR 5.85", 9.1),
            Cosechadora("New Holland", "CR 6.90", 10.7),
            Cosechadora("New Holland", "CR 8.90", 12.2),
            Cosechadora("New Holland", "CR 10.90", 13.7),
            Cosechadora("Claas", "Lexion 760", 10.7),
            Cosechadora("Claas", "Lexion 770", 12.2),
            Cosechadora("Claas", "Tucano", 7.6),
            Cosechadora("Massey Ferguson", "MF 9690", 10.7),
            Cosechadora("Massey Ferguson", "Activa", 6.1),
            Cosechadora("Vassalli", "V700", 9.1),
            Cosechadora("Vassalli", "AX 7500", 9.1),
            Cosechadora("Don Roque", "RV170", 7.6),
            Cosechadora("Don Roque", "RV180", 9.1),
            Cosechadora("Metalfor", "Axial 2635", 9.1),
        };

        // ----- PULVERIZADORAS (ancho = botalón + secciones) -----
        private static readonly MaquinaTemplate[] _pulverizadoras = new[]
        {
            Pulverizadora("Metalfor", "MULTIPLE 2750", 24, 5),
            Pulverizadora("Metalfor", "MULTIPLE 2800", 28, 7),
            Pulverizadora("Metalfor", "MULTIPLE 3025", 30, 7),
            Pulverizadora("Metalfor", "MULTIPLE 3200", 36, 9),
            Pulverizadora("Metalfor", "M 7040", 36, 9),
            Pulverizadora("Metalfor", "Futura 2500", 24, 5),
            Pulverizadora("Pla", "MD 3300", 36, 9),
            Pulverizadora("Pla", "MD 3600", 40, 9),
            Pulverizadora("Pla", "Serie GD", 28, 7),
            Pulverizadora("John Deere", "4730", 27, 7),
            Pulverizadora("John Deere", "M4030", 36, 9),
            Pulverizadora("John Deere", "R4040", 36, 9),
            Pulverizadora("Caimán", "2500", 24, 5),
            Pulverizadora("Caimán", "3000", 30, 7),
            Pulverizadora("Jacto", "Uniport 2530", 28, 7),
            Pulverizadora("Jacto", "Uniport 3030", 36, 9),
            Pulverizadora("Praba", "AR 3.4 S2", 28, 7),
            Pulverizadora("Case IH", "Patriot", 36, 9),
            Pulverizadora("Ombú", "Genérica", 24, 5),
            Pulverizadora("Pulqui", "Genérica", 21, 5),
        };

        // ----- FERTILIZADORAS (ancho + subtipo/sistema; secciones si líquida) -----
        private static readonly MaquinaTemplate[] _fertilizadoras = new[]
        {
            Fertilizadora("Altina", "LSI 4000", "solido", "neumatico", 36, 1),
            Fertilizadora("Altina", "MAF 3600", "solido", "neumatico", 36, 1),
            Fertilizadora("Fertec", "4000", "solido", "doble_disco", 24, 1),
            Fertilizadora("Fertec", "6000", "solido", "doble_disco", 36, 1),
            Fertilizadora("Yomel", "Impala 6000", "solido", "doble_disco", 36, 1),
            Fertilizadora("Yomel", "RDA 1050", "solido", "doble_disco", 18, 1),
            Fertilizadora("Metalfor", "7050", "solido", "doble_disco", 36, 1),
            Fertilizadora("Stara", "Hércules 6.0", "solido", "doble_disco", 36, 1),
            Fertilizadora("Stara", "Hércules 10000 Inox", "solido", "doble_disco", 36, 1),
            Fertilizadora("Pla", "Duplo MAP", "liquido", "botalon", 30, 7),
        };

        private static MaquinaTemplate Cosechadora(string marca, string modelo, double cabezalM)
        {
            return new MaquinaTemplate
            {
                Categoria = "cosechadora",
                Marca = marca,
                Modelo = modelo,
                AnchoLaborM = cabezalM,
                NumeroSecciones = 1,
                Descripcion = "Cabezal " + Fmt(cabezalM) + " m."
            };
        }

        private static MaquinaTemplate Pulverizadora(string marca, string modelo, double botalonM, int secciones)
        {
            return new MaquinaTemplate
            {
                Categoria = "pulverizadora",
                Marca = marca,
                Modelo = modelo,
                AnchoLaborM = botalonM,
                NumeroSecciones = secciones,
                Descripcion = "Botalón " + Fmt(botalonM) + " m · " + secciones + " secciones."
            };
        }

        private static MaquinaTemplate Fertilizadora(string marca, string modelo, string subtipo, string sistema, double anchoM, int secciones)
        {
            string sis = sistema.Replace("_", " ");
            string desc = char.ToUpper(subtipo[0]) + subtipo.Substring(1) + " " + sis + " · " + Fmt(anchoM) + " m";
            if (secciones > 1) desc += " · " + secciones + " secciones";
            desc += ".";
            return new MaquinaTemplate
            {
                Categoria = "fertilizadora",
                Marca = marca,
                Modelo = modelo,
                AnchoLaborM = anchoM,
                NumeroSecciones = secciones,
                Subtipo = subtipo,
                Sistema = sistema,
                Descripcion = desc
            };
        }

        // Formato compacto: "36" si entero, si no "7.6".
        private static string Fmt(double v)
        {
            var inv = CultureInfo.InvariantCulture;
            return (v % 1 == 0) ? ((int)v).ToString() : v.ToString("0.##", inv);
        }

        /// <summary>Todas las plantillas, sin agrupar.</summary>
        public static IEnumerable<MaquinaTemplate> Templates =>
            _cosechadoras.Concat(_pulverizadoras).Concat(_fertilizadoras);

        /// <summary>Agrupa por Tipo → Marca para la UI (select cascada).</summary>
        public static List<MaquinaTipoDto> GroupedByTipo()
        {
            return new List<MaquinaTipoDto>
            {
                Tipo("cosechadora", "Cosechadoras", _cosechadoras),
                Tipo("pulverizadora", "Pulverizadoras", _pulverizadoras),
                Tipo("fertilizadora", "Fertilizadoras", _fertilizadoras),
            };
        }

        private static MaquinaTipoDto Tipo(string categoria, string etiqueta, MaquinaTemplate[] modelos)
        {
            var tipo = new MaquinaTipoDto { Categoria = categoria, Etiqueta = etiqueta };
            var byMarca = new Dictionary<string, MaquinaMarcaDto>();
            foreach (var t in modelos)
            {
                if (!byMarca.TryGetValue(t.Marca, out var bundle))
                {
                    bundle = new MaquinaMarcaDto { Marca = t.Marca };
                    byMarca[t.Marca] = bundle;
                    tipo.Marcas.Add(bundle);
                }
                bundle.Modelos.Add(t);
            }
            return tipo;
        }

        /// <summary>Busca una plantilla por categoría + marca + modelo. Null si no existe.</summary>
        public static MaquinaTemplate Find(string categoria, string marca, string modelo)
        {
            if (string.IsNullOrEmpty(categoria) || string.IsNullOrEmpty(marca) || string.IsNullOrEmpty(modelo))
                return null;
            foreach (var t in Templates)
            {
                if (string.Equals(t.Categoria, categoria, System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Marca, marca, System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Modelo, modelo, System.StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return null;
        }
    }
}
