// ============================================================================
// CamaraDto / CamarasConfigDto
// POCO transfer types for the Cámaras module (HTML UI <-> WebHost <-> GPS).
// Shape espejo de Documents\AgOpenGPS\camaras.json — se serializa con
// PropertyNameCaseInsensitive=true así "nombre"/"Nombre" funcionan igual.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    public sealed class CamaraDto
    {
        public string Nombre { get; set; } = "Cámara";
        public string Ip { get; set; } = "192.168.1.64";
        public int Puerto { get; set; } = 80;
        public int Canal { get; set; } = 1;
        public string Usuario { get; set; } = "admin";
        public string Clave { get; set; } = "";
        public bool Activa { get; set; } = true;
    }

    public sealed class CamarasConfigDto
    {
        public List<CamaraDto> Camaras { get; set; } = new List<CamaraDto>();
        public int RefrescoMs { get; set; } = 1000;
    }
}
