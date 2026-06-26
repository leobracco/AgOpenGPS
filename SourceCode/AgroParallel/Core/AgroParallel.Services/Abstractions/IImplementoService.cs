// ============================================================================
// IImplementoService.cs — Fuente única de verdad del implemento.
//
// VistaX/QuantiX/SectionX consumen este servicio (no definen su propia
// geometría). La página `pages/herramienta.html` es la única que escribe.
//
// Persistencia: directorio implementos/ en BaseDirectory.
//   implementos/<slug>.json     — un archivo por implemento
//   implementos/_active.txt     — slug del implemento activo
// Migración legacy: si no existe el directorio pero sí implemento.json (formato
// viejo de un implemento único) o config legacy de VistaX/AOG, se siembra
// "default" en la primera llamada.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    /// <summary>Entrada de lista de implementos guardados.</summary>
    public sealed class ImplementoListEntry
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; } = "";
        [JsonPropertyName("nombre")]
        public string Nombre { get; set; } = "";
        [JsonPropertyName("activo")]
        public bool Activo { get; set; }
    }

    public interface IImplementoService
    {
        /// <summary>
        /// Devuelve el implemento ACTIVO. Para compatibilidad con consumidores
        /// (VistaX/QuantiX/SectionX) que sólo leen "el implemento".
        /// </summary>
        ImplementoDto GetImplemento();

        /// <summary>Persiste el implemento activo.</summary>
        bool SaveImplemento(ImplementoDto dto);

        // -- CRUD multi-implemento (Hub UI) ----------------------------------

        /// <summary>Lista todos los implementos guardados.</summary>
        List<ImplementoListEntry> List();

        /// <summary>Slug del implemento activo (o "" si no hay).</summary>
        string GetActiveSlug();

        /// <summary>Carga un implemento por slug. Null si no existe.</summary>
        ImplementoDto Load(string slug);

        /// <summary>Guarda un implemento bajo ese slug. Crea si no existe.</summary>
        bool Save(string slug, ImplementoDto dto);

        /// <summary>RMW atómico bajo lock — usar desde controllers que hacen
        /// Load → modify → Save (NodosController.Aceptar/Eliminar, etc.) para
        /// evitar que dos requests concurrentes pisen los cambios del otro.
        /// Devuelve el dto resultante post-Sanitize, o null si falló.</summary>
        ImplementoDto Update(string slug, Action<ImplementoDto> mutate);

        /// <summary>Marca un slug como activo. False si no existe.</summary>
        bool SetActive(string slug);

        /// <summary>Borra un implemento. No deja el sistema sin activo.</summary>
        bool Delete(string slug);

        /// <summary>Copia un implemento existente bajo un nuevo slug+nombre.</summary>
        bool Copy(string fromSlug, string toSlug, string nombre);

        /// <summary>Path absoluto del directorio implementos/ (para diagnóstico UI).</summary>
        string GetPath();
    }
}
