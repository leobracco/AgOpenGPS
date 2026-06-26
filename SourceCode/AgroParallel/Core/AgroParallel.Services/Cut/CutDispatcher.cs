// ============================================================================
// CutDispatcher.cs - Despachador único del corte de PilotX a los nodos.
//
// Lee una sola vez por tick el estado de corte de PilotX (SectionOnRequest) y lo
// despacha a cada nodo según su producto, vía adapters (ICutAdapter). Reemplaza
// al viejo SectionXBridge: el transporte MQTT, el timing, el desfase de tren
// trasero (PositionHistory) y la deduplicación viven acá una sola vez; lo
// específico de cada producto (mapeo salida→sección, topic, payload) vive en su
// adapter.
//
// Arranca SIEMPRE (no hay gate enabled/nodos): cada adapter se auto-filtra por su
// config y recarga cada ReloadMs. Esto elimina el bug histórico (2026-05-19) en
// que el bridge quedaba clavado con config vieja y nunca publicaba.
//
// Productos manejados en esta iteración: SectionX (relays → agp/quantix/.../sections)
// y LineX (servo/embrague → agp/linex/.../sections). FlowX y QuantiX-motor (dosis)
// siguen con su bridge propio.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.Cut
{
    public sealed class CutDispatcher : IDisposable
    {
        private const int TickMs = 100;
        private const int ReloadMs = 2000;
        // Republicar al menos cada HeartbeatMs aunque el payload no cambie, para
        // que el firmware no dispare su timeout de seguridad (LineX comm_timeout
        // default 3000ms; relays similar). Por-nodo + por-tiempo, igual que FlowX.
        private const int HeartbeatMs = 1000;

        private readonly IAogStateProvider _state;
        private readonly ICutAdapter[] _adapters;
        private readonly Dictionary<string, ICutAdapter> _byProduct =
            new Dictionary<string, ICutAdapter>(StringComparer.OrdinalIgnoreCase);

        private IMqttClient _mqtt;
        private System.Timers.Timer _tickTimer;
        private System.Timers.Timer _reloadTimer;
        private bool _disposed, _connected;

        private readonly PositionHistory _posHistory = new PositionHistory(Log);

        // Dedup por-UID.
        private readonly Dictionary<string, string> _lastPayload =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastPublishUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Para el panel debug: último publish por UID (incluye producto para filtrar).
        private readonly Dictionary<string, LastPublishInfo> _lastInfo =
            new Dictionary<string, LastPublishInfo>(StringComparer.OrdinalIgnoreCase);

        // UIDs corriendo test de relés: el tick saltea sus comandos automáticos.
        private readonly HashSet<string> _inTest =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Stats por producto para el chip de la UI.
        private readonly Dictionary<string, ProductStats> _stats =
            new Dictionary<string, ProductStats>(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new object();

        public bool IsRunning { get; private set; }
        public bool MqttConnected { get { return _connected; } }

        private static CutDispatcher s_current;
        public static CutDispatcher Current { get { return s_current; } }

        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "cut_dispatcher.log");
        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public CutDispatcher(IAogStateProvider state, IEnumerable<ICutAdapter> adapters)
        {
            _state = state ?? throw new ArgumentNullException("state");
            _adapters = (adapters != null ? adapters.Where(a => a != null).ToArray() : new ICutAdapter[0]);
            foreach (var a in _adapters)
            {
                _byProduct[a.Product] = a;
                _stats[a.Product] = new ProductStats();
            }
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;

            try
            {
                var vCfg = VistaXConfig.Load();
                var factory = new MqttFactory();
                _mqtt = factory.CreateMqttClient();
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(
                        string.IsNullOrEmpty(vCfg.BrokerAddress) ? "127.0.0.1" : vCfg.BrokerAddress,
                        vCfg.BrokerPort > 0 ? vCfg.BrokerPort : 1883)
                    .WithClientId("CUT_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                    .WithCleanSession(true)
                    .Build();
                await _mqtt.ConnectAsync(opts);
                _connected = true;
            }
            catch (Exception ex) { Log("MQTT error: " + ex.Message); return; }

            ReloadNow();

            _tickTimer = new System.Timers.Timer { Interval = TickMs, AutoReset = true };
            _tickTimer.Elapsed += OnTick;
            _tickTimer.Start();

            _reloadTimer = new System.Timers.Timer { Interval = ReloadMs, AutoReset = true };
            _reloadTimer.Elapsed += (s, e) => ReloadNow();
            _reloadTimer.Start();

            IsRunning = true;
            s_current = this;
            Log("Iniciado con adapters: " + string.Join(", ", _adapters.Select(a => a.Product)));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_tickTimer != null) { _tickTimer.Stop(); _tickTimer.Dispose(); _tickTimer = null; }
            if (_reloadTimer != null) { _reloadTimer.Stop(); _reloadTimer.Dispose(); _reloadTimer = null; }

            SendAllOff();

            if (_mqtt != null)
            {
                var m = _mqtt; _mqtt = null;
                Task.Run(() =>
                {
                    try { m.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build()).Wait(2000); m.Dispose(); }
                    catch { }
                });
            }
            _connected = false;
            if (ReferenceEquals(s_current, this)) s_current = null;
        }

        public void ReloadNow()
        {
            foreach (var a in _adapters)
            {
                try { a.Reload(); }
                catch (Exception ex) { Log("Reload " + a.Product + ": " + ex.Message); }
            }
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _mqtt == null || !_connected) return;
            try
            {
                AogStateSnapshot snap = null;
                try { snap = _state.GetSnapshot(); } catch { }
                if (snap == null) return;

                if (snap.SectionOnRequest != null)
                    _posHistory.Record(snap.PivotEasting, snap.PivotNorthing, snap.SectionOnRequest);

                foreach (var adapter in _adapters)
                {
                    List<CutCommand> cmds;
                    try { cmds = adapter.ComputePublishes(snap, _posHistory).ToList(); }
                    catch (Exception ex) { Log("Compute " + adapter.Product + ": " + ex.Message); continue; }

                    foreach (var cmd in cmds)
                    {
                        if (cmd == null || string.IsNullOrEmpty(cmd.Uid)) continue;
                        lock (_lock) { if (_inTest.Contains(cmd.Uid)) continue; }

                        string last;
                        bool changed = !_lastPayload.TryGetValue(cmd.Uid, out last) || last != cmd.Payload;
                        DateTime lastPub;
                        bool heartbeatDue =
                            !_lastPublishUtc.TryGetValue(cmd.Uid, out lastPub) ||
                            (DateTime.UtcNow - lastPub).TotalMilliseconds >= HeartbeatMs;
                        if (!changed && !heartbeatDue) continue;

                        _lastPayload[cmd.Uid] = cmd.Payload;
                        _lastPublishUtc[cmd.Uid] = DateTime.UtcNow;

                        try
                        {
                            await PublishRawAsync(cmd.Topic, cmd.Payload);
                            var now = DateTime.UtcNow;
                            lock (_lock)
                            {
                                ProductStats st;
                                if (_stats.TryGetValue(adapter.Product, out st))
                                {
                                    st.Messages++;
                                    st.LastPublishUtc = now;
                                }
                                _lastInfo[cmd.Uid] = new LastPublishInfo
                                {
                                    Product = adapter.Product,
                                    Topic = cmd.Topic,
                                    Payload = cmd.Payload,
                                    Bits = cmd.Bits,
                                    AtUtc = now
                                };
                            }
                            if (changed) Log("-> " + cmd.Uid + " " + cmd.Payload);
                        }
                        catch (Exception ex) { Log("publish " + cmd.Uid + ": " + ex.Message); }
                    }
                }
            }
            catch { /* nunca romper el timer */ }
        }

        private void SendAllOff()
        {
            if (_mqtt == null) return;
            foreach (var adapter in _adapters)
            {
                IEnumerable<CutCommand> offs;
                try { offs = adapter.OffCommands(); }
                catch { continue; }
                foreach (var cmd in offs)
                {
                    if (cmd == null || string.IsNullOrEmpty(cmd.Uid)) continue;
                    try
                    {
                        var msg = new MqttApplicationMessageBuilder()
                            .WithTopic(cmd.Topic).WithPayload(cmd.Payload).Build();
                        _mqtt.PublishAsync(msg).Wait(500);
                    }
                    catch { }
                }
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

        // ---------------------------------------------------------------------
        // Test de relés SectionX: secuencia "un cable a la vez". El adapter arma
        // los pasos; el dispatcher los publica respetando stepMs y marca el UID
        // en _inTest para que el tick no pise la secuencia.
        // ---------------------------------------------------------------------
        public async Task<bool> RunRelayTestAsync(string uid, int[] cables, int stepMs)
        {
            if (string.IsNullOrEmpty(uid) || cables == null || cables.Length == 0) return false;
            if (_mqtt == null || !_connected) return false;

            ICutAdapter a;
            if (!_byProduct.TryGetValue("sectionx", out a)) return false;
            var sx = a as SectionXCutAdapter;
            if (sx == null) return false;

            if (stepMs < 100) stepMs = 100;
            if (stepMs > 5000) stepMs = 5000;

            var steps = sx.BuildTestSequence(uid, cables);
            if (steps.Count == 0) return false;

            lock (_lock) { _inTest.Add(uid); }
            try
            {
                foreach (var step in steps)
                {
                    await PublishRawAsync(step.Topic, step.Payload);
                    Log("TEST -> " + uid + " " + step.Payload);
                    await Task.Delay(stepMs);
                }
                return true;
            }
            catch (Exception ex) { Log("TEST error: " + ex.Message); return false; }
            finally { lock (_lock) { _inTest.Remove(uid); } }
        }

        // ---------------------------------------------------------------------
        // API para los controllers (status/debug por producto).
        // ---------------------------------------------------------------------
        public StatusSnapshot GetStatus(string product)
        {
            var snap = new StatusSnapshot
            {
                Running = IsRunning,
                Connected = _connected,
                NodeCount = 0,
                MessagesSent = 0,
                LastPublishMsAgo = null
            };
            ICutAdapter a;
            if (_byProduct.TryGetValue(product ?? "", out a)) snap.NodeCount = a.NodeCount;
            lock (_lock)
            {
                ProductStats st;
                if (_stats.TryGetValue(product ?? "", out st))
                {
                    snap.MessagesSent = st.Messages;
                    if (st.LastPublishUtc.HasValue)
                        snap.LastPublishMsAgo = (long)(DateTime.UtcNow - st.LastPublishUtc.Value).TotalMilliseconds;
                }
            }
            return snap;
        }

        public DebugSnapshot GetDebugSnapshot(string product, int logLines = 30)
        {
            var snap = new DebugSnapshot { LastByNodo = new Dictionary<string, DebugEntry>() };
            lock (_lock)
            {
                foreach (var kv in _lastInfo)
                {
                    if (!string.IsNullOrEmpty(product) &&
                        !string.Equals(kv.Value.Product, product, StringComparison.OrdinalIgnoreCase))
                        continue;
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

        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }

        // ---------------------------------------------------------------------
        // Tipos de salida.
        // ---------------------------------------------------------------------
        private sealed class ProductStats
        {
            public int Messages;
            public DateTime? LastPublishUtc;
        }

        private sealed class LastPublishInfo
        {
            public string Product { get; set; }
            public string Topic { get; set; }
            public string Payload { get; set; }
            public int[] Bits { get; set; }
            public DateTime AtUtc { get; set; }
        }

        public sealed class StatusSnapshot
        {
            public bool Running { get; set; }
            public bool Connected { get; set; }
            public int NodeCount { get; set; }
            public int MessagesSent { get; set; }
            public long? LastPublishMsAgo { get; set; }
        }

        public sealed class DebugSnapshot
        {
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
    }
}
