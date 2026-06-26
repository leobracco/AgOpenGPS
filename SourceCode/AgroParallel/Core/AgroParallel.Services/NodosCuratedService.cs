// ============================================================================
// NodosCuratedService.cs
// Persiste la lista curada de nodos (aceptados/ignorados/alias) en nodos.json.
// Combina con NodoRegistryService para producir la vista unificada que consume
// la página /pages/nodos.html del Hub.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class NodosCuratedService : INodosCuratedService
    {
        private static readonly string FileName = "nodos.json";
        private readonly string _path;
        private readonly object _lock = new object();

        public NodosCuratedService()
        {
            _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
        }

        public NodosCuratedDto Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path))
                {
                    var def = new NodosCuratedDto();
                    SaveInternal(def);
                    return def;
                }
                try
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var dto = JsonSerializer.Deserialize<NodosCuratedDto>(File.ReadAllText(_path), opts)
                              ?? new NodosCuratedDto();
                    if (dto.Aceptados == null) dto.Aceptados = new List<NodoAceptadoDto>();
                    if (dto.Ignorados == null) dto.Ignorados = new List<string>();
                    return dto;
                }
                catch
                {
                    return new NodosCuratedDto();
                }
            }
        }

        public void Save(NodosCuratedDto dto)
        {
            if (dto == null) return;
            lock (_lock) { SaveInternal(dto); }
        }

        private void SaveInternal(NodosCuratedDto dto)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, opts));
        }

        public IReadOnlyList<NodoUnifiedDto> GetUnified(INodoRegistryService registry)
        {
            var curado = Load();
            var aceptadosByUid = new Dictionary<string, NodoAceptadoDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in curado.Aceptados)
                if (!string.IsNullOrEmpty(a.Uid)) aceptadosByUid[a.Uid] = a;

            var ignoradosSet = new HashSet<string>(curado.Ignorados ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            var liveByUid = new Dictionary<string, NodoStatus>(StringComparer.OrdinalIgnoreCase);
            if (registry != null)
            {
                var live = registry.GetAll();
                if (live != null)
                {
                    foreach (var n in live)
                        if (n != null && !string.IsNullOrEmpty(n.Uid)) liveByUid[n.Uid] = n;
                }
            }

            var result = new List<NodoUnifiedDto>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Primero: nodos aceptados (siempre aparecen, online o no).
            foreach (var kv in aceptadosByUid)
            {
                var a = kv.Value;
                NodoStatus live;
                liveByUid.TryGetValue(a.Uid, out live);
                var row = new NodoUnifiedDto
                {
                    Uid = a.Uid,
                    Tipo = !string.IsNullOrEmpty(a.Tipo) ? a.Tipo : (live != null ? live.Type : ""),
                    Alias = a.Alias ?? "",
                    Estado = (live != null && live.Online) ? "aceptado" : "offline",
                    Ip = live != null ? (live.Ip ?? "") : "",
                    Firmware = live != null ? (live.Firmware ?? "") : "",
                    LastSeenUtc = live != null && live.LastSeenUtc != default(DateTime)
                        ? live.LastSeenUtc.ToString("O")
                        : "",
                    Online = live != null && live.Online,
                    BootReason = live != null ? live.BootReason : null,
                    SafeMode = live != null && live.SafeMode,
                    CrashCount = live != null ? live.CrashCount : 0,
                    FechaAltaUtc = a.FechaAltaUtc ?? ""
                };
                result.Add(row);
                visited.Add(a.Uid);
            }

            // Después: descubiertos en MQTT que no están aceptados ni ignorados → pendientes.
            // Si están ignorados los mostramos como "ignorado" (para poder restaurar).
            foreach (var kv in liveByUid)
            {
                if (visited.Contains(kv.Key)) continue;
                var live = kv.Value;
                bool ignorado = ignoradosSet.Contains(live.Uid);
                var row = new NodoUnifiedDto
                {
                    Uid = live.Uid,
                    Tipo = live.Type ?? "",
                    Alias = "",
                    Estado = ignorado ? "ignorado" : "pendiente",
                    Ip = live.Ip ?? "",
                    Firmware = live.Firmware ?? "",
                    LastSeenUtc = live.LastSeenUtc != default(DateTime)
                        ? live.LastSeenUtc.ToString("O")
                        : "",
                    Online = live.Online,
                    BootReason = live.BootReason,
                    SafeMode = live.SafeMode,
                    CrashCount = live.CrashCount,
                    FechaAltaUtc = ""
                };
                result.Add(row);
                visited.Add(live.Uid);
            }

            // Por último: ignorados que no están live (puede pasar si ignoró y luego apagó el nodo).
            foreach (var uid in ignoradosSet)
            {
                if (visited.Contains(uid)) continue;
                result.Add(new NodoUnifiedDto
                {
                    Uid = uid,
                    Estado = "ignorado"
                });
            }

            return result;
        }

        public void Aceptar(string uid, string tipo, string alias)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (_lock)
            {
                var dto = LoadNoLock();
                // Quitar de ignorados si estaba.
                dto.Ignorados.RemoveAll(x => string.Equals(x, uid, StringComparison.OrdinalIgnoreCase));
                // Upsert en aceptados.
                NodoAceptadoDto a = dto.Aceptados.Find(x => string.Equals(x.Uid, uid, StringComparison.OrdinalIgnoreCase));
                if (a == null)
                {
                    a = new NodoAceptadoDto
                    {
                        Uid = uid,
                        Tipo = tipo ?? "",
                        Alias = string.IsNullOrEmpty(alias) ? ("Nodo " + uid) : alias,
                        FechaAltaUtc = DateTime.UtcNow.ToString("O")
                    };
                    dto.Aceptados.Add(a);
                }
                else
                {
                    if (!string.IsNullOrEmpty(tipo)) a.Tipo = tipo;
                    if (!string.IsNullOrEmpty(alias)) a.Alias = alias;
                }
                SaveInternal(dto);
            }
        }

        public void Ignorar(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (_lock)
            {
                var dto = LoadNoLock();
                // Si estaba aceptado, quitamos.
                dto.Aceptados.RemoveAll(x => string.Equals(x.Uid, uid, StringComparison.OrdinalIgnoreCase));
                // Agregamos a ignorados si no está.
                if (!dto.Ignorados.Exists(x => string.Equals(x, uid, StringComparison.OrdinalIgnoreCase)))
                    dto.Ignorados.Add(uid);
                SaveInternal(dto);
            }
        }

        public void Restaurar(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (_lock)
            {
                var dto = LoadNoLock();
                dto.Ignorados.RemoveAll(x => string.Equals(x, uid, StringComparison.OrdinalIgnoreCase));
                SaveInternal(dto);
            }
        }

        public void Eliminar(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            lock (_lock)
            {
                var dto = LoadNoLock();
                dto.Aceptados.RemoveAll(x => string.Equals(x.Uid, uid, StringComparison.OrdinalIgnoreCase));
                dto.Ignorados.RemoveAll(x => string.Equals(x, uid, StringComparison.OrdinalIgnoreCase));
                SaveInternal(dto);
            }
        }

        public void Renombrar(string uid, string nuevoAlias)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(nuevoAlias)) return;
            lock (_lock)
            {
                var dto = LoadNoLock();
                var a = dto.Aceptados.Find(x => string.Equals(x.Uid, uid, StringComparison.OrdinalIgnoreCase));
                if (a != null)
                {
                    a.Alias = nuevoAlias;
                    SaveInternal(dto);
                }
            }
        }

        // Versión interna sin re-lock (asume que el caller ya tiene _lock).
        private NodosCuratedDto LoadNoLock()
        {
            if (!File.Exists(_path)) return new NodosCuratedDto();
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var dto = JsonSerializer.Deserialize<NodosCuratedDto>(File.ReadAllText(_path), opts)
                          ?? new NodosCuratedDto();
                if (dto.Aceptados == null) dto.Aceptados = new List<NodoAceptadoDto>();
                if (dto.Ignorados == null) dto.Ignorados = new List<string>();
                return dto;
            }
            catch
            {
                return new NodosCuratedDto();
            }
        }
    }
}
