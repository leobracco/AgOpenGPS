using System;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

// Modo "Física realista": ECU AiO (Keya.hex) + WAS analógico + motor Keya,
// bicicleta e implemento. Con el modo apagado, todo igual que antes.
public class FisicaRealistaTests
{
    // PilotX de fábrica: Kp 50, PWM alto 180, mínimo 25, 110 cuentas/grado.
    private static PgnProcessor PgnEnganchado(double setPoint, double velKmh = 8)
    {
        var p = new PgnProcessor
        {
            Kp = 50, HighPwm = 180, MinPwm = 25, LowPwm = 30, SensorCounts = 110,
            WasOffset = 0, AckermanPct = 100,
            GuidanceStatus = 1, SteerSwitch = 0,
            SteerAngleSetPoint = setPoint, GpsSpeedPilotX = velKmh,
        };
        return p;
    }

    // ---------------- regresión: apagado = ModSim ----------------

    [Test]
    public void Apagada_el_simulador_da_exactamente_lo_mismo_que_antes()
    {
        var hoy = new SimuladorVehiculo { Latitude = -38.345, Longitude = -60.265 };
        var conFlag = new SimuladorVehiculo
        {
            Latitude = -38.345, Longitude = -60.265,
            FisicaRealista = false, DistanciaEntreEjesM = 2.1, AntenaAdelanteM = 1.7,
        };
        var rnd = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            double v = rnd.NextDouble() * 30 - 5, d = rnd.NextDouble() * 80 - 40;
            hoy.SpeedKmh = conFlag.SpeedKmh = v;
            hoy.SteerAngleDeg = conFlag.SteerAngleDeg = d;
            hoy.Avanzar(); conFlag.Avanzar();
        }
        Assert.That(conFlag.Latitude, Is.EqualTo(hoy.Latitude));
        Assert.That(conFlag.Longitude, Is.EqualTo(hoy.Longitude));
        Assert.That(conFlag.HeadingRad, Is.EqualTo(hoy.HeadingRad));
        Assert.That(conFlag.Estado.LatNmea, Is.EqualTo(hoy.Estado.LatNmea));
        Assert.That(conFlag.Estado.LonNmea, Is.EqualTo(hoy.Estado.LonNmea));
    }

    [Test]
    public void Apagada_el_253_lleva_el_pwm_44_de_siempre_y_no_hay_pgn_250()
    {
        var p = new PgnProcessor { CurrentSensor = 1 };
        byte[] t254 = { 0x80, 0x81, 0x7F, 254, 8, 80, 0, 1, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 8; i++)
        {
            var r = p.Procesar(t254);
            Assert.That(r.Respuestas, Has.Count.EqualTo(1));
            Assert.That(r.Respuestas[0][12], Is.EqualTo(44));
        }
    }

    [Test]
    public void Encendida_el_253_lleva_el_pwm_real_y_cada_4_va_un_pgn_250()
    {
        var p = new PgnProcessor { CurrentSensor = 1, EnviarPgn250 = true, PwmDisplay = 77, SensorReading = 33 };
        byte[] t254 = { 0x80, 0x81, 0x7F, 254, 8, 80, 0, 1, 0, 0, 0, 0, 0, 0 };
        int con250 = 0;
        for (int i = 0; i < 8; i++)
        {
            var r = p.Procesar(t254);
            Assert.That(r.Respuestas[0][3], Is.EqualTo(253));
            Assert.That(r.Respuestas[0][12], Is.EqualTo(77));
            if (r.Respuestas.Count == 2)
            {
                con250++;
                Assert.That(r.Respuestas[1][3], Is.EqualTo(250));
                Assert.That(r.Respuestas[1], Has.Length.EqualTo(14)); // PilotX exige 14
                Assert.That(r.Respuestas[1][5], Is.EqualTo(33));
            }
        }
        Assert.That(con250, Is.EqualTo(2));
    }

    // ---------------- WAS ----------------

    [Test]
    public void Was_sin_errores_y_calibrado_lee_el_angulo_real()
    {
        var was = new SensorWas();
        var p = PgnEnganchado(0);
        foreach (double a in new[] { -30.0, -5.0, 0.0, 7.5, 30.0 })
            Assert.That(SensorWas.AnguloEcu(was.LeerCrudo(a), p), Is.EqualTo(a).Within(0.01));
    }

    [Test]
    public void Was_con_errores_los_deshace_la_calibracion_de_pilotx()
    {
        var was = new SensorWas { GananciaPct = 10, AckermannPct = 90, Invertido = true };
        var mal = PgnEnganchado(0);
        Assert.That(Math.Abs(SensorWas.AnguloEcu(was.LeerCrudo(10), mal) - 10), Is.GreaterThan(0.5));

        // Calibrado: cuentas 121, invertir WAS, Ackermann 111 %.
        var bien = PgnEnganchado(0);
        bien.SensorCounts = 121; bien.InvertWas = 1; bien.AckermanPct = 100 / 0.9;
        Assert.That(SensorWas.AnguloEcu(was.LeerCrudo(10), bien), Is.EqualTo(10).Within(0.02));
        Assert.That(SensorWas.AnguloEcu(was.LeerCrudo(-10), bien), Is.EqualTo(-10).Within(0.02));
    }

    [Test]
    public void Was_con_offset_se_corrige_con_el_offset_de_pilotx_en_cuentas()
    {
        var was = new SensorWas { OffsetGrados = 2 };
        var p = PgnEnganchado(0);
        Assert.That(SensorWas.AnguloEcu(was.LeerCrudo(0), p), Is.EqualTo(2).Within(0.01));
        p.WasOffset = unchecked((ushort)(short)-220);   // así llega por el PGN 252 (int16 sin signo)
        Assert.That(SensorWas.AnguloEcu(was.LeerCrudo(0), p), Is.EqualTo(0).Within(0.01));
    }

    // ---------------- lazo de la ECU (AutosteerPID.ino) ----------------

    [Test]
    public void Pid_del_firmware_p_mas_minimo_y_topes()
    {
        var p = PgnEnganchado(0);
        // error 0,5° → 50·0,5 = 25 + min 25 = 50; tope en la banda: 0,5·(180−30)/1,5 + 30 = 80
        Assert.That(ActuadorDireccion.CalcularPwm(0.5, 0, 8, p), Is.EqualTo(50));
        // error 5° → 250 + 25, tope highPWM 180
        Assert.That(ActuadorDireccion.CalcularPwm(5, 0, 8, p), Is.EqualTo(180));
        Assert.That(ActuadorDireccion.CalcularPwm(-5, 0, 8, p), Is.EqualTo(-180));
        // parado (< 0,2 km/h) la ECU no mueve la rueda
        Assert.That(ActuadorDireccion.CalcularPwm(5, 0, 0.1, p), Is.EqualTo(0));
        // invertir motor en PilotX da vuelta el signo
        p.MotorDir = 1;
        Assert.That(ActuadorDireccion.CalcularPwm(5, 0, 8, p), Is.EqualTo(-180));
    }

    // ---------------- motor ----------------

    [Test]
    public void Motor_con_pwm_minimo_real_22_no_se_mueve_con_15_y_si_con_30()
    {
        var a = new ActuadorDireccion { PwmMinimoReal = 22 };
        for (int i = 0; i < 100; i++) a.AvanzarMotor(ActuadorDireccion.PasoEcuS, 15);
        Assert.That(a.AnguloFisico, Is.EqualTo(0));

        var b = new ActuadorDireccion { PwmMinimoReal = 22 };
        for (int i = 0; i < 100; i++) b.AvanzarMotor(ActuadorDireccion.PasoEcuS, 30);
        Assert.That(Math.Abs(b.AnguloFisico), Is.GreaterThan(0.5));
        Assert.That(b.AnguloFisico, Is.LessThan(0)); // PWM + lleva la rueda a la izquierda (convención del firmware)
    }

    [Test]
    public void Lazo_cerrado_llega_al_setpoint()
    {
        var a = new ActuadorDireccion();
        var p = PgnEnganchado(8);
        for (int i = 0; i < 30; i++) a.Avanzar(0.1, p);
        Assert.That(a.AnguloFisico, Is.EqualTo(8).Within(0.5));
        Assert.That(a.AnguloWas, Is.EqualTo(8).Within(0.5));
        Assert.That(p.SteerSwitch, Is.EqualTo(0)); // sin cortes
    }

    [Test]
    public void Motor_invertido_diverge()
    {
        var a = new ActuadorDireccion { MotorInvertido = true };
        var p = PgnEnganchado(8);
        double errorMax = 0;
        for (int i = 0; i < 30; i++)
        {
            a.Avanzar(0.1, p);
            errorMax = Math.Max(errorMax, Math.Abs(a.AnguloFisico - 8));
        }
        Assert.That(errorMax, Is.GreaterThan(30));   // se fue al tope del otro lado

        // Y el "invertir motor" de PilotX lo arregla.
        var b = new ActuadorDireccion { MotorInvertido = true };
        var q = PgnEnganchado(8); q.MotorDir = 1;
        for (int i = 0; i < 30; i++) b.Avanzar(0.1, q);
        Assert.That(b.AnguloFisico, Is.EqualTo(8).Within(0.5));
    }

    [Test]
    public void Parado_la_ecu_no_mueve_la_rueda()
    {
        var a = new ActuadorDireccion();
        var p = PgnEnganchado(15, velKmh: 0.1);
        for (int i = 0; i < 20; i++) a.Avanzar(0.1, p);
        Assert.That(a.AnguloFisico, Is.EqualTo(0));
    }

    [Test]
    public void Agarrar_el_volante_corta_por_corriente_si_pilotx_tiene_el_sensor()
    {
        var a = new ActuadorDireccion();
        var p = PgnEnganchado(8);
        p.CurrentSensor = 1; p.PulseCountMax = 60;
        for (int i = 0; i < 20; i++) a.Avanzar(0.1, p);
        Assert.That(p.SteerSwitch, Is.EqualTo(0), "esfuerzo normal no corta");

        p.SteerAngleSetPoint = 20;   // que el motor esté empujando
        a.AgarrarVolante = true;
        a.Avanzar(0.3, p);
        Assert.That(p.SteerSwitch, Is.EqualTo(1));
        Assert.That(a.UltimoCorte, Does.Contain("corriente"));
    }

    [Test]
    public void Agarrar_el_volante_sin_sensor_de_corriente_no_corta()
    {
        var a = new ActuadorDireccion();
        var p = PgnEnganchado(20);
        a.AgarrarVolante = true;
        a.Avanzar(1.0, p);
        Assert.That(p.SteerSwitch, Is.EqualTo(0));
        Assert.That(a.AnguloFisico, Is.EqualTo(0)); // lo frena la mano
    }

    [Test]
    public void Was_fuera_de_50_grados_o_mas_de_25_kmh_corta()
    {
        var a = new ActuadorDireccion();
        var p = PgnEnganchado(0);
        p.SensorCounts = 20;            // mal calibrado: 10° reales se leen como 55°
        a.PonerAnguloManual(10);
        a.Avanzar(0.01, p);
        Assert.That(p.SteerSwitch, Is.EqualTo(1));
        Assert.That(a.UltimoCorte, Does.Contain("50"));

        var b = new ActuadorDireccion();
        var q = PgnEnganchado(0, velKmh: 26);
        b.Avanzar(0.01, q);
        Assert.That(q.SteerSwitch, Is.EqualTo(1));
        Assert.That(b.UltimoCorte, Does.Contain("25"));
    }

    // ---------------- bicicleta ----------------

    [Test]
    public void Bicicleta_a_v_y_delta_constantes_el_radio_es_L_sobre_tan_delta()
    {
        const double L = 3.3, delta = 12;
        double r = L / Math.Tan(delta * Math.PI / 180);
        var s = new SimuladorVehiculo
        {
            Latitude = -38.345, Longitude = -60.265, HeadingRad = 0,
            FisicaRealista = true, DistanciaEntreEjesM = L, AntenaAdelanteM = 0,
            SpeedKmh = 10, SteerAngleDeg = delta,
        };
        s.Avanzar();
        // Arranca en (0,0) mirando al norte; giro a la derecha → centro al este, en (R, 0).
        double maxErr = 0;
        for (int i = 0; i < 400; i++)
        {
            s.Avanzar();
            double d = Math.Sqrt((s.PivotEste - r) * (s.PivotEste - r) + s.PivotNorte * s.PivotNorte);
            maxErr = Math.Max(maxErr, Math.Abs(d - r));
        }
        Assert.That(maxErr, Is.LessThan(1e-6));
    }

    [Test]
    public void Bicicleta_el_gps_sale_por_la_antena_y_avanza_lo_que_corresponde()
    {
        var s = new SimuladorVehiculo
        {
            Latitude = -38.0, Longitude = -60.0, HeadingRad = 0,
            FisicaRealista = true, AntenaAdelanteM = 2, SpeedKmh = 36,
        };
        s.Avanzar(); // 1 m al norte
        Assert.That(s.PivotNorte, Is.EqualTo(-2 + 1).Within(1e-9));
        Assert.That((s.Latitude + 38.0) * Math.PI / 180 * 6371000, Is.EqualTo(1).Within(1e-6));
        Assert.That(s.SpeedKnots, Is.EqualTo(19.4).Within(1e-9));
    }

    // ---------------- implemento ----------------

    [Test]
    public void Implemento_de_arrastre_en_curva_R30_L6_va_0_6m_por_adentro()
    {
        const double R = 30, L = 6, wb = 3.3;
        var s = new SimuladorVehiculo
        {
            Latitude = -38.345, Longitude = -60.265, HeadingRad = 0,
            FisicaRealista = true, DistanciaEntreEjesM = wb, AntenaAdelanteM = 0,
            SpeedKmh = 8, SteerAngleDeg = Math.Atan(wb / R) * 180 / Math.PI,
        };
        var imp = new Implemento { Tipo = TipoImplemento.Arrastre, EngancheM = 0, LargoBarraM = L };
        for (int i = 0; i < 3000; i++)   // ~670 m, más de 3 vueltas
        {
            s.Avanzar();
            imp.Actualizar(s.PivotEste, s.PivotNorte, s.HeadingRad, s.PasoM);
        }
        double rTractor = Math.Sqrt((s.PivotEste - R) * (s.PivotEste - R) + s.PivotNorte * s.PivotNorte);
        double rImpl = Math.Sqrt((imp.Este - R) * (imp.Este - R) + imp.Norte * imp.Norte);
        Assert.That(rTractor, Is.EqualTo(R).Within(1e-6));
        Assert.That(rTractor - rImpl, Is.EqualTo(R - Math.Sqrt(R * R - L * L)).Within(0.05)); // 0,606 m
    }

    [Test]
    public void Implemento_de_arrastre_en_recta_va_derecho_atras_y_la_ladera_lo_corre()
    {
        var imp = new Implemento { Tipo = TipoImplemento.Arrastre, EngancheM = -1, LargoBarraM = 6 };
        double n = 0;
        for (int i = 0; i < 300; i++) { n += 0.25; imp.Actualizar(0, n, 0, 0.25); }
        Assert.That(imp.Este, Is.EqualTo(0).Within(1e-9));
        Assert.That(imp.Norte, Is.EqualTo(n - 7).Within(1e-9));

        imp.DerivaLateralM = 0.4;
        for (int i = 0; i < 600; i++) { n += 0.25; imp.Actualizar(0, n, 0, 0.25); }
        Assert.That(Implemento.DesvioLateral(imp.Este, imp.Norte, 0, 0, 0), Is.EqualTo(0.4).Within(0.01));
    }

    [Test]
    public void Implemento_de_3_puntos_es_rigido_en_el_enganche()
    {
        var imp = new Implemento { Tipo = TipoImplemento.TresPuntos, EngancheM = -1.5, DerivaLateralM = -0.2 };
        imp.Actualizar(10, 20, Math.PI / 2, 0.3); // mirando al este
        Assert.That(imp.Este, Is.EqualTo(8.5).Within(1e-9));
        Assert.That(imp.Norte, Is.EqualTo(20.2).Within(1e-9)); // −0,2 = izquierda = norte mirando al este
        Assert.That(imp.RumboRad, Is.EqualTo(Math.PI / 2));
    }
}
