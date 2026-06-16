// ============================================================================
// LineXConfigService.cs
// Lee/escribe lineX.json — patrón idéntico a FlowXConfigService.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class LineXConfigService : ILineXConfigService
    {
        private const string FileName = "lineX.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public LineXConfigDto Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                var def = new LineXConfigDto();
                Save(def);
                return def;
            }
            try
            {
                return JsonSerializer.Deserialize<LineXConfigDto>(File.ReadAllText(path), ReadOpts)
                    ?? new LineXConfigDto();
            }
            catch { return new LineXConfigDto(); }
        }

        public void Save(LineXConfigDto dto)
        {
            if (dto == null) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, WriteOpts));
        }
    }
}
