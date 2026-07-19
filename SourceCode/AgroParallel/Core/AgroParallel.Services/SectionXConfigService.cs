// ============================================================================
// SectionXConfigService.cs
// Lee/escribe sectionX.json — compatible 1:1 con el SectionXConfig legacy
// (mismo path, snake_case en el JSON vía [JsonPropertyName] en el DTO).
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class SectionXConfigService : ISectionXConfigService
    {
        private const string FileName = "sectionX.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public event Action ConfigSaved;

        public SectionXConfigDto Load()
        {
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            var cfg = AgroParallel.Common.AtomicJson.Read<SectionXConfigDto>(path, ReadOpts);
            if (cfg != null) return cfg;
            var def = new SectionXConfigDto();
            Save(def);
            return def;
        }

        public void Save(SectionXConfigDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            AgroParallel.Common.AtomicJson.Write(path, JsonSerializer.Serialize(dto, WriteOpts));
            // Dispara después de persistir — FormGPS escucha y relanza el bridge
            // para que /sections empiece a publicarse sin reiniciar AOG.
            try { ConfigSaved?.Invoke(); } catch { /* swallow — no podemos romper el Save por un subscriber */ }
        }
    }
}
