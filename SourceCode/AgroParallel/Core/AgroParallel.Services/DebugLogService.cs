// ============================================================================
// DebugLogService.cs — implementación del log unificado.
//
// - Ring buffer thread-safe en RAM (List + lock + tope MaxBufferLines).
// - Captura automática del Debug.WriteLine("[Módulo] msg") legacy mediante
//   un TraceListener. El prefijo "[xxx]" → módulo; el resto → mensaje.
//   Heurística de nivel: si contiene "error/fallo/exception" → error;
//                         "warn/aviso/timeout"             → warn;
//                         caso contrario                   → info.
// - Persistencia de config: AppDomain.BaseDirectory\AgroParallel\debug.json.
// - Grabación NDJSON: rota al pasar 10 MB.
// - Suscripción para WS push.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class DebugLogService : IDebugLogService, IDisposable
    {
        private readonly object _lock = new object();
        private readonly List<DebugEntryDto> _buffer = new List<DebugEntryDto>();
        private readonly HashSet<string> _knownModules =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Action<DebugEntryDto>> _subs = new List<Action<DebugEntryDto>>();

        private DebugConfigDto _cfg;
        private long _seq;

        private StreamWriter _recWriter;
        private string _recPath;
        private long _recBytes;
        private const long RecordRotateBytes = 10L * 1024 * 1024; // 10 MB

        private TraceListener _trace;

        private static readonly Regex ModuleTagRx =
            new Regex(@"^\s*\[(?<mod>[A-Za-z0-9_\-]+)\]\s*", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public DebugLogService()
        {
            _cfg = LoadConfig();
            HookTrace();
        }

        // ---------- Config persistence ------------------------------------
        private static string ConfigPath()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AgroParallel");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "debug.json");
        }

        private static DebugConfigDto LoadConfig()
        {
            try
            {
                string p = ConfigPath();
                if (File.Exists(p))
                {
                    var c = JsonSerializer.Deserialize<DebugConfigDto>(File.ReadAllText(p));
                    if (c != null)
                    {
                        if (c.Modules == null) c.Modules = new Dictionary<string, bool>();
                        if (c.MaxBufferLines <= 0) c.MaxBufferLines = 5000;
                        if (string.IsNullOrEmpty(c.MinLevel)) c.MinLevel = "debug";
                        return c;
                    }
                }
            }
            catch { }
            // Default: todos los módulos conocidos en true.
            var def = new DebugConfigDto
            {
                MaxBufferLines = 5000,
                MinLevel = "debug",
                Modules = new Dictionary<string, bool>
                {
                    { "quantix",  true },
                    { "vistax",   true },
                    { "orbitx",   true },
                    { "sectionx", true },
                    { "camaras",  true },
                    { "sistema",  true },
                    { "aog",      true },
                    { "host",     true },
                    { "ui",       true }
                }
            };
            return def;
        }

        public DebugConfigDto GetConfig()
        {
            lock (_lock) return CloneConfig(_cfg);
        }

        public void SaveConfig(DebugConfigDto cfg)
        {
            if (cfg == null) return;
            lock (_lock)
            {
                _cfg = CloneConfig(cfg);
                if (_cfg.Modules == null) _cfg.Modules = new Dictionary<string, bool>();
                if (_cfg.MaxBufferLines <= 0) _cfg.MaxBufferLines = 5000;
                try
                {
                    File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(_cfg, JsonOpts));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[host] no se pudo guardar debug.json: " + ex.Message);
                }
                TrimBufferLocked();
            }
        }

        public void SetModuleEnabled(string module, bool enabled)
        {
            if (string.IsNullOrEmpty(module)) return;
            lock (_lock)
            {
                _cfg.Modules[module.ToLowerInvariant()] = enabled;
                try { File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(_cfg, JsonOpts)); }
                catch { }
            }
        }

        private static DebugConfigDto CloneConfig(DebugConfigDto src)
        {
            return new DebugConfigDto
            {
                MinLevel = src.MinLevel,
                RecordToFile = src.RecordToFile,
                RecordDir = src.RecordDir,
                MaxBufferLines = src.MaxBufferLines,
                Modules = src.Modules == null
                    ? new Dictionary<string, bool>()
                    : new Dictionary<string, bool>(src.Modules, StringComparer.OrdinalIgnoreCase)
            };
        }

        // ---------- Append ------------------------------------------------
        private static int LevelRank(string lvl)
        {
            if (string.IsNullOrEmpty(lvl)) return 1;
            switch (lvl.ToLowerInvariant())
            {
                case "debug": return 0;
                case "info": return 1;
                case "warn": return 2;
                case "warning": return 2;
                case "error": return 3;
                case "err": return 3;
                default: return 1;
            }
        }

        public void Append(string module, string level, string message)
        {
            if (message == null) message = "";
            module = string.IsNullOrEmpty(module) ? "host" : module.ToLowerInvariant();
            level = string.IsNullOrEmpty(level) ? "info" : level.ToLowerInvariant();

            DebugEntryDto entry;
            Action<DebugEntryDto>[] subsCopy;
            lock (_lock)
            {
                _knownModules.Add(module);
                if (!_cfg.Modules.ContainsKey(module)) _cfg.Modules[module] = true;

                if (LevelRank(level) < LevelRank(_cfg.MinLevel)) return;
                if (_cfg.Modules.TryGetValue(module, out bool ena) && !ena) return;

                _seq++;
                entry = new DebugEntryDto
                {
                    Seq = _seq,
                    Ts = DateTime.UtcNow.ToString("O"),
                    Module = module,
                    Level = level,
                    Message = message
                };
                _buffer.Add(entry);
                TrimBufferLocked();

                if (_cfg.RecordToFile && _recWriter != null)
                {
                    WriteRecordLocked(entry);
                }

                subsCopy = _subs.ToArray();
            }

            foreach (var s in subsCopy)
            {
                try { s(entry); } catch { }
            }
        }

        private void TrimBufferLocked()
        {
            int max = _cfg.MaxBufferLines;
            if (max <= 0) max = 5000;
            if (_buffer.Count > max)
            {
                int remove = _buffer.Count - max;
                _buffer.RemoveRange(0, remove);
            }
        }

        // ---------- Reads -------------------------------------------------
        public List<DebugEntryDto> GetEntriesSince(long sinceSeq, IReadOnlyCollection<string> modules)
        {
            lock (_lock)
            {
                IEnumerable<DebugEntryDto> q = _buffer.Where(e => e.Seq > sinceSeq);
                if (modules != null && modules.Count > 0)
                {
                    var set = new HashSet<string>(modules.Select(m => (m ?? "").ToLowerInvariant()));
                    q = q.Where(e => set.Contains(e.Module));
                }
                return q.ToList();
            }
        }

        public DebugSnapshotDto GetSnapshot(int maxEntries)
        {
            lock (_lock)
            {
                int take = maxEntries > 0 ? Math.Min(maxEntries, _buffer.Count) : _buffer.Count;
                var entries = _buffer.Skip(_buffer.Count - take).ToList();
                return new DebugSnapshotDto
                {
                    Seq = _seq,
                    Config = CloneConfig(_cfg),
                    Entries = entries,
                    Recording = _recWriter != null,
                    RecordingFile = _recPath,
                    KnownModules = _knownModules.OrderBy(s => s).ToArray()
                };
            }
        }

        public void Clear()
        {
            lock (_lock) _buffer.Clear();
        }

        public IReadOnlyList<string> KnownModules
        {
            get { lock (_lock) return _knownModules.OrderBy(s => s).ToList(); }
        }

        // ---------- Recording ---------------------------------------------
        public bool IsRecording { get { lock (_lock) return _recWriter != null; } }
        public string RecordingFile { get { lock (_lock) return _recPath; } }

        public string StartRecording()
        {
            lock (_lock)
            {
                StopRecordingLocked();
                string dir = !string.IsNullOrEmpty(_cfg.RecordDir)
                    ? _cfg.RecordDir
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AgroParallel", "debug-logs");
                try { Directory.CreateDirectory(dir); } catch { }
                string file = "debug-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
                _recPath = Path.Combine(dir, file);
                _recBytes = 0;
                try
                {
                    var fs = new FileStream(_recPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    _recWriter = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    _cfg.RecordToFile = true;
                    try { File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(_cfg, JsonOpts)); } catch { }
                }
                catch (Exception ex)
                {
                    _recWriter = null;
                    _recPath = null;
                    Debug.WriteLine("[host] no se pudo abrir archivo de grabación: " + ex.Message);
                }
                return _recPath;
            }
        }

        public void StopRecording()
        {
            lock (_lock)
            {
                StopRecordingLocked();
                _cfg.RecordToFile = false;
                try { File.WriteAllText(ConfigPath(), JsonSerializer.Serialize(_cfg, JsonOpts)); } catch { }
            }
        }

        private void StopRecordingLocked()
        {
            if (_recWriter != null)
            {
                try { _recWriter.Flush(); _recWriter.Dispose(); } catch { }
                _recWriter = null;
            }
            _recPath = null;
            _recBytes = 0;
        }

        private void WriteRecordLocked(DebugEntryDto e)
        {
            try
            {
                string line = JsonSerializer.Serialize(new
                {
                    ts = e.Ts,
                    mod = e.Module,
                    lvl = e.Level,
                    msg = e.Message
                });
                _recWriter.WriteLine(line);
                _recBytes += line.Length + 1;
                if (_recBytes >= RecordRotateBytes)
                {
                    // Rotación: cerrar y abrir uno nuevo.
                    string oldDir = Path.GetDirectoryName(_recPath);
                    StopRecordingLocked();
                    string file = "debug-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
                    _recPath = Path.Combine(oldDir, file);
                    var fs = new FileStream(_recPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    _recWriter = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    _recBytes = 0;
                }
            }
            catch { }
        }

        // ---------- Subscriptions ----------------------------------------
        public IDisposable Subscribe(Action<DebugEntryDto> handler)
        {
            if (handler == null) return new NullDisposable();
            lock (_lock) _subs.Add(handler);
            return new Sub(this, handler);
        }

        private void Unsubscribe(Action<DebugEntryDto> handler)
        {
            lock (_lock) _subs.Remove(handler);
        }

        private sealed class Sub : IDisposable
        {
            private DebugLogService _owner;
            private Action<DebugEntryDto> _h;
            public Sub(DebugLogService o, Action<DebugEntryDto> h) { _owner = o; _h = h; }
            public void Dispose()
            {
                var o = _owner; var h = _h;
                _owner = null; _h = null;
                if (o != null && h != null) o.Unsubscribe(h);
            }
        }

        private sealed class NullDisposable : IDisposable { public void Dispose() { } }

        // ---------- Trace listener (captura Debug.WriteLine legacy) -------
        private void HookTrace()
        {
            try
            {
                _trace = new ForwardingTraceListener(this);
                Trace.Listeners.Add(_trace);
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                if (_trace != null)
                {
                    Trace.Listeners.Remove(_trace);
                    _trace.Dispose();
                    _trace = null;
                }
            }
            catch { }
            lock (_lock) StopRecordingLocked();
        }

        private sealed class ForwardingTraceListener : TraceListener
        {
            private readonly DebugLogService _owner;
            private readonly StringBuilder _pending = new StringBuilder();

            public ForwardingTraceListener(DebugLogService owner)
            {
                _owner = owner;
                Name = "AgpDebugLog";
            }

            public override void Write(string message)
            {
                if (string.IsNullOrEmpty(message)) return;
                lock (_pending) _pending.Append(message);
            }

            public override void WriteLine(string message)
            {
                string full;
                lock (_pending)
                {
                    _pending.Append(message ?? "");
                    full = _pending.ToString();
                    _pending.Clear();
                }
                EmitLine(full);
            }

            private void EmitLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                string module = "host";
                string msg = line;
                var m = ModuleTagRx.Match(line);
                if (m.Success)
                {
                    module = m.Groups["mod"].Value.ToLowerInvariant();
                    msg = line.Substring(m.Length);
                }
                string lvl = GuessLevel(msg);
                try { _owner.Append(module, lvl, msg); } catch { }
            }

            private static string GuessLevel(string msg)
            {
                if (string.IsNullOrEmpty(msg)) return "info";
                string l = msg.ToLowerInvariant();
                if (l.Contains("error") || l.Contains("fallo") || l.Contains("exception")
                    || l.Contains("excepcion") || l.Contains("traceback")) return "error";
                if (l.Contains("warn") || l.Contains("aviso") || l.Contains("timeout")
                    || l.Contains("retry") || l.Contains("reintento")) return "warn";
                if (l.Contains("[dbg]") || l.StartsWith("dbg ")) return "debug";
                return "info";
            }
        }
    }
}
