using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgOpenGPS.AvaloniaApp.Services.Branding
{
    public sealed class BrandingService : IBrandingService, IDisposable
    {
        private readonly ILogger<BrandingService>? _logger;
        private readonly string _packagedPath;
        private readonly string _userPath;
        private readonly object _gate = new();
        private readonly System.Threading.Timer _debounce;
        private FileSystemWatcher? _packagedWatcher;
        private FileSystemWatcher? _userWatcher;

        public BrandingService(ILogger<BrandingService>? logger = null)
        {
            _logger = logger;

            string baseDir = AppContext.BaseDirectory;
            _packagedPath = Path.Combine(baseDir, "Config", "branding.json");

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _userPath = Path.Combine(appData, "AgOpenGPS", "branding.json");

            Current = LoadInternal();

            _debounce = new System.Threading.Timer(_ => Reload(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            TryWatch(_packagedPath, ref _packagedWatcher);
            TryWatch(_userPath, ref _userWatcher);
        }

        public BrandingDefinition Current { get; private set; }

        public event EventHandler<BrandingDefinition>? BrandingChanged;

        public void Reload()
        {
            BrandingDefinition next;
            lock (_gate)
            {
                next = LoadInternal();
                Current = next;
            }
            try { BrandingChanged?.Invoke(this, next); }
            catch (Exception ex) { _logger?.LogError(ex, "BrandingChanged subscriber threw"); }
        }

        public void Dispose()
        {
            _packagedWatcher?.Dispose();
            _userWatcher?.Dispose();
            _debounce.Dispose();
        }

        private BrandingDefinition LoadInternal()
        {
            // User override wins over packaged default. Either may be missing — fall back to type defaults.
            var packaged = TryRead(_packagedPath);
            var user = TryRead(_userPath);
            return user ?? packaged ?? new BrandingDefinition();
        }

        private BrandingDefinition? TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                return JsonSerializer.Deserialize<BrandingDefinition>(json, options);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read branding file: {Path}", path);
                return null;
            }
        }

        private void TryWatch(string path, ref FileSystemWatcher? watcher)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Renamed += OnChanged;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not watch branding path: {Path}", path);
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            // 250ms debounce — editors often write multiple times.
            _debounce.Change(250, System.Threading.Timeout.Infinite);
        }
    }
}
