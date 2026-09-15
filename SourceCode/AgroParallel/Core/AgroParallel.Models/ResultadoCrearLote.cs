// ResultadoCrearLote.cs — por qué falló crear un lote.
// Antes CreateFieldAsync devolvía bool y "ya existe" era indistinguible de
// "error": la pantalla de lote cerraba muda y el operario creía que había
// creado su lote cuando en realidad quedaba abierto otro (reporte 2026-09-12).

namespace AgroParallel.Models
{
    public enum MotivoCrearLote
    {
        Ok,
        YaExiste,
        NombreInvalido,
        SinDirectorioDeLotes,
        Error,
    }

    public sealed class ResultadoCrearLote
    {
        public bool Ok { get; set; }
        public MotivoCrearLote Motivo { get; set; }

        public static ResultadoCrearLote Bien()
            => new ResultadoCrearLote { Ok = true, Motivo = MotivoCrearLote.Ok };

        public static ResultadoCrearLote Falla(MotivoCrearLote motivo)
            => new ResultadoCrearLote { Ok = false, Motivo = motivo };

        /// <summary>snake_case para el JSON de la UI: "ya_existe", "nombre_invalido"…</summary>
        public string MotivoTexto()
        {
            switch (Motivo)
            {
                case MotivoCrearLote.Ok:                   return "ok";
                case MotivoCrearLote.YaExiste:             return "ya_existe";
                case MotivoCrearLote.NombreInvalido:       return "nombre_invalido";
                case MotivoCrearLote.SinDirectorioDeLotes: return "sin_directorio_de_lotes";
                default:                                   return "error";
            }
        }
    }
}
