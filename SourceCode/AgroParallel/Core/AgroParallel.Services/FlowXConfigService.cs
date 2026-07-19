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
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            var cfg = AgroParallel.Common.AtomicJson.Read<FlowXConfigDto>(path, ReadOpts);
            if (cfg != null) return cfg;
            var def = new FlowXConfigDto();
            Save(def);
            return def;
        }

        public void Save(FlowXConfigDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, FileName);
            AgroParallel.Common.AtomicJson.Write(path, JsonSerializer.Serialize(dto, WriteOpts));
        }
    }
}
