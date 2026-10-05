// ============================================================================
// PlanimetriaTests.cs — port C# de lib/planimetria.js (OrbitX, fase 2).
//
// Dos familias de tests:
//   1. Los MISMOS tests de tests/lib/planimetria.test.mjs, contra el lote
//      sintético con la verdad conocida (error de la grilla, nivelación,
//      celdas sin dato, bajos, curvas).
//   2. EQUIVALENCIA con Node: Fixtures/planimetria-ref.json es la grilla que
//      calculó Node con el mismo lote (PlanimetriaSintetico es un port exacto
//      del generador). La cabina y el panel web tienen que mostrar el mismo
//      mapa: misma geometría de grilla y misma altura por celda ±2 mm.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class PlanimetriaTests
    {
        private static readonly Lazy<LoteSintetico> Lote = new Lazy<LoteSintetico>(() => PlanimetriaSintetico.Generar());
        private static readonly Lazy<ResultadoPlanimetria> ResDefault =
            new Lazy<ResultadoPlanimetria>(() => Planimetria.Calcular(Lote.Value.Partes, new OpcionesPlanimetria { Res = 3 }));

        private static JObject Ref()
        {
            string p = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "planimetria-ref.json");
            return JObject.Parse(File.ReadAllText(p));
        }

        private sealed class Errores
        {
            public double MaxInterior, RmsInterior, Rms;
        }

        private static Errores CalcErrores(ResultadoPlanimetria R, LoteSintetico L)
        {
            var G = R.Grilla;
            var interior = new List<double>();
            var todos = new List<double>();
            for (int r = 0; r < G.Ny; r++)
            {
                for (int c = 0; c < G.Nx; c++)
                {
                    double v = G.Z[r * G.Nx + c];
                    if (double.IsNaN(v)) continue;
                    double la, lo;
                    R.CeldaALatLon(c, r, out la, out lo);
                    double e = Math.Abs(v - L.VerdadLatLon(la, lo));
                    todos.Add(e);
                    double x, y;
                    L.AXY(la, lo, out x, out y);
                    if (x >= 0 && x <= 400 && y >= 0 && y <= 300) interior.Add(e);
                }
            }
            Func<List<double>, double> rms = a => Math.Sqrt(a.Sum(e => e * e) / a.Count);
            return new Errores { MaxInterior = interior.Max(), RmsInterior = rms(interior), Rms = rms(todos) };
        }

        private static double SesgoPorPasada(ResultadoPlanimetria R, LoteSintetico L, bool corregida)
        {
            int nS = R.Stats.Pasadas;
            var s = new double[nS];
            var c = new double[nS];
            double[] zz = corregida ? R.ZCorregida : R.ZOriginal;
            for (int i = 0; i < R.X.Length; i++)
            {
                double la, lo;
                R.Proyeccion.ALatLon(R.X[i], R.Y[i], out la, out lo);
                s[R.Seg[i]] += zz[i] - L.VerdadLatLon(la, lo);
                c[R.Seg[i]]++;
            }
            double m = s.Sum() / c.Sum();
            double q = 0;
            int n = 0;
            for (int k = 0; k < nS; k++)
            {
                if (c[k] > 30) { double v = s[k] / c[k] - m; q += v * v; n++; }
            }
            return Math.Sqrt(q / n);
        }

        // ---------------------------------------------------------------------
        //  Port de planimetria.test.mjs
        // ---------------------------------------------------------------------

        [Test]
        public void Parseo_SoloFilasDe8CamposConQuality4_LaCabeceraNoCuenta()
        {
            string txt = PlanimetriaSintetico.Cabecera
                + "-33.1000000,-61.7000000,100.123,4,0.00,0.00,0.000,0\r\n"
                + "-33.1000100,-61.7000000,100.200,1,0.00,1.11,0.000,0\r\n"
                + "-33.1000200,-61.7000000,100.300,4,1,234.56,2.22,0.000,0\r\n"
                + "-33.1000300,-61.7000000,100.400,4,0.00,3.33,0.000,0\r\n";
            var P = Planimetria.JuntarPuntos(new[] { txt });
            Assert.That(P.N, Is.EqualTo(2));
            Assert.That(P.Cuenta.Partes, Is.EqualTo(1));
            Assert.That(P.Cuenta.Filas, Is.EqualTo(4));
            Assert.That(P.Cuenta.Q4, Is.EqualTo(2));
            Assert.That(P.Cuenta.NoRtk, Is.EqualTo(1));
            Assert.That(P.Cuenta.Invalidas, Is.EqualTo(1));
            Assert.That(P.Z[0], Is.EqualTo(100.123));
            Assert.That(P.Z[1], Is.EqualTo(100.4));
        }

        [Test]
        public void Parseo_ArchivoDePilotXEscritoConPuntoDecimal()
        {
            // Lo que escribe EscritorElevacion: InvariantCulture, sin separador de miles.
            string txt = PlanimetriaSintetico.Cabecera + "-33.1234567,-61.7654321,98.765,4,12.34,-5.67,1.571,-2.5";
            var P = Planimetria.JuntarPuntos(new[] { txt });
            Assert.That(P.N, Is.EqualTo(1));
            Assert.That(P.Lat[0], Is.EqualTo(-33.1234567));
            Assert.That(P.Lon[0], Is.EqualTo(-61.7654321));
        }

        [Test]
        public void LecturaPorLineas_DaLoMismoQueElTextoEntero()
        {
            var acc = new AcumuladorElevacion();
            foreach (string parte in Lote.Value.Partes)
                using (var sr = new StringReader(parte)) acc.AgregarLineas(sr);
            var a = acc.Resultado();
            var b = Planimetria.JuntarPuntos(Lote.Value.Partes);
            Assert.That(a.N, Is.EqualTo(b.N));
            Assert.That(a.Cuenta.Filas, Is.EqualTo(b.Cuenta.Filas));
            Assert.That(a.Cuenta.NoRtk, Is.EqualTo(b.Cuenta.NoRtk));
            Assert.That(a.Cuenta.Invalidas, Is.EqualTo(b.Cuenta.Invalidas));
            Assert.That(a.Z, Is.EqualTo(b.Z));
            Assert.That(a.Lat, Is.EqualTo(b.Lat));
        }

        [Test]
        public void LoteSintetico_FiltraFilasViejasYDetectaPasadas()
        {
            var R = ResDefault.Value;
            Assert.That(R.Ok, Is.True);
            Assert.That(R.Stats.PuntosRtk, Is.EqualTo(Lote.Value.NPuntos));
            Assert.That(R.Stats.PuntosDescartadosNoRtk, Is.EqualTo(2));
            Assert.That(R.Stats.PuntosInvalidos, Is.EqualTo(1));
            Assert.That(Math.Abs(R.Stats.EspaciadoPasadasM.Value - 8), Is.LessThan(0.5));
            Assert.That(R.Stats.Pasadas, Is.InRange(40, 60));
        }

        [Test]
        public void Grilla_RecuperaLaLomaConErrorMenorA5cmDentroDelLote()
        {
            var L = Lote.Value;
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3, Limite = L.Limite });
            var e = CalcErrores(R, L);
            Assert.That(e.MaxInterior, Is.LessThanOrEqualTo(0.05), "error máximo interior");
            Assert.That(e.RmsInterior, Is.LessThanOrEqualTo(0.015), "RMS interior");
            Assert.That(R.Stats.DesnivelM.Value, Is.InRange(3.2, 4.0));
            Assert.That(R.Stats.CoberturaPct.Value, Is.GreaterThan(98));
        }

        [Test]
        public void Nivelacion_ReduceElSesgoEntrePasadas()
        {
            var L = Lote.Value;
            var sin = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3, Nivelar = false });
            var con = ResDefault.Value;
            double antes = SesgoPorPasada(con, L, false);
            double despues = SesgoPorPasada(con, L, true);
            Assert.That(con.Stats.NivelacionAplicada, Is.True);
            Assert.That(despues, Is.LessThan(antes * 0.5));
            Assert.That(con.Stats.SesgoRmsDespuesCm.Value, Is.LessThan(con.Stats.SesgoRmsAntesCm.Value * 0.5));
            Assert.That(CalcErrores(con, L).RmsInterior, Is.LessThan(CalcErrores(sin, L).RmsInterior));
            Assert.That(Math.Abs(con.Stats.ZMediaM.Value - sin.Stats.ZMediaM.Value), Is.LessThan(0.005));
        }

        [Test]
        public void SinDatosCerca_EsNaN_UnaFranjaSinPasadasQuedaVacia()
        {
            var L = PlanimetriaSintetico.Generar(hueco: new double[] { 100, 140 }, cabeceras: false);
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
            var G = R.Grilla;
            Func<double, double, double> celda = (x, y) =>
            {
                double la, lo;
                L.ALatLon(x, y, out la, out lo);
                double px = R.Proyeccion.X(lo), py = R.Proyeccion.Y(la);
                int c = (int)Math.Round((px - G.X0) / G.Res), r = (int)Math.Round((G.YTop - py) / G.Res);
                return G.Z[r * G.Nx + c];
            };
            Assert.That(double.IsNaN(celda(200, 120)), Is.True, "el medio de la franja vacía tiene que ser NaN");
            Assert.That(double.IsNaN(celda(200, 60)), Is.False, "donde hay pasadas tiene que haber dato");
            double la2, lo2;
            L.ALatLon(200, 330, out la2, out lo2);
            double px2 = R.Proyeccion.X(lo2), py2 = R.Proyeccion.Y(la2);
            int r2 = (int)Math.Round((G.YTop - py2) / G.Res);
            Assert.That(r2 < 0 || double.IsNaN(G.Z[r2 * G.Nx + (int)Math.Round((px2 - G.X0) / G.Res)]), Is.True);
        }

        [Test]
        public void SinPuntosRtk_OkFalseConMotivo_SinTirar()
        {
            var R = Planimetria.Calcular(new[] { PlanimetriaSintetico.Cabecera });
            Assert.That(R.Ok, Is.False);
            Assert.That(R.Motivo, Does.Contain("sin puntos"));
        }

        [Test]
        public void Bajos_UnPozoEnUnPlanoSeDetectaConSuProfundidad()
        {
            int nx = 40, ny = 30;
            var z = new float[nx * ny];
            for (int r = 0; r < ny; r++)
                for (int c = 0; c < nx; c++)
                {
                    double d2 = (c - 20) * (c - 20) + (r - 15) * (r - 15);
                    z[r * nx + c] = (float)(100 + 0.01 * c - 0.3 * Math.Exp(-d2 / (2 * 3 * 3)));
                }
            var B = Planimetria.Bajos(new GrillaAlturas { Z = z, Nx = nx, Ny = ny, Res = 2 }, 0.05, 20);
            Assert.That(B.Zonas.Count, Is.EqualTo(1));
            Assert.That(B.Zonas[0].ProfMaxM, Is.InRange(0.2, 0.31));
            Assert.That(Math.Abs(B.Zonas[0].Col - 20), Is.LessThan(2));
            Assert.That(Math.Abs(B.Zonas[0].Fila - 15), Is.LessThan(2));
            var plano = new float[nx * ny];
            for (int i = 0; i < plano.Length; i++) plano[i] = (float)(100 + 0.01 * (i % nx));
            Assert.That(Planimetria.Bajos(new GrillaAlturas { Z = plano, Nx = nx, Ny = ny, Res = 2 }).Zonas.Count, Is.EqualTo(0));
        }

        [Test]
        public void Bajos_EnElLoteSintetico_ElPozoDe50cmApareceConSuProfundidad()
        {
            var L = PlanimetriaSintetico.Generar(amplitud: 0);
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
            var G = R.Grilla;
            double px = R.Proyeccion.X(L.PozoLon), py = R.Proyeccion.Y(L.PozoLat);
            int c = (int)Math.Round((px - G.X0) / G.Res), r = (int)Math.Round((G.YTop - py) / G.Res);
            double prof = R.Bajos.Prof[r * G.Nx + c];
            Assert.That(prof, Is.InRange(0.3, 0.55));
            Assert.That(R.Stats.BajosCantidad, Is.EqualTo(1));
        }

        [Test]
        public void Curvas_ALaAlturaCorrectaYEncadenadas()
        {
            var L = Lote.Value;
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3, Intervalo = 0.25 });
            Assert.That(R.Curvas.Niveles.Count(nv => nv.Lineas.Count > 0), Is.GreaterThanOrEqualTo(10));
            double peor = 0;
            int n = 0;
            var largos = new List<int>();
            foreach (var nv in R.Curvas.Niveles)
            {
                foreach (var linea in nv.Lineas)
                {
                    var s = Planimetria.Simplificar(linea, 0.12);
                    largos.Add(s.Count);
                    foreach (var p in s)
                    {
                        double la, lo, x, y;
                        R.CeldaALatLon(p.C, p.F, out la, out lo);
                        L.AXY(la, lo, out x, out y);
                        if (x < 0 || x > 400 || y < 0 || y > 300) continue;
                        peor = Math.Max(peor, Math.Abs(L.VerdadLatLon(la, lo) - nv.Elev));
                        n++;
                    }
                }
            }
            Assert.That(n, Is.GreaterThan(200));
            Assert.That(peor, Is.LessThan(0.08), "peor vértice");
            Assert.That(largos.Average(), Is.GreaterThan(8), "líneas encadenadas, no segmentos sueltos");
        }

        [Test]
        public void CurvaDeCota_EsLaMismaQueLaDelNivelCalculado()
        {
            var R = ResDefault.Value;
            var nv = R.Curvas.Niveles[R.Curvas.Niveles.Count / 2];
            var una = Planimetria.CurvaDeCota(R.Grilla, nv.Elev);
            Assert.That(una.Lineas.Count, Is.EqualTo(nv.Lineas.Count));
            Assert.That(una.Lineas.Sum(l => l.Count), Is.EqualTo(nv.Lineas.Sum(l => l.Count)));
        }

        // ---------------------------------------------------------------------
        //  Equivalencia con Node (misma grilla que el panel de OrbitX)
        // ---------------------------------------------------------------------

        private static void CompararGrilla(JObject refG, ResultadoPlanimetria R, string nombre)
        {
            var G = R.Grilla;
            Assert.That(G.Nx, Is.EqualTo((int)refG["nx"]), nombre + " nx");
            Assert.That(G.Ny, Is.EqualTo((int)refG["ny"]), nombre + " ny");
            Assert.That(G.Res, Is.EqualTo((double)refG["res"]), nombre + " res");
            Assert.That(G.X0, Is.EqualTo((double)refG["x0"]).Within(1e-6), nombre + " x0");
            Assert.That(G.YTop, Is.EqualTo((double)refG["yTop"]).Within(1e-6), nombre + " yTop");
            Assert.That(R.Proyeccion.Lat0, Is.EqualTo((double)refG["lat0"]).Within(1e-9));
            Assert.That(R.Proyeccion.Lon0, Is.EqualTo((double)refG["lon0"]).Within(1e-9));

            double zBase = (double)refG["z_base"];
            byte[] zb = Convert.FromBase64String((string)refG["z_mm"]);
            byte[] bb = Convert.FromBase64String((string)refG["bajos_cm"]);
            int N = G.Nx * G.Ny, distintasNaN = 0, conDato = 0, bajosDistintos = 0;
            double peor = 0;
            for (int i = 0; i < N; i++)
            {
                int q = zb[2 * i] | (zb[2 * i + 1] << 8);
                bool refNaN = q == 0xFFFF;
                bool csNaN = float.IsNaN(G.Z[i]);
                if (refNaN != csNaN) { distintasNaN++; continue; }
                if (refNaN) continue;
                conDato++;
                peor = Math.Max(peor, Math.Abs(G.Z[i] - (zBase + q / 1000.0)));
                int bCs = R.Bajos.Prof[i] > 0 ? Math.Max(1, Math.Min(254, (int)Math.Floor(R.Bajos.Prof[i] * 100 + 0.5))) : 0;
                if (Math.Abs(bCs - bb[i]) > 1) bajosDistintos++;
            }
            TestContext.Out.WriteLine(nombre + ": celdas con dato " + conDato + ", peor |Δz| " + (peor * 1000).ToString("0.00") + " mm, NaN distintas "
                + distintasNaN + ", bajos distintos " + bajosDistintos);
            // El volcado de Node está cuantizado a 1 mm (±0,5 mm): ±2 mm es "la misma grilla".
            Assert.That(peor, Is.LessThanOrEqualTo(0.002), nombre + " peor |Δz| contra Node");
            Assert.That(distintasNaN, Is.LessThanOrEqualTo(N / 1000), nombre + " celdas con/sin dato distintas");
            Assert.That(bajosDistintos, Is.LessThanOrEqualTo(N / 1000), nombre + " bajos distintos");

            var st = refG["stats"];
            Assert.That(R.Stats.Pasadas, Is.EqualTo((int)st["pasadas"]), nombre + " pasadas");
            Assert.That(R.Stats.PuntosUsados, Is.EqualTo((int)st["puntos_usados"]), nombre + " puntos usados");
            Assert.That(R.Stats.EspaciadoPasadasM, Is.EqualTo((double)st["espaciado_pasadas_m"]), nombre + " espaciado");
            Assert.That(R.Stats.DesnivelM.Value, Is.EqualTo((double)st["desnivel_m"]).Within(0.002), nombre + " desnivel");
            Assert.That(R.Stats.BajosCantidad, Is.EqualTo((int)st["bajos_cantidad"]), nombre + " bajos");
            Assert.That(R.Stats.PendienteMediaPct.Value, Is.EqualTo((double)st["pendiente_media_pct"]).Within(0.02), nombre + " pendiente");
            Assert.That(R.Stats.NivelacionAplicada, Is.EqualTo((bool)st["nivelacion"]["aplicada"]));
            Assert.That(R.Stats.SesgoRmsDespuesCm.GetValueOrDefault(), Is.EqualTo(st["nivelacion"]["sesgo_rms_despues_cm"].Type == JTokenType.Null
                ? 0 : (double)st["nivelacion"]["sesgo_rms_despues_cm"]).Within(0.05), nombre + " sesgo después");
        }

        private static void CompararCurvas(JToken refC, ResultadoPlanimetria R, string nombre)
        {
            Assert.That(R.Curvas.Intervalo, Is.EqualTo((double)refC["intervalo"]).Within(1e-12), nombre + " intervalo");
            var niveles = (JArray)refC["curvas"];
            Assert.That(R.Curvas.Niveles.Count, Is.EqualTo(niveles.Count), nombre + " cantidad de niveles");
            for (int k = 0; k < niveles.Count; k++)
            {
                var nv = R.Curvas.Niveles[k];
                Assert.That(nv.Elev, Is.EqualTo((double)niveles[k]["elev"]).Within(1e-9), nombre + " cota " + k);
                int vRef = (int)niveles[k]["vertices"];
                int vCs = nv.Lineas.Sum(l => l.Count);
                // Una celda que en Node quedó en el límite (±0,5 mm) puede sumar o sacar un cruce.
                Assert.That(Math.Abs(vCs - vRef), Is.LessThanOrEqualTo(Math.Max(4, vRef / 100)), nombre + " vértices en la cota " + nv.Elev);
            }
        }

        [Test]
        public void EquivalenciaNode_LoteConLomas_MismaGrillaBajosYCurvas()
        {
            var refJ = Ref();
            CompararGrilla((JObject)refJ["lote"], ResDefault.Value, "lomas");
            CompararCurvas(refJ["lote"], ResDefault.Value, "lomas i=0,1");
            var R25 = Planimetria.Calcular(Lote.Value.Partes, new OpcionesPlanimetria { Res = 3, Intervalo = 0.25 });
            CompararCurvas(refJ["curvas025"], R25, "lomas i=0,25");
        }

        [Test]
        public void EquivalenciaNode_LoteSinLomas_MismaGrillaYElPozo()
        {
            var L = PlanimetriaSintetico.Generar(amplitud: 0);
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
            CompararGrilla((JObject)Ref()["sinLomas"], R, "sin lomas");
        }
    }
}
