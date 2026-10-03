// GeneradorSemillasTests — el VistaX emulado como firmware 3.1: estadística
// conocida de semillas, formato exacto de la telemetría agp.vistax.telemetry/2
// y, de punta a punta, que el cálculo ISO 7256-1 de PilotX (VxEspaciamiento,
// linkeado) devuelve los dobles/fallas/CV configurados en BenchX.
using System;
using System.Collections.Generic;
using System.Text.Json;
using AgroParallel.Services.VistaX;
using BenchX.Sim;
using NUnit.Framework;

namespace BenchX.Tests;

[TestFixture]
public class GeneradorSemillasTests
{
    [Test]
    public void La_primera_semilla_y_la_vuelta_de_una_parada_larga_son_hueco()
    {
        var g = new GeneradorSemillas(1);
        var dts = new List<int>();
        g.Avanzar(1.0, 10);                       // ~10 semillas
        g.Pop(64, dts, out _);
        Assert.That(dts[0], Is.EqualTo(GeneradorSemillas.DtHueco));
        dts.Clear();

        g.Avanzar(10.0, 0);                       // 10 s parado
        g.Avanzar(1.0, 10);
        g.Pop(64, dts, out _);
        Assert.That(dts[0], Is.EqualTo(GeneradorSemillas.DtHueco), "≥ 6,5535 s = 0xFFFF");
        Assert.That(dts.Count, Is.InRange(8, 12));
    }

    [Test]
    public void Sin_dobles_ni_fallas_ni_CV_los_intervalos_son_exactos()
    {
        var g = new GeneradorSemillas(2);
        var dts = new List<int>();
        for (int i = 0; i < 20; i++) g.Avanzar(0.1, 11.1111111);
        g.Pop(64, dts, out _);
        for (int i = 1; i < dts.Count; i++) Assert.That(dts[i], Is.EqualTo(900).Within(1));
    }

    [Test]
    public void Buffer_de_64_lo_que_no_entra_va_a_dt_lost_y_maximo_24_por_mensaje()
    {
        var g = new GeneradorSemillas(3);
        int n = g.Avanzar(0.25, 400);             // 100 semillas en una ventana
        Assert.That(n, Is.EqualTo(100).Within(1));
        var dts = new List<int>();
        g.Pop(GeneradorSemillas.MaxDtPorMsg, dts, out uint perdidos);
        Assert.That(dts.Count, Is.EqualTo(24));
        Assert.That(perdidos, Is.EqualTo((uint)(n - 64)));
        Assert.That(g.EnBuffer, Is.EqualTo(40), "lo que quedó sale en el próximo mensaje");
        g.Pop(24, dts, out perdidos);
        Assert.That(perdidos, Is.EqualTo(0u), "dt_lost se informa una sola vez");
    }

    [Test]
    public void Sin_broker_descarta_y_cuenta_perdidos()
    {
        var g = new GeneradorSemillas(4);
        g.Avanzar(0.5, 20);
        int enBuffer = g.EnBuffer;
        g.Descartar();
        var dts = new List<int>();
        g.Pop(24, dts, out uint perdidos);
        Assert.That(dts, Is.Empty);
        Assert.That(perdidos, Is.EqualTo((uint)enBuffer));
    }

    [Test]
    public void Acumulado_cuenta_todas_las_semillas()
    {
        var g = new GeneradorSemillas(5);
        int total = 0;
        for (int i = 0; i < 100; i++) total += g.Avanzar(0.1, 30);
        Assert.That(g.Acumulado, Is.EqualTo(total));
        Assert.That(total, Is.EqualTo(300).Within(3));
    }

    [Test]
    public void Payload_igual_al_firmware_3_1()
    {
        var c1 = new NodosEmulados.CableTelemetria { Cable = 1, Valor = 12, Pulsos = 3, Acum = 1234 };
        c1.Dt.AddRange(new[] { 900, 65535, 310 });
        var c2 = new NodosEmulados.CableTelemetria { Cable = 2, Valor = 0, Pulsos = 0, Acum = 7, DtLost = 5 };
        string p = NodosEmulados.PayloadTelemetriaV2("VX-BENCH000001", 42, new[] { c1, c2 });

        Assert.That(p, Is.EqualTo(
            "{\"schema\":\"agp.vistax.telemetry/2\",\"seq\":42,\"uid\":\"VX-BENCH000001\",\"sensores\":[" +
            "{\"cable\":1,\"valor\":12,\"raw\":3,\"acum\":1234,\"dt\":[900,65535,310]}," +
            "{\"cable\":2,\"valor\":0,\"raw\":0,\"acum\":7,\"dt\":[],\"dt_lost\":5}]}"));

        // Lo lee el parser de PilotX.
        using var doc = JsonDocument.Parse(p);
        var s2 = doc.RootElement.GetProperty("sensores")[1];
        var dt = VxEspaciamiento.LeerDt(s2, out int lost);
        Assert.That(dt, Is.Empty, "dt vacío = el nodo es telemetry/2 pero no cayó semilla");
        Assert.That(lost, Is.EqualTo(5));
    }

    // De punta a punta: generador → mensajes de 250 ms (24 dt como máximo) →
    // JSON → parser + clasificación ISO de PilotX. Maíz a 5 sem/m y 8 km/h.
    [TestCase(0.02, 0.03, 0.15)]
    [TestCase(0.00, 0.00, 0.10)]
    [TestCase(0.05, 0.08, 0.25)]
    public void PilotX_mide_la_estadistica_configurada(double pd, double pf, double cv)
    {
        const double velKmh = 8, semM = 5;
        double tasa = semM * velKmh / 3.6;            // sem/s
        var g = new GeneradorSemillas(77) { PDoble = pd, PFalla = pf, Cv = cv };
        var esp = new VxEspaciamiento();
        int seq = 0;
        for (int msg = 0; msg < 2400; msg++)          // 10 min de siembra ≈ 6.700 semillas
        {
            int pulsos = 0;
            for (int k = 0; k < 5; k++) pulsos += g.Avanzar(0.05, tasa);
            var c = new NodosEmulados.CableTelemetria { Cable = 1, Pulsos = pulsos, Valor = pulsos * 4.0, Acum = g.Acumulado };
            g.Pop(GeneradorSemillas.MaxDtPorMsg, c.Dt, out uint lost);
            c.DtLost = lost;
            Assert.That(lost, Is.EqualTo(0u));
            string json = NodosEmulados.PayloadTelemetriaV2("VX-1", ++seq, new[] { c });
            using var doc = JsonDocument.Parse(json);
            foreach (var s in doc.RootElement.GetProperty("sensores").EnumerateArray())
            {
                var dts = VxEspaciamiento.LeerDt(s, out _);
                foreach (var dt in dts) esp.Agregar(dt, velKmh, 1.0 / semM);
            }
        }
        var ix = esp.Lote();
        Assert.That(ix.NEspacios, Is.GreaterThan(6000));
        Assert.That(ix.DoblesPct, Is.EqualTo(pd * 100).Within(0.6));
        Assert.That(ix.FallasPct, Is.EqualTo(pf * 100).Within(0.8));
        Assert.That(ix.SingulacionPct, Is.EqualTo(100 - (pd + pf) * 100).Within(1.0));
        Assert.That(ix.CvPct, Is.EqualTo(cv * 100).Within(0.8));
    }

    [TestCase(0.10)]
    [TestCase(0.25)]
    [TestCase(0.28)]
    public void Sigma_compensa_el_recorte(double cv)
    {
        double s = GeneradorSemillas.SigmaParaCv(cv);
        if (double.IsPositiveInfinity(s)) Assert.That(cv, Is.GreaterThan(0.28 - 1e-9));
        else Assert.That(GeneradorSemillas.DesvioRecortado(s), Is.EqualTo(cv).Within(1e-4));
        Assert.That(GeneradorSemillas.SigmaParaCv(0), Is.EqualTo(0));
    }

    [Test]
    public void A01ms_redondea_y_satura()
    {
        Assert.That(GeneradorSemillas.A01ms(0.09), Is.EqualTo(900));
        Assert.That(GeneradorSemillas.A01ms(0.00001), Is.EqualTo(1));
        Assert.That(GeneradorSemillas.A01ms(6.6), Is.EqualTo(0xFFFF));
    }
}
