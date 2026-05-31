// ============================================================================
// FlowXConfigService.cs
// Lee/escribe flowX.json — patrón idéntico a SectionXConfigService.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class FlowXConfigService : IFlowXConfigService
    {
        private const string FileName = "flowX.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public FlowXConfigDto Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = new FlowXConfigDto();
                Save(def);
                return def;
            }
            try
            {
                return JsonSerializer.Deserialize<FlowXConfigDto>(File.ReadAllText(path), ReadOpts)
                    ?? new FlowXConfigDto();
            }
            catch { return new FlowXConfigDto(); }
        }

        public void Save(FlowXConfigDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, WriteOpts));
        }
    }
}
