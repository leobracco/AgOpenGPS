// ============================================================================
// PerfilDtos.cs — DTOs de gestión de perfiles de vehículo (XML en Vehicles/).
// Consumidos por PerfilesController (WebHost) y pages/perfiles.html.
// Un perfil = TODAS las configuraciones del vehículo/implemento (el XML entero).
// "Protegido" = tiene clave (hash sidecar) y no se puede borrar/sobrescribir
// sin ella.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    public class PerfilItemDto
    {
        public string Nombre { get; set; }
        public bool Protegido { get; set; }
        public bool Activo { get; set; }
    }

    public class PerfilesSnapshotDto
    {
        public string Activo { get; set; }
        /// <summary>Con lote abierto no se permite cargar/crear/borrar.</summary>
        public bool IsJobStarted { get; set; }
        public List<PerfilItemDto> Perfiles { get; set; } = new List<PerfilItemDto>();
    }

    public class PerfilResultDto
    {
        public bool Ok { get; set; }
        /// <summary>Mensaje amigable para el operario cuando Ok=false.</summary>
        public string Error { get; set; }

        public static PerfilResultDto Exito() { return new PerfilResultDto { Ok = true }; }
        public static PerfilResultDto Fallo(string error) { return new PerfilResultDto { Ok = false, Error = error }; }
    }
}
