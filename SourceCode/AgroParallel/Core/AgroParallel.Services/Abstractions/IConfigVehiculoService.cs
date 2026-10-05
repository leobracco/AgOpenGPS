// IConfigVehiculoService — réplica HTML de FormConfig (pages/config.html).
// Mismo patrón que IPerfilVehiculoService: la interfaz vive en netstandard2.0
// y la implementación real ("using AgOpenGPS") vive del lado GPS en
// AgroParallel.Adapters (FormGpsConfigService), que hace Invoke al hilo UI
// porque TODAS las acciones tocan Settings + estado vivo de FormGPS.

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IConfigVehiculoService
    {
        /// <summary>Snapshot completo de todas las secciones en unidades persistidas.</summary>
        ConfigSnapshotDto GetSnapshot();

        /// <summary>
        /// Guarda una sección replicando los side-effects del Leave nativo de la
        /// pestaña correspondiente + Settings.Default.Save() + LoadSettings().
        /// Secciones: vehiculo, dimensiones, antena, enganche_estilo, enganche_dist,
        /// offset_implemento, pivote, timing, secciones, switches, relay, maquina,
        /// rumbo, rolido, uturn, tram, display, botones.
        /// </summary>
        ConfigResultDto Guardar(string seccion, ConfigGuardarBody body);

        /// <summary>Acciones live de calibración de rolido: zero | quitar | subir | bajar | reset_imu.
        /// Persisten inmediato y devuelven el estado actualizado.</summary>
        ConfigRolidoResultDto AccionRolido(string accion);

        /// <summary>Al entrar a la pestaña secciones con un lote abierto se apagan
        /// los masters Auto/Manual (réplica del Enter nativo).</summary>
        ConfigResultDto PrepararSecciones();
    }
}
