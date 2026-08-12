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
        EmularGps = _config.EmularGps; EmularDireccion = _config.EmularDireccion;
        EmularMaquina = _config.EmularMaquina; EmularImu = _config.EmularImu;
        LatInicial = _config.Latitud.ToString("N7", Inv);
        LonInicial = _config.Longitud.ToString("N7", Inv);

        _link = new UdpLink(_config.Subred1, _config.Subred2, _config.Subred3);
        _link.DatagramaRecibido += AlRecibir;

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
    private bool _emularGps = true, _emularDireccion = true, _emularMaquina = true, _emularImu = true;
    public bool EmularGps { get => _emularGps; set { _emularGps = value; Notificar(); } }
    public bool EmularDireccion { get => _emularDireccion; set { _emularDireccion = value; _pgn.EmularDireccion = value; Notificar(); } }
    public bool EmularMaquina { get => _emularMaquina; set { _emularMaquina = value; _pgn.EmularMaquina = value; Notificar(); } }
    public bool EmularImu { get => _emularImu; set { _emularImu = value; _pgn.EmularImu = value; Notificar(); } }
    public void BotonDireccionRemoto() => _pgn.SteerSwitch = _pgn.SteerSwitch > 0 ? 0 : 1;

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
        // Con guiado activo el volante lo maneja PilotX: el slider sigue al setpoint.
        if (_pgn.GuidanceStatus != 0)
            AnguloDireccion = _pgn.SteerAngleSetPoint;

        _sim.SpeedKmh = VelocidadKmh;
        _sim.SteerAngleDeg = AnguloDireccion;
        _sim.RollDeg = Roll;
        _sim.Estado.TimeNow = DateTime.UtcNow.ToString("HHmmss.fff,", Inv);
        _sim.Avanzar();
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
        _config.EmularGps = EmularGps; _config.EmularDireccion = EmularDireccion;
        _config.EmularMaquina = EmularMaquina; _config.EmularImu = EmularImu;
        try { _config.Guardar(_rutaConfig); } catch { }
        _link.Dispose();
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
