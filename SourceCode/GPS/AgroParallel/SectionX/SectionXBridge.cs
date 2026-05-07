// ============================================================================
// SectionXBridge.cs - Puente secciones AOG → MQTT → ESP32 (PCA9685)
// Mapeo explícito: cable N del PCA9685 → sección M de AOG.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.SectionX
{
    public class SectionXBridge : IDisposable
    {
        private readonly AgOpenGPS.FormGPS _parent;
        private readonly SectionXConfig _config;
        private IMqttClient _mqtt;
        private System.Windows.Forms.Timer _timer;
        private bool _disposed, _connected;

        private readonly Dictionary<string, byte[]> _lastSent =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Historial para desfase de tren trasero.
        private readonly List<PosRecord> _posHistory = new List<PosRecord>();
        private struct PosRecord { public double DistAccum; public bool[] Sections; }
        private double _totalDist;
        private double _lastE, _lastN;
        private bool _hasLast;

        public bool IsRunning { get; private set; }
        public int MessagesSent { get; private set; }

        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "sx_bridge.log");
        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public SectionXBridge(AgOpenGPS.FormGPS parent, SectionXConfig config)
        {
            _parent = parent;
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

            _timer = new System.Windows.Forms.Timer { Interval = 100 };
            _timer.Tick += OnTick;
            _timer.Start();
            IsRunning = true;
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
        }

        private async void OnTick(object sender, EventArgs e)
        {
            if (_disposed || _mqtt == null || !_connected) return;
            try
            {
                double vel = _parent.avgSpeed;

                // Leer secciones AOG actuales.
                bool[] secAOG = null;
                try
                {
                    if (_parent.tool != null)
                    {
                        int n = _parent.tool.numOfSections;
                        secAOG = new bool[n];
                        for (int i = 0; i < n; i++)
                        {
                            var sec = _parent.section[i];
                            secAOG[i] = sec != null && sec.sectionOnRequest;
                        }
                    }
                }
                catch { }
                if (secAOG == null) return;

                if (vel < _config.VelMinima)
                    for (int i = 0; i < secAOG.Length; i++) secAOG[i] = false;

                RecordPosition(secAOG);

                // Cache de secciones del tren trasero por distancia (cada nodo
                // puede tener su propio DistanciaEntreTrenes — calculamos solo
                // la primera vez que aparece esa distancia en este tick).
                Dictionary<double, bool[]> secTraseroCache = null;

                foreach (var nodo in _config.Nodos)
                {
                    if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;

                    bool[] secTrasero = secAOG;
                    if (nodo.DistanciaEntreTrenes > 0.05)
                    {
                        if (secTraseroCache == null) secTraseroCache = new Dictionary<double, bool[]>();
                        bool[] cached;
                        if (!secTraseroCache.TryGetValue(nodo.DistanciaEntreTrenes, out cached))
                        {
                            cached = GetSectionsAtDistance(nodo.DistanciaEntreTrenes) ?? secAOG;
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
                            var msg = new MqttApplicationMessageBuilder()
                                .WithTopic(topic).WithPayload(sb.ToString())
                                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                                .Build();
                            await _mqtt.PublishAsync(msg);
                            MessagesSent++;
                            if (changed)
                            {
                                if (nodo.DistanciaEntreTrenes > 0.05)
                                    Log(string.Format(
                                        "-> {0} {1}  v={2:F1}m/s  desfase={3:F2}m  histN={4}",
                                        nodo.Uid, sb.ToString(), vel, nodo.DistanciaEntreTrenes, _posHistory.Count));
                                else
                                    Log("-> " + nodo.Uid + " " + sb.ToString());
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Maximo movimiento aceptable entre ticks (100ms). 5m = 180km/h: arriba
        // de eso es glitch GPS / freeze del sistema y NO acumulamos distancia
        // (rompe el historial del tren trasero generando cortes fantasma).
        private const double MaxStepMeters = 5.0;
        private int _glitchCount;

        private void RecordPosition(bool[] sections)
        {
            double ex = _parent.pivotAxlePos.easting, ny = _parent.pivotAxlePos.northing;
            if (_hasLast)
            {
                double dx = ex - _lastE, dy = ny - _lastN;
                double step = Math.Sqrt(dx * dx + dy * dy);
                if (step <= MaxStepMeters)
                {
                    _totalDist += step;
                }
                else
                {
                    _glitchCount++;
                    Log(string.Format("GPS glitch ignorado: salto={0:F1}m (count={1})", step, _glitchCount));
                }
            }
            _lastE = ex; _lastN = ny; _hasLast = true;

            bool[] copy = new bool[sections.Length];
            Array.Copy(sections, copy, sections.Length);
            _posHistory.Add(new PosRecord { DistAccum = _totalDist, Sections = copy });
            while (_posHistory.Count > 500) _posHistory.RemoveAt(0);
        }

        private bool[] GetSectionsAtDistance(double dist)
        {
            double target = _totalDist - dist;
            if (target < 0 || _posHistory.Count < 2) return null;
            for (int i = _posHistory.Count - 1; i >= 0; i--)
                if (_posHistory[i].DistAccum <= target) return _posHistory[i].Sections;
            return _posHistory[0].Sections;
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

        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
    }
}
