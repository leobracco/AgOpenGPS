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
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = new StormXConfigDto();
                Save(def);
                return def;
            }
            try
            {
                return JsonSerializer.Deserialize<StormXConfigDto>(File.ReadAllText(path), ReadOpts)
                    ?? new StormXConfigDto();
            }
            catch { return new StormXConfigDto(); }
        }

        public void Save(StormXConfigDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, WriteOpts));
        }
    }
}
