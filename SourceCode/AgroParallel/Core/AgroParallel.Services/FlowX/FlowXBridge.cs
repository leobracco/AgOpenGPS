// ============================================================================
// FlowXBridge.cs - Puente PilotX -> MQTT -> nodo FlowX (bomba pulverizadora).
//
// Diferencia clave con SectionXBridge / QuantiXMotorBridge:
//   - SectionX maneja relays (on/off) por sección, sin caudal.
//   - QuantiX tiene UN motor por línea, cada motor con su target propio.
//   - FlowX tiene UNA bomba central que alimenta TODOS los picos del aguilón.
//     Si una sección PilotX se apaga, los picos de esa sección se cierran pero
//     la bomba sigue siendo única -> el target de caudal (L/min) tiene que
//     escalar proporcionalmente al ancho activo, no al ancho total.
//
// Formula:
//   target_L_min = dosis_Lha * vel_kmh * ancho_activo_m / 600
//   ancho_activo = sum(ancho_seccion[i]) para i en (cables del nodo cuya
//                                                   sección PilotX está abierta)
//   Si SectionPositions no está disponible, fallback:
//     ancho_activo = ancho_barra * (cables_activos / cables_totales)
//
// Topic publicado: agp/flow/<uid>/target
// Payload JSON:
//   {
//     "t": 12.34,             // target L/min
//     "sec": [1,1,0,1,...],   // bits de cable (1=abierto)
//     "pwm_min": 40,
//     "pid": { "kp":1.0, "ki":0.1, "kd":0 }
//   }
//
// Notas:
//   - Por ahora soporta UN producto por nodo (primer FxProducto de la lista).
//     Multiproducto exige una bomba por producto -> diseño futuro.
//   - El bridge NO arranca solo: hay que instanciarlo y hacer StartAsync()
//     desde el shell. No se cabló todavía en FormGPS porque el firmware FlowX
//     tiene bugs documentados (ver memoria project_flowx_stormx_scaffold.md).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.FlowX
{
    public class FlowXBridge : IDisposable
    {
        private readonly IAogStateProvider _state;
        private readonly FlowXConfig _config;
        private IMqttClient _mqtt;
        private System.Timers.Timer _timer;
        private bool _disposed, _connected;

        // Última payload publicada por nodo - evita spamear MQTT si nada cambió.
        // Key: uid. Value: hash simple del payload (target redondeado + bits sec).
        private readonly Dictionary<string, string> _lastPayload =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsRunning { get; private set; }
        public int MessagesSent { get; private set; }

        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "fx_bridge.log");
        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public FlowXBridge(IAogStateProvider state, FlowXConfig config)
        {
            if (state == null) throw new ArgumentNullException("state");
            _state = state;
            _config = config ?? FlowXConfig.Load();
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;
            if (!_config.Enabled || _config.Nodos.Count == 0)
            {
                Log("Deshabilitado o sin nodos");
                return;
            }

            try
            {
                var vCfg = VistaXConfig.Load();
                var factory = new MqttFactory();
                _mqtt = factory.CreateMqttClient();
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(vCfg.BrokerAddress ?? "127.0.0.1",
                                   vCfg.BrokerPort > 0 ? vCfg.BrokerPort : 1883)
                    .WithClientId("FX_" + Guid.NewGuid().ToString("N").Substring(0, 6))
                    .WithCleanSession(true)
                    .Build();
                await _mqtt.ConnectAsync(opts);
                _connected = true;
            }
            catch (Exception ex)
            {
                Log("MQTT error: " + ex.Message);
                return;
            }

            _timer = new System.Timers.Timer { Interval = 200, AutoReset = true };
            _timer.Elapsed += OnTick;
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
                Task.Run(() =>
                {
                    try
                    {
                        m.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build()).Wait(2000);
                        m.Dispose();
                    }
                    catch { }
                });
            }
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _mqtt == null || !_connected) return;

            AogStateSnapshot snap = null;
            try { snap = _state.GetSnapshot(); } catch { }
            if (snap == null) return;

            double velKmh = snap.AvgSpeed;
            bool[] secAOG = snap.SectionOnRequest;
            int numSec = snap.NumSections;
            if (secAOG == null) return;

            foreach (var nodo in _config.Nodos)
            {
                if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;
                if (nodo.Productos == null || nodo.Productos.Count == 0) continue;

                var prod = nodo.Productos[0];

                // --- 1. Bits de cable (relay por pico/grupo) ---
                int cableCount = 0;
                int cablesOpen = 0;
                var bits = new List<bool>();
                int maxCable = 0;
                foreach (var c in nodo.Cables)
                {
                    if (c.Cable > maxCable) maxCable = c.Cable;
                }
                // Inicializar al menos hasta maxCable o 8.
                int bitLen = Math.Max(maxCable, 8);
                for (int i = 0; i < bitLen; i++) bits.Add(false);

                // ancho_activo proporcional: si tenemos SectionPositions usamos el
                // ancho real de las secciones abiertas que este nodo controla.
                // Si no, fallback a (cables_abiertos / cables_totales) * anchoBarra.
                double anchoActivoReal = 0; // usado si hay SectionPositions
                bool hasPositions = snap.SectionPositions != null && snap.SectionPositions.Count > 0;

                foreach (var cable in nodo.Cables)
                {
                    if (cable.Cable < 1 || cable.Cable > bits.Count) continue;
                    if (cable.SeccionAOG < 1) continue;
                    cableCount++;

                    int secIdx = cable.SeccionAOG - 1;
                    bool open = secIdx >= 0 && secIdx < secAOG.Length && secAOG[secIdx];
                    bits[cable.Cable - 1] = open;
                    if (open)
                    {
                        cablesOpen++;
                        if (hasPositions)
                        {
                            // Buscar el extent por Index = secIdx.
                            for (int k = 0; k < snap.SectionPositions.Count; k++)
                            {
                                var ext = snap.SectionPositions[k];
                                if (ext != null && ext.Index == secIdx)
                                {
                                    double w = ext.Right - ext.Left;
                                    if (w < 0) w = -w;
                                    anchoActivoReal += w;
                                    break;
                                }
                            }
                        }
                    }
                }

                // --- 2. ancho activo final ---
                double anchoBarra = nodo.AnchoBarraM;
                double anchoActivo;
                if (hasPositions && anchoActivoReal > 0)
                {
                    anchoActivo = anchoActivoReal;
                }
                else if (cableCount > 0 && anchoBarra > 0)
                {
                    anchoActivo = anchoBarra * cablesOpen / cableCount;
                }
                else
                {
                    anchoActivo = (cablesOpen > 0) ? anchoBarra : 0;
                }

                // --- 3. target L/min ---
                double dosisLha = prod.DosisLha;
                double targetLmin = 0;
                if (dosisLha > 0 && velKmh > 0 && anchoActivo > 0)
                {
                    targetLmin = dosisLha * velKmh * anchoActivo / 600.0;
                }

                // --- 4. armar payload + dedup ---
                var sb = new StringBuilder();
                sb.Append("{\"t\":").Append(targetLmin.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"sec\":[");
                for (int i = 0; i < bits.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(bits[i] ? '1' : '0');
                }
                sb.Append(']');
                sb.Append(",\"pwm_min\":").Append(prod.PwmMin);
                sb.Append(",\"pid\":{");
                sb.Append("\"kp\":").Append(prod.Kp.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"ki\":").Append(prod.Ki.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"kd\":").Append(prod.Kd.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("}}");
                string payload = sb.ToString();

                // Dedup grueso: si el payload exacto coincide con el anterior y no
                // pasaron muchos ticks, no republicamos. Aun así enviamos heartbeat
                // cada 10 mensajes para que el nodo sepa que el bridge está vivo.
                string last;
                bool changed = !_lastPayload.TryGetValue(nodo.Uid, out last) || last != payload;
                bool heartbeat = (MessagesSent % 10) == 0;

                if (!changed && !heartbeat) continue;
                _lastPayload[nodo.Uid] = payload;

                string topic = "agp/flow/" + nodo.Uid + "/target";
                try
                {
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                        .Build();
                    await _mqtt.PublishAsync(msg);
                    MessagesSent++;
                    if (changed)
                    {
                        Log(string.Format(
                            "-> {0} t={1:F2}L/min v={2:F1}km/h ancho={3:F2}m sec={4}/{5}",
                            nodo.Uid, targetLmin, velKmh, anchoActivo, cablesOpen, cableCount));
                    }
                }
                catch (Exception ex)
                {
                    Log("publish error " + nodo.Uid + ": " + ex.Message);
                }
            }
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
                        .WithTopic("agp/flow/" + n.Uid + "/target")
                        .WithPayload("{\"t\":0,\"sec\":[0,0,0,0,0,0,0,0],\"pwm_min\":0}")
                        .Build();
                    _mqtt.PublishAsync(msg).Wait(500);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
