using System;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using BenchX.Config;
using BenchX.Red;
using BenchX.Sim;

namespace BenchX;

// Une simulador + NMEA + PGN + UDP + config con la ventana. Regla de oro:
// el socket dispara en su hilo; TODO lo que toque propiedades bindeadas pasa
// por el tick del DispatcherTimer o por Dispatcher.UIThread.Post.
public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly BenchXConfig _config;
    private readonly SimuladorVehiculo _sim = new();
    private readonly PgnProcessor _pgn = new();
    private readonly UdpLink _link;
    private readonly NodosEmulados _nodos;
    private readonly DispatcherTimer _timer;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notificar([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    // Pide reinicio con la subred nueva (PGN 201) — lo cablea MainWindow.
    public event Action<string>? ReinicioPedido;

    public MainViewModel() : this(BenchXConfig.RutaDefault) { }

    public MainViewModel(string rutaConfig)
    {
        _rutaConfig = rutaConfig;
        _config = BenchXConfig.Cargar(rutaConfig);
        _sim.Latitude = _config.Latitud;
        _sim.Longitude = _config.Longitud;
        _pgn.Subred1 = _config.Subred1; _pgn.Subred2 = _config.Subred2; _pgn.Subred3 = _config.Subred3;

        Gga = _config.Gga; Vtg = _config.Vtg; Avr = _config.Avr; Hdt = _config.Hdt;
        Rmc = _config.Rmc; Ogi = _config.Ogi; Nda = _config.Nda; Ksxt = _config.Ksxt;
        EmularGps = _config.EmularGps; EmularWas = _config.EmularWas; EmularMotor = _config.EmularMotor;
        EmularMaquina = _config.EmularMaquina; EmularImu = _config.EmularImu;
        LatInicial = _config.Latitud.ToString("N7", Inv);
        LonInicial = _config.Longitud.ToString("N7", Inv);

        _link = new UdpLink(_config.Subred1, _config.Subred2, _config.Subred3);
        _link.DatagramaRecibido += AlRecibir;

        // Nodos QuantiX/VistaX emulados por MQTT (broker embebido de PilotX).
        _nodos = new NodosEmulados(_config);
        _emularQuantiX = _config.EmularQuantiX; _emularVistaX = _config.EmularVistaX;
        BrokerTexto = $"{_config.BrokerHost}:{_config.BrokerPort}  ·  {_config.QxUid} ({_config.QxMotores} motores)  ·  {_config.VxUid} ({_config.VxCables} sensores)";
        _nodos.Start();

        // Demo que se maneja sola (Expo): config o argumento --demo.
        _demo = new DemoAuto(_config);
        bool argDemo = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
        _demoActivo = _config.Demo || argDemo;
        _demo.Activo = _demoActivo;
        if (_demoActivo) SwitchTrabajo = true;   // por si PilotX tiene el switch de trabajo habilitado
        DemoResumenTexto = $"lote \"{_config.DemoLote}\" · {_config.DemoAnchoM:0} x {_config.DemoLargoM:0} m · labor {_config.DemoAnchoLaborM:0.00} m · {_config.DemoVueltasCabecera} vueltas de cabecera · {_config.DemoVelocidadKmh:0} km/h";
        _demo.Start();

        IpsLocales = LeerIpsLocales();
        SubredTexto = $"{_config.Subred1}.{_config.Subred2}.{_config.Subred3}.255:9999";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private readonly string _rutaConfig;

    // ------------------------- sliders / entradas -------------------------

    private double _velocidadKmh, _anguloDireccion, _roll;
    public double VelocidadKmh { get => _velocidadKmh; set { _velocidadKmh = value; Notificar(); Notificar(nameof(VelocidadTexto)); Notificar(nameof(MSegTexto)); } }
    public double AnguloDireccion { get => _anguloDireccion; set { _anguloDireccion = value; Notificar(); Notificar(nameof(AnguloTexto)); } }
    public double Roll { get => _roll; set { _roll = value; Notificar(); Notificar(nameof(RollTexto)); } }

    public string VelocidadTexto => VelocidadKmh.ToString("N1", Inv) + " km/h";
    public string MSegTexto => (VelocidadKmh * 0.27777777).ToString("N1", Inv) + " m/s";
    public string AnguloTexto => AnguloDireccion.ToString("N2", Inv) + "°";
    public string RollTexto => Roll.ToString("N2", Inv) + "°";

    public void CeroVelocidad() => VelocidadKmh = 0;
    public void CeroAngulo() => AnguloDireccion = 0;
    public void CeroRoll() => Roll = 0;

    public bool Gga { get; set; }
    public bool Vtg { get; set; }
    public bool Avr { get; set; }
    public bool Hdt { get; set; }
    public bool Rmc { get; set; }
    public bool Ogi { get; set; }
    public bool Nda { get; set; }
    public bool Ksxt { get; set; }

    private string _latInicial = "", _lonInicial = "";
    public string LatInicial { get => _latInicial; set { _latInicial = value; Notificar(); } }
    public string LonInicial { get => _lonInicial; set { _lonInicial = value; Notificar(); } }

    public void GuardarPosicion()
    {
        if (double.TryParse(LatInicial, NumberStyles.Float, Inv, out double lat) &&
            double.TryParse(LonInicial, NumberStyles.Float, Inv, out double lon) &&
            Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180)
        {
            _sim.Latitude = lat; _sim.Longitude = lon;
            _config.Latitud = lat; _config.Longitud = lon;
            _config.Guardar(_rutaConfig);
        }
    }

    // Switches activo-bajo (igual que ModSim): prendido en UI = 0 en el wire.
    private bool _switchTrabajo, _switchDireccion;
    public bool SwitchTrabajo { get => _switchTrabajo; set { _switchTrabajo = value; _pgn.WorkSwitch = value ? 0 : 1; Notificar(); } }
    public bool SwitchDireccion { get => _switchDireccion; set { _switchDireccion = value; _pgn.SteerSwitch = value ? 0 : 1; Notificar(); } }

    // Banco con ECU real: se apaga el módulo que maneje la ECU conectada.
    private bool _emularGps = true, _emularWas = true, _emularMotor = true, _emularMaquina = true, _emularImu = true;
    public bool EmularGps { get => _emularGps; set { _emularGps = value; Notificar(); } }
    public bool EmularWas { get => _emularWas; set { _emularWas = value; _pgn.EmularWas = value; Notificar(); } }
    public bool EmularMotor { get => _emularMotor; set { _emularMotor = value; Notificar(); } }
    public bool EmularMaquina { get => _emularMaquina; set { _emularMaquina = value; _pgn.EmularMaquina = value; Notificar(); } }
    public bool EmularImu { get => _emularImu; set { _emularImu = value; _pgn.EmularImu = value; Notificar(); } }
    public void BotonDireccionRemoto() => _pgn.SteerSwitch = _pgn.SteerSwitch > 0 ? 0 : 1;

    // Nodos emulados por MQTT (QuantiX 7 motores + VistaX 14 sensores).
    private bool _emularQuantiX = true, _emularVistaX = true;
    public bool EmularQuantiX { get => _emularQuantiX; set { _emularQuantiX = value; _nodos.EmularQuantiX = value; Notificar(); } }
    public bool EmularVistaX { get => _emularVistaX; set { _emularVistaX = value; _nodos.EmularVistaX = value; Notificar(); } }
    public string BrokerTexto { get; private set; } = "";
    public bool QxConectado { get; private set; }
    public bool VxConectado { get; private set; }
    public string QxEstadoTexto { get; private set; } = "sin broker";
    public string VxEstadoTexto { get; private set; } = "sin broker";
    public string MotoresTexto { get; private set; } = "—";
    public string SemillasTexto { get; private set; } = "—";
    public string NodosStatsTexto { get; private set; } = "—";
    public string NodosErrorTexto { get; private set; } = "";
    public bool NodosConError => !string.IsNullOrEmpty(NodosErrorTexto);

    // Demo que se maneja sola (Expo).
    private readonly DemoAuto _demo;
    private bool _demoActivo;
    public bool DemoActivo
    {
        get => _demoActivo;
        set
        {
            _demoActivo = value; _demo.Activo = value;
            if (!value) { VelocidadKmh = 0; AnguloDireccion = 0; }
            Notificar();
        }
    }
    public string DemoResumenTexto { get; private set; } = "";
    public string DemoEstadoTexto { get; private set; } = "—";
    public string DemoPilotXTexto { get; private set; } = "—";
    public bool DemoPilotXListo { get; private set; }

    // ------------------------- lecturas live -------------------------

    public string RumboTexto { get; private set; } = "0.00°";
    public string LatActualTexto { get; private set; } = "";
    public string LonActualTexto { get; private set; } = "";

    public bool GuiadoActivo { get; private set; }
    public string GuiadoTexto { get; private set; } = "guiado inactivo";
    public string SetPointTexto { get; private set; } = "—";
    public string VelPilotXTexto { get; private set; } = "—";
    public string KpTexto { get; private set; } = "—";
    public string HighPwmTexto { get; private set; } = "—";
    public string LowPwmTexto { get; private set; } = "—";
    public string MinPwmTexto { get; private set; } = "—";
    public string CountsTexto { get; private set; } = "—";
    public string OffsetTexto { get; private set; } = "—";
    public string AckermanTexto { get; private set; } = "—";
    public string FlagsTexto { get; private set; } = "—";

    public string RelesDireccion { get; private set; } = Puntos(0, 16);
    public string RelesMaquina { get; private set; } = Puntos(0, 16);
    public string Zona1 { get; private set; } = Puntos(0, 8);
    public string Zona2 { get; private set; } = Puntos(0, 8);
    public string Zona3 { get; private set; } = Puntos(0, 8);
    public string Zona4 { get; private set; } = Puntos(0, 8);
    public string Zona5 { get; private set; } = Puntos(0, 8);
    public string Zona6 { get; private set; } = Puntos(0, 8);
    public string Zona7 { get; private set; } = Puntos(0, 8);
    public string Zona8 { get; private set; } = Puntos(0, 8);
    public string UTurnTexto { get; private set; } = "—";
    public string HydLiftTexto { get; private set; } = "—";
    public string TramTexto { get; private set; } = "—";
    public string VelMaquinaTexto { get; private set; } = "—";

    public string IpsLocales { get; }
    public string SubredTexto { get; }
    public string RxTexto { get; private set; } = "0";
    public string TxTexto { get; private set; } = "0";
    public bool ScanRespondido { get; private set; }
    public string ScanTexto { get; private set; } = "sin responder";
    public string? ErrorRed => _link.ErrorBind;
    public bool RedOk => _link.Conectado;

    // ------------------------- ciclo -------------------------

    private void Tick()
    {
        // Con guiado activo el volante lo maneja PilotX: el slider sigue al
        // setpoint — ese es el "motor perfecto" simulado. Con Motor apagado
        // (motor real en el banco) el ángulo queda en manos del slider.
        if (DemoActivo)
        {
            // La demo maneja: velocidad y volante salen del conductor
            // automatico, no de los sliders ni del setpoint de PilotX.
            _demo.Conducir(_sim.Latitude, _sim.Longitude, _sim.HeadingDeg);
            VelocidadKmh = _demo.SalidaVelocidadKmh;
            AnguloDireccion = _demo.SalidaAnguloDeg;
        }
        else if (_pgn.GuidanceStatus != 0 && EmularMotor)
            AnguloDireccion = _pgn.SteerAngleSetPoint;

        _sim.SpeedKmh = VelocidadKmh;
        _sim.SteerAngleDeg = AnguloDireccion;
        _sim.RollDeg = Roll;
        _sim.Estado.TimeNow = DateTime.UtcNow.ToString("HHmmss.fff,", Inv);
        _sim.Avanzar();
        _sim.Estado.ImuValido = EmularImu; // IMU apagado → PANDA con campos neutros
        _pgn.SteerAngleActual = _sim.SteerAngleDeg;

        var g = _sim.Estado;
        if (EmularGps)
        {
            if (Vtg) _link.Enviar(NmeaBuilder.BuildVtg(g));
            if (Avr) _link.Enviar(NmeaBuilder.BuildAvr(g));
            if (Hdt) _link.Enviar(NmeaBuilder.BuildHdt(g));
            if (Gga) _link.Enviar(NmeaBuilder.BuildGga(g));
            if (Rmc) _link.Enviar(NmeaBuilder.BuildRmc(g));
            if (Ogi) _link.Enviar(NmeaBuilder.BuildOgi(g));
            if (Nda) _link.Enviar(NmeaBuilder.BuildNda(g));
            if (Ksxt) _link.Enviar(NmeaBuilder.BuildKsxt(g));
        }

        RefrescarLecturas();
    }

    private void RefrescarLecturas()
    {
        RumboTexto = _sim.HeadingDeg.ToString("N2", Inv) + "°";
        LatActualTexto = _sim.Latitude.ToString("N7", Inv);
        LonActualTexto = _sim.Longitude.ToString("N7", Inv);

        GuiadoActivo = _pgn.GuidanceStatus != 0;
        GuiadoTexto = GuiadoActivo ? "guiado activo" : "guiado inactivo";
        SetPointTexto = _pgn.SteerAngleSetPoint.ToString("N2", Inv) + "°";
        VelPilotXTexto = _pgn.GpsSpeedPilotX.ToString("N1", Inv) + " km/h";
        KpTexto = _pgn.Kp.ToString(Inv);
        HighPwmTexto = _pgn.HighPwm.ToString(Inv);
        LowPwmTexto = _pgn.LowPwm.ToString(Inv);
        MinPwmTexto = _pgn.MinPwm.ToString(Inv);
        CountsTexto = _pgn.SensorCounts.ToString(Inv);
        OffsetTexto = _pgn.WasOffset.ToString(Inv);
        AckermanTexto = _pgn.AckermanPct.ToString("N0", Inv) + "%";
        FlagsTexto =
            $"InvertWAS {_pgn.InvertWas} · RelayAlto {_pgn.RelayActiveHigh} · Motor {_pgn.MotorDir} · " +
            $"WAS único {_pgn.SingleInputWas} · Cytron {_pgn.Cytron} · Switch {_pgn.SteerSwitchCfg} · " +
            $"Botón {_pgn.SteerButtonCfg} · Encoder {_pgn.ShaftEncoder} · Danfoss {_pgn.Danfoss}";

        RelesDireccion = Puntos(_pgn.Relay | (_pgn.RelayHi << 8), 16);
        RelesMaquina = Puntos(_pgn.RelayLoM | (_pgn.RelayHiM << 8), 16);
        Zona1 = Puntos(_pgn.Zonas[0], 8); Zona2 = Puntos(_pgn.Zonas[1], 8);
        Zona3 = Puntos(_pgn.Zonas[2], 8); Zona4 = Puntos(_pgn.Zonas[3], 8);
        Zona5 = Puntos(_pgn.Zonas[4], 8); Zona6 = Puntos(_pgn.Zonas[5], 8);
        Zona7 = Puntos(_pgn.Zonas[6], 8); Zona8 = Puntos(_pgn.Zonas[7], 8);
        UTurnTexto = _pgn.UTurn.ToString(Inv);
        HydLiftTexto = _pgn.HydLift.ToString(Inv);
        TramTexto = _pgn.Tramline.ToString(Inv);
        VelMaquinaTexto = _pgn.GpsSpeedMaquina.ToString("N1", Inv) + " km/h";

        RxTexto = _link.Rx.ToString(Inv);
        TxTexto = _link.Tx.ToString(Inv);
        ScanRespondido = _pgn.ScanRespondido;
        ScanTexto = ScanRespondido ? "respondido" : "sin responder";

        // Nodos emulados
        QxConectado = _nodos.QxConectado;
        VxConectado = _nodos.VxConectado;
        bool hayTarget = (DateTime.UtcNow - _nodos.UltimoTargetUtc).TotalSeconds < 2;
        QxEstadoTexto = !EmularQuantiX ? "apagado" : !QxConectado ? "sin broker" : hayTarget ? "recibiendo targets" : "conectado, sin targets";
        VxEstadoTexto = !EmularVistaX ? "apagado" : !VxConectado ? "sin broker" : "publicando";
        var ms = _nodos.SnapshotMotores();
        var sbM = new StringBuilder();
        foreach (var m in ms)
            sbM.Append('M').Append(m.Id + 1).Append(m.SeccionOn ? " ●" : " ○").Append(' ')
               .Append(m.PpsReal.ToString("0", Inv)).Append('/').Append(m.PpsTarget.ToString("0", Inv)).Append(" pps  ");
        MotoresTexto = ms.Length == 0 ? "—" : sbM.ToString().TrimEnd();
        var sem = _nodos.SnapshotSemillas();
        var sbS = new StringBuilder();
        for (int i = 0; i < sem.Length; i++) sbS.Append(sem[i].ToString("0.0", Inv)).Append(i == sem.Length - 1 ? "" : "  ");
        SemillasTexto = sem.Length == 0 ? "—" : sbS.ToString();
        NodosStatsTexto = $"targets {_nodos.TargetsRecibidos} · publicados {_nodos.Publicados}";
        NodosErrorTexto = _nodos.UltimoError ?? "";
        Notificar(nameof(QxConectado)); Notificar(nameof(VxConectado)); Notificar(nameof(QxEstadoTexto)); Notificar(nameof(VxEstadoTexto));
        Notificar(nameof(MotoresTexto)); Notificar(nameof(SemillasTexto)); Notificar(nameof(NodosStatsTexto));
        Notificar(nameof(NodosErrorTexto)); Notificar(nameof(NodosConError));

        // Demo
        DemoEstadoTexto = DemoActivo ? _demo.Estado : "apagada";
        DemoPilotXTexto = _demo.EstadoPilotX;
        DemoPilotXListo = _demo.PilotXListo;
        Notificar(nameof(DemoEstadoTexto)); Notificar(nameof(DemoPilotXTexto)); Notificar(nameof(DemoPilotXListo));

        Notificar(nameof(RumboTexto)); Notificar(nameof(LatActualTexto)); Notificar(nameof(LonActualTexto));
        Notificar(nameof(GuiadoActivo)); Notificar(nameof(GuiadoTexto));
        Notificar(nameof(SetPointTexto)); Notificar(nameof(VelPilotXTexto));
        Notificar(nameof(KpTexto)); Notificar(nameof(HighPwmTexto)); Notificar(nameof(LowPwmTexto));
        Notificar(nameof(MinPwmTexto)); Notificar(nameof(CountsTexto)); Notificar(nameof(OffsetTexto));
        Notificar(nameof(AckermanTexto)); Notificar(nameof(FlagsTexto));
        Notificar(nameof(RelesDireccion)); Notificar(nameof(RelesMaquina));
        Notificar(nameof(Zona1)); Notificar(nameof(Zona2)); Notificar(nameof(Zona3)); Notificar(nameof(Zona4));
        Notificar(nameof(Zona5)); Notificar(nameof(Zona6)); Notificar(nameof(Zona7)); Notificar(nameof(Zona8));
        Notificar(nameof(UTurnTexto)); Notificar(nameof(HydLiftTexto)); Notificar(nameof(TramTexto));
        Notificar(nameof(VelMaquinaTexto)); Notificar(nameof(RxTexto)); Notificar(nameof(TxTexto));
        Notificar(nameof(ScanRespondido)); Notificar(nameof(ScanTexto));
    }

    // Hilo de red: procesar y responder acá mismo (rápido), UI ni tocarla.
    private void AlRecibir(byte[] datos)
    {
        var res = _pgn.Procesar(datos);
        foreach (var r in res.Respuestas) _link.Enviar(r);

        if (res.NuevaSubred is { } s)
        {
            _config.Subred1 = s.S1; _config.Subred2 = s.S2; _config.Subred3 = s.S3;
            _config.Guardar(_rutaConfig);
            Dispatcher.UIThread.Post(() =>
                ReinicioPedido?.Invoke($"PilotX cambió la subred a {s.S1}.{s.S2}.{s.S3}. BenchX se reinicia para aplicarla."));
        }
    }

    public void Cerrar()
    {
        _timer.Stop();
        _config.Gga = Gga; _config.Vtg = Vtg; _config.Avr = Avr; _config.Hdt = Hdt;
        _config.Rmc = Rmc; _config.Ogi = Ogi; _config.Nda = Nda; _config.Ksxt = Ksxt;
        _config.EmularGps = EmularGps; _config.EmularWas = EmularWas; _config.EmularMotor = EmularMotor;
        _config.EmularMaquina = EmularMaquina; _config.EmularImu = EmularImu;
        _config.EmularQuantiX = EmularQuantiX; _config.EmularVistaX = EmularVistaX;
        _config.Demo = DemoActivo;
        try { _config.Guardar(_rutaConfig); } catch { }
        _link.Dispose();
        try { _demo.Dispose(); } catch { }
        try { _nodos.Dispose(); } catch { }
    }

    // bit 0 = sección 1, a la izquierda (mismo orden visual que el swapBits del original)
    private static string Puntos(int valor, int bits)
    {
        var sb = new StringBuilder(bits + 1);
        for (int i = 0; i < bits; i++)
        {
            if (i == 8) sb.Append(' ');
            sb.Append(((valor >> i) & 1) != 0 ? '●' : '○');
        }
        return sb.ToString();
    }

    private static string LeerIpsLocales()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    sb.AppendLine(ip.ToString());
            return sb.ToString().TrimEnd();
        }
        catch { return "—"; }
    }
}
