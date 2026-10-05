// NodosEmulados.cs — nodos QuantiX y VistaX emulados por MQTT.
//
// BenchX ya hace de GPS, WAS, motor de direccion y maquina por UDP. Con esto
// tambien hace de los nodos ESP32 que PilotX ve por el broker embebido
// (:1883): un QuantiX de 7 motores (placa V0.4, 14 surcos a 2 por motor) y un
// VistaX de 14 sensores de semilla. Sirve para demo/Expo y para probar el
// bridge QuantiX y el monitor VistaX sin hardware.
//
// Protocolo (el mismo que hablan los firmwares reales, ver
// NodoRegistryService / QuantiXMotorBridge / SeedMonitor en PilotX):
//   QuantiX
//     agp/quantix/{uid}/announcement   ESP->PC  {uid, ip, fw, hw, device, motors, uptime, boot_reason}
//     agp/quantix/{uid}/lwt            ESP->PC  {"online":true} (retained; el broker manda false al caer)
//     agp/quantix/{uid}/target         PC->ESP  {"id":i,"pps":x,"seccion_on":b}   (cada 200 ms por motor)
//     agp/quantix/{uid}/status_live    ESP->PC  {id, pps_target, pps_real, pwm, rpm, pulsos}
//     agp/quantix/{uid}/config/desired PC->ESP  config completa (retained)
//     agp/quantix/{uid}/config/reported ESP->PC eco de la config aplicada (retained)
//   VistaX
//     agp/vistax/{uid}/announcement    ESP->PC  {uid, ip, fw, hw, device, cables, uptime, boot_reason}
//     agp/vistax/{uid}/lwt             ESP->PC  {"online":true}
//     vistax/{uid}/telemetria          ESP->PC  {uid, sensores:[{cable, valor(sem/s), raw}]}
//       con "Firmware 3.1" (default): igual que vistax-node v3.1.0 cada 250 ms
//       {schema:"agp.vistax.telemetry/2", seq, uid, sensores:[{cable, valor,
//        raw, acum, dt:[...0,1 ms...], dt_lost?}]} — ver GeneradorSemillas.
//
// Fisica simulada: cada motor sigue el pps objetivo con un retardo de primer
// orden (tau 0,6 s) y ruido del 1 %. Las semillas por segundo de cada surco
// salen de los pulsos del motor que lo alimenta:
//   vueltas/s = pps / dientes_engranaje ; sem/s motor = vueltas/s x semillas_vuelta
//   sem/s surco = sem/s motor / surcos_por_motor  (+ ruido 4 %)
// Asi VistaX "ve" exactamente lo que QuantiX dosifica, y al cruzar de ambiente
// en la prescripcion cambian los dos paneles a la vez.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchX.Config;
using MQTTnet;
using MQTTnet.Client;

namespace BenchX.Sim;

public sealed class MotorEmulado
{
    public int Id;
    public double PpsTarget;
    public double PpsReal;
    public bool SeccionOn;
    public int Pwm;
    public int Rpm;
    public long Pulsos;
    public DateTime UltimoTargetUtc = DateTime.MinValue;
}

public sealed class NodosEmulados : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly BenchXConfig _cfg;
    private readonly object _lock = new();
    private readonly Random _rnd = new();

    private IMqttClient? _qx, _vx;
    private CancellationTokenSource? _cts;
    private Task? _bucle;
    private readonly DateTime _arranque = DateTime.UtcNow;

    private readonly MotorEmulado[] _motores;
    private readonly double[] _semSeg;      // por cable (surco)
    private readonly long[] _rawCable;
    private double _dientes, _semVuelta;
    private int _seq;

    // Firmware 3.1: un generador por cable + pulsos de la ventana en curso.
    private readonly GeneradorSemillas[] _gen;
    private readonly long[] _pulsosVentana;
    private int _seqTelemetria;
    private DateTime _ultimaTelemetriaVx = DateTime.UtcNow;

    public bool EmularQuantiX { get; set; }
    public bool EmularVistaX { get; set; }

    /// <summary>VistaX como firmware 3.1 (dt + dt_lost, cada 250 ms).</summary>
    public bool VxFirmware31 { get; set; }

    /// <summary>Probabilidades del generador de semillas, en % (UI de BenchX).</summary>
    public double VxDoblesPct
    {
        get => _vxDoblesPct;
        set { _vxDoblesPct = Math.Clamp(value, 0, 50); AplicarEstadistica(); }
    }
    public double VxFallasPct
    {
        get => _vxFallasPct;
        set { _vxFallasPct = Math.Clamp(value, 0, 50); AplicarEstadistica(); }
    }
    public double VxCvPct
    {
        get => _vxCvPct;
        set { _vxCvPct = Math.Clamp(value, 0, 28); AplicarEstadistica(); }   // tope físico ~28,8 %
    }
    private double _vxDoblesPct, _vxFallasPct, _vxCvPct;

    /// <summary>Intervalos "dt" publicados y avisados como perdidos (diagnóstico).</summary>
    public long DtPublicados { get; private set; }
    public long DtPerdidos { get; private set; }
    public bool QxConectado { get; private set; }
    public bool VxConectado { get; private set; }
    public string UltimoError { get; private set; } = "";
    public long Publicados { get; private set; }
    public long TargetsRecibidos { get; private set; }
    public DateTime UltimoTargetUtc { get; private set; } = DateTime.MinValue;

    public NodosEmulados(BenchXConfig cfg)
    {
        _cfg = cfg;
        EmularQuantiX = cfg.EmularQuantiX;
        EmularVistaX = cfg.EmularVistaX;
        int nm = Math.Max(1, cfg.QxMotores);
        _motores = new MotorEmulado[nm];
        for (int i = 0; i < nm; i++) _motores[i] = new MotorEmulado { Id = i };
        _semSeg = new double[Math.Max(1, cfg.VxCables)];
        _rawCable = new long[_semSeg.Length];
        _dientes = cfg.DientesEngranaje > 0 ? cfg.DientesEngranaje : 600;
        _semVuelta = cfg.SemillasPorVuelta > 0 ? cfg.SemillasPorVuelta : 24;
        _gen = new GeneradorSemillas[_semSeg.Length];
        for (int i = 0; i < _gen.Length; i++) _gen[i] = new GeneradorSemillas(1000 + i);
        _pulsosVentana = new long[_semSeg.Length];
        VxFirmware31 = cfg.VxFirmware31;
        _vxDoblesPct = Math.Clamp(cfg.VxDoblesPct, 0, 50);
        _vxFallasPct = Math.Clamp(cfg.VxFallasPct, 0, 50);
        _vxCvPct = Math.Clamp(cfg.VxCvPct, 0, 28);
        AplicarEstadistica();
    }

    private void AplicarEstadistica()
    {
        if (_gen == null) return;
        lock (_lock)
        {
            foreach (var g in _gen)
            {
                g.PDoble = _vxDoblesPct / 100.0;
                g.PFalla = _vxFallasPct / 100.0;
                g.Cv = _vxCvPct / 100.0;
            }
        }
    }

    // ------------------------------------------------------------------
    //  Ciclo de vida
    // ------------------------------------------------------------------
    public void Start()
    {
        if (_bucle != null) return;
        _cts = new CancellationTokenSource();
        _bucle = Task.Run(() => Bucle(_cts.Token));
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _bucle?.Wait(1500); } catch { }
        Desconectar(ref _qx, "agp/quantix/" + _cfg.QxUid + "/lwt");
        Desconectar(ref _vx, "agp/vistax/" + _cfg.VxUid + "/lwt");
    }

    private void Desconectar(ref IMqttClient? c, string lwtTopic)
    {
        var cli = c; c = null;
        if (cli == null) return;
        try
        {
            if (cli.IsConnected)
            {
                // Salida limpia: el broker no dispara el LWT, asi que lo mandamos nosotros.
                cli.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(lwtTopic)
                    .WithPayload("{\"online\":false,\"reason\":\"bench_stop\"}").WithRetainFlag(true).Build())
                    .Wait(800);
                cli.DisconnectAsync().Wait(800);
            }
        }
        catch { }
        try { cli.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------
    //  Snapshot para la UI (copia bajo lock; la UI no toca el estado vivo)
    // ------------------------------------------------------------------
    public MotorEmulado[] SnapshotMotores()
    {
        lock (_lock)
        {
            return SnapshotMotoresSinLock();
        }
    }

    public double[] SnapshotSemillas()
    {
        lock (_lock) return (double[])_semSeg.Clone();
    }

    // ------------------------------------------------------------------
    //  Bucle principal: conecta, anuncia, simula, publica
    // ------------------------------------------------------------------
    private async Task Bucle(CancellationToken ct)
    {
        DateTime ultimoAnuncio = DateTime.MinValue, ultimoLive = DateTime.MinValue, ultimoTick = DateTime.UtcNow, ultimoIntento = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ahora = DateTime.UtcNow;

                // Conexiones (reintento cada 3 s, sin bloquear el resto)
                if ((ahora - ultimoIntento).TotalSeconds >= 3)
                {
                    ultimoIntento = ahora;
                    if (EmularQuantiX && (_qx == null || !_qx.IsConnected)) await ConectarQuantiX(ct);
                    if (EmularVistaX && (_vx == null || !_vx.IsConnected)) await ConectarVistaX(ct);
                    if (!EmularQuantiX && _qx != null) { Desconectar(ref _qx, "agp/quantix/" + _cfg.QxUid + "/lwt"); QxConectado = false; }
                    if (!EmularVistaX && _vx != null) { Desconectar(ref _vx, "agp/vistax/" + _cfg.VxUid + "/lwt"); VxConectado = false; }
                }
                QxConectado = _qx != null && _qx.IsConnected;
                VxConectado = _vx != null && _vx.IsConnected;

                // Fisica de los motores (100 ms)
                double dt = Math.Min(0.5, (ahora - ultimoTick).TotalSeconds);
                ultimoTick = ahora;
                Simular(dt);

                // Anuncio cada 10 s (igual que los firmwares)
                if ((ahora - ultimoAnuncio).TotalSeconds >= 10)
                {
                    ultimoAnuncio = ahora;
                    await Anunciar(ct);
                }

                // Telemetria QuantiX cada 500 ms
                if ((ahora - ultimoLive).TotalMilliseconds >= 500)
                {
                    ultimoLive = ahora;
                    await PublicarLive(ct);
                }

                // Telemetria VistaX: 250 ms como el firmware 3.1 (deltaT del
                // nodo), 500 ms la vieja.
                if ((ahora - _ultimaTelemetriaVx).TotalMilliseconds >= (VxFirmware31 ? 250 : 500))
                {
                    double deltaMs = (ahora - _ultimaTelemetriaVx).TotalMilliseconds;
                    _ultimaTelemetriaVx = ahora;
                    await PublicarVistaX(deltaMs, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { UltimoError = ex.Message; }

            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConectarQuantiX(CancellationToken ct)
    {
        try
        {
            _qx?.Dispose();
            var cli = new MqttFactory().CreateMqttClient();
            var opts = new MqttClientOptionsBuilder()
                .WithTcpServer(_cfg.BrokerHost, _cfg.BrokerPort)
                .WithClientId(_cfg.QxUid)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                .WithTimeout(TimeSpan.FromSeconds(4))
                .WithWillTopic("agp/quantix/" + _cfg.QxUid + "/lwt")
                .WithWillPayload("{\"online\":false,\"reason\":\"lwt\"}")
                .WithWillRetain(true)
                .Build();
            cli.ApplicationMessageReceivedAsync += e =>
            {
                try { AlRecibirQuantiX(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? ""); }
                catch (Exception ex) { UltimoError = ex.Message; }
                return Task.CompletedTask;
            };
            await cli.ConnectAsync(opts, ct);
            string b = "agp/quantix/" + _cfg.QxUid + "/";
            await cli.SubscribeAsync(b + "target", cancellationToken: ct);
            await cli.SubscribeAsync(b + "config", cancellationToken: ct);
            await cli.SubscribeAsync(b + "config/desired", cancellationToken: ct);
            await cli.SubscribeAsync(b + "cmd/#", cancellationToken: ct);
            await Publicar(cli, b + "lwt", "{\"online\":true}", true, ct);
            _qx = cli;
            UltimoError = "";
        }
        catch (Exception ex) { UltimoError = "QuantiX: " + ex.Message; }
    }

    private async Task ConectarVistaX(CancellationToken ct)
    {
        try
        {
            _vx?.Dispose();
            var cli = new MqttFactory().CreateMqttClient();
            var opts = new MqttClientOptionsBuilder()
                .WithTcpServer(_cfg.BrokerHost, _cfg.BrokerPort)
                .WithClientId(_cfg.VxUid)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                .WithTimeout(TimeSpan.FromSeconds(4))
                .WithWillTopic("agp/vistax/" + _cfg.VxUid + "/lwt")
                .WithWillPayload("{\"online\":false,\"reason\":\"lwt\"}")
                .WithWillRetain(true)
                .Build();
            await cli.ConnectAsync(opts, ct);
            await Publicar(cli, "agp/vistax/" + _cfg.VxUid + "/lwt", "{\"online\":true}", true, ct);
            _vx = cli;
            UltimoError = "";
        }
        catch (Exception ex) { UltimoError = "VistaX: " + ex.Message; }
    }

    // ------------------------------------------------------------------
    //  Entrada: targets y config de PilotX
    // ------------------------------------------------------------------
    private void AlRecibirQuantiX(string topic, string payload)
    {
        if (topic.EndsWith("/target", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            int id = r.TryGetProperty("id", out var jid) ? jid.GetInt32() : -1;
            double pps = r.TryGetProperty("pps", out var jp) ? jp.GetDouble() : 0;
            bool on = r.TryGetProperty("seccion_on", out var jo) && jo.ValueKind == JsonValueKind.True;
            if (id < 0 || id >= _motores.Length) return;
            lock (_lock)
            {
                _motores[id].PpsTarget = on ? Math.Max(0, pps) : 0;
                _motores[id].SeccionOn = on;
                _motores[id].UltimoTargetUtc = DateTime.UtcNow;
                UltimoTargetUtc = _motores[id].UltimoTargetUtc;
                TargetsRecibidos++;
            }
            return;
        }

        if (topic.EndsWith("/config/desired", StringComparison.Ordinal) || topic.EndsWith("/config", StringComparison.Ordinal))
        {
            // Tomar dientes/semillas si vienen, y "aplicar" la config: se
            // reporta tal cual con ts_ms nuevo, que es lo que el tracker de
            // sincronizacion de PilotX mira (reported >= desired -> in_sync).
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var r = doc.RootElement;
                if (r.TryGetProperty("configs", out var cfgs) && cfgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cfgs.EnumerateArray())
                    {
                        if (c.TryGetProperty("dientes_engranaje", out var d) && d.TryGetDouble(out var dv) && dv > 0) _dientes = dv;
                        else if (c.TryGetProperty("pulses_per_rev", out var p) && p.TryGetDouble(out var pv) && pv > 0) _dientes = pv;
                        break;
                    }
                }
            }
            catch { }
            if (topic.EndsWith("/config/desired", StringComparison.Ordinal) && _qx != null)
            {
                string reportado = ConTsMs(payload);
                _ = Publicar(_qx, "agp/quantix/" + _cfg.QxUid + "/config/reported", reportado, true, CancellationToken.None);
            }
            return;
        }

        if (topic.Contains("/cmd/", StringComparison.Ordinal) && _qx != null)
        {
            // Acuse generico: el nodo real contesta {cmd_id, status}. Sin cmd_id no hay nada que acusar.
            string cmdId = CampoJson(payload, "cmd_id");
            if (!string.IsNullOrEmpty(cmdId))
                _ = Publicar(_qx, "agp/quantix/" + _cfg.QxUid + "/ack",
                    "{\"cmd_id\":\"" + cmdId + "\",\"status\":\"ok\",\"detail\":\"bench\",\"ts_ms\":" + Ms() + "}", false, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------
    //  Simulacion
    // ------------------------------------------------------------------
    private void Simular(double dt)
    {
        const double tau = 0.6;              // s, respuesta del motor
        double alfa = dt <= 0 ? 0 : Math.Min(1, dt / tau);
        var ahora = DateTime.UtcNow;
        lock (_lock)
        {
            for (int i = 0; i < _motores.Length; i++)
            {
                var m = _motores[i];
                // Sin target hace mas de 2 s: el firmware real corta por seguridad.
                double objetivo = (ahora - m.UltimoTargetUtc).TotalSeconds > 2 ? 0 : m.PpsTarget;
                m.PpsReal += (objetivo - m.PpsReal) * alfa;
                double ruido = 1 + (_rnd.NextDouble() - 0.5) * 0.02;
                double real = m.PpsReal < 0.5 ? 0 : m.PpsReal * ruido;
                m.Rpm = (int)Math.Round(real * 60.0 / _dientes);
                m.Pwm = real <= 0 ? 0 : (int)Math.Clamp(600 + real / 1700.0 * 3495, 0, 4095);
                m.Pulsos += (long)Math.Round(real * dt);

                // Semillas: vueltas/s x semillas por vuelta, repartidas en los surcos del motor
                double semSegMotor = real / _dientes * _semVuelta;
                double porSurco = semSegMotor / Math.Max(1, _cfg.SurcosPorMotor);
                for (int s = 0; s < _cfg.SurcosPorMotor; s++)
                {
                    int cable = i * _cfg.SurcosPorMotor + s;     // 0-based
                    if (cable >= _semSeg.Length) break;
                    if (VxFirmware31)
                    {
                        // Firmware 3.1: semillas una por una con la estadística
                        // configurada; la tasa nominal (sin el ruido del 4 %) es
                        // la referencia del espaciamiento ideal.
                        double nominal = porSurco < 0.05 ? 0 : porSurco;
                        int caidas = _gen[cable].Avanzar(dt, nominal);
                        _pulsosVentana[cable] += caidas;
                        _semSeg[cable] = nominal;
                        _rawCable[cable] = _gen[cable].Acumulado;
                        continue;
                    }
                    double v = porSurco * (1 + (_rnd.NextDouble() - 0.5) * 0.08);
                    _semSeg[cable] = v < 0.05 ? 0 : v;
                    _rawCable[cable] += (long)Math.Round(_semSeg[cable] * dt);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    //  Salida
    // ------------------------------------------------------------------
    private async Task Anunciar(CancellationToken ct)
    {
        long up = (long)(DateTime.UtcNow - _arranque).TotalSeconds;
        if (_qx != null && _qx.IsConnected)
        {
            string a = "{\"uid\":\"" + _cfg.QxUid + "\",\"ip\":\"" + _cfg.BrokerHost + "\",\"fw\":\"3.0.0-bench\",\"hw\":\"V0.4\"," +
                       "\"device\":\"QuantiX\",\"motors\":" + _motores.Length + ",\"uptime\":" + up +
                       ",\"boot_reason\":\"bench\",\"safe_mode\":false,\"crash_count\":0,\"schema\":\"quantix.announce\",\"seq\":" + (++_seq) + "}";
            await Publicar(_qx, "agp/quantix/" + _cfg.QxUid + "/announcement", a, false, ct);
        }
        if (_vx != null && _vx.IsConnected)
        {
            string fw = VxFirmware31 ? "3.1.0-bench" : "2.0.0-bench";
            string a = "{\"uid\":\"" + _cfg.VxUid + "\",\"ip\":\"" + _cfg.BrokerHost + "\",\"fw\":\"" + fw + "\",\"hw\":\"V1\"," +
                       "\"device\":\"VistaX\",\"cables\":" + _semSeg.Length + ",\"uptime\":" + up +
                       ",\"boot_reason\":\"bench\",\"safe_mode\":false,\"crash_count\":0,\"schema\":\"vistax.announce\",\"seq\":" + (++_seq) + "}";
            await Publicar(_vx, "agp/vistax/" + _cfg.VxUid + "/announcement", a, false, ct);
        }
    }

    private async Task PublicarLive(CancellationToken ct)
    {
        MotorEmulado[] ms;
        lock (_lock) { ms = SnapshotMotoresSinLock(); }

        if (_qx != null && _qx.IsConnected)
        {
            foreach (var m in ms)
            {
                string p = "{\"id\":" + m.Id +
                           ",\"pps_target\":" + m.PpsTarget.ToString("0.##", Inv) +
                           ",\"pps_real\":" + m.PpsReal.ToString("0.##", Inv) +
                           ",\"pwm\":" + m.Pwm + ",\"rpm\":" + m.Rpm + ",\"pulsos\":" + m.Pulsos +
                           ",\"seccion_on\":" + (m.SeccionOn ? "true" : "false") + "}";
                await Publicar(_qx, "agp/quantix/" + _cfg.QxUid + "/status_live", p, false, ct);
            }
        }
    }

    private async Task PublicarVistaX(double deltaMs, CancellationToken ct)
    {
        var cli = _vx;
        bool conectado = cli != null && cli.IsConnected;
        if (!VxFirmware31)
        {
            if (!conectado) return;
            double[] sem; long[] raw;
            lock (_lock) { sem = (double[])_semSeg.Clone(); raw = (long[])_rawCable.Clone(); }
            var sb = new StringBuilder(64 + sem.Length * 40);
            sb.Append("{\"uid\":\"").Append(_cfg.VxUid).Append("\",\"sensores\":[");
            for (int c = 0; c < sem.Length; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append("{\"cable\":").Append(c + 1).Append(",\"valor\":").Append(sem[c].ToString("0.##", Inv))
                  .Append(",\"raw\":").Append(raw[c]).Append('}');
            }
            sb.Append("]}");
            await Publicar(cli!, "vistax/" + _cfg.VxUid + "/telemetria", sb.ToString(), false, ct);
            return;
        }

        // Firmware 3.1: drenar la ventana SIEMPRE (como enviarTelemetria):
        // sin broker los pulsos se tiran y los intervalos van a dt_lost.
        var cables = new CableTelemetria[_gen.Length];
        lock (_lock)
        {
            for (int c = 0; c < _gen.Length; c++)
            {
                long pulsos = _pulsosVentana[c];
                _pulsosVentana[c] = 0;
                if (!conectado) { _gen[c].Descartar(); continue; }
                var ct2 = new CableTelemetria
                {
                    Cable = c + 1,
                    Pulsos = pulsos,
                    Valor = deltaMs > 0 ? pulsos * 1000.0 / deltaMs : 0,
                    Acum = _gen[c].Acumulado,
                };
                _gen[c].Pop(GeneradorSemillas.MaxDtPorMsg, ct2.Dt, out uint perdidos);
                ct2.DtLost = perdidos;
                DtPublicados += ct2.Dt.Count;
                DtPerdidos += perdidos;
                cables[c] = ct2;
            }
        }
        if (!conectado) return;
        string payload = PayloadTelemetriaV2(_cfg.VxUid, ++_seqTelemetria, cables);
        await Publicar(cli!, "vistax/" + _cfg.VxUid + "/telemetria", payload, false, ct);
    }

    /// <summary>Un cable PULSE en la telemetría del firmware 3.1.</summary>
    public sealed class CableTelemetria
    {
        public int Cable;
        public double Valor;
        public long Pulsos;
        public long Acum;
        public List<int> Dt = new();
        public uint DtLost;
    }

    /// <summary>
    /// Telemetría agp.vistax.telemetry/2 con el mismo orden de campos que
    /// vistax-node v3.1.0 (Network.cpp, enviarTelemetria): schema, seq, uid,
    /// sensores[{cable, valor, raw, acum, dt[], dt_lost (solo si &gt; 0)}].
    /// "dt" va siempre, vacío si no cayó ninguna semilla.
    /// </summary>
    public static string PayloadTelemetriaV2(string uid, int seq, IReadOnlyList<CableTelemetria> cables)
    {
        var sb = new StringBuilder(96 + cables.Count * 200);
        sb.Append("{\"schema\":\"agp.vistax.telemetry/2\",\"seq\":").Append(seq.ToString(Inv))
          .Append(",\"uid\":\"").Append(uid).Append("\",\"sensores\":[");
        bool primero = true;
        foreach (var c in cables)
        {
            if (c == null) continue;
            if (!primero) sb.Append(',');
            primero = false;
            sb.Append("{\"cable\":").Append(c.Cable.ToString(Inv))
              .Append(",\"valor\":").Append(c.Valor.ToString("0.###", Inv))
              .Append(",\"raw\":").Append(c.Pulsos.ToString(Inv))
              .Append(",\"acum\":").Append(c.Acum.ToString(Inv))
              .Append(",\"dt\":[");
            for (int i = 0; i < c.Dt.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(c.Dt[i].ToString(Inv));
            }
            sb.Append(']');
            if (c.DtLost > 0) sb.Append(",\"dt_lost\":").Append(c.DtLost.ToString(Inv));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private MotorEmulado[] SnapshotMotoresSinLock()
    {
        var copia = new MotorEmulado[_motores.Length];
        for (int i = 0; i < _motores.Length; i++)
        {
            var m = _motores[i];
            copia[i] = new MotorEmulado
            {
                Id = m.Id,
                PpsTarget = m.PpsTarget,
                PpsReal = m.PpsReal,
                SeccionOn = m.SeccionOn,
                Pwm = m.Pwm,
                Rpm = m.Rpm,
                Pulsos = m.Pulsos,
            };
        }
        return copia;
    }

    private async Task Publicar(IMqttClient cli, string topic, string payload, bool retain, CancellationToken ct)
    {
        var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).Build();
        await cli.PublishAsync(msg, ct);
        Publicados++;
    }

    private static long Ms() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Reemplaza o agrega "ts_ms" en un JSON chato sin deserializar todo.
    private static string ConTsMs(string json)
    {
        string ts = "\"ts_ms\":" + Ms();
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"ts_ms\"\\s*:\\s*[0-9]+");
        if (m.Success) return json.Substring(0, m.Index) + ts + json.Substring(m.Index + m.Length);
        int i = json.LastIndexOf('}');
        return i < 0 ? json : json.Substring(0, i) + (json.TrimEnd().EndsWith("{") ? "" : ",") + ts + "}";
    }

    private static string CampoJson(string json, string campo)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + campo + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
}
