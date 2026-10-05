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
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.FlowX
{
    public class FlowXBridge : IDisposable
    {
        private readonly IAogStateProvider _state;
        // No readonly: el _reloadTimer lo reasigna cada ReloadMs para capturar
        // cambios de config guardados desde la UI (pwm_min/pwm_max/PID/dosis/
        // cortes). Antes era readonly y quedaba clavado con la config del
        // arranque → el operario cambiaba pwm_min a 706, el bridge seguía
        // publicando el 40 default en el target y el firmware nunca lo veía.
        private FlowXConfig _config;
        // Enlace MQTT compartido (MqttPublisherLink): reconecta solo con
        // backoff y una publicación sin enlace devuelve false en vez de tirar.
        // Antes acá había un IMqttClient propio que conectaba UNA vez y, si el
        // broker embebido todavía no había levantado, el bridge quedaba mudo;
        // y si el enlace se caía DESPUÉS, nadie bajaba el flag _connected, así
        // que el tick seguía publicando contra un cliente muerto y cada target
        // de válvula se perdía con una línea "publish error ... not connected"
        // en el log y nada más.
        private MqttPublisherLink _link;
        private System.Timers.Timer _timer;
        private System.Timers.Timer _reloadTimer;
        private bool _disposed;

        // Última payload publicada por nodo - evita spamear MQTT si nada cambió.
        // Key: uid. Value: hash simple del payload (target redondeado + bits sec).
        private readonly Dictionary<string, string> _lastPayload =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Timestamp (UTC) del último publish POR NODO. Habilita un heartbeat
        // temporizado: aunque el payload no cambie, republicamos cada
        // HeartbeatMs para que el firmware no dispare su timeout de seguridad.
        // El firmware (Relays.cpp::CheckRelays) cierra TODAS las secciones a los
        // 4s sin recibir target → corta válvulas y resetea el lazo PID.
        private readonly Dictionary<string, DateTime> _lastPublishUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Republicar al menos cada 1s aunque nada cambie. Bien por debajo de los
        // 4000ms del timeout de seguridad del firmware, con margen para jitter de
        // red y pausas de GC.
        private const int HeartbeatMs = 1000;

        // Recargar la config de disco cada 2s para capturar cambios guardados
        // desde la UI (mismo criterio que QuantiXMotorBridge). El cambio se
        // refleja en el target dentro de ≤2s sin reiniciar el bridge.
        private const int ReloadMs = 2000;

        public bool IsRunning { get; private set; }
        public int MessagesSent { get; private set; }

        /// <summary>Hay enlace vivo con el broker. False mientras reconecta.</summary>
        public bool MqttConectado { get { return _link != null && _link.Conectado; } }

        /// <summary>Targets de válvula que NO llegaron al nodo por falta de
        /// enlace. Distinto de cero = la dosificación quedó a ciegas un rato.</summary>
        public long PublicacionesPerdidas { get { return _link != null ? _link.Perdidas : 0; } }

        /// <summary>Último motivo de falla del enlace, en castellano.</summary>
        public string MqttUltimoMotivo { get { return _link != null ? _link.UltimoMotivo : null; } }

        private static readonly string LogPath = Path.Combine(
            AgroParallel.Common.AgpPaths.ConfigRoot, "fx_bridge.log");
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

            // El bridge arranca IGUAL si el broker todavía no levantó: el
            // enlace reintenta solo con backoff. Abortar acá era la carrera de
            // arranque real — Program.cs hace webHost.Start() (que dispara este
            // bridge) ANTES de coreX.StartServices(), que es donde el broker
            // embebido empieza a escuchar en :1883.
            _link = new MqttPublisherLink("FlowX", "FX", Log);
            bool conectado = await _link.StartAsync();
            if (!conectado)
                Log("Broker todavia no disponible: el enlace reintenta en segundo plano");

            _timer = new System.Timers.Timer { Interval = 200, AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();

            // Recargar config cada ReloadMs para capturar cambios manuales
            // (pwm_min/pwm_max/PID/dosis/cortes) sin reiniciar el bridge.
            _reloadTimer = new System.Timers.Timer { Interval = ReloadMs, AutoReset = true };
            _reloadTimer.Elapsed += (s2, ev2) => { try { _config = FlowXConfig.Load(); } catch { } };
            _reloadTimer.Start();

            IsRunning = true;
            Log("Iniciado con " + _config.Nodos.Count + " nodo(s)");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_reloadTimer != null) { _reloadTimer.Stop(); _reloadTimer.Dispose(); _reloadTimer = null; }
            SendAllOff();
            if (_link != null)
            {
                var l = _link; _link = null;
                try { l.Dispose(); }
                catch { }
            }
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            // Sin enlace el tick SIGUE corriendo: arma el target igual y lo
            // intenta publicar, así el enlace recuperado manda la consigna
            // vigente en el primer tick y las que no salieron quedan contadas
            // (antes se salía mudo y nadie se enteraba de lo que se perdió).
            if (_disposed || _link == null) return;

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

                // --- 1. Bits de cable (relay por corte) ---
                // El usuario configura "N cortes"; cada corte = 1 cable físico del PCA9685.
                // Varias secciones de PilotX pueden apuntar al mismo cable (corte agrupa secciones),
                // por eso usamos OR sobre bits[cable-1] y contamos cables únicos abiertos.
                int maxCable = 0;
                foreach (var c in nodo.Cables)
                {
                    if (c.Cable > maxCable) maxCable = c.Cable;
                }
                int bitLen = Math.Max(maxCable, 8);
                var bits = new List<bool>();
                for (int i = 0; i < bitLen; i++) bits.Add(false);

                // ancho_activo proporcional: si tenemos SectionPositions usamos el
                // ancho real de las secciones abiertas que este nodo controla.
                // Si no, fallback a (cables_abiertos / cables_totales) * anchoBarra.
                double anchoActivoReal = 0;
                bool hasPositions = snap.SectionPositions != null && snap.SectionPositions.Count > 0;
                var cablesUsed = new HashSet<int>();      // cables únicos asignados
                var cablesOpenSet = new HashSet<int>();   // cables únicos con ≥1 sección abierta
                var secsAdded = new HashSet<int>();       // anti-doble-conteo de ancho

                foreach (var cable in nodo.Cables)
                {
                    if (cable.Cable < 1 || cable.Cable > bits.Count) continue;
                    if (cable.SeccionAOG < 1) continue;
                    cablesUsed.Add(cable.Cable);

                    int secIdx = cable.SeccionAOG - 1;
                    bool open = secIdx >= 0 && secIdx < secAOG.Length && secAOG[secIdx];
                    if (open)
                    {
                        bits[cable.Cable - 1] = true; // OR: cualquier sección del grupo abre el corte
                        cablesOpenSet.Add(cable.Cable);
                        if (hasPositions && !secsAdded.Contains(secIdx))
                        {
                            for (int k = 0; k < snap.SectionPositions.Count; k++)
                            {
                                var ext = snap.SectionPositions[k];
                                if (ext != null && ext.Index == secIdx)
                                {
                                    double w = ext.Right - ext.Left;
                                    if (w < 0) w = -w;
                                    anchoActivoReal += w;
                                    secsAdded.Add(secIdx);
                                    break;
                                }
                            }
                        }
                    }
                }
                int cableCount = cablesUsed.Count;
                int cablesOpen = cablesOpenSet.Count;

                // --- 1b. Master como corte ---
                // master_cable: -1 = salida dedicada (firmware MasterPin, no
                // tocamos nada acá), 0 = sin master, 1..N = ese corte hace de
                // master → su bit = OR de todas las secciones abiertas del nodo
                // (lógica any-open: master abierto si hay cualquier corte abierto,
                // cerrado cuando todos cierran). El firmware lo trata como canal
                // normal. Pura lógica PC, sin flashear.
                if (nodo.MasterCable >= 1 && nodo.MasterCable <= bits.Count)
                {
                    bits[nodo.MasterCable - 1] = cablesOpen > 0;
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
                if (prod.ModoManual)
                {
                    // Manual: caudal L/min FIJO, independiente de la velocidad.
                    // Las secciones siguen mandando: si no hay ningún corte
                    // abierto, cerramos (target 0). El firmware regula el lazo
                    // para sostener este L/min aunque cambie la velocidad.
                    if (cablesOpen > 0 && prod.ManualLmin > 0)
                        targetLmin = prod.ManualLmin;
                }
                else if (dosisLha > 0 && velKmh > 0 && anchoActivo > 0)
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
                sb.Append(",\"pwm_max\":").Append(prod.PwmMax);
                sb.Append(",\"pid\":{");
                sb.Append("\"kp\":").Append(prod.Kp.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"ki\":").Append(prod.Ki.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"kd\":").Append(prod.Kd.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("}}");
                string payload = sb.ToString();

                // Dedup grueso: si el payload exacto coincide con el anterior NO
                // republicamos... salvo que ya haya pasado HeartbeatMs desde el
                // último envío a ESTE nodo (heartbeat temporizado por-nodo).
                //
                // Antes el heartbeat era (MessagesSent % 10) == 0: con un solo nodo
                // y payload estático, MessagesSent quedaba clavado en 1 y la
                // condición NUNCA volvía a cumplirse → el bridge enmudecía. El
                // firmware entonces no recibía target, su CheckRelays (4s) cerraba
                // las secciones (seccionesBits=0) y "Detectar PWM" / caracterización
                // abortaba con "sections_closed". Por-nodo + por-tiempo lo evita.
                string last;
                bool changed = !_lastPayload.TryGetValue(nodo.Uid, out last) || last != payload;

                DateTime lastPub;
                bool heartbeatDue =
                    !_lastPublishUtc.TryGetValue(nodo.Uid, out lastPub) ||
                    (DateTime.UtcNow - lastPub).TotalMilliseconds >= HeartbeatMs;

                if (!changed && !heartbeatDue) continue;

                string topic = "agp/flow/" + nodo.Uid + "/target";
                // PublicarAsync nunca tira: devuelve false y cuenta la consigna
                // perdida (con aviso resumido en el log y en AgpLog).
                bool enviado = await _link.PublicarAsync(topic, payload);
                if (!enviado) continue;  // dedup NO se actualiza: al reconectar sale ya

                _lastPayload[nodo.Uid] = payload;
                _lastPublishUtc[nodo.Uid] = DateTime.UtcNow;
                MessagesSent++;
                if (changed)
                {
                    Log(string.Format(
                        "-> {0} t={1:F2}L/min v={2:F1}km/h ancho={3:F2}m sec={4}/{5}",
                        nodo.Uid, targetLmin, velKmh, anchoActivo, cablesOpen, cableCount));
                }
            }
        }

        private void SendAllOff()
        {
            if (_link == null) return;
            foreach (var n in _config.Nodos)
            {
                if (string.IsNullOrEmpty(n.Uid)) continue;
                try
                {
                    // Bloquea acotado a propósito: esto NO corre en el tick,
                    // corre en Stop() y el cierre ordenado de las válvulas vale
                    // medio segundo de espera. Si no hay enlace sale por false
                    // al toque (el firmware igual cierra por comms-loss).
                    _link.PublicarAsync("agp/flow/" + n.Uid + "/target",
                                        "{\"t\":0,\"sec\":[0,0,0,0,0,0,0,0],\"pwm_min\":0}")
                         .Wait(500);
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
