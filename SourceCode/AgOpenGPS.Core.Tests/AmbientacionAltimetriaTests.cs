// ============================================================================
// AmbientacionAltimetriaTests.cs — ambientes loma/media/bajo y la prescripción
// que sale de ellos. El último test cierra el circuito con la maquinaria REAL
// de prescripciones (PrescripcionService): escribe el GeoJSON en la carpeta,
// lo activa en el lote y pregunta la dosis en una loma y en un bajo.
// ============================================================================

using System;
using System.IO;
using System.Linq;
using AgroParallel.Services;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class AmbientacionAltimetriaTests
    {
        private static GrillaAlturas Rampa(int nx = 20, int ny = 10, double res = 2)
        {
            // Sube 1 cm por columna: columnas 0..19 → 100,00 .. 100,19 m.
            var z = new float[nx * ny];
            for (int r = 0; r < ny; r++)
                for (int c = 0; c < nx; c++) z[r * nx + c] = (float)(100 + 0.01 * c);
            return new GrillaAlturas { Z = z, Nx = nx, Ny = ny, Res = res, X0 = 0, YTop = (ny - 1) * res };
        }

        [Test]
        public void Percentiles_UnCuartoBajoUnCuartoLomaLaMitadMedia()
        {
            var g = Rampa();
            var A = AmbientacionAltimetria.Clasificar(g, new ConfigAmbientacion { Suavizar = false });
            int bajo = A.Zona.Count(z => z == ResultadoAmbientacion.Bajo);
            int loma = A.Zona.Count(z => z == ResultadoAmbientacion.Loma);
            int media = A.Zona.Count(z => z == ResultadoAmbientacion.Media);
            Assert.That(bajo, Is.EqualTo(5 * 10));          // columnas 0..4
            Assert.That(loma, Is.EqualTo(4 * 10));          // columnas 16..19 (la 15 es el percentil 75)
            Assert.That(media, Is.EqualTo(11 * 10));
            Assert.That(A.AreaHa[1] + A.AreaHa[2] + A.AreaHa[3], Is.EqualTo(200 * 4 / 1e4).Within(1e-9));
        }

        [Test]
        public void Desnivel_RespectoDeLaMediana()
        {
            var g = Rampa();
            var A = AmbientacionAltimetria.Clasificar(g, new ConfigAmbientacion
            {
                Modo = ModoAmbientacion.Desnivel,
                DBajoM = 0.05,
                DLomaM = 0.05,
                Suavizar = false,
            });
            // mediana = 100,095 → bajo < 100,045 (col 0..4), loma > 100,145 (col 15..19)
            Assert.That(A.CotaBajo, Is.EqualTo(100.045).Within(1e-4));
            Assert.That(A.CotaLoma, Is.EqualTo(100.145).Within(1e-4));
            Assert.That(A.Zona[0], Is.EqualTo(ResultadoAmbientacion.Bajo));
            Assert.That(A.Zona[19], Is.EqualTo(ResultadoAmbientacion.Loma));
            Assert.That(A.Zona[10], Is.EqualTo(ResultadoAmbientacion.Media));
        }

        [Test]
        public void SinDato_NoTieneZona()
        {
            var g = Rampa();
            g.Z[3] = float.NaN;
            var A = AmbientacionAltimetria.Clasificar(g, new ConfigAmbientacion());
            Assert.That(A.Zona[3], Is.EqualTo(ResultadoAmbientacion.SinDato));
        }

        [Test]
        public void Mayoria_UnaCeldaSueltaTomaLaZonaDeSusVecinas()
        {
            int nx = 5, ny = 5;
            var z = Enumerable.Repeat((byte)2, nx * ny).ToArray();
            z[12] = 3;
            var f = AmbientacionAltimetria.Mayoria(z, nx, ny);
            Assert.That(f[12], Is.EqualTo(2));
            // un bloque grande no se come
            var b = Enumerable.Repeat((byte)2, nx * ny).ToArray();
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) b[r * nx + c] = 3;
            Assert.That(AmbientacionAltimetria.Mayoria(b, nx, ny)[6], Is.EqualTo(3));
        }

        [Test]
        public void Rectangulos_CubrenExactamenteLasCeldasSinSolaparse()
        {
            var L = PlanimetriaSintetico.Generar();
            var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
            var A = AmbientacionAltimetria.Clasificar(R.Grilla, new ConfigAmbientacion());
            var rects = AmbientacionAltimetria.Rectangulos(A.Zona, R.Grilla.Nx, R.Grilla.Ny);
            var cubierta = new byte[A.Zona.Length];
            foreach (var q in rects)
                for (int r = q.R0; r <= q.R1; r++)
                    for (int c = q.C0; c <= q.C1; c++)
                    {
                        int i = r * R.Grilla.Nx + c;
                        Assert.That(cubierta[i], Is.EqualTo(0), "solape en " + i);
                        cubierta[i] = q.Zona;
                    }
            Assert.That(cubierta, Is.EqualTo(A.Zona));
            // y muchos menos polígonos que celdas
            int celdas = A.Zona.Count(z => z != 0);
            TestContext.Out.WriteLine("celdas " + celdas + " → rectángulos " + rects.Count);
            Assert.That(rects.Count, Is.LessThan(celdas / 5));
        }

        [Test]
        public void Prescripcion_SeCargaEnPrescripcionServiceYDaLaDosisDeCadaZona()
        {
            string previo = AgroParallel.Common.AgpPaths.ConfigRoot;
            var proveedorPrevio = PrescripcionService.LoteActualProvider;
            string dir = Path.Combine(Path.GetTempPath(), "agp-alti-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "data", "prescripciones"));
                AgroParallel.Common.AgpPaths.ConfigRoot = dir;
                PrescripcionService.LoteActualProvider = () => "Sintetico";

                var L = PlanimetriaSintetico.Generar();
                var R = Planimetria.Calcular(L.Partes, new OpcionesPlanimetria { Res = 3 });
                var A = AmbientacionAltimetria.Clasificar(R.Grilla, new ConfigAmbientacion());
                string geo = AmbientacionAltimetria.PrescripcionGeoJson(R, A, new double[] { 0, 90, 70, 50 });
                File.WriteAllText(Path.Combine(dir, "data", "prescripciones", "Altimetria Sintetico.geojson"), geo);

                var svc = new PrescripcionService();
                var item = svc.ListAvailable().Single(i => i.Id == "altimetria-sintetico");
                Assert.That(item.PropiedadesCandidatas, Does.Contain("dosis"));
                bool act = svc.SetActive("altimetria-sintetico", "dosis");
                Assert.That(act, Is.True);

                // La celda más alta es loma y la más baja es bajo.
                var G = R.Grilla;
                int iMax = -1, iMin = -1;
                for (int i = 0; i < G.Z.Length; i++)
                {
                    if (float.IsNaN(G.Z[i])) continue;
                    if (iMax < 0 || G.Z[i] > G.Z[iMax]) iMax = i;
                    if (iMin < 0 || G.Z[i] < G.Z[iMin]) iMin = i;
                }
                double la, lo;
                R.CeldaALatLon(iMax % G.Nx, iMax / G.Nx, out la, out lo);
                Assert.That(svc.GetDoseAt(la, lo), Is.EqualTo(50), "dosis en la loma");
                R.CeldaALatLon(iMin % G.Nx, iMin / G.Nx, out la, out lo);
                Assert.That(svc.GetDoseAt(la, lo), Is.EqualTo(90), "dosis en el bajo");
                // fuera del relevamiento: 0 (cae a la dosis fija del producto)
                R.CeldaALatLon(-50, -50, out la, out lo);
                Assert.That(svc.GetDoseAt(la, lo), Is.EqualTo(0));
                svc.ClearActive();
            }
            finally
            {
                AgroParallel.Common.AgpPaths.ConfigRoot = previo;
                PrescripcionService.LoteActualProvider = proveedorPrevio;
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
