// ============================================================================
// SetupStateService.cs
// Estado del asistente de primera vez del Hub PilotX (setup.json).
// Pequeño JSON con flags de pasos; se persiste cada vez que el operario
// avanza una pantalla del wizard.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class SetupStateService : ISetupStateService
    {
        private static readonly string FileName = "setup.json";
        private readonly string _path;
        private readonly object _lock = new object();

        public SetupStateService()
        {
            _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
        }

        public SetupStateDto Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path))
                {
                    var def = new SetupStateDto();
                    SaveInternal(def);
                    return def;
                }
                try
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<SetupStateDto>(File.ReadAllText(_path), opts)
                           ?? new SetupStateDto();
                }
                catch
                {
                    return new SetupStateDto();
                }
            }
        }

        public void Save(SetupStateDto dto)
        {
            if (dto == null) return;
            lock (_lock) { SaveInternal(dto); }
        }

        private void SaveInternal(SetupStateDto dto)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, opts));
        }

        public void MarkPaso(string paso, bool valor)
        {
            if (string.IsNullOrEmpty(paso)) return;
            lock (_lock)
            {
                var dto = LoadNoLock();
                switch (paso.ToLowerInvariant())
                {
                    case "pc_ok":      dto.PasoPcOk = valor; break;
                    case "orbitx":     dto.PasoOrbitx = valor; break;
                    case "encender":   dto.PasoEncender = valor; break;
                    case "aceptar":    dto.PasoAceptar = valor; break;
                    case "configurar": dto.PasoConfigurar = valor; break;
                }
                dto.UltimoPaso = paso;
                SaveInternal(dto);
            }
        }

        public void MarkCompleted(bool completed)
        {
            lock (_lock)
            {
                var dto = LoadNoLock();
                dto.WizardCompleted = completed;
                SaveInternal(dto);
            }
        }

        public void MarkDismissed(bool dismissed)
        {
            lock (_lock)
            {
                var dto = LoadNoLock();
                dto.WizardDismissed = dismissed;
                SaveInternal(dto);
            }
        }

        private SetupStateDto LoadNoLock()
        {
            if (!File.Exists(_path)) return new SetupStateDto();
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<SetupStateDto>(File.ReadAllText(_path), opts)
                       ?? new SetupStateDto();
            }
            catch
            {
                return new SetupStateDto();
            }
        }
    }
}
