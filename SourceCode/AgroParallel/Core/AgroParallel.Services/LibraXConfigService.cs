// ============================================================================
// LibraXConfigService.cs
// Lee/escribe libraX.json — patrón idéntico a StormXConfigService.
// ============================================================================

using System.Text.Json;
using System.IO;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class LibraXConfigService : ILibraXConfigService
    {
        private const string FileName = "libraX.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public LibraXConfigDto Load()
        {
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            // AtomicJson.Read recupera del .bak si el principal está corrupto/vacío.
            var cfg = AgroParallel.Common.AtomicJson.Read<LibraXConfigDto>(path, ReadOpts);
            if (cfg != null) return cfg;
            var def = new LibraXConfigDto();
            Save(def);
            return def;
        }

        public void Save(LibraXConfigDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            AgroParallel.Common.AtomicJson.Write(path, JsonSerializer.Serialize(dto, WriteOpts));
        }
    }
}
