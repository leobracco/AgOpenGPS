// IPerfilVehiculoService — gestión de perfiles de vehículo (XML de Vehicles/).
// Mismo patrón que IGuidanceCalculator/ITrackListService: la interfaz vive en
// netstandard2.0 y la implementación real ("using AgOpenGPS") vive del lado
// GPS en AgroParallel.Adapters (FormGpsPerfilService), que hace Invoke al
// hilo UI cuando la acción toca estado vivo de FormGPS (cargar/crear).

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IPerfilVehiculoService
    {
        PerfilesSnapshotDto GetSnapshot();

        /// <summary>Activa el perfil (recrea vehicle/tool + LoadSettings + SendSettings).</summary>
        PerfilResultDto Cargar(string nombre);

        /// <summary>Crea un perfil nuevo. desde=null/"" arranca vacío (reset);
        /// si no, copia TODAS las configs del perfil origen y lo activa.</summary>
        PerfilResultDto Nuevo(string nombre, string desde);

        /// <summary>Duplica el XML completo de un perfil con otro nombre,
        /// SIN cambiar el perfil activo.</summary>
        PerfilResultDto Copiar(string origen, string nuevo);

        /// <summary>Borra el perfil. Si está protegido exige la clave.</summary>
        PerfilResultDto Borrar(string nombre, string clave);

        /// <summary>Protege el perfil con una clave elegida en el momento.</summary>
        PerfilResultDto Proteger(string nombre, string clave);

        /// <summary>Quita la protección verificando la clave.</summary>
        PerfilResultDto Desproteger(string nombre, string clave);
    }
}
