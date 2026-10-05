// ============================================================================
// Tarea.cs — "Tareas de trabajo" (idea del T-Wave de Sensor).
//
// Hasta acá el operario abría un lote y trabajaba: no quedaba registro de QUÉ
// se hizo (siembra, pulverización…), con qué cultivo/insumo, cuándo empezó y
// terminó, ni cuántas hectáreas fueron de ESE trabajo. La tarea es eso: un
// registro asociado al lote abierto, que se pausa, se reanuda, se cierra y se
// exporta (informe HTML + SHP de la cobertura) a un pendrive.
//
// Persistencia: Tareas.json en la carpeta del lote (Fields/<Nombre>/). Archivo
// NUEVO: no se toca ninguno de los que ya existen y OrbitXSync no lo sube (su
// lista de archivos del lote es explícita), así que el contrato de subtipos
// del cloud queda igual.
//
// ÁREA: el contador del lote (WorkedAreaTotalM2) es del LOTE — trae lo de
// jornadas anteriores y vuelve a 0 con "Borrar pintado". La tarea guarda solo
// lo que se trabajó mientras estuvo ACTIVA:
//   área = AreaAcumuladaM2 + (área_lote_ahora − AreaBaseM2)   (si está activa)
// con AreaBaseM2 = área del lote al arrancar/reanudar. Las reglas viven en
// TareaReglas (puras, testeadas).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AgroParallel.Services.Tareas
{
    /// <summary>Tipos de trabajo. Strings (no enum) porque viajan tal cual en
    /// el JSON del lote y por la API: un enum renumerado rompería archivos.</summary>
    public static class TipoTrabajo
    {
        public const string Siembra = "siembra";
        public const string Pulverizacion = "pulverizacion";
        public const string Fertilizacion = "fertilizacion";
        public const string Cosecha = "cosecha";
        public const string Otro = "otro";

        public static readonly string[] Todos = { Siembra, Pulverizacion, Fertilizacion, Cosecha, Otro };

        /// <summary>Lo desconocido cae en "otro": nunca se rechaza una tarea
        /// por el tipo, el operario está trabajando.</summary>
        public static string Normalizar(string tipo)
        {
            string t = (tipo ?? "").Trim().ToLowerInvariant();
            foreach (var x in Todos) if (x == t) return x;
            return Otro;
        }

        public static string Etiqueta(string tipo)
        {
            switch (Normalizar(tipo))
            {
                case Siembra: return "Siembra";
                case Pulverizacion: return "Pulverización";
                case Fertilizacion: return "Fertilización";
                case Cosecha: return "Cosecha";
                default: return "Otro";
            }
        }

        /// <summary>Sugerencia a partir del tipo del insumo activo del catálogo
        /// ("semilla" | "fertilizante" | "fitosanitario").</summary>
        public static string DesdeTipoInsumo(string tipoInsumo)
        {
            switch ((tipoInsumo ?? "").Trim().ToLowerInvariant())
            {
                case "semilla": return Siembra;
                case "fertilizante": return Fertilizacion;
                case "fitosanitario": return Pulverizacion;
                default: return Otro;
            }
        }
    }

    public static class EstadoTarea
    {
        public const string Activa = "activa";
        public const string Pausada = "pausada";
        public const string Cerrada = "cerrada";

        public static string Etiqueta(string estado)
        {
            switch (estado)
            {
                case Activa: return "En curso";
                case Pausada: return "En pausa";
                // En el archivo sigue siendo "cerrada" (compatibilidad con los
                // Tareas.json que ya existen); al operario se le dice
                // "Finalizada": quedó sellada y no se edita ni se reabre.
                case Cerrada: return "Finalizada";
                default: return estado ?? "";
            }
        }
    }

    /// <summary>Un período en que la tarea estuvo activa (entre arrancar/reanudar
    /// y pausar/cerrar). Fin null = en curso.</summary>
    public sealed class TareaTramo
    {
        [JsonPropertyName("inicio")] public DateTime Inicio { get; set; }
        [JsonPropertyName("fin")] public DateTime? Fin { get; set; }
    }

    public sealed class Tarea
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        /// <summary>Nombre del lote (carpeta) al que pertenece.</summary>
        [JsonPropertyName("lote")] public string Lote { get; set; } = "";
        [JsonPropertyName("cultivo")] public string Cultivo { get; set; } = "";
        [JsonPropertyName("tipo_trabajo")] public string TipoTrabajo { get; set; } = "otro";
        [JsonPropertyName("notas")] public string Notas { get; set; } = "";

        // ---- insumo (copia del catálogo AL CREAR la tarea) ------------------
        // Copia y no referencia: si después editan o borran el insumo del
        // catálogo, la tarea tiene que seguir diciendo con qué se trabajó.
        [JsonPropertyName("insumo_nombre")] public string InsumoNombre { get; set; } = "";
        [JsonPropertyName("dosis")] public double Dosis { get; set; }
        /// <summary>"kg_ha" | "sem_ha" | "sem_m" | "l_ha" ("" sin dosis).</summary>
        [JsonPropertyName("dosis_unidad")] public string DosisUnidad { get; set; } = "";

        [JsonPropertyName("estado")] public string Estado { get; set; } = EstadoTarea.Activa;
        [JsonPropertyName("inicio")] public DateTime Inicio { get; set; }
        [JsonPropertyName("fin")] public DateTime? Fin { get; set; }
        [JsonPropertyName("tramos")] public List<TareaTramo> Tramos { get; set; } = new List<TareaTramo>();

        // ---- área (ver cabecera del archivo) --------------------------------
        /// <summary>Lo trabajado en tramos ya cerrados (y antes de un "Borrar
        /// pintado"), en m².</summary>
        [JsonPropertyName("area_acumulada_m2")] public double AreaAcumuladaM2 { get; set; }
        /// <summary>Área del lote cuando arrancó el tramo activo, en m².</summary>
        [JsonPropertyName("area_base_m2")] public double AreaBaseM2 { get; set; }
        /// <summary>Última área del lote vista con la tarea activa. Sirve para no
        /// perder lo trabajado si el contador del lote vuelve a 0.</summary>
        [JsonPropertyName("ultima_area_vista_m2")] public double UltimaAreaVistaM2 { get; set; }

        // ---- trazabilidad (campos NUEVOS, opcionales) ------------------------
        // Un Tareas.json viejo no los trae y se lee igual (null). Se omiten al
        // escribir si están vacíos, así una tarea vieja no cambia de forma.

        /// <summary>Copia de con QUÉ se trabajó, tomada al INICIAR la tarea
        /// (implemento, perfil de vehículo, FlowX, operario). Ver TareaSnapshot.</summary>
        [JsonPropertyName("snapshot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TareaSnapshot Snapshot { get; set; }

        /// <summary>SHA-256 (hex) del contenido de la tarea al FINALIZARLA. Con
        /// sello la tarea es inmutable: si alguien edita el archivo a mano, el
        /// sello deja de coincidir y el informe lo dice. Ver TareaSello.</summary>
        [JsonPropertyName("sello")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Sello { get; set; }

        /// <summary>Versión de la forma canónica con que se calculó el sello.</summary>
        [JsonPropertyName("sello_v")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SelloVersion { get; set; }
    }

    /// <summary>
    /// Foto de la configuración al INICIAR la tarea. Copia y no referencia, por
    /// la misma razón que el insumo: si mañana cambian el implemento, el perfil
    /// o la dosis de FlowX, el registro de ESTA aplicación tiene que seguir
    /// diciendo con qué se hizo (trazabilidad de pulverizaciones).
    /// </summary>
    public sealed class TareaSnapshot
    {
        [JsonPropertyName("tomado")] public DateTime Tomado { get; set; }
        /// <summary>Perfil de vehículo activo (nombre del archivo de perfil).</summary>
        [JsonPropertyName("vehiculo")] public string Vehiculo { get; set; } = "";
        /// <summary>Nombre del implemento activo (catálogo de implementos).</summary>
        [JsonPropertyName("implemento")] public string Implemento { get; set; } = "";
        /// <summary>Ancho de trabajo del implemento, en metros.</summary>
        [JsonPropertyName("ancho_m")] public double AnchoM { get; set; }
        [JsonPropertyName("secciones")] public int Secciones { get; set; }
        [JsonPropertyName("anchos_secciones_m")] public List<double> AnchosSeccionesM { get; set; } = new List<double>();
        /// <summary>"semilla" | "fertilizante" | "fitosanitario" | "" — del insumo activo.</summary>
        [JsonPropertyName("insumo_tipo")] public string InsumoTipo { get; set; } = "";
        /// <summary>Productos de FlowX (dosificación líquida) configurados al
        /// iniciar. Solo en trabajos que pueden ser líquidos (no siembra/cosecha).</summary>
        [JsonPropertyName("flowx")] public List<TareaSnapshotFlowX> FlowX { get; set; } = new List<TareaSnapshotFlowX>();
        /// <summary>Quién manejó / aplicó, si el operario lo cargó. "" si no.</summary>
        [JsonPropertyName("operario")] public string Operario { get; set; } = "";
    }

    public sealed class TareaSnapshotFlowX
    {
        [JsonPropertyName("nodo")] public string Nodo { get; set; } = "";
        [JsonPropertyName("producto")] public string Producto { get; set; } = "";
        [JsonPropertyName("dosis_lha")] public double DosisLha { get; set; }
        /// <summary>Calibración del caudalímetro, pulsos por litro.</summary>
        [JsonPropertyName("meter_cal")] public double MeterCal { get; set; }
    }

    /// <summary>Contenido de Tareas.json.</summary>
    public sealed class TareasArchivo
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("tareas")] public List<Tarea> Tareas { get; set; } = new List<Tarea>();
    }
}
