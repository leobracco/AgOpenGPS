// ============================================================================
// PlanimetriaCabinaTests.cs — la capa que dibuja el mapa de PilotX: esquinas
// en el plano local, grilla compacta (cm) y curvas convertidas.
// ============================================================================

using System;
using System.Linq;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class PlanimetriaCabinaTests
    {
        [Test]
        public void ArmarCapa_EsquinasCurvasYGrillaCompacta()
        {
            var L = PlanimetriaSintetico.Generar();
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
            var A = AmbientacionAltimetria.Clasificar(R.Grilla, new ConfigAmbientacion());
            // "plano local" = proyección del generador (origen en la esquina SO del lote)
            ConvertirALocal conv = (double la, double lo, out double e, out double n) => { e = L.Pr.X(lo); n = L.Pr.Y(la); };
            var capa = PlanimetriaCabina.ArmarCapa(R, A, conv);

            Assert.That(capa.Nx, Is.EqualTo(R.Grilla.Nx));
            Assert.That(capa.EEste - capa.EOeste, Is.EqualTo(capa.Nx * R.Grilla.Res).Within(0.05));
            Assert.That(capa.NNorte - capa.NSur, Is.EqualTo(capa.Ny * R.Grilla.Res).Within(0.05));
            // el lote (0..400 × 0..300) cae adentro de la grilla
            Assert.That(capa.EOeste, Is.LessThan(0));
            Assert.That(capa.EEste, Is.GreaterThan(400));
            Assert.That(capa.NSur, Is.LessThan(0));
            Assert.That(capa.NNorte, Is.GreaterThan(300));

            double peor = 0;
            for (int i = 0; i < capa.ZCm.Length; i++)
            {
                bool nan = float.IsNaN(R.Grilla.Z[i]);
                Assert.That(capa.ZCm[i] == CapaPlanimetria.Nulo, Is.EqualTo(nan));
                if (!nan) peor = Math.Max(peor, Math.Abs(capa.ZBase + capa.ZCm[i] / 100.0 - R.Grilla.Z[i]));
            }
            Assert.That(peor, Is.LessThanOrEqualTo(0.0051));
            Assert.That(capa.Zona, Is.SameAs(A.Zona));

            // curvas: vértices sobre su cota en la superficie verdadera (±8 cm)
            Assert.That(capa.Curvas.Count, Is.GreaterThan(10));
            Assert.That(capa.Curvas.Any(c => c.Maestra), Is.True);
            double peorCurva = 0;
            foreach (var c in capa.Curvas)
                foreach (var l in c.Lineas)
                    for (int i = 0; i < l.Length; i += 2)
                    {
                        if (l[i] < 0 || l[i] > 400 || l[i + 1] < 0 || l[i + 1] > 300) continue;
                        peorCurva = Math.Max(peorCurva, Math.Abs(PlanimetriaSintetico.Terreno(l[i], l[i + 1]) - c.Elev));
                    }
            Assert.That(peorCurva, Is.LessThan(0.08));
        }

        [Test]
        public void CotaEn_FueraDeLaGrillaEsNaN()
        {
            var L = PlanimetriaSintetico.Generar();
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
            double la, lo;
            L.ALatLon(5000, 5000, out la, out lo);
            Assert.That(double.IsNaN(PlanimetriaCabina.CotaEn(R, la, lo)), Is.True);
            L.ALatLon(200, 150, out la, out lo);
            Assert.That(PlanimetriaCabina.CotaEn(R, la, lo), Is.EqualTo(PlanimetriaSintetico.Terreno(200, 150)).Within(0.03));
        }
    }
}
