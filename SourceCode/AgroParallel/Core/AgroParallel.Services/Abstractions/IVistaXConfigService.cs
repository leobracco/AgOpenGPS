// ============================================================================
// IVistaXConfigService.cs — config global VistaX + implemento (perfil JSON).
// ============================================================================

using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IVistaXConfigService
    {
        /// <summary>Lee vistaX.json (o defaults si no existe).</summary>
        VistaXConfigDto GetConfig();

        /// <summary>Persiste vistaX.json.</summary>
        void SaveConfig(VistaXConfigDto dto);

        /// <summary>Lee el implemento activo (path en config o data/implementos/*.json).</summary>
        VistaXImplementoDto GetImplemento();

        /// <summary>Persiste el implemento activo al path configurado.</summary>
        void SaveImplemento(VistaXImplementoDto dto);

        /// <summary>Path absoluto del archivo de implemento activo (o null).</summary>
        string GetImplementoPath();
    }
}
