// ============================================================================
// SectionXBridge.cs - Puente secciones PilotX → MQTT → ESP32 (PCA9685)
// Mapeo explícito: cable N del PCA9685 → sección M de PilotX.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.SectionX
{
    public class SectionXBridge : IDisposable
    {
        // Fase A: el bridge consume PilotX a través de IAogStateProvider.
        // El constructor legacy (FormGPS, SectionXConfig) sigue funcionando
        // hasta que el wire-up viejo se migre.
        private readonly IAogStateProvider _state;
        private readonly SectionXConfig _config;
        private IMqttClient _mqtt;
        private System.Timers.Timer _timer;
        private bool _disposed, _connected;

        private readonly Dictionary<string, byte[]> _lastSent =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Para el panel debug de la UI: además de los bits crudos, necesitamos
        // saber el timestamp y el topic. Mismo locking que _lastSent (no compite,
        // se actualizan juntos).
        private readonly Dictionary<string, LastPublishInfo> _lastInfo =
            new Dictionary<string, LastPublishInfo>(StringComparer.OrdinalIgnoreCase);

        // UIDs en modo test: el OnTick los saltea para no pisar la secuencia
        // que está ejecutando RunRelayTestAsync.
        private readonly HashSet<string> _inTest =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new object();

        // Historial para desfase de tren trasero.
        private readonly PositionHistory _posHistory = new PositionHistory(Log);

        public bool IsRunning { get; private set; }
        public int MessagesSent { get; private set; }

        // Expuesto para el chip de estado de la UI. _connected ya existía como
        // flag interno; estos getters lo hacen visible sin romper encapsulación.
        public bool MqttConnected { get { return _connected; } }
        public int NodoCount { get { return _config != null ? _config.Nodos.Count : 0; } }
        public DateTime? LastPublishUtc { get; private set; }

        // Singleton de instancia activa — la UI/controller llegan al bridge sin
        // tener que pasar por FormGPS (que es WinForms y no es alcanzable desde
        // el WebHost). Se setea en StartAsync, se libera en Stop.
        private static SectionXBridge s_current;
        public static SectionXBridge Current { get { return s_current; } }

        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "sx_bridge.log");
        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public SectionXBridge(IAogStateProvider state, SectionXConfig config)
        {
            _state = state ?? throw new ArgumentNullException("state");
            _config = config ?? SectionXConfig.Load();
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;
            if (!_config.Enabled || _config.Nodos.Count == 0) { Log("Deshabilitado o sin nodos"); return; }

            try
            {
                var vCfg = VistaXConfig.Load();
                var factory = new MqttFactory();
                _mqtt = factory.CreateMqttClient();
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(vCfg.BrokerAddress ?? "127.0.0.1", vCfg.BrokerPort > 0 ? vCfg.BrokerPort : 1883)
                    .WithClientId("SX_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                    .WithCleanSession(true).Build();
                await _mqtt.ConnectAsync(opts);
                _connected = true;
            }
            catch (Exception ex) { Log("MQTT error: " + ex.Message); return; }

            _timer = new System.Timers.Timer { Interval = 100, AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
            IsRunning = true;
            // Reemplazar el Current activo (puede haber uno viejo si esto vino
            // de un Reload — el Stop del viejo limpia s_current sólo si era él).
            s_current = this;
            Log("Iniciado con " + _config.Nodos.Count + " nodo(s)");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            SendAllOff();
            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                System.Threading.Tasks.Task.Run(() =>
                { try { m.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build()).Wait(2000); m.Dispose(); } catch { } });
            }
            _connected = false;
            // Sólo limpio s_current si todavía me apuntaba — si un nuevo bridge
            // ya tomó el spot durante el Reload, no quiero borrarlo de paso.
            if (object.ReferenceEquals(s_current, this)) s_current = null;
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _mqtt == null || !_connected) return;
            try
            {
                AogStateSnapshot snap = null;
                try { snap = _state.GetSnapshot(); } catch { }
                if (snap == null) return;

                double vel = snap.AvgSpeed;
                bool[] secAOG = snap.SectionOnRequest;
                if (secAOG == null) return;

                _posHistory.Record(snap.PivotEasting, snap.PivotNorthing, secAOG);

                // Cache de secciones del tren trasero por distancia (cada nodo
                // puede tener su propio DistanciaEntreTrenes — calculamos solo
                // la primera vez que aparece esa distancia en este tick).
                Dictionary<double, bool[]> secTraseroCache = null;

                foreach (var nodo in _config.Nodos)
                {
                    if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;
                    // Si el operario lanzó "Probar relés" desde la UI, este
                    // nodo está en modo test — saltearlo para no pisar la
                    // secuencia con el publish automático del bridge.
                    lock (_lock) { if (_inTest.Contains(nodo.Uid)) continue; }

                    bool[] secTrasero = secAOG;
                    if (nodo.DistanciaEntreTrenes > 0.05)
                    {
                        if (secTraseroCache == null) secTraseroCache = new Dictionary<double, bool[]>();
                        bool[] cached;
                        if (!secTraseroCache.TryGetValue(nodo.DistanciaEntreTrenes, out cached))
                        {
                            cached = _posHistory.GetSectionsAtDistanceBack(nodo.DistanciaEntreTrenes) ?? secAOG;
                            secTraseroCache[nodo.DistanciaEntreTrenes] = cached;
                        }
                        secTrasero = cached;
                    }

                    // Armar los 16 bits de relay según el mapeo cable->seccion.
                    var bits = new bool[16];
                    foreach (var cable in nodo.Cables)
                    {
                        if (cable.Cable < 1 || cable.Cable > 16) continue;
                        if (cable.SeccionAOG < 1) continue;

                        int secIdx = cable.SeccionAOG - 1;
                        bool[] fuente = (cable.Tren == 0) ? secAOG : secTrasero;

                        bits[cable.Cable - 1] = secIdx < fuente.Length && fuente[secIdx];
                    }

                    byte lo = 0, hi = 0;
                    for (int i = 0; i < 8; i++) if (bits[i]) lo |= (byte)(1 << i);
                    for (int i = 0; i < 8; i++) if (bits[8 + i]) hi |= (byte)(1 << i);

                    byte[] cur = { lo, hi };
                    byte[] last;
                    bool changed = true;
                    if (_lastSent.TryGetValue(nodo.Uid, out last))
                        changed = last[0] != lo || last[1] != hi;
                    _lastSent[nodo.Uid] = cur;

                    if (changed || MessagesSent % 10 == 0)
                    {
                        string topic = "agp/quantix/" + nodo.Uid + "/sections";
                        int maxCable = 0;
                        foreach (var c in nodo.Cables) if (c.Cable > maxCable) maxCable = c.Cable;
                        var sb = new StringBuilder("[");
                        for (int i = 0; i < Math.Max(maxCable, 8); i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append(bits[i] ? '1' : '0');
                        }
                        sb.Append(']');

                        try
                        {
                            var payloadStr = sb.ToString();
                            var msg = new MqttApplicationMessageBuilder()
                                .WithTopic(topic).WithPayload(payloadStr)
                                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                                .Build();
                            await _mqtt.PublishAsync(msg);
                            MessagesSent++;
                            LastPublishUtc = DateTime.UtcNow;
                            // Cache para el panel debug — bits crudos en int[]
                            // así el JS no tiene que parsear el string payload.
                            var bitsCopy = new int[bits.Length];
                            for (int bi = 0; bi < bits.Length; bi++) bitsCopy[bi] = bits[bi] ? 1 : 0;
                            lock (_lock)
                            {
                                _lastInfo[nodo.Uid] = new LastPublishInfo
                                {
                                    Topic = topic,
                                    Payload = payloadStr,
                                    Bits = bitsCopy,
                                    AtUtc = LastPublishUtc.Value
                                };
                            }
                            if (changed)
                            {
                                if (nodo.DistanciaEntreTrenes > 0.05)
                                    Log(string.Format(
                                        "-> {0} {1}  v={2:F1}m/s  desfase={3:F2}m  histN={4}",
                                        nodo.Uid, payloadStr, vel, nodo.DistanciaEntreTrenes, _posHistory.Count));
                                else
                                    Log("-> " + nodo.Uid + " " + payloadStr);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void SendAllOff()
        {
            if (_mqtt == null) return;
            foreach (var n in _config.Nodos)
            {
                if (string.IsNullOrEmpty(n.Uid)) continue;
                try
                {
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic("agp/quantix/" + n.Uid + "/sections")
                        .WithPayload("[0,0,0,0,0,0,0,0,0,0,0,0,0,0]").Build();
                    _mqtt.PublishAsync(msg).Wait(500);
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------------
        // API pública para la UI (controller). Devuelve snapshots inmutables —
        // ningún caller modifica estado interno. Si necesitás más datos, agregá
        // campos al DTO; mantener Bits como int[] (no bool[]) para que System.Text.Json
        // los serialice como números, no como true/false (más compacto en el wire).
        // ---------------------------------------------------------------------
        public sealed class LastPublishInfo
        {
            public string Topic { get; set; }
            public string Payload { get; set; }
            public int[] Bits { get; set; }
            public DateTime AtUtc { get; set; }
        }

        public sealed class StatusSnapshot
        {
            public bool Running { get; set; }
            public bool Connected { get; set; }
            public int NodoCount { get; set; }
            public int MessagesSent { get; set; }
            public long? LastPublishMsAgo { get; set; }
        }

        public StatusSnapshot GetStatus()
        {
            var snap = new StatusSnapshot
            {
                Running = IsRunning,
                Connected = MqttConnected,
                NodoCount = NodoCount,
                MessagesSent = MessagesSent
            };
            var t = LastPublishUtc;
            if (t.HasValue) snap.LastPublishMsAgo = (long)(DateTime.UtcNow - t.Value).TotalMilliseconds;
            return snap;
        }

        public sealed class DebugSnapshot
        {
            // Keys = UID del nodo. JsonPropertyName en controller; acá usamos
            // la shape directa para no atar Core a System.Text.Json attrs.
            public Dictionary<string, DebugEntry> LastByNodo { get; set; }
            public string[] LogTail { get; set; }
        }

        public sealed class DebugEntry
        {
            public string Topic { get; set; }
            public string Payload { get; set; }
            public int[] Bits { get; set; }
            public long? MsAgo { get; set; }
        }

        public DebugSnapshot GetDebugSnapshot(int logLines = 30)
        {
            var snap = new DebugSnapshot { LastByNodo = new Dictionary<string, DebugEntry>() };
            lock (_lock)
            {
                foreach (var kv in _lastInfo)
                {
                    var ago = (long)(DateTime.UtcNow - kv.Value.AtUtc).TotalMilliseconds;
                    snap.LastByNodo[kv.Key] = new DebugEntry
                    {
                        Topic = kv.Value.Topic,
                        Payload = kv.Value.Payload,
                        Bits = kv.Value.Bits,
                        MsAgo = ago
                    };
                }
            }
            // Tail del sx_bridge.log — usado solo para diagnóstico. Si el log es
            // gigante, leer todo y quedarse con las últimas N es aceptable porque
            // los logs son cortos (un append por cambio de estado).
            try
            {
                if (File.Exists(LogPath))
                {
                    var all = File.ReadAllLines(LogPath);
                    int take = Math.Min(logLines, all.Length);
                    snap.LogTail = all.Skip(all.Length - take).ToArray();
                }
                else snap.LogTail = new string[0];
            }
            catch { snap.LogTail = new string[0]; }
            return snap;
        }

        /// <summary>
        /// Activa secuencialmente cada cable de la lista (1s c/u por defecto)
        /// publicando directo al topic /sections del UID. Marca el UID como
        /// "in test" para que OnTick no pise los bits durante la secuencia.
        /// Fire-and-forget desde el caller: devuelve true si arrancó OK.
        /// </summary>
        public async Task<bool> RunRelayTestAsync(string uid, int[] cables, int stepMs)
        {
            if (string.IsNullOrEmpty(uid) || cables == null || cables.Length == 0) return false;
            if (_mqtt == null || !_connected) return false;
            if (stepMs < 100) stepMs = 100;
            if (stepMs > 5000) stepMs = 5000; // cap defensivo

            lock (_lock) { _inTest.Add(uid); }
            try
            {
                int maxCable = 0;
                foreach (var c in cables) if (c > maxCable) maxCable = c;
                int width = Math.Max(maxCable, 8);

                foreach (var cable in cables)
                {
                    if (cable < 1 || cable > 16) continue;
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < width; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append((i == cable - 1) ? '1' : '0');
                    }
                    sb.Append(']');
                    await PublishRawAsync("agp/quantix/" + uid + "/sections", sb.ToString());
                    Log("TEST -> " + uid + " cable=" + cable + " " + sb.ToString());
                    await Task.Delay(stepMs);
                }
                // Apagar todo al final.
                var off = new StringBuilder("[");
                for (int i = 0; i < width; i++) { if (i > 0) off.Append(','); off.Append('0'); }
                off.Append(']');
                await PublishRawAsync("agp/quantix/" + uid + "/sections", off.ToString());
                Log("TEST end -> " + uid + " " + off.ToString());
                return true;
            }
            catch (Exception ex) { Log("TEST error: " + ex.Message); return false; }
            finally
            {
                lock (_lock) { _inTest.Remove(uid); }
            }
        }

        private async Task PublishRawAsync(string topic, string payload)
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                .Build();
            await _mqtt.PublishAsync(msg);
        }

        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
    }
}
