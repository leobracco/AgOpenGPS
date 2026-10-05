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

        /// <summary>Copia de BenchX/Sim/TerrenoSimulado (suelo, sin la antena):
        /// lomas de ±2 m en ~300 m + ±1 m en ~220 m. Es lo que ve PilotX con
        /// "RTK fijo + relieve" en BenchX.</summary>
        private static double SueloBenchX(double lat, double lon)
        {
            double norteM = lat * 111320.0;
            double esteM = lon * 111320.0 * Math.Cos(lat * Math.PI / 180.0);
            return 100.0 + 2.0 * Math.Sin(2.0 * Math.PI * esteM / 300.0) + 1.0 * Math.Cos(2.0 * Math.PI * norteM / 220.0);
        }

        [Test]
        public void RelieveDeBenchX_ElMapaRecuperaLasLomasSimuladas()
        {
            // Pasadas norte-sur cada 6 m sobre 300 × 400 m, un punto por metro,
            // como las graba EscritorElevacion (alturas redondeadas a mm).
            var pr = new ProyeccionLocal(-33.5, -61.2);
            var sb = new System.Text.StringBuilder(PlanimetriaSintetico.Cabecera);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int k = 0; k * 6 <= 300; k++)
            {
                double x = k * 6;
                for (int s = 0; s <= 400; s++)
                {
                    double y = k % 2 == 0 ? s : 400 - s;
                    double la, lo;
                    pr.ALatLon(x, y, out la, out lo);
                    double z = Math.Round(SueloBenchX(la, lo), 3);
                    sb.Append(la.ToString("F7", inv)).Append(',').Append(lo.ToString("F7", inv)).Append(',')
                      .Append(z.ToString("F3", inv)).Append(",4,").Append(x.ToString("F2", inv)).Append(',')
                      .Append(y.ToString("F2", inv)).Append(",0.000,0\r\n");
                }
            }
            var R = Planimetria.Calcular(new[] { sb.ToString() }, new OpcionesPlanimetria { Res = 3, IntervaloAuto = true });
            Assert.That(R.Ok, Is.True, R.Motivo);
            double peor = 0;
            for (int r = 0; r < R.Grilla.Ny; r++)
                for (int c = 0; c < R.Grilla.Nx; c++)
                {
                    double v = R.Grilla.Z[r * R.Grilla.Nx + c];
                    if (double.IsNaN(v)) continue;
                    double la, lo;
                    R.CeldaALatLon(c, r, out la, out lo);
                    double x = pr.X(lo), y = pr.Y(la);
                    if (x < 3 || x > 297 || y < 3 || y > 397) continue;
                    peor = Math.Max(peor, Math.Abs(v - SueloBenchX(la, lo)));
                }
            TestContext.Out.WriteLine("BenchX: desnivel " + R.Stats.DesnivelM + " m, curvas cada " + R.Intervalo + " m, peor " + (peor * 100).ToString("0.0") + " cm");
            Assert.That(peor, Is.LessThan(0.05));
            Assert.That(R.Stats.DesnivelM.Value, Is.InRange(3.0, 6.1));
            Assert.That(R.Curvas.Niveles.Count, Is.InRange(5, 16));
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
