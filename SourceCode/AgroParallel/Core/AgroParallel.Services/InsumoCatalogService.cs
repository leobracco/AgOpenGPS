// ============================================================================
// InsumoCatalogService.cs
// Persistencia del catálogo de insumos compartido. Patrón idéntico a
// FlowXConfigService / SectionXConfigService (JSON en BaseDirectory de PilotX).
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class InsumoCatalogService : IInsumoCatalogService
    {
        private const string FileName = "insumos.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public InsumoCatalogDto Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                // Primer arranque: catálogo con dos ejemplos típicos para que el
                // operario vea de qué se trata y los edite. No quedan "activos"
                // por defecto — se elige a mano desde la UI.
                var def = CreateDefault();
                Save(def);
                return def;
            }
            try
            {
                var dto = JsonSerializer.Deserialize<InsumoCatalogDto>(File.ReadAllText(path), ReadOpts);
                if (dto == null) return CreateDefault();
                // Si el archivo existía de una versión previa con items vacío,
                // re-poblar con defaults y persistir. Conserva ActivoId si quedó.
                if (dto.Items == null || dto.Items.Count == 0)
                {
                    var seeded = CreateDefault();
                    seeded.ActivoId = dto.ActivoId ?? "";
                    Save(seeded);
                    return seeded;
                }
                return dto;
            }
            catch { return CreateDefault(); }
        }

        public void Save(InsumoCatalogDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, WriteOpts));
        }

        public InsumoDto GetActivo()
        {
            var cat = Load();
            if (string.IsNullOrEmpty(cat.ActivoId) || cat.Items == null) return null;
            return cat.Items.Find(i => i != null && i.Id == cat.ActivoId);
        }

        public bool SetActivo(string id)
        {
            var cat = Load();
            if (cat.Items == null) return false;
            // id == "" significa "deseleccionar". Permitido siempre.
            if (string.IsNullOrEmpty(id))
            {
                cat.ActivoId = "";
                Save(cat);
                return true;
            }
            if (cat.Items.Find(i => i != null && i.Id == id) == null) return false;
            cat.ActivoId = id;
            Save(cat);
            return true;
        }

        // Catálogo inicial: insumos más usados en Argentina (campaña 2025/26).
        // Cubre los 4 cultivos extensivos (soja, maíz, trigo, girasol) + fertilizantes
        // base + fitosanitarios típicos. Los valores son orientativos; el operario
        // edita por variedad/lote real. Precios USD aproximados (mover según mercado).
        private static InsumoCatalogDto CreateDefault()
        {
            return new InsumoCatalogDto
            {
                Version = "1.0",
                ActivoId = "",
                Items = new System.Collections.Generic.List<InsumoDto>
                {
                    // ---- SEMILLAS - SOJA -------------------------------------
                    // GM IV-VI son los más sembrados en pampa húmeda.
                    new InsumoDto {
                        Id = "soja-dm-46r18", Nombre = "Soja DM 46R18",
                        Tipo = "semilla", Cultivo = "soja",
                        DensidadObjetivoSemM = 14, DensidadAsumidaSaturadoSemM = 90,
                        SingulacionObjetivoPct = 97, DosisKgha = 60,
                        DropMinSemM = 11, DropMaxSemM = 17,
                        PrecioUsdPorKg = 1.40,
                        Notas = "Don Mario · GM 4.6 · INTACTA RR2 PRO. Variedad estándar pampa húmeda."
                    },
                    new InsumoDto {
                        Id = "soja-nidera-na5009", Nombre = "Soja Nidera NA 5009",
                        Tipo = "semilla", Cultivo = "soja",
                        DensidadObjetivoSemM = 14, DensidadAsumidaSaturadoSemM = 90,
                        SingulacionObjetivoPct = 97, DosisKgha = 62,
                        DropMinSemM = 11, DropMaxSemM = 17,
                        PrecioUsdPorKg = 1.35,
                        Notas = "Nidera · GM 5.0 · RR. Buen comportamiento NOA/Centro."
                    },
                    new InsumoDto {
                        Id = "soja-credenz-cz4806", Nombre = "Soja Credenz CZ 4806",
                        Tipo = "semilla", Cultivo = "soja",
                        DensidadObjetivoSemM = 14, DensidadAsumidaSaturadoSemM = 90,
                        SingulacionObjetivoPct = 97, DosisKgha = 58,
                        DropMinSemM = 11, DropMaxSemM = 17,
                        PrecioUsdPorKg = 1.45,
                        Notas = "Credenz/Corteva · GM 4.8 · STS+RR. Resistencia sulfonilureas."
                    },

                    // ---- SEMILLAS - MAÍZ -------------------------------------
                    // 5-7 sem/m según fecha y zona. Maíz tardío 5.5-6.5.
                    new InsumoDto {
                        Id = "maiz-dk7210", Nombre = "Maíz DK 72-10 VT3P",
                        Tipo = "semilla", Cultivo = "maiz",
                        DensidadObjetivoSemM = 6, DensidadAsumidaSaturadoSemM = 9,
                        SingulacionObjetivoPct = 99, DosisKgha = 22,
                        DropMinSemM = 5, DropMaxSemM = 7.5,
                        PrecioUsdPorKg = 5.20,
                        Notas = "Dekalb · ciclo intermedio · VT Triple PRO (lepidópteros + glifosato)."
                    },
                    new InsumoDto {
                        Id = "maiz-p2089", Nombre = "Maíz P2089 VYHR",
                        Tipo = "semilla", Cultivo = "maiz",
                        DensidadObjetivoSemM = 6, DensidadAsumidaSaturadoSemM = 9,
                        SingulacionObjetivoPct = 99, DosisKgha = 23,
                        DropMinSemM = 5, DropMaxSemM = 7.5,
                        PrecioUsdPorKg = 5.40,
                        Notas = "Pioneer · ciclo largo · VYHR (Viptera + Herculex + RR). Maíz tardío."
                    },
                    new InsumoDto {
                        Id = "maiz-ax7822", Nombre = "Maíz AX 7822 VT3P",
                        Tipo = "semilla", Cultivo = "maiz",
                        DensidadObjetivoSemM = 5.5, DensidadAsumidaSaturadoSemM = 9,
                        SingulacionObjetivoPct = 99, DosisKgha = 21,
                        DropMinSemM = 4.5, DropMaxSemM = 7,
                        PrecioUsdPorKg = 4.80,
                        Notas = "Nidera · ciclo largo · VT3P. Tardío típico zona núcleo."
                    },

                    // ---- SEMILLAS - TRIGO ------------------------------------
                    // Densidad mucho mayor (270 sem/m para 18 cm entre hileras).
                    new InsumoDto {
                        Id = "trigo-baguette-620", Nombre = "Trigo Baguette 620",
                        Tipo = "semilla", Cultivo = "trigo",
                        DensidadObjetivoSemM = 270, DensidadAsumidaSaturadoSemM = 400,
                        SingulacionObjetivoPct = 90, DosisKgha = 130,
                        DropMinSemM = 220, DropMaxSemM = 320,
                        PrecioUsdPorKg = 0.65,
                        Notas = "Nidera · ciclo intermedio-corto. Trigo pan zona núcleo."
                    },
                    new InsumoDto {
                        Id = "trigo-klein-rayo", Nombre = "Trigo Klein Rayo",
                        Tipo = "semilla", Cultivo = "trigo",
                        DensidadObjetivoSemM = 270, DensidadAsumidaSaturadoSemM = 400,
                        SingulacionObjetivoPct = 90, DosisKgha = 135,
                        DropMinSemM = 220, DropMaxSemM = 320,
                        PrecioUsdPorKg = 0.62,
                        Notas = "Klein · ciclo corto · alto potencial de rinde."
                    },

                    // ---- SEMILLAS - GIRASOL ----------------------------------
                    new InsumoDto {
                        Id = "girasol-paraiso-1600cl", Nombre = "Girasol Paraíso 1600 CL Plus",
                        Tipo = "semilla", Cultivo = "girasol",
                        DensidadObjetivoSemM = 3, DensidadAsumidaSaturadoSemM = 6,
                        SingulacionObjetivoPct = 98, DosisKgha = 3.5,
                        DropMinSemM = 2.5, DropMaxSemM = 3.5,
                        PrecioUsdPorKg = 18.00,
                        Notas = "Nuseed · tecnología Clearfield Plus (Imazapir). Alto oleico."
                    },

                    // ---- FERTILIZANTES BASE ----------------------------------
                    new InsumoDto {
                        Id = "fert-urea-46", Nombre = "Urea granulada 46-0-0",
                        Tipo = "fertilizante", Cultivo = "",
                        DosisKgha = 150, PrecioUsdPorKg = 0.55,
                        Notas = "46% N · cobertura o V6 maíz · típico Profertil/Bunge."
                    },
                    new InsumoDto {
                        Id = "fert-fda-18-46", Nombre = "Fosfato diamónico (FDA) 18-46-0",
                        Tipo = "fertilizante", Cultivo = "",
                        DosisKgha = 100, PrecioUsdPorKg = 0.75,
                        Notas = "DAP · arrancador en línea de siembra · suelos con bajo P."
                    },
                    new InsumoDto {
                        Id = "fert-mapcal", Nombre = "MicroEssentials / MAP+S",
                        Tipo = "fertilizante", Cultivo = "",
                        DosisKgha = 90, PrecioUsdPorKg = 0.80,
                        Notas = "MAP con azufre · respuesta en soja/maíz suelos S-deficitarios."
                    },
                    new InsumoDto {
                        Id = "fert-san-21-24", Nombre = "Sulfato de amonio 21-0-0-24S",
                        Tipo = "fertilizante", Cultivo = "",
                        DosisKgha = 120, PrecioUsdPorKg = 0.45,
                        Notas = "N+S · acidifica suelo · alternativa a urea en lotes alcalinos."
                    },

                    // ---- FITOSANITARIOS (líquidos) ---------------------------
                    new InsumoDto {
                        Id = "herb-glifo-48", Nombre = "Glifosato 48% (sal isopropilamina)",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 3.0, PrecioUsdPorL = 4.50,
                        Notas = "Barbecho + post-emergente cultivos RR. Genérico ~ Round-Up."
                    },
                    new InsumoDto {
                        Id = "herb-24d-100", Nombre = "2,4-D éster butílico 100%",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 0.8, PrecioUsdPorL = 5.20,
                        Notas = "Hoja ancha en barbecho/trigo. Aplicar con baja deriva."
                    },
                    new InsumoDto {
                        Id = "herb-dicamba", Nombre = "Dicamba 57.8% (sal diglicolamina)",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 0.5, PrecioUsdPorL = 12.00,
                        Notas = "Yuyo colorado / hoja ancha resistente. Cuidar deriva."
                    },
                    new InsumoDto {
                        Id = "herb-atrazina-50", Nombre = "Atrazina 50% SC",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 2.5, PrecioUsdPorL = 3.20,
                        Notas = "Pre-emergente maíz/sorgo. Residual largo."
                    },
                    new InsumoDto {
                        Id = "insec-cipermetrina-25", Nombre = "Cipermetrina 25% EC",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 0.15, PrecioUsdPorL = 8.50,
                        Notas = "Piretroide · oruga militar/medidora soja-maíz."
                    },
                    new InsumoDto {
                        Id = "insec-clorpirifos-48", Nombre = "Clorpirifós 48% EC",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 0.8, PrecioUsdPorL = 6.80,
                        Notas = "OF · chinches en soja. Restringido en algunos municipios — verificar."
                    },
                    new InsumoDto {
                        Id = "fung-azox-cipro", Nombre = "Azoxistrobina 20% + Ciproconazol 8%",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 0.4, PrecioUsdPorL = 28.00,
                        Notas = "Roya soja / mancha ojo de rana. Tipo Amistar Xtra."
                    },
                    new InsumoDto {
                        Id = "coadyu-aceite-min", Nombre = "Aceite mineral coadyuvante",
                        Tipo = "fitosanitario", Cultivo = "",
                        DosisLha = 0.5, PrecioUsdPorL = 3.00,
                        Notas = "Mejora penetración + reduce evaporación. Mezcla con herbicidas/fungicidas."
                    }
                }
            };
        }
    }
}
