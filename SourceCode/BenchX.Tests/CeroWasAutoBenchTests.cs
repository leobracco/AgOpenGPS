using System;
using AgOpenGPS.SteerCal;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

// Cero automático del WAS (AgOpenGPS.Core/SteerCal/CeroWasEstadistico.cs,
// linkeado) contra el WAS simulado de BenchX: SensorWas con el offset de
// montaje inyectado (el mismo campo "Offset" de la tarjeta Física realista) y
// la conversión del firmware AiO (SensorWas.AnguloEcu con lo que llegó por PGN
// 252). Verifica el SIGNO de la propuesta contra la placa, con y sin
// "Invertir WAS" y con Ackermann: aplicada la propuesta, las ruedas derechas
// tienen que leer ~0°.
public class CeroWasAutoBenchTests
{
    [TestCase(1.5, false, 100)]
    [TestCase(-2.0, false, 100)]
    [TestCase(1.5, true, 100)]
    [TestCase(-2.0, true, 100)]
    [TestCase(-1.2, false, 80)]
    [TestCase(-1.2, true, 80)]
    public void LaPropuestaDejaLasRuedasDerechasEnCero(double offsetMontajeGrados, bool invertido, int ack)
    {
        var was = new SensorWas(semilla: 42) { OffsetGrados = offsetMontajeGrados, Invertido = invertido, RuidoGrados = 0.1 };
        var p = new PgnProcessor { SensorCounts = 110, WasOffset = 0, AckermanPct = ack, InvertWas = (byte)(invertido ? 1 : 0) };
        var cfg = new ConfigWasCeroAuto { WasOffset = 0, CuentasPorGrado = 110, AckermanPct = ack, InvertWas = invertido };

        var c = new CeroWasEstadistico();
        var rnd = new Random(3);
        for (int i = 0; i < 900; i++)   // 90 s a 10 Hz, andando derecho y enganchado
        {
            double t = i * 0.1;
            double fisico = 0.25 * Math.Sin(t * 0.8) + 0.1 * (rnd.NextDouble() - 0.5);   // el piloto serpentea apenas
            double leido = SensorWas.AnguloEcu(was.LeerCrudo(fisico), p);
            c.Evaluar(new EntradaCeroWas
            {
                T = t, AnguloReal = leido, PilotoEnganchado = true, GuiaRecta = true, VelKmh = 8,
                ErrorLateralM = 0.04, RumboRad = 0.3, RolidoGrados = CeroWasEstadistico.CentinelaSinImu,
            }, cfg);
        }

        var r = c.Resultado();
        Assert.That(r.Estado, Is.EqualTo(EstadoCeroWas.Propuesta));

        // "Aplicar": lo que viajaría por el PGN 252.
        p.WasOffset = r.OffsetPropuesto;
        double suma = 0;
        for (int i = 0; i < 200; i++) suma += SensorWas.AnguloEcu(was.LeerCrudo(0), p);
        Assert.That(suma / 200, Is.EqualTo(0).Within(0.15),
            $"offset propuesto {r.OffsetPropuesto}, sesgo medido {r.SesgoGrados:F2}°");
    }
}
