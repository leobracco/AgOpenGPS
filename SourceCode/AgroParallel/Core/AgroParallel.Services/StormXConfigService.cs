// ============================================================================
// StormXConfigService.cs
// Lee/escribe stormX.json — patrón idéntico a SectionXConfigService.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class StormXConfigService : IStormXConfigService
    {
        private const string FileName = "stormX.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public StormXConfigDto Load()
        {
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            // AtomicJson.Read recupera del .bak si el principal está corrupto/vacío.
            var cfg = AgroParallel.Common.AtomicJson.Read<StormXConfigDto>(path, ReadOpts);
            if (cfg != null) return cfg;
            var def = new StormXConfigDto();
            Save(def);
            return def;
        }

        public void Save(StormXConfigDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            AgroParallel.Common.AtomicJson.Write(path, JsonSerializer.Serialize(dto, WriteOpts));
        }
    }
}
