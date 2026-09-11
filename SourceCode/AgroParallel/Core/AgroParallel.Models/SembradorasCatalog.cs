// ============================================================================
// SembradorasCatalog.cs
// Catálogo estático de modelos de sembradoras (foco mercado argentino).
// El usuario selecciona Marca → Modelo en herramienta.html y se aplica la
// plantilla, que pre-llena los campos físicos del ImplementoDto:
//   NumeroSurcos, DistanciaEntreSurcosM, NumeroTorres, TipoSiembra,
//   TipoDosificador, TipoCultivo, TieneFertilizacion, TipoEstructura.
//
// Después el operario puede ajustar a mano sin perder la marca/modelo.
// El catálogo es solo lectura: para agregar modelos, editar este archivo
// (no hay UI de edición — los modelos son curados).
//
// Clasificación (ver memoria reference_precision_planting_market):
//  · Grano fino: chorrillo (rodillo/chevron), air-drill (central neumático).
//  · Grano grueso: monograno mecánico (placa) o neumático (vacío/presión).
//  · Air planter: monograno neumático con dosificador central (Bertini).
//  · Combinadas: hacen ambos (Pierobon, Giorgi).
//  · Eléctrica (LineX): motor por surco — habilita corte surco-a-surco.
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AgroParallel.Models
{
    /// <summary>Una plantilla pre-armada para un modelo conocido de sembradora.</summary>
    public sealed class SembradoraTemplate
    {
        [JsonPropertyName("marca")] public string Marca { get; set; } = "";
        [JsonPropertyName("modelo")] public string Modelo { get; set; } = "";
        [JsonPropertyName("tipo_cultivo")] public string TipoCultivo { get; set; } = "";
        [JsonPropertyName("tipo_siembra")] public string TipoSiembra { get; set; } = "";
        [JsonPropertyName("tipo_dosificador")] public string TipoDosificador { get; set; } = "";

        /// <summary>Cantidad de torres (0 = no aplica para mecánicas).</summary>
        [JsonPropertyName("numero_torres")] public int NumeroTorres { get; set; }

        /// <summary>Cantidad típica de surcos para este modelo.</summary>
        [JsonPropertyName("numero_surcos")] public int NumeroSurcos { get; set; }

        /// <summary>Distancia típica entre surcos (m).</summary>
        [JsonPropertyName("distancia_entre_surcos_m")] public double DistanciaEntreSurcosM { get; set; }

        /// <summary>
        /// Cantidad de trenes físicos. 1 = un solo cuerpo de surcos.
        /// 2 = sembradora de tren doble (delantero + trasero, ej. Tanzi 14500).
        /// Default 1 si no se especifica.
        /// </summary>
        [JsonPropertyName("numero_trenes")] public int NumeroTrenes { get; set; } = 1;

        /// <summary>"rigida" | "plegable" | "telescopica".</summary>
        [JsonPropertyName("tipo_estructura")] public string TipoEstructura { get; set; } = "rigida";

        [JsonPropertyName("tiene_fertilizacion")] public bool TieneFertilizacion { get; set; }

        /// <summary>Texto descriptivo corto para mostrar como ayuda en la UI.</summary>
        [JsonPropertyName("descripcion")] public string Descripcion { get; set; } = "";
    }

    /// <summary>Bundle Marca → lista de modelos para el UI.</summary>
    public sealed class SembradoraMarcaDto
    {
        [JsonPropertyName("marca")] public string Marca { get; set; } = "";
        [JsonPropertyName("modelos")] public List<SembradoraTemplate> Modelos { get; set; }
            = new List<SembradoraTemplate>();
    }

    /// <summary>
    /// Familia paramétrica de sembradoras. Comparten chasis/dosificador y solo
    /// varían en ancho de labor (m) y espaciado entre surcos (m). La tabla
    /// <see cref="Configuraciones"/> mapea ancho → espaciado → cantidad de líneas.
    /// Patrón común a Tanzi 14500, Gringa, ERCA Línea 7F y Plantor Pro
    /// (mismos folletos comerciales con tabla ancho×espaciado).
    /// </summary>
    public sealed class SembradoraFamilia
    {
        public string Marca { get; set; } = "";
        /// <summary>Nombre comercial de la familia, ej "Gringa", "Línea 7F", "Plantor Pro".</summary>
        public string Familia { get; set; } = "";
        public string TipoCultivo { get; set; } = "";
        public string TipoSiembra { get; set; } = "";
        public string TipoDosificador { get; set; } = "";
        public string TipoEstructura { get; set; } = "plegable";
        public bool TieneFertilizacion { get; set; }
        public int NumeroTrenes { get; set; } = 1;
        /// <summary>0 = sin torres (chorrillo mecánico). Ej Tanzi 14500 = 12.</summary>
        public int SurcosPorTorre { get; set; }
        public string DescripcionBase { get; set; } = "";

        /// <summary>tabla[ancho_m][espaciado_m] → cantidad de líneas (surcos).</summary>
        public Dictionary<double, Dictionary<double, int>> Configuraciones { get; set; }
            = new Dictionary<double, Dictionary<double, int>>();
    }

    public static class SembradorasCatalog
    {
        // Plantillas puntuales — un solo modelo, no escala con tabla.
        private static readonly SembradoraTemplate[] _puntuales = new[]
        {
            // ----- AGROMETAL -----
            new SembradoraTemplate {
                Marca="Agrometal", Modelo="MX",
                TipoCultivo="fino", TipoSiembra="chorrillo", TipoDosificador="rodillo",
                NumeroTorres=0, NumeroSurcos=21, DistanciaEntreSurcosM=0.175,
                TipoEstructura="rigida", TieneFertilizacion=true,
                Descripcion="Chorrillo mecánica, fertilización por surco."
            },
            new SembradoraTemplate {
                Marca="Agrometal", Modelo="TX Mega",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="neumatico-vacio",
                NumeroTorres=4, NumeroSurcos=16, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Monograno neumática por vacío, 4 torres."
            },
            new SembradoraTemplate {
                Marca="Agrometal", Modelo="MEGA APX",
                TipoCultivo="grueso", TipoSiembra="air-planter", TipoDosificador="central",
                NumeroTorres=6, NumeroSurcos=24, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Air planter, dosificación central."
            },

            // ----- CRUCIANELLI -----
            // Gringa y Plantor Pro se generan via SembradoraFamilia (tabla ancho × espaciado).
            new SembradoraTemplate {
                Marca="Crucianelli", Modelo="Plantor Pro Eléctrica",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="electrica",
                NumeroTorres=0, NumeroSurcos=24, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Motor por surco — habilita corte surco-a-surco (LineX)."
            },

            // ----- APACHE -----
            new SembradoraTemplate {
                Marca="Apache", Modelo="27000",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="neumatico-presion",
                NumeroTorres=4, NumeroSurcos=16, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Monograno neumática por presión."
            },
            new SembradoraTemplate {
                Marca="Apache", Modelo="55000",
                TipoCultivo="grueso", TipoSiembra="air-planter", TipoDosificador="central",
                NumeroTorres=8, NumeroSurcos=32, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Air planter pesado, 8 torres."
            },

            // ----- TANZI -----
            new SembradoraTemplate {
                Marca="Tanzi", Modelo="T-19",
                TipoCultivo="fino", TipoSiembra="chorrillo", TipoDosificador="chevron",
                NumeroTorres=0, NumeroSurcos=19, DistanciaEntreSurcosM=0.175,
                TipoEstructura="rigida", TieneFertilizacion=true,
                Descripcion="Chorrillo con dosificador chevrón."
            },
            new SembradoraTemplate {
                Marca="Tanzi", Modelo="TX Mega",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="neumatico-vacio",
                NumeroTorres=6, NumeroSurcos=24, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Monograno neumática, 6 torres."
            },
            // Tanzi 14500 y demás familias paramétricas se generan via SembradoraFamilia (ver abajo).

            // ----- PIEROBON -----
            new SembradoraTemplate {
                Marca="Pierobon", Modelo="PSA",
                TipoCultivo="combinada", TipoSiembra="air-drill", TipoDosificador="central",
                NumeroTorres=6, NumeroSurcos=36, DistanciaEntreSurcosM=0.190,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Air drill combinada — siembra fina/gruesa."
            },

            // ----- BERTINI -----
            new SembradoraTemplate {
                Marca="Bertini", Modelo="22000",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="neumatico-vacio",
                NumeroTorres=4, NumeroSurcos=16, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Monograno neumática clásica."
            },
            new SembradoraTemplate {
                Marca="Bertini", Modelo="32000 AP",
                TipoCultivo="grueso", TipoSiembra="air-planter", TipoDosificador="central",
                NumeroTorres=8, NumeroSurcos=48, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Air planter de gran porte, 8 torres."
            },

            // ----- GIORGI -----
            new SembradoraTemplate {
                Marca="Giorgi", Modelo="Precisa",
                TipoCultivo="combinada", TipoSiembra="air-drill", TipoDosificador="central",
                NumeroTorres=4, NumeroSurcos=24, DistanciaEntreSurcosM=0.190,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Air drill combinada — granos finos y gruesos."
            },
            new SembradoraTemplate {
                Marca="Giorgi", Modelo="G-100",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="neumatico-vacio",
                NumeroTorres=4, NumeroSurcos=16, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Monograno neumática."
            },

            // ----- FERCAM / FABIMAG / ERCA / INDECAR / BAUMER -----
            new SembradoraTemplate {
                Marca="Fercam", Modelo="Granos Finos",
                TipoCultivo="fino", TipoSiembra="chorrillo", TipoDosificador="rodillo",
                NumeroTorres=0, NumeroSurcos=21, DistanciaEntreSurcosM=0.175,
                TipoEstructura="rigida", TieneFertilizacion=false,
                Descripcion="Chorrillo simple, sin fertilización."
            },
            new SembradoraTemplate {
                Marca="Fabimag", Modelo="Sembradora Gruesa",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="placa",
                NumeroTorres=0, NumeroSurcos=12, DistanciaEntreSurcosM=0.525,
                TipoEstructura="rigida", TieneFertilizacion=true,
                Descripcion="Monograno mecánica a placa."
            },
            new SembradoraTemplate {
                Marca="Erca", Modelo="ER",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="placa",
                NumeroTorres=0, NumeroSurcos=14, DistanciaEntreSurcosM=0.525,
                TipoEstructura="rigida", TieneFertilizacion=true,
                Descripcion="Monograno mecánica a placa, robusta."
            },
            new SembradoraTemplate {
                Marca="Indecar", Modelo="Genia",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="neumatico-vacio",
                NumeroTorres=4, NumeroSurcos=16, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Monograno neumática por vacío."
            },
            new SembradoraTemplate {
                Marca="Baumer", Modelo="Air Drill",
                TipoCultivo="combinada", TipoSiembra="air-drill", TipoDosificador="central",
                NumeroTorres=4, NumeroSurcos=30, DistanciaEntreSurcosM=0.190,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Air drill combinada."
            },

            // ----- Genéricos (para usuarios con sembradoras no listadas) -----
            new SembradoraTemplate {
                Marca="Genérico", Modelo="Grano fino — chorrillo",
                TipoCultivo="fino", TipoSiembra="chorrillo", TipoDosificador="rodillo",
                NumeroTorres=0, NumeroSurcos=21, DistanciaEntreSurcosM=0.175,
                TipoEstructura="rigida", TieneFertilizacion=false,
                Descripcion="Template genérico para chorrillo mecánica."
            },
            new SembradoraTemplate {
                Marca="Genérico", Modelo="Grano grueso — neumática",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="neumatico-vacio",
                NumeroTorres=4, NumeroSurcos=16, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Template genérico para monograno neumática."
            },
            new SembradoraTemplate {
                Marca="Genérico", Modelo="Sembradora eléctrica (LineX)",
                TipoCultivo="grueso", TipoSiembra="monograno", TipoDosificador="electrica",
                NumeroTorres=0, NumeroSurcos=24, DistanciaEntreSurcosM=0.525,
                TipoEstructura="plegable", TieneFertilizacion=true,
                Descripcion="Motor eléctrico por surco — corte surco-a-surco con LineX."
            },
        };

        // ====================================================================
        // Familias paramétricas — cada una expande a N plantillas (una por
        // combinación de ancho × espaciado) via Expandir().
        // ====================================================================
        public static readonly SembradoraFamilia[] Familias = new[]
        {
            // Tanzi 14500 — air-drill tren doble, 1 sola configuración.
            new SembradoraFamilia {
                Marca = "Tanzi", Familia = "14500",
                TipoCultivo = "fino", TipoSiembra = "air-drill", TipoDosificador = "central",
                TipoEstructura = "plegable", TieneFertilizacion = true,
                NumeroTrenes = 2, SurcosPorTorre = 12,
                DescripcionBase = "Air drill tren doble (mitad trasero + mitad delantero) · torres de 12 surcos.",
                Configuraciones = new Dictionary<double, Dictionary<double, int>> {
                    { 14.5, new Dictionary<double, int> { { 0.151, 96 } } },
                }
            },

            // Crucianelli Gringa — chorrillo grano fino, dosificador rodillo/chevrón,
            // tabla extraída del folleto comercial Gringa (10 anchos × 5 espaciados).
            new SembradoraFamilia {
                Marca = "Crucianelli", Familia = "Gringa",
                TipoCultivo = "fino", TipoSiembra = "chorrillo", TipoDosificador = "rodillo",
                TipoEstructura = "plegable", TieneFertilizacion = true,
                NumeroTrenes = 2, SurcosPorTorre = 0,
                DescripcionBase = "Chorrillo grano fino, tren doble (líneas separadas trasero/delantero).",
                Configuraciones = new Dictionary<double, Dictionary<double, int>> {
                    {  6.0, new Dictionary<double, int> { { 0.35, 18 }, { 0.42, 14 }, { 0.525, 12 }, { 0.70, 9  } } },
                    {  7.0, new Dictionary<double, int> { { 0.35, 20 }, { 0.42, 16 }, { 0.525, 14 }, { 0.70, 10 } } },
                    {  8.0, new Dictionary<double, int> { { 0.35, 23 }, { 0.38, 20 }, { 0.42, 19 }, { 0.525, 15 }, { 0.70, 12 } } },
                    {  8.4, new Dictionary<double, int> { { 0.42, 20 }, { 0.525, 16 } } },
                    {  9.0, new Dictionary<double, int> { { 0.35, 26 }, { 0.42, 22 }, { 0.525, 18 }, { 0.70, 13 } } },
                    { 10.0, new Dictionary<double, int> { { 0.35, 28 }, { 0.42, 24 }, { 0.525, 20 }, { 0.70, 14 } } },
                    { 11.0, new Dictionary<double, int> { { 0.35, 32 }, { 0.38, 30 }, { 0.42, 28 }, { 0.525, 22 }, { 0.70, 16 } } },
                    { 12.6, new Dictionary<double, int> { { 0.35, 36 }, { 0.42, 30 }, { 0.525, 24 }, { 0.70, 18 } } },
                    { 15.0, new Dictionary<double, int> { { 0.35, 44 }, { 0.42, 36 }, { 0.525, 30 } } },
                    { 18.0, new Dictionary<double, int> { { 0.42, 42 }, { 0.525, 36 } } },
                }
            },

            // ERCA Línea 7F — chorrillo grano fino, tren doble (split back/front),
            // 6 anchos × 6 espaciados.
            new SembradoraFamilia {
                Marca = "Erca", Familia = "Línea 7F",
                TipoCultivo = "fino", TipoSiembra = "chorrillo", TipoDosificador = "rodillo",
                TipoEstructura = "plegable", TieneFertilizacion = true,
                NumeroTrenes = 2, SurcosPorTorre = 0,
                DescripcionBase = "Chorrillo grano fino con tren doble — líneas alternas trasero/delantero.",
                Configuraciones = new Dictionary<double, Dictionary<double, int>> {
                    {  4.2, new Dictionary<double, int> { { 0.175, 23 }, { 0.191, 21 }, { 0.21, 19 }, { 0.2625, 16 }, { 0.35, 12 }, { 0.52, 8 } } },
                    {  5.25,new Dictionary<double, int> { { 0.175, 29 }, { 0.191, 27 }, { 0.21, 25 }, { 0.2625, 20 }, { 0.35, 15 }, { 0.52, 10 } } },
                    {  6.3, new Dictionary<double, int> { { 0.175, 35 }, { 0.191, 33 }, { 0.21, 30 }, { 0.2625, 24 }, { 0.35, 18 }, { 0.52, 12 } } },
                    {  7.0, new Dictionary<double, int> { { 0.175, 39 }, { 0.191, 36 }, { 0.21, 33 }, { 0.2625, 26 }, { 0.35, 20 }, { 0.52, 13 } } },
                    {  8.4, new Dictionary<double, int> { { 0.175, 47 }, { 0.191, 43 }, { 0.21, 40 }, { 0.2625, 32 }, { 0.35, 24 }, { 0.52, 16 } } },
                    { 10.5, new Dictionary<double, int> { { 0.175, 59 }, { 0.191, 54 }, { 0.21, 50 }, { 0.2625, 40 }, { 0.35, 30 }, { 0.52, 20 } } },
                }
            },

            // Crucianelli Plantor Pro — monograno neumática, 3 módulos articulados,
            // 3 anchos × 6 espaciados.
            new SembradoraFamilia {
                Marca = "Crucianelli", Familia = "Plantor Pro",
                TipoCultivo = "grueso", TipoSiembra = "monograno", TipoDosificador = "neumatico-vacio",
                TipoEstructura = "plegable", TieneFertilizacion = true,
                NumeroTrenes = 1, SurcosPorTorre = 0,
                DescripcionBase = "Monograno neumática · chasis 3 módulos articulados.",
                Configuraciones = new Dictionary<double, Dictionary<double, int>> {
                    { 12.6, new Dictionary<double, int> { { 0.35, 36 }, { 0.381, 33 }, { 0.42, 30 }, { 0.525, 24 }, { 0.70, 18 }, { 0.76, 17 } } },
                    { 15.0, new Dictionary<double, int> { { 0.35, 42 }, { 0.381, 41 }, { 0.42, 36 }, { 0.525, 30 }, { 0.70, 22 }, { 0.76, 20 } } },
                    { 18.9, new Dictionary<double, int> { { 0.35, 54 }, { 0.381, 50 }, { 0.42, 45 }, { 0.525, 36 }, { 0.70, 27 }, { 0.76, 25 } } },
                }
            },
        };

        /// <summary>
        /// Expande una familia paramétrica en N plantillas, una por cada
        /// combinación válida de (ancho_m, espaciado_m).
        /// Nombre del modelo: "{Familia} {ancho}m {espaciadoCm}cm".
        /// </summary>
        public static IEnumerable<SembradoraTemplate> Expandir(SembradoraFamilia f)
        {
            foreach (var ancho in f.Configuraciones.Keys.OrderBy(x => x))
            {
                var porEsp = f.Configuraciones[ancho];
                foreach (var espaciado in porEsp.Keys.OrderBy(x => x))
                {
                    int surcos = porEsp[espaciado];
                    if (surcos <= 0) continue;

                    int torres = f.SurcosPorTorre > 0 ? (surcos / f.SurcosPorTorre) : 0;
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    double espCm = System.Math.Round(espaciado * 100.0, 2);

                    // Formato compacto: "15" si entero, si no "15.1" o "52.5".
                    string fmt(double v) => (v % 1 == 0)
                        ? ((int)v).ToString()
                        : v.ToString("0.##", inv);

                    string anchoStr = fmt(ancho);
                    string espStr = fmt(espCm);

                    string modelo = $"{f.Familia} {anchoStr}m {espStr}cm";
                    string desc = $"{f.DescripcionBase} {surcos} surcos a {espStr} cm "
                                + $"({anchoStr} m de labor).";

                    yield return new SembradoraTemplate {
                        Marca = f.Marca,
                        Modelo = modelo,
                        TipoCultivo = f.TipoCultivo,
                        TipoSiembra = f.TipoSiembra,
                        TipoDosificador = f.TipoDosificador,
                        TipoEstructura = f.TipoEstructura,
                        TieneFertilizacion = f.TieneFertilizacion,
                        NumeroTrenes = f.NumeroTrenes,
                        NumeroTorres = torres,
                        NumeroSurcos = surcos,
                        DistanciaEntreSurcosM = espaciado,
                        Descripcion = desc,
                    };
                }
            }
        }

        // Cache materializado: puntuales + expansión de todas las familias.
        private static readonly SembradoraTemplate[] _allTemplates =
            _puntuales.Concat(Familias.SelectMany(Expandir)).ToArray();

        /// <summary>Array plano de plantillas (puntuales + familias expandidas).</summary>
        public static SembradoraTemplate[] Templates => _allTemplates;

        /// <summary>Agrupa por Marca para la UI (select cascada).</summary>
        public static List<SembradoraMarcaDto> GroupedByMarca()
        {
            var dict = new Dictionary<string, SembradoraMarcaDto>();
            foreach (var t in Templates)
            {
                if (!dict.TryGetValue(t.Marca, out var bundle))
                {
                    bundle = new SembradoraMarcaDto { Marca = t.Marca };
                    dict[t.Marca] = bundle;
                }
                bundle.Modelos.Add(t);
            }
            // Marca alfabetica, "Generico" al final.
            return dict.Values
                .OrderBy(b => b.Marca == "Genérico" ? 1 : 0)
                .ThenBy(b => b.Marca, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Busca una plantilla por marca + modelo. Null si no existe.</summary>
        public static SembradoraTemplate Find(string marca, string modelo)
        {
            if (string.IsNullOrEmpty(marca) || string.IsNullOrEmpty(modelo)) return null;
            foreach (var t in Templates)
            {
                if (string.Equals(t.Marca, marca, System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Modelo, modelo, System.StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return null;
        }
    }
}
