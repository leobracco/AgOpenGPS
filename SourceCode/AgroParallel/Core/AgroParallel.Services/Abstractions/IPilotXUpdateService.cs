// IPilotXUpdateService — interfaz del auto-update de la app PC (PilotX).
// La impl FormGpsPilotXUpdateService vive en el proyecto GPS (net48) porque
// necesita HttpClient + ZipFile + Process.Start + OrbitXConfig. Esta interfaz
// expone solo Status + 3 acciones que un controller HTTP puede llamar.

using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IPilotXUpdateService
    {
        PilotXUpdateStatus GetStatus();

        /// <summary>Consulta el catálogo del cloud para "PilotX". No descarga.</summary>
        Task<PilotXUpdateStatus> CheckAsync();

        /// <summary>Descarga el ZIP de la última versión a staging y verifica SHA256.</summary>
        Task<PilotXUpdateStatus> DownloadAsync();

        /// <summary>Lanza el updater externo, que esperará a que PilotX cierre,
        /// extraerá el ZIP encima del install dir y relanzará el ejecutable.
        /// En Windows espera el veredicto del Updater: si va a instalar, pide el
        /// cierre del host (ApplyRequested); si aborta, devuelve Phase=Error con
        /// el motivo en LastError y PilotX sigue abierto.</summary>
        Task<PilotXUpdateStatus> ApplyAsync();
    }
}
