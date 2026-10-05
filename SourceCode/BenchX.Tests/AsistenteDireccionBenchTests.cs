using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenGPS.SteerCal;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

// Asistente de calibración de la dirección (AgOpenGPS.Core/SteerCal, linkeado)
// contra la "Física realista" de BenchX: la ECU AiO Keya.hex de verdad
// (ActuadorDireccion: lazo P del firmware, corte por corriente, WAS > ±50°),
// el WAS analógico con errores de montaje (SensorWas) y la bicicleta
// (SimuladorVehiculo). Lo que el asistente graba en la placa se vuelca al
// PgnProcessor como si viajara por el PGN 252/251.
public class AsistenteDireccionBenchTests
{
    private SteerCalWizard _w = null!;
    private ActuadorDireccion _a = null!;
    private PgnProcessor _p = null!;
    private SimuladorVehiculo _s = null!;
    private double _t;
    private bool _apretado;
    private double _velKmh;
    private int _grabaciones;

    private static SteerCalConfig ConfigPilotX() => new()
    {
        Kp = 50, MinPwm = 25, HighPwm = 180, WasOffset = 0, CountsPerDegree = 110, Ackerman = 100,
        CurrentSensor = true, SensorLimit = 120, MaxSteerAngle = 35, WheelbaseM = 3.3,
        HoldLookAhead = 30, LookAheadMult = 14, AcquireFactor = 90,
    };

    [SetUp]
    public void SetUp()
    {
        _w = new SteerCalWizard();
        _a = new ActuadorDireccion();
        _p = new PgnProcessor { SteerSwitch = 0, GuidanceStatus = 0 };
        _s = new SimuladorVehiculo
        {
            Latitude = -38.345, Longitude = -60.265, HeadingRad = 0,
            FisicaRealista = true, DistanciaEntreEjesM = 3.3, AntenaAdelanteM = 0,
        };
        _t = 10;
        _apretado = false;
        _velKmh = 0;
        _grabaciones = 0;
        Volcar(ConfigPilotX());
    }

    // Lo que el PGN 252/251 lleva a la placa.
    private void Volcar(SteerCalConfig c)
    {
        _p.Kp = (byte)c.Kp;
        _p.MinPwm = (byte)c.MinPwm;
        _p.HighPwm = (byte)c.HighPwm;
        _p.LowPwm = (byte)(c.HighPwm / 3);
        _p.SensorCounts = c.CountsPerDegree;
        _p.WasOffset = c.WasOffset;
        _p.AckermanPct = c.Ackerman;
        _p.InvertWas = (byte)(c.InvertWas ? 1 : 0);
        _p.MotorDir = (byte)(c.InvertSteer ? 1 : 0);
        _p.CurrentSensor = (byte)(c.CurrentSensor ? 1 : 0);
        _p.PulseCountMax = (byte)c.SensorLimit;
    }

    private void Correr(double segundos, Action? cadaTick = null)
    {
        int n = (int)Math.Round(segundos / 0.1);
        for (int i = 0; i < n; i++)
        {
            _t += 0.1;
            // Antes de la ECU: el WAS (AnguloWas) se relee recién en Avanzar.
            cadaTick?.Invoke();
            // PGN 254 de PilotX: con el asistente moviendo, su setpoint y su velocidad.
            _p.GuidanceStatus = (byte)(_w.MotorActivo ? 1 : 0);
            _p.SteerAngleSetPoint = _w.Setpoint;
            _p.GpsSpeedPilotX = _w.MotorActivo ? _w.VelocidadPgnKmh : _velKmh;
            _a.Avanzar(0.1, _p);

            _s.SpeedKmh = _velKmh;
            _s.SteerAngleDeg = _a.AnguloFisico;
            _s.Avanzar();

            if (_apretado) _w.Latido(true, _t);
            _w.Tick(new SteerCalEntrada
            {
                T = _t,
                Edad253 = 0.05,
                AnguloWas = _a.AnguloWas,
                Pwm = _a.PwmDisplay,
                Corriente = (int)Math.Round(_a.LecturaCorriente),
                VelKmh = _velKmh,
                FixValido = true,
                RumboRad = _s.HeadingRad,
                Este = _s.PivotEste,
                Norte = _s.PivotNorte,
                SwitchAbierto = _p.SteerSwitch != 0,
            });
            Drenar();
        }
    }

    private void Drenar()
    {
        foreach (var pe in _w.TomarPedidos())
        {
            if (pe == SteerCalPedido.EscribirPlaca) { Volcar(_w.EnPlaca); _grabaciones++; }
            if (pe == SteerCalPedido.Restaurar) Volcar(_w.Original);
        }
        _w.TomarEventos();
    }

    private void Hacer(string accion)
    {
        Assert.That(_w.Accion(accion), Is.True, $"'{accion}' en {_w.Paso}/{_w.Fase}: {_w.Mensaje}");
        Drenar();
    }

    private void HastaSentidoMotor()
    {
        Assert.That(_w.Iniciar(ConfigPilotX(), _t), Is.True);
        Correr(0.2);
        Hacer("siguiente");
        Hacer("empezar");
        Correr(1.2, () => _a.PonerAnguloManual(10));
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
        Correr(0.1, () => _a.PonerAnguloManual(0));
        Hacer("siguiente");
    }

    private void HastaPaso(SteerCalPaso destino)
    {
        HastaSentidoMotor();
        Hacer("empezar");
        _apretado = true;
        Correr(3);
        _apretado = false;
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
        Hacer("siguiente");
        while (_w.Paso != destino) Hacer(_w.PuedeSaltar ? "saltar" : "siguiente");
    }

    [Test]
    public void Motor_invertido_en_la_ecu_real_se_detecta_corta_y_se_arregla()
    {
        _a.MotorInvertido = true;
        HastaSentidoMotor();
        Hacer("empezar");
        _apretado = true;
        double peor = 0;
        Correr(2, () => peor = Math.Min(peor, _a.AnguloFisico));
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
        Assert.That(peor, Is.GreaterThan(-6));
        Hacer("aceptar");
        Assert.That(_p.MotorDir, Is.EqualTo(1));

        _apretado = false;
        Correr(0.1, () => _a.PonerAnguloManual(0));
        Hacer("empezar");
        _apretado = true;
        Correr(3);
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
    }

    [Test]
    public void Parado_la_velocidad_falsa_de_medio_kmh_alcanza_para_que_la_ecu_mueva()
    {
        HastaSentidoMotor();
        Hacer("empezar");
        _apretado = true;
        Correr(3);
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
        // Al llegar se suelta el motor: la inercia lo corre un poco más.
        Assert.That(_a.AnguloFisico, Is.EqualTo(3).Within(1.5));
    }

    [Test]
    public void Cero_y_circulos_deshacen_los_errores_de_montaje_del_was()
    {
        _a.Was.OffsetGrados = 1.5;
        _a.Was.GananciaPct = 12;
        _a.Was.AckermannPct = 90;
        HastaPaso(SteerCalPaso.CeroWas);

        Hacer("empezar");
        _velKmh = 4.5;
        Correr(60, () => { _a.PonerAnguloManual(0); });
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
        Hacer("aceptar");
        Correr(0.1, () => _a.PonerAnguloManual(0));
        Assert.That(_a.AnguloWas, Is.EqualTo(0).Within(0.05));
        Hacer("siguiente");

        Assert.That(_w.Paso, Is.EqualTo(SteerCalPaso.CuentasAckermann));
        Hacer("empezar");
        Correr(8, () => _a.PonerAnguloManual(15));
        Hacer("empezar");
        Correr(8, () => _a.PonerAnguloManual(-15));
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
        Hacer("aceptar");

        Correr(0.1, () => _a.PonerAnguloManual(15));
        Assert.That(_a.AnguloWas, Is.EqualTo(15).Within(0.4));
        Correr(0.1, () => _a.PonerAnguloManual(-15));
        Assert.That(_a.AnguloWas, Is.EqualTo(-15).Within(0.5));
    }

    [Test]
    public void Ganancia_contra_la_ecu_real_elige_una_que_no_se_pasa_del_10_por_ciento()
    {
        HastaPaso(SteerCalPaso.Ganancia);
        Hacer("empezar");
        _velKmh = 1.5;
        _apretado = true;
        for (int i = 0; i < 1500 && _w.Fase == SteerCalFase.Midiendo; i++)
            Correr(0.1, () => _s.HeadingRad = 0);
        _apretado = false;
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta).Or.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
        Assert.That(_w.EscriturasPaso, Is.LessThanOrEqualTo(SteerCalWizard.TopeEscrituras(SteerCalPaso.Ganancia)));
        TestContext.Out.WriteLine(_w.Medicion);

        if (_w.Fase == SteerCalFase.Propuesta) Hacer("aceptar");
        Assert.That(_p.Kp, Is.EqualTo((byte)_w.Trabajo.Kp), "la placa quedó con la elegida");
        Assert.That(SteerCalWizard.CandidatosKp.Contains(_w.Trabajo.Kp) || _w.Trabajo.Kp == 50, Is.True);
    }

    [Test]
    public void Corte_por_corriente_se_calibra_andando_y_corta_al_agarrar_el_volante()
    {
        HastaPaso(SteerCalPaso.CorteCorriente);
        Hacer("empezar");
        _velKmh = 1.5;
        _apretado = true;
        for (int i = 0; i < 800 && _w.Fase == SteerCalFase.Midiendo; i++)
            Correr(0.1, () => _s.HeadingRad = 0);
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Propuesta), _w.Mensaje);
        Assert.That(_p.SteerSwitch, Is.EqualTo(0), "andando normal no cortó");
        Hacer("aceptar");
        int umbral = _p.PulseCountMax;
        TestContext.Out.WriteLine("umbral " + umbral + " — " + _w.Medicion);

        _velKmh = 0;
        Correr(0.2);
        Hacer("empezar");
        Correr(1);
        _a.AgarrarVolante = true;
        Correr(3);
        Assert.That(_w.Fase, Is.EqualTo(SteerCalFase.Hecho), _w.Mensaje);
        Assert.That(_a.UltimoCorte, Does.Contain("corriente"));
    }

    [Test]
    public void Cancelar_vuelve_la_ecu_a_la_config_de_antes()
    {
        _a.Was.Invertido = true;
        Assert.That(_w.Iniciar(ConfigPilotX(), _t), Is.True);
        Correr(0.2);
        Hacer("siguiente");
        Hacer("empezar");
        Correr(1.2, () => _a.PonerAnguloManual(10));
        Hacer("aceptar");
        Assert.That(_p.InvertWas, Is.EqualTo(1));
        Hacer("cancelar");
        Correr(0.1);
        Assert.That(_p.InvertWas, Is.EqualTo(0));
        Assert.That(_p.Kp, Is.EqualTo(50));
    }
}
