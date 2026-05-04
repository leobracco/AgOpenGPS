// ============================================================================
// SeedMonitor.cs - Motor de monitoreo adaptado a VistaX real
// Ubicación: SourceCode/GPS/AgroParallel/VistaX/SeedMonitor.cs
// Target: net48 (C# 7.3)
//
// FIX CRITICO: La key del diccionario _surcos ahora incluye el tren:
//   key = tren + "-" + bajada + "-" + tipo
// Sin esto, tren1 surco 1 y tren2 surco 1 colisionan en "1-semilla"
// y el diccionario queda con 22 entries en vez de 43.
// ============================================================================

using AgOpenGPS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgroParallel.VistaX
{
    public class SeedMonitor : IDisposable
    {
        private readonly VistaXConfig _config;
        private readonly FormGPS _parent;
        private ImplementoConfig _implemento;
        private MqttClientWrapper _mqtt;
        private Timer _uiTimer;

        private readonly object _lock = new object();
        private readonly Dictionary<string, SurcoState> _surcos = new Dictionary<string, SurcoState>();
        private double _velocidad;
        private List<int> _seccionesT1 = new List<int>();
        private List<int> _seccionesT2 = new List<int>();
        private DateTime _lastDataTime = DateTime.MinValue;
        private bool _isConnected;
        private bool _disposed;
        private bool _monitoreoActivo;
        private MetodoInicioMonitoreo _metodoInicio;
        private DateTime _confirmacionInicio = DateTime.MinValue;
        private DateTime _paradaDesde = DateTime.MinValue;

        public event Action<SeedMonitorSnapshot> SnapshotUpdated;
        public event Action<string> AlarmTriggered;

        public bool IsRunning { get; private set; }
        public bool IsConnected { get { return _isConnected; } }
        public bool MonitoreoActivo { get { return _monitoreoActivo; } }

        public SeedMonitor(FormGPS parent, VistaXConfig config)
        {
            _parent = parent;
            _config = config ?? VistaXConfig.Load();
        }

        // Inyección directa de velocidad desde PGN de AgOpenGPS (km/h).
        // Llamado por el timer de snapshot, lee avgSpeed sin pasar por MQTT.
        private void SyncSpeedFromAOG()
        {
            if (_parent == null) return;
            try
            {
                double speed = _parent.avgSpeed;
                lock (_lock) { _velocidad = speed; }
            }
            catch { }
        }

        public async Task StartAsync()
        {
            if (!_config.Enabled)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] Deshabilitado");
                return;
            }
            if (IsRunning) return;

            _implemento = _config.LoadImplemento();
            System.Diagnostics.Debug.WriteLine("[VistaX] Implemento: " + (_implemento.Nombre ?? "?")
                + " | Sensores: " + _implemento.MapeoSensores.Count);

            // Parsear método de inicio
            _monitoreoActivo = false;
            switch ((_config.MetodoInicio ?? "sensores").ToLowerInvariant())
            {
                case "herramienta": _metodoInicio = MetodoInicioMonitoreo.Herramienta; break;
                case "pintando": _metodoInicio = MetodoInicioMonitoreo.Pintando; break;
                case "manual": _metodoInicio = MetodoInicioMonitoreo.Manual; break;
                default: _metodoInicio = MetodoInicioMonitoreo.Sensores; break;
            }
            System.Diagnostics.Debug.WriteLine("[VistaX] Método inicio: " + _metodoInicio);

            // Inicializar surcos — KEY INCLUYE TREN.
            // Soporta 1:1 (43 sensores / 43 surcos) y 1:N (8 sensores / 96 surcos).
            lock (_lock)
            {
                foreach (var sensor in _implemento.MapeoSensores)
                {
                    if (!sensor.IsActive) continue;
                    int tren = sensor.Tren > 0 ? sensor.Tren : 0;

                    if (sensor.SurcoDesde > 0 && sensor.SurcoHasta > 0)
                    {
                        // 1:N — este sensor cubre un rango de surcos.
                        for (int b = sensor.SurcoDesde; b <= sensor.SurcoHasta; b++)
                        {
                            string key = tren + "-" + b + "-" + sensor.Tipo;
                            if (!_surcos.ContainsKey(key))
                            {
                                _surcos[key] = new SurcoState
                                {
                                    Bajada = b,
                                    Tipo = sensor.Tipo,
                                    Tren = tren,
                                    LastUpdate = DateTime.MinValue
                                };
                            }
                        }
                    }
                    else
                    {
                        // 1:1 — sensor cubre un solo surco.
                        string key = tren + "-" + sensor.Bajada + "-" + sensor.Tipo;
                        if (!_surcos.ContainsKey(key))
                        {
                            _surcos[key] = new SurcoState
                            {
                                Bajada = sensor.Bajada,
                                Tipo = sensor.Tipo,
                                Tren = tren,
                                LastUpdate = DateTime.MinValue
                            };
                        }
                    }
                }

                // También crear surcos para el total configurado aunque no
                // tengan sensor directo (se llenarán por sección AOG).
                if (_implemento.Setup != null && _implemento.Setup.TotalSurcos > 0
                    && _implemento.Trenes != null)
                {
                    foreach (var trenCfg in _implemento.Trenes)
                    {
                        for (int b = 1; b <= trenCfg.Surcos; b++)
                        {
                            string key = trenCfg.Id + "-" + b + "-semilla";
                            if (!_surcos.ContainsKey(key))
                            {
                                _surcos[key] = new SurcoState
                                {
                                    Bajada = b,
                                    Tipo = "semilla",
                                    Tren = trenCfg.Id,
                                    LastUpdate = DateTime.MinValue
                                };
                            }
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine("[VistaX] Surcos inicializados: " + _surcos.Count);

            _mqtt = new MqttClientWrapper(_config);
            _mqtt.MessageReceived += OnMqttMessage;
            _mqtt.ConnectionStateChanged += delegate (bool c) { _isConnected = c; };
            _mqtt.ErrorOccurred += delegate (string err)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] " + err);
            };

            await _mqtt.ConnectAsync();

            // Publicar configuración de sensores especiales a los nodos.
            // Los nodos retienen el mensaje y lo aplican al reconectar.
            PublishAllSensorConfigs();

            // Clamp: mínimo 200ms entre snapshots para evitar presión de memoria en CefSharp
            int intervalMs = Math.Max(200, _config.UiUpdateIntervalMs);
            _uiTimer = new Timer(
                delegate { EmitSnapshot(); },
                null,
                intervalMs,
                intervalMs);

            IsRunning = true;
            System.Diagnostics.Debug.WriteLine("[VistaX] Monitor iniciado — "
                + _surcos.Count + " surcos mapeados");
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;

            if (_uiTimer != null)
            {
                _uiTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _uiTimer.Dispose();
                _uiTimer = null;
            }

            if (_mqtt != null)
            {
                await _mqtt.DisconnectAsync();
                _mqtt.Dispose();
                _mqtt = null;
            }

            IsRunning = false;
        }

        // =====================================================================
        // MQTT Message Processing
        // =====================================================================

        private void OnMqttMessage(string topic, string payload)
        {
            try
            {
                if (topic == _config.SpeedTopic)
                {
                    double vel;
                    if (double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out vel))
                    {
                        lock (_lock) { _velocidad = vel; }
                    }
                    return;
                }

                if (topic == _config.SectionsTopic)
                {
                    ProcessSections(payload);
                    return;
                }

                if (topic == _config.TelemetriaTopic)
                {
                    ProcessTelemetria(payload);
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] Error: " + ex.Message);
            }
        }

        private void ProcessSections(string payload)
        {
            var data = JsonSerializer.Deserialize<SectionsStatePayload>(payload);
            if (data == null) return;

            lock (_lock)
            {
                if (data.T1 != null) _seccionesT1 = data.T1;
                if (data.T2 != null) _seccionesT2 = data.T2;
            }
        }

        private void ProcessTelemetria(string payload)
        {
            var data = JsonSerializer.Deserialize<EspTelemetriaPayload>(payload);
            if (data == null || data.Sensores == null || _implemento.MapeoSensores == null)
                return;

            string uidNodo = data.Uid;

            foreach (var sensorRaw in data.Sensores)
            {
                int cableFisico = sensorRaw.Cable;

                // Buscar en mapeo_sensores
                SensorConfig cfg = null;
                foreach (var s in _implemento.MapeoSensores)
                {
                    if (s.Uid != uidNodo) continue;
                    bool matchPin = s.Pin >= 0 && s.Pin == cableFisico - 1;
                    bool matchCable = s.Cable > 0 && s.Cable == cableFisico;
                    if (matchPin || matchCable)
                    {
                        cfg = s;
                        break;
                    }
                }

                if (cfg == null) continue;
                if (!cfg.IsActive) continue;

                double valorFlujoTotal = sensorRaw.Valor;  // Flujo COMBINADO del sensor.
                int rawPulsos = sensorRaw.Raw;
                int numTren = cfg.Tren > 0 ? cfg.Tren : 0;
                bool generaAlarma = TipoSensor.GeneraAlarmaFlujo(cfg.Tipo);

                // Cantidad de surcos que cubre este sensor.
                int surcosCubiertos = cfg.SurcosCubiertos; // 1 si es 1:1, N si es rango.

                // Flujo POR SURCO: dividir el total entre la cantidad de surcos.
                // Ej: sensor ve 48 sem/s combinadas de 12 surcos → 4 sem/s por surco.
                double valorPorSurco = surcosCubiertos > 1
                    ? valorFlujoTotal / surcosCubiertos
                    : valorFlujoTotal;

                // Calcular SPM por surco.
                double spmPorSurco = 0;
                lock (_lock)
                {
                    if (_velocidad > 0.5)
                    {
                        double velMs = _velocidad / 3.6;
                        spmPorSurco = valorPorSurco / velMs;
                    }
                }

                // Evaluar alarma (basada en el valor POR SURCO, no el total).
                bool alerta = false;
                bool seccionCortada = false;

                if (generaAlarma)
                {
                    lock (_lock)
                    {
                        List<int> seccionesTren = numTren == 1 ? _seccionesT1 : _seccionesT2;
                        if (seccionesTren.Count > 0)
                        {
                            int secIdx = -1;

                            if (cfg.SeccionAOG > 0)
                            {
                                secIdx = cfg.SeccionAOG - 1;
                            }
                            else
                            {
                                int totalSurcosTren = 0;
                                if (_implemento.Trenes != null)
                                {
                                    foreach (var tr in _implemento.Trenes)
                                        if (tr.Id == numTren) { totalSurcosTren = tr.Surcos; break; }
                                }
                                if (totalSurcosTren <= 0) totalSurcosTren = seccionesTren.Count;

                                int numSecciones = seccionesTren.Count;
                                if (totalSurcosTren > 0 && numSecciones > 0)
                                {
                                    int surcosPorSeccion = Math.Max(1, totalSurcosTren / numSecciones);
                                    secIdx = Math.Min((cfg.Bajada - 1) / surcosPorSeccion, numSecciones - 1);
                                }
                            }

                            if (secIdx >= 0 && secIdx < seccionesTren.Count)
                                seccionCortada = seccionesTren[secIdx] == 0;
                        }

                        if (!seccionCortada && _velocidad > 1.5)
                        {
                            // Evaluar contra el flujo total si es rango (el sensor
                            // no puede distinguir surcos individuales, pero sí
                            // detectar si el flujo total cayó a 0 o es muy bajo).
                            double objetivoPorSurco = GetObjetivoTren(numTren);
                            double objetivoTotal = objetivoPorSurco * surcosCubiertos;

                            if (valorFlujoTotal == 0)
                            {
                                alerta = true;
                            }
                            else if (objetivoTotal > 0 && valorFlujoTotal < objetivoTotal * 0.5)
                            {
                                alerta = true;
                            }
                        }
                    }
                }

                // Propagar dato a todos los surcos que cubre este sensor.
                int bDesde = cfg.SurcoDesde > 0 && cfg.SurcoHasta > 0 ? cfg.SurcoDesde : cfg.Bajada;
                int bHasta = cfg.SurcoDesde > 0 && cfg.SurcoHasta > 0 ? cfg.SurcoHasta : cfg.Bajada;

                lock (_lock)
                {
                    for (int b = bDesde; b <= bHasta; b++)
                    {
                        string key = numTren + "-" + b + "-" + cfg.Tipo;
                        SurcoState state;
                        if (!_surcos.TryGetValue(key, out state))
                        {
                            state = new SurcoState
                            {
                                Bajada = b,
                                Tipo = cfg.Tipo,
                                Tren = numTren
                            };
                            _surcos[key] = state;
                        }

                        // Cada surco recibe el valor DIVIDIDO (por surco).
                        state.Valor = Math.Round(valorPorSurco, 2);
                        state.Spm = Math.Round(spmPorSurco, 1);
                        state.NuevasSemillas = surcosCubiertos > 1
                            ? rawPulsos / surcosCubiertos : rawPulsos;
                        state.Alerta = alerta;
                        state.SeccionCortada = seccionCortada;
                        state.LastUpdate = DateTime.UtcNow;
                    }
                    _lastDataTime = DateTime.UtcNow;
                }

                if (alerta)
                {
                    var handler = AlarmTriggered;
                    if (handler != null)
                    {
                        handler(string.Format("FALLA surco {0} (tren {1}): flujo={2:F1} (total={3:F1}, x{4} surcos)",
                            cfg.Bajada, numTren, valorPorSurco, valorFlujoTotal, surcosCubiertos));
                    }
                }
            }
        }

        // =====================================================================
        // Inicio de monitoreo configurable (4 modos)
        // =====================================================================

        private void EvaluarInicio()
        {
            // Auto-detener si la velocidad cae a 0 por más de 10s (parada).
            if (_monitoreoActivo)
            {
                lock (_lock)
                {
                    if (_velocidad < 0.3)
                    {
                        if (_paradaDesde == DateTime.MinValue)
                            _paradaDesde = DateTime.UtcNow;
                        else if ((DateTime.UtcNow - _paradaDesde).TotalSeconds > 10)
                            DetenerMonitoreo();
                    }
                    else
                    {
                        _paradaDesde = DateTime.MinValue;
                    }
                }
                return;
            }

            switch (_metodoInicio)
            {
                case MetodoInicioMonitoreo.Sensores:
                    lock (_lock)
                    {
                        int activos = 0;
                        foreach (var s in _surcos.Values)
                        {
                            if (s.Tipo == "semilla" && s.Valor > 0) activos++;
                        }
                        int umbral = Math.Max(1, _config.UmbralSensoresActivos);
                        if (activos >= umbral && _velocidad > 1.0)
                        {
                            if (_confirmacionInicio == DateTime.MinValue)
                            {
                                _confirmacionInicio = DateTime.UtcNow;
                            }
                            else if ((DateTime.UtcNow - _confirmacionInicio).TotalMilliseconds >= _config.TiempoConfirmacionMs)
                            {
                                IniciarMonitoreo("sensores (" + activos + " activos, vel=" + _velocidad.ToString("F1") + ")");
                            }
                        }
                        else
                        {
                            _confirmacionInicio = DateTime.MinValue;
                        }
                    }
                    break;

                case MetodoInicioMonitoreo.Herramienta:
                    lock (_lock)
                    {
                        bool bajada = _seccionesT1.Count > 0 || _seccionesT2.Count > 0;
                        if (bajada)
                        {
                            bool algunaActiva = _seccionesT1.Any(s => s == 1) || _seccionesT2.Any(s => s == 1);
                            if (algunaActiva)
                                IniciarMonitoreo("herramienta bajada");
                        }
                    }
                    break;

                case MetodoInicioMonitoreo.Pintando:
                    lock (_lock)
                    {
                        bool pintando = _seccionesT1.Any(s => s == 1) || _seccionesT2.Any(s => s == 1);
                        if (pintando && _velocidad > 0.5)
                            IniciarMonitoreo("pintando (secciones activas)");
                    }
                    break;

                case MetodoInicioMonitoreo.Manual:
                    // Se inicia externamente via IniciarMonitoreoManual()
                    break;
            }
        }

        private void IniciarMonitoreo(string motivo)
        {
            _monitoreoActivo = true;
            _confirmacionInicio = DateTime.MinValue;
            System.Diagnostics.Debug.WriteLine("[VistaX] MONITOREO INICIADO — " + motivo);
        }

        // Notifica al nodo por MQTT la configuración de un canal.
        // Se usa cuando se asigna un sensor de tipo especial (bajada_herramienta,
        // tolva, turbina) para que el nodo sepa qué modo usar en ese pin.
        // Topic: vistax/nodos/{uid}/config
        // Payload: {"cable":N,"tipo":"bajada_herramienta","modo":"digital"}
        public void PublishSensorConfig(string uid, int cable, string tipo)
        {
            if (_mqtt == null || !_mqtt.IsConnected) return;
            if (string.IsNullOrEmpty(uid)) return;

            string modo = TipoSensor.EsGlobal(tipo) ? "digital" : "pulsos";
            string topic = "vistax/nodos/" + uid + "/config";
            string payload = "{\"cable\":" + cable
                + ",\"tipo\":\"" + (tipo ?? "semilla") + "\""
                + ",\"modo\":\"" + modo + "\"}";

            try
            {
                var msg = new MQTTnet.MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(true) // Retained para que el nodo lo reciba al reconectar.
                    .Build();

                _ = _mqtt.PublishAsync(msg);
                System.Diagnostics.Debug.WriteLine("[VistaX] Config enviada a " + uid
                    + " c" + cable + " tipo=" + tipo + " modo=" + modo);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VistaX] Error enviando config: " + ex.Message);
            }
        }

        // Publica la configuración de TODOS los sensores especiales a los nodos.
        // Se llama al iniciar el monitor y cuando cambia la config.
        private void PublishAllSensorConfigs()
        {
            if (_implemento == null || _implemento.MapeoSensores == null) return;
            if (_mqtt == null || !_mqtt.IsConnected) return;

            foreach (var sensor in _implemento.MapeoSensores)
            {
                if (sensor == null || !sensor.IsActive) continue;
                if (string.IsNullOrEmpty(sensor.Uid) || sensor.Uid == "UNASSIGNED") continue;
                if (sensor.Cable <= 0) continue;

                // Solo notificar tipos que necesitan modo especial en el nodo.
                if (sensor.Tipo == TipoSensor.BajadaHerramienta
                    || sensor.Tipo == TipoSensor.Tolva
                    || sensor.Tipo == TipoSensor.Turbina)
                {
                    PublishSensorConfig(sensor.Uid, sensor.Cable, sensor.Tipo);
                }
            }
        }

        public void DetenerMonitoreo()
        {
            _monitoreoActivo = false;
            _confirmacionInicio = DateTime.MinValue;
            _paradaDesde = DateTime.MinValue;
            System.Diagnostics.Debug.WriteLine("[VistaX] MONITOREO DETENIDO");
        }

        public void IniciarMonitoreoManual()
        {
            IniciarMonitoreo("manual (botón UI)");
        }

        // Actualiza el objetivo de siembra (semillas/m) in-memory. Si tren > 0
        // actualiza solo ese tren (via ObjetivosTren); si tren == 0 actualiza
        // DensidadObjetivo (global) y limpia overrides por tren. Thread-safe.
        public void SetObjetivo(double value, int tren)
        {
            if (value < 0) value = 0;
            if (_implemento == null) return;
            if (_implemento.Setup == null) _implemento.Setup = new ImplementoSetup();

            lock (_lock)
            {
                if (tren > 0)
                {
                    if (_implemento.Setup.ObjetivosTren == null)
                        _implemento.Setup.ObjetivosTren = new Dictionary<string, double>();
                    _implemento.Setup.ObjetivosTren[tren.ToString()] = value;
                }
                else
                {
                    _implemento.Setup.DensidadObjetivo = value;
                }
            }

            System.Diagnostics.Debug.WriteLine("[VistaX] Objetivo actualizado: tren="
                + tren + " valor=" + value);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private double GetObjetivoTren(int numTren)
        {
            if (_implemento.Setup.ObjetivosTren != null)
            {
                double val;
                if (_implemento.Setup.ObjetivosTren.TryGetValue(numTren.ToString(), out val))
                    return val;
            }
            return _implemento.Setup.DensidadObjetivo > 0
                ? _implemento.Setup.DensidadObjetivo
                : 16;
        }

        // =====================================================================
        // Snapshot
        // =====================================================================

        private void EmitSnapshot()
        {
            if (_disposed) return;

            // Siempre sincronizar velocidad desde PGN de AOG (no depende de MQTT).
            SyncSpeedFromAOG();

            EvaluarInicio();

            // Evaluar alarmas para surcos sin datos recientes (timeout).
            EvaluarAlarmasPorTimeout();

            var snap = CreateSnapshot();
            var handler = SnapshotUpdated;
            if (handler != null) handler(snap);
        }

        // Marca como alerta los surcos de semilla que no recibieron telemetría
        // dentro del timeout, siempre que haya velocidad y monitoreo activo.
        private void EvaluarAlarmasPorTimeout()
        {
            lock (_lock)
            {
                if (!_monitoreoActivo) return;
                if (_velocidad < 1.5) return;

                var timeout = TimeSpan.FromMilliseconds(
                    _config.SensorTimeoutMs > 0 ? _config.SensorTimeoutMs : 3000);
                var now = DateTime.UtcNow;

                foreach (var kv in _surcos)
                {
                    var s = kv.Value;
                    if (s.SeccionCortada) continue;
                    if (!string.Equals(s.Tipo, "semilla", StringComparison.OrdinalIgnoreCase)) continue;

                    bool timedOut = s.LastUpdate == DateTime.MinValue
                        || (now - s.LastUpdate) > timeout;

                    if (timedOut)
                    {
                        s.Alerta = true;
                    }
                }
            }
        }

        // Ancho de cada sensor (tubo) y spacing en píxeles — constantes de layout
        private const int SensorWidthPx = 32;
        private const int SensorSpacingPx = 4;
        private const int ContainerWidthPx = 1200;

        public SeedMonitorSnapshot CreateSnapshot()
        {
            lock (_lock)
            {
                var surcosList = _surcos.Values.ToArray();
                var semillas = surcosList.Where(s => s.Tipo == "semilla").ToArray();

                int fallas = 0;
                double sumaSpm = 0;
                int countSpm = 0;

                foreach (var s in semillas)
                {
                    if (s.Alerta) fallas++;
                    if (s.Spm > 0) { sumaSpm += s.Spm; countSpm++; }
                }

                double promedio = countSpm > 0 ? Math.Round(sumaSpm / countSpm, 1) : 0;

                bool hasAlarm = fallas > 0;
                string alarmMsg = "";
                if (fallas > 0)
                {
                    var fallasStr = string.Join(", ",
                        semillas.Where(s => s.Alerta)
                            .Select(s => "T" + s.Tren + ":" + s.Bajada)
                            .ToArray());
                    alarmMsg = "FALLA EN " + fallasStr;
                }

                // Agrupar surcos por tren y calcular layout de centrado
                var trenesGroup = surcosList
                    .GroupBy(s => s.Tren)
                    .OrderBy(g => g.Key);

                var trenes = new List<TrenLayout>();
                foreach (var grupo in trenesGroup)
                {
                    var surcosTren = grupo.OrderBy(s => s.Bajada).ToArray();
                    int count = surcosTren.Length;

                    int totalWidth = count * SensorWidthPx + (count > 1 ? (count - 1) * SensorSpacingPx : 0);

                    // Clamp: si el grupo excede el contenedor, reducir spacing
                    int effectiveSpacing = SensorSpacingPx;
                    if (totalWidth > ContainerWidthPx && count > 1)
                    {
                        effectiveSpacing = Math.Max(0, (ContainerWidthPx - count * SensorWidthPx) / (count - 1));
                        totalWidth = count * SensorWidthPx + (count - 1) * effectiveSpacing;
                    }

                    // Offset X para centrar horizontalmente dentro del contenedor
                    int offsetX = Math.Max(0, (ContainerWidthPx - totalWidth) / 2);

                    trenes.Add(new TrenLayout
                    {
                        Tren = grupo.Key,
                        Count = count,
                        SensorWidthPx = SensorWidthPx,
                        SpacingPx = effectiveSpacing,
                        TotalWidthPx = totalWidth,
                        OffsetXPx = offsetX,
                        Objetivo = GetObjetivoTren(grupo.Key),
                        Surcos = surcosTren
                    });
                }

                var snapshot = new SeedMonitorSnapshot();
                snapshot.Velocidad = _velocidad;
                snapshot.SpmPromedio = promedio;
                snapshot.FallasActivas = fallas;
                snapshot.SurcosActivos = semillas.Length;
                snapshot.Surcos = surcosList;
                snapshot.Trenes = trenes.ToArray();
                snapshot.ContainerWidthPx = ContainerWidthPx;
                snapshot.LastUpdate = _lastDataTime;
                snapshot.IsConnected = _isConnected;
                snapshot.HasAlarm = hasAlarm;
                snapshot.AlarmMessage = alarmMsg;
                snapshot.NombreImplemento = _implemento.Nombre ?? "";
                snapshot.MonitoreoActivo = _monitoreoActivo;
                snapshot.MetodoInicio = _metodoInicio;
                snapshot.ToleranciaDesvio = _implemento != null && _implemento.Setup != null
                    ? _implemento.Setup.ToleranciaDesvio : 0;

                // Capturar posición GPS y geometría del implemento para logging.
                try
                {
                    if (_parent != null && _parent.AppModel != null)
                    {
                        snapshot.Latitude = _parent.AppModel.CurrentLatLon.Latitude;
                        snapshot.Longitude = _parent.AppModel.CurrentLatLon.Longitude;
                    }

                    if (_parent != null)
                    {
                        // Posición y heading del implemento (tool pivot).
                        snapshot.ToolEasting = _parent.toolPos.easting;
                        snapshot.ToolNorthing = _parent.toolPos.northing;
                        snapshot.ToolHeading = _parent.toolPos.heading;

                        // Calcular offset lateral de cada surco basado en la
                        // geometría del implemento. Distribuimos los surcos de
                        // cada tren uniformemente dentro del ancho de la herramienta.
                        if (_implemento != null && surcosList.Length > 0)
                        {
                            double toolWidth = _parent.tool != null ? _parent.tool.width : 0;
                            double toolOffset = _parent.tool != null ? _parent.tool.offset : 0;

                            if (toolWidth > 0)
                            {
                                var offsets = new double[surcosList.Length];
                                // Agrupar por tren para distribuir.
                                int idx = 0;
                                foreach (var grupo in trenesGroup)
                                {
                                    var surcosTren = grupo.OrderBy(s2 => s2.Bajada).ToArray();
                                    int cnt = surcosTren.Length;
                                    if (cnt == 0) continue;

                                    // Rango dentro del ancho total (dividido por trenes).
                                    double spacing = toolWidth / cnt;
                                    double startOffset = -(toolWidth / 2.0) + (spacing / 2.0) + toolOffset;

                                    for (int si = 0; si < cnt; si++)
                                    {
                                        if (idx < offsets.Length)
                                            offsets[idx] = startOffset + si * spacing;
                                        idx++;
                                    }
                                }
                                snapshot.SurcoLateralOffsets = offsets;
                            }
                        }
                    }
                }
                catch { }

                return snapshot;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_uiTimer != null) _uiTimer.Dispose();
            if (_mqtt != null) _mqtt.Dispose();
        }
    }
}
