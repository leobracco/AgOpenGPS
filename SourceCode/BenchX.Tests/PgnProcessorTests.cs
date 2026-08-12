using System.Linq;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

public class PgnProcessorTests
{
    private static PgnProcessor Proc() => new() { Subred1 = 127, Subred2 = 255, Subred3 = 255 };

    // Trama PGN con header 0x80 0x81 0x7F + pgn + len + payload (sin checksum válido:
    // ModSim nunca validó el checksum entrante y BenchX mantiene eso).
    private static byte[] Trama(byte pgn, byte len, params byte[] payload)
    {
        var d = new byte[5 + payload.Length + 1];
        d[0] = 0x80; d[1] = 0x81; d[2] = 0x7F; d[3] = pgn; d[4] = len;
        payload.CopyTo(d, 5);
        return d;
    }

    private static void AssertCrc(byte[] r)
    {
        int ck = 0;
        for (int i = 2; i < r.Length - 1; i++) ck += r[i];
        Assert.That(r[^1], Is.EqualTo(unchecked((byte)ck)));
    }

    [Test]
    public void Pgn254_actualiza_guiado_y_responde_253_con_el_was()
    {
        var p = Proc();
        p.SteerAngleActual = 12.34;
        p.WorkSwitch = 0; p.SteerSwitch = 1; p.RemoteSwitch = 1;

        // speed=8.0 (80*0.1), guidance=1, setpoint=-5.00° (-500), xte=7, relay=0b101, relayHi=1
        short sp = -500;
        var res = p.Procesar(Trama(254, 8, 80, 0, 1, (byte)(sp & 0xFF), (byte)((sp >> 8) & 0xFF), 7, 0b101, 1, 0));

        Assert.That(p.GuidanceStatus, Is.EqualTo(1));
        Assert.That(p.SteerAngleSetPoint, Is.EqualTo(-5.0).Within(1e-6));
        Assert.That(p.GpsSpeedPilotX, Is.EqualTo(8.0).Within(1e-9));
        Assert.That(p.Relay, Is.EqualTo(0b101));
        Assert.That(p.RelayHi, Is.EqualTo(1));

        Assert.That(res.Respuestas, Has.Count.EqualTo(1));
        var r = res.Respuestas[0];
        Assert.That(r.Take(5), Is.EqualTo(new byte[] { 128, 129, 126, 253, 8 }));
        int sa = (short)(r[5] | (r[6] << 8));
        Assert.That(sa, Is.EqualTo(1234));                     // 12.34° * 100
        // dummies históricos de heading/roll que PilotX espera ver
        Assert.That((short)(r[7] | (r[8] << 8)), Is.EqualTo(9999));
        Assert.That((short)(r[9] | (r[10] << 8)), Is.EqualTo(8888));
        Assert.That(r[11], Is.EqualTo(0b110));                 // remote<<2 | steer<<1 | work
        Assert.That(r[12], Is.EqualTo(44));                    // pwmDisplay fijo histórico
        AssertCrc(r);
    }

    [Test]
    public void Pgn200_hello_responde_los_tres_modulos()
    {
        var p = Proc();
        p.SteerAngleActual = 1.0;
        var res = p.Procesar(Trama(200, 3, 56, 0, 0));
        Assert.That(res.Respuestas, Has.Count.EqualTo(3));
        Assert.That(res.Respuestas[0][3], Is.EqualTo(126));    // autosteer
        Assert.That(res.Respuestas[1][3], Is.EqualTo(123));    // machine
        Assert.That(res.Respuestas[2][3], Is.EqualTo(121));    // IMU
        int sa = (short)(res.Respuestas[0][5] | (res.Respuestas[0][6] << 8));
        Assert.That(sa, Is.EqualTo(100));
        // ModSim nunca recalculó el CRC de los hellos (viaja el 71 fijo): parity.
        Assert.That(res.Respuestas[0][^1], Is.EqualTo(71));
    }

    [Test]
    public void Pgn202_scan_responde_tres_scan_reply_con_la_subred()
    {
        var p = Proc();
        var res = p.Procesar(Trama(202, 3, 202, 202, 5));
        Assert.That(p.ScanRespondido, Is.True);
        Assert.That(res.Respuestas, Has.Count.EqualTo(3));
        foreach (var (r, modulo) in res.Respuestas.Zip(new byte[] { 126, 123, 121 }))
        {
            Assert.That(r[2], Is.EqualTo(modulo));
            Assert.That(r[3], Is.EqualTo(203));
            Assert.That(new[] { r[5], r[6], r[7] }, Is.EqualTo(new byte[] { 127, 255, 255 }));
            Assert.That(r[8], Is.EqualTo(modulo));
            AssertCrc(r);
        }
    }

    [Test]
    public void Pgn252_parsea_settings_de_direccion()
    {
        var p = Proc();
        // Kp=120, highPWM=160, lowPWM(ignorado, se recalcula)=30, minPWM=25,
        // counts=30, wasOffset=513 (1|2<<8), ackerman=95%
        p.Procesar(Trama(252, 8, 120, 160, 30, 25, 30, 1, 2, 95));
        Assert.That(p.Kp, Is.EqualTo(120));
        Assert.That(p.HighPwm, Is.EqualTo(160));
        Assert.That(p.MinPwm, Is.EqualTo(25));
        Assert.That(p.LowPwm, Is.EqualTo((byte)(25 * 1.2f)));  // ModSim pisa lowPWM con minPWM*1.2
        Assert.That(p.SensorCounts, Is.EqualTo(30));
        Assert.That(p.WasOffset, Is.EqualTo(513));
        Assert.That(p.AckermanPct, Is.EqualTo(95).Within(1e-6));
    }

    [Test]
    public void Pgn251_parsea_flags_de_config()
    {
        var p = Proc();
        // set0: invertWAS(b0)=1, relayHigh(b1)=0, motorDir(b2)=1, singleWAS(b3)=0,
        //       cytron(b4)=1, steerSwitch(b5)=0, steerButton(b6)=1, encoder(b7)=0 → 0b01010101
        // pulseMax=5, was_speed(ignorado)=0, set1: danfoss(b0)=1, presion(b1)=0, corriente(b2)=1, y-axis(b3)=0 → 0b0101
        p.Procesar(Trama(251, 8, 0b01010101, 5, 0, 0b0101, 0, 0, 0, 0));
        Assert.That(p.InvertWas, Is.EqualTo(1));
        Assert.That(p.RelayActiveHigh, Is.EqualTo(0));
        Assert.That(p.MotorDir, Is.EqualTo(1));
        Assert.That(p.SingleInputWas, Is.EqualTo(0));
        Assert.That(p.Cytron, Is.EqualTo(1));
        Assert.That(p.SteerSwitchCfg, Is.EqualTo(0));
        Assert.That(p.SteerButtonCfg, Is.EqualTo(1));
        Assert.That(p.ShaftEncoder, Is.EqualTo(0));
        Assert.That(p.PulseCountMax, Is.EqualTo(5));
        Assert.That(p.Danfoss, Is.EqualTo(1));
        Assert.That(p.PressureSensor, Is.EqualTo(0));
        Assert.That(p.CurrentSensor, Is.EqualTo(1));
        Assert.That(p.UseYAxis, Is.EqualTo(0));
    }

    [Test]
    public void Pgn239_parsea_datos_de_maquina()
    {
        var p = Proc();
        // uTurn=3, speed=25(→2.5), hydLift=2, tram=1, [9][10] libres, relayLo=0xF0, relayHi=0x0F
        p.Procesar(Trama(239, 8, 3, 25, 2, 1, 0, 0, 0xF0, 0x0F));
        Assert.That(p.UTurn, Is.EqualTo(3));
        Assert.That(p.GpsSpeedMaquina, Is.EqualTo(2.5).Within(1e-9));
        Assert.That(p.HydLift, Is.EqualTo(2));
        Assert.That(p.Tramline, Is.EqualTo(1));
        Assert.That(p.RelayLoM, Is.EqualTo(0xF0));
        Assert.That(p.RelayHiM, Is.EqualTo(0x0F));
    }

    [Test]
    public void Pgn229_guarda_las_8_zonas()
    {
        var p = Proc();
        p.Procesar(Trama(229, 8, 1, 2, 3, 4, 5, 6, 7, 8));
        Assert.That(p.Zonas, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Test]
    public void Pgn238_parsea_config_de_maquina()
    {
        var p = Proc();
        p.Procesar(Trama(238, 8, 2, 4, 1, 0b1, 11, 22, 33, 44));
        Assert.That(p.RaiseTime, Is.EqualTo(2));
        Assert.That(p.LowerTime, Is.EqualTo(4));
        Assert.That(p.EnableToolLift, Is.EqualTo(1));
        Assert.That(p.RelayActiveHighM, Is.EqualTo(1));
        Assert.That(p.User1, Is.EqualTo(11));
        Assert.That(p.User4, Is.EqualTo(44));
    }

    [Test]
    public void Pgn201_pide_cambio_de_subred_sin_respuestas()
    {
        var p = Proc();
        var res = p.Procesar(Trama(201, 5, 201, 201, 192, 168, 5));
        Assert.That(res.NuevaSubred, Is.EqualTo(((byte)192, (byte)168, (byte)5)));
        Assert.That(res.Respuestas, Is.Empty);
    }

    [Test]
    public void Header_invalido_o_trama_corta_se_ignoran()
    {
        var p = Proc();
        Assert.That(p.Procesar(new byte[] { 1, 2, 3 }).Respuestas, Is.Empty);
        Assert.That(p.Procesar(Trama(254, 8, 80)).Respuestas, Is.Empty); // payload corto → sin explotar
    }

    [Test]
    public void Direccion_apagada_no_responde_253_pero_sigue_parseando()
    {
        // Banco con ECU real: si la ECU maneja el WAS/motor, BenchX no puede
        // contestar 253 también — habría dos autosteer en la red.
        var p = Proc();
        p.EmularDireccion = false;

        short sp = -500;
        var r254 = p.Procesar(Trama(254, 8, 80, 0, 1, (byte)(sp & 0xFF), (byte)((sp >> 8) & 0xFF), 7, 0b101, 1, 0));
        Assert.That(r254.Respuestas, Is.Empty);                           // sin 253
        Assert.That(p.SteerAngleSetPoint, Is.EqualTo(-5.0).Within(1e-6)); // el estado sí llega (cinemática/UI)
        Assert.That(p.GuidanceStatus, Is.EqualTo(1));
    }

    [Test]
    public void Hellos_y_scan_solo_de_los_modulos_emulados()
    {
        var p = Proc();
        p.EmularDireccion = false;   // la ECU real es el autosteer
        p.EmularImu = false;         // y trae su IMU

        var hellos = p.Procesar(Trama(200, 3, 56, 0, 0));
        Assert.That(hellos.Respuestas, Has.Count.EqualTo(1));
        Assert.That(hellos.Respuestas[0][3], Is.EqualTo(123)); // solo máquina

        var scan = p.Procesar(Trama(202, 3, 202, 202, 5));
        Assert.That(scan.Respuestas, Has.Count.EqualTo(1));
        Assert.That(scan.Respuestas[0][2], Is.EqualTo(123));
        Assert.That(p.ScanRespondido, Is.True); // contestó como máquina
    }

    [Test]
    public void Todos_los_modulos_apagados_no_contesta_nada()
    {
        var p = Proc();
        p.EmularDireccion = false; p.EmularMaquina = false; p.EmularImu = false;

        Assert.That(p.Procesar(Trama(200, 3, 56, 0, 0)).Respuestas, Is.Empty);
        var scan = p.Procesar(Trama(202, 3, 202, 202, 5));
        Assert.That(scan.Respuestas, Is.Empty);
        Assert.That(p.ScanRespondido, Is.False); // no contestó: el badge no miente
    }
}
