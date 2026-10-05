// IAogCommandSink — write-side acotado al PilotX legacy. Whitelist estrecha:
// los servicios nuevos solo pueden pedir acciones acotadas, no manipular
// FormGPS libremente. La implementación FormGpsCommandSink vive en GPS.

namespace AgroParallel.Services.Abstractions
{
    public interface IAogCommandSink
    {
        /// <summary>
        /// Pide a PilotX que recargue la configuración de tool/secciones
        /// (típicamente tras un cambio externo del JSON de tool).
        /// </summary>
        void RequestReloadTool();

        /// <summary>
        /// Pide encender/apagar una sección puntual en modo manual.
        /// </summary>
        void RequestSectionToggle(int sectionIndex, bool on);
    }
}
