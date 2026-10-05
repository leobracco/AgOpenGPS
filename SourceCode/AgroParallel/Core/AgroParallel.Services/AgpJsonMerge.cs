// ============================================================================
// AgpJsonMerge.cs — aplica SOLO los campos presentes en un JSON sobre un objeto
// ya cargado.
//
// El problema que resuelve: varios POST de configuración reemplazaban el objeto
// entero. Un cliente que mandaba un subconjunto —el Hub manda los tres flags de
// overlays y nada más— reseteaba en silencio todo lo que no venía en el body.
// En overlays eso borraba las posiciones de los widgets: el operario acomodaba
// el widget en la pantalla, tocaba un toggle y volvía al rincón.
//
// Un campo ausente significa "no lo toques". Un campo presente con su valor por
// defecto (0, false) SÍ se aplica: es una decisión explícita del cliente, y
// distinguir eso es justo lo que un Deserialize<T> plano no puede hacer.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgroParallel.Services
{
    public static class AgpJsonMerge
    {
        /// <summary>
        /// Escribe sobre <paramref name="destino"/> solo las propiedades que
        /// aparecen en <paramref name="json"/>. Devuelve la cantidad aplicada
        /// (0 = el body no traía ningún campo reconocido).
        /// </summary>
        public static int Apply<T>(T destino, string json) where T : class
        {
            if (destino == null || string.IsNullOrWhiteSpace(json)) return 0;

            JsonElement raiz;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;
                raiz = doc.RootElement.Clone();
            }

            int aplicados = 0;
            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite || !prop.CanRead) continue;

                if (!raiz.TryGetProperty(NombreEnElCable(prop), out var valor)) continue;
                if (valor.ValueKind == JsonValueKind.Null) continue;   // null = no tocar

                try
                {
                    prop.SetValue(destino, JsonSerializer.Deserialize(valor.GetRawText(), prop.PropertyType));
                    aplicados++;
                }
                catch
                {
                    // Tipo incompatible en ese campo: se ignora ESE campo y el
                    // resto del merge sigue. Un body medio malo no debe tirar
                    // abajo una config entera.
                }
            }
            return aplicados;
        }

        /// <summary>Nombre con el que viaja la propiedad: el [JsonPropertyName]
        /// si lo tiene, si no el nombre tal cual.</summary>
        private static string NombreEnElCable(PropertyInfo prop)
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            return attr != null && !string.IsNullOrEmpty(attr.Name) ? attr.Name : prop.Name;
        }
    }
}
