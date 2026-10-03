// ============================================================================
// IsoXmlFieldExporterTests.cs — export del lote a ISO-XML (TASKDATA.XML).
//
// La prueba que manda es la de IDA Y VUELTA: se exporta un lote (lindero con
// un hueco, cabecera, una AB y una curva) y se vuelve a leer con NUESTRO
// importador (IsoXmlFieldImporter). La geometría tiene que volver igual, con
// tolerancia de centímetros. Además el archivo tiene que abrir sin errores en
// la librería ISO 11783-10 de Dev4Agriculture (la que ya trae AgOpenGPS.Core),
// que valida estructura, tipos y referencias.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Protocols.ISOBUS;
using AgroParallel.Models;
using Dev4Agriculture.ISO11783.ISOXML;
using Dev4Agriculture.ISO11783.ISOXML.Messaging;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class IsoXmlFieldExporterTests
    {
        private const double TolM = 0.02;   // 2 cm
        private static readonly Wgs84 Origen = new Wgs84(-33.1234567, -61.7654321);

        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx_isoxml_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static LocalPlane Plano() => new LocalPlane(Origen, new SharedFieldProperties());

        private static CBoundaryList Anillo(params (double e, double n)[] pts)
        {
            var b = new CBoundaryList();
            foreach (var (e, n) in pts) b.fenceLine.Add(new vec3(e, n, 0));
            return b;
        }

        private static List<CBoundaryList> Linderos()
        {
            var exterior = Anillo((0, 0), (400, 0), (400, 250), (180, 300), (0, 250));
            // cabecera del exterior (lo que guarda Headland.txt)
            foreach (var (e, n) in new[] { (12.0, 12.0), (388.0, 12.0), (388.0, 240.0), (180.0, 288.0), (12.0, 240.0) })
                exterior.hdLine.Add(new vec3(e, n, 0));
            var hueco = Anillo((100, 100), (140, 100), (140, 130), (100, 130));
            var virtualDeGiro = Anillo((-50, -50), (-40, -50), (-40, -40));
            virtualDeGiro.isVirtualTurnBoundary = true;
            return new List<CBoundaryList> { exterior, hueco, virtualDeGiro };
        }

        private static List<CTrk> Guias()
        {
            var ab = new CTrk
            {
                mode = TrackMode.AB,
                name = "AB Norte",
                ptA = new vec2(20, 20),
                ptB = new vec2(25, 220),
            };
            ab.heading = Math.Atan2(ab.ptB.easting - ab.ptA.easting, ab.ptB.northing - ab.ptA.northing);

            var curva = new CTrk { mode = TrackMode.Curve, name = "Curva arroyo" };
            for (int i = 0; i <= 60; i++)
            {
                double e = 50 + i * 5;
                double n = 150 + 30 * Math.Sin(i / 10.0);
                curva.curvePts.Add(new vec3(e, n, 0));
            }
            curva.ptA = new vec2(curva.curvePts[0].easting, curva.curvePts[0].northing);
            curva.ptB = new vec2(curva.curvePts[60].easting, curva.curvePts[60].northing);

            var contorno = new CTrk { mode = TrackMode.bndCurve, name = "no se exporta" };
            return new List<CTrk> { ab, curva, contorno };
        }

        private IsoXmlExportResultadoDto Exportar(IsoXmlFieldExporter.IsoVersion v, string destino = null)
            => IsoXmlFieldExporter.Exportar(destino ?? _dir, "La Loma", Linderos(), Guias(), Plano(), v, "1.0.89");

        private static XmlNodeList PartesDelLote(string archivo)
        {
            var doc = new XmlDocument();
            doc.Load(archivo);
            var pfd = doc.SelectSingleNode("/ISO11783_TaskData/PFD");
            Assert.That(pfd, Is.Not.Null, "falta el PFD");
            return pfd.ChildNodes;
        }

        private ApplicationModel ModeloImport()
        {
            var am = new ApplicationModel(new DirectoryInfo(Path.Combine(_dir, "am")));
            am.LocalPlane = Plano();
            return am;
        }

        private static double DistAPolilinea(double e, double n, IList<vec3> pl)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < pl.Count; i++)
            {
                double ax = pl[i].easting, ay = pl[i].northing, bx = pl[i + 1].easting, by = pl[i + 1].northing;
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((e - ax) * dx + (n - ay) * dy) / l2));
                double px = ax + t * dx - e, py = ay + t * dy - n;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }
            return best;
        }

        // ---------------------------------------------------------------------

        [TestCase(IsoXmlFieldExporter.IsoVersion.V4)]
        [TestCase(IsoXmlFieldExporter.IsoVersion.V3)]
        public void IdaYVuelta_LinderoYHueco_MismaGeometria(IsoXmlFieldExporter.IsoVersion v)
        {
            var r = Exportar(v);
            Assert.That(r.Ok, Is.True, r.Error);

            var imp = new IsoXmlFieldImporter(PartesDelLote(r.Archivo), ModeloImport());
            var bnds = imp.GetBoundaries();
            var orig = Linderos();

            Assert.That(bnds, Has.Count.EqualTo(2), "exterior + hueco (el virtual de giro NO se exporta)");
            for (int k = 0; k < 2; k++)
            {
                Assert.That(bnds[k].fenceLine, Has.Count.EqualTo(orig[k].fenceLine.Count));
                for (int i = 0; i < orig[k].fenceLine.Count; i++)
                {
                    Assert.That(bnds[k].fenceLine[i].easting, Is.EqualTo(orig[k].fenceLine[i].easting).Within(TolM));
                    Assert.That(bnds[k].fenceLine[i].northing, Is.EqualTo(orig[k].fenceLine[i].northing).Within(TolM));
                }
            }
        }

        [TestCase(IsoXmlFieldExporter.IsoVersion.V4)]
        [TestCase(IsoXmlFieldExporter.IsoVersion.V3)]
        public void IdaYVuelta_Cabecera(IsoXmlFieldExporter.IsoVersion v)
        {
            var r = Exportar(v);
            var hd = new IsoXmlFieldImporter(PartesDelLote(r.Archivo), ModeloImport()).GetHeadland();
            var orig = Linderos()[0].hdLine;

            Assert.That(hd, Has.Count.EqualTo(orig.Count));
            for (int i = 0; i < orig.Count; i++)
            {
                Assert.That(hd[i].easting, Is.EqualTo(orig[i].easting).Within(TolM));
                Assert.That(hd[i].northing, Is.EqualTo(orig[i].northing).Within(TolM));
            }
        }

        [TestCase(IsoXmlFieldExporter.IsoVersion.V4)]
        [TestCase(IsoXmlFieldExporter.IsoVersion.V3)]
        public void IdaYVuelta_AB_MismosPuntosYRumbo(IsoXmlFieldExporter.IsoVersion v)
        {
            var r = Exportar(v);
            var trks = new IsoXmlFieldImporter(PartesDelLote(r.Archivo), ModeloImport()).GetGuidanceLines();
            var orig = Guias()[0];

            var ab = trks.Single(t => t.mode == TrackMode.AB);
            Assert.That(ab.name, Is.EqualTo("AB Norte"));
            Assert.That(ab.ptA.easting, Is.EqualTo(orig.ptA.easting).Within(TolM));
            Assert.That(ab.ptA.northing, Is.EqualTo(orig.ptA.northing).Within(TolM));
            Assert.That(ab.ptB.easting, Is.EqualTo(orig.ptB.easting).Within(TolM));
            Assert.That(ab.ptB.northing, Is.EqualTo(orig.ptB.northing).Within(TolM));
            Assert.That(ab.heading, Is.EqualTo(orig.heading).Within(1e-4));
        }

        [TestCase(IsoXmlFieldExporter.IsoVersion.V4)]
        [TestCase(IsoXmlFieldExporter.IsoVersion.V3)]
        public void IdaYVuelta_Curva_LosPuntosOriginalesCaenSobreLaImportada(IsoXmlFieldExporter.IsoVersion v)
        {
            // El importador estira 100 m las puntas y re-muestrea cada 0,5 m (es
            // lo que necesita el guiado): no vuelven los mismos vértices, vuelve
            // la misma LÍNEA. Cada vértice original tiene que caer sobre ella.
            var r = Exportar(v);
            var trks = new IsoXmlFieldImporter(PartesDelLote(r.Archivo), ModeloImport()).GetGuidanceLines();
            var orig = Guias()[1];

            var curva = trks.Single(t => t.mode == TrackMode.Curve);
            Assert.That(curva.name, Is.EqualTo("Curva arroyo"));
            foreach (var p in orig.curvePts)
                Assert.That(DistAPolilinea(p.easting, p.northing, curva.curvePts), Is.LessThan(TolM));
        }

        [Test]
        public void SoloSeExportanABYCurvas()
        {
            var r = Exportar(IsoXmlFieldExporter.IsoVersion.V4);
            var trks = new IsoXmlFieldImporter(PartesDelLote(r.Archivo), ModeloImport()).GetGuidanceLines();

            Assert.That(trks, Has.Count.EqualTo(2));
            Assert.That(r.Guias, Is.EqualTo(2));
            Assert.That(r.Linderos, Is.EqualTo(2));
            Assert.That(r.Cabecera, Is.True);
        }

        [TestCase(IsoXmlFieldExporter.IsoVersion.V4, "4")]
        [TestCase(IsoXmlFieldExporter.IsoVersion.V3, "3")]
        public void ArchivoEnCarpetaTASKDATA_ConVersion(IsoXmlFieldExporter.IsoVersion v, string major)
        {
            var r = Exportar(v);

            Assert.That(r.Archivo, Is.EqualTo(Path.Combine(_dir, "TASKDATA", "TASKDATA.XML")));
            var doc = new XmlDocument();
            doc.Load(r.Archivo);
            var raiz = doc.DocumentElement;
            Assert.That(raiz.Name, Is.EqualTo("ISO11783_TaskData"));
            Assert.That(raiz.GetAttribute("VersionMajor"), Is.EqualTo(major));
            Assert.That(raiz.GetAttribute("DataTransferOrigin"), Is.EqualTo("1"));
            Assert.That(raiz.GetAttribute("ManagementSoftwareManufacturer"), Is.EqualTo("Agro Parallel"));
            Assert.That(doc.SelectSingleNode("/ISO11783_TaskData/PFD/@C").Value, Is.EqualTo("La Loma"));
            // Sin BOM: hay terminales que no lo digieren.
            byte[] b = File.ReadAllBytes(r.Archivo);
            Assert.That(b[0], Is.EqualTo((byte)'<'));
        }

        [Test]
        public void V4_GuiasComoGGPConGPNPropio_V3ComoLSGSuelta()
        {
            var r4 = Exportar(IsoXmlFieldExporter.IsoVersion.V4);
            var d4 = new XmlDocument(); d4.Load(r4.Archivo);
            var gpns = d4.SelectNodes("//GGP/GPN");
            Assert.That(gpns.Count, Is.EqualTo(2));
            var ids = gpns.Cast<XmlElement>().Select(x => x.GetAttribute("A")).ToList();
            Assert.That(ids, Is.All.StartWith("GPN"));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(2));
            Assert.That(d4.SelectNodes("/ISO11783_TaskData/PFD/LSG").Count, Is.EqualTo(0));

            var r3 = Exportar(IsoXmlFieldExporter.IsoVersion.V3, Path.Combine(_dir, "v3"));
            var d3 = new XmlDocument(); d3.Load(r3.Archivo);
            Assert.That(d3.SelectNodes("//GGP").Count, Is.EqualTo(0), "GGP no existe en v3");
            Assert.That(d3.SelectNodes("/ISO11783_TaskData/PFD/LSG[@A='5']").Count, Is.EqualTo(2));
        }

        [Test]
        public void AreaDelLote_ExteriorMenosHuecos()
        {
            var r = Exportar(IsoXmlFieldExporter.IsoVersion.V4);
            var doc = new XmlDocument(); doc.Load(r.Archivo);
            ulong area = ulong.Parse(doc.SelectSingleNode("/ISO11783_TaskData/PFD/@D").Value);

            // exterior: 400×250 + triángulo 400×50/2 = 110000; hueco 40×30 = 1200
            Assert.That(area, Is.EqualTo(108800UL));
        }

        [TestCase(IsoXmlFieldExporter.IsoVersion.V4)]
        [TestCase(IsoXmlFieldExporter.IsoVersion.V3)]
        public void AbreSinErroresNiAvisosEnLaLibreriaISO11783(IsoXmlFieldExporter.IsoVersion v)
        {
            var r = Exportar(v);
            var iso = ISOXML.Load(Path.GetDirectoryName(r.Archivo));

            // Ni errores ni avisos: la librería avisa (Warning) atributos y
            // elementos que no existen en el esquema ISO 11783-10.
            var msgs = iso.Messages.Select(m => m.Type + " " + m.Title + ": " + m.Description).ToList();
            Assert.That(msgs, Is.Empty, string.Join("\n", msgs));
            Assert.That(iso.Data.Partfield, Has.Count.EqualTo(1));
            Assert.That(iso.Data.Partfield[0].PartfieldDesignator, Is.EqualTo("La Loma"));
            Assert.That(iso.Data.Partfield[0].PolygonnonTreatmentZoneonly, Has.Count.EqualTo(3));
            if (v == IsoXmlFieldExporter.IsoVersion.V4)
                Assert.That(iso.Data.Partfield[0].GuidanceGroup, Has.Count.EqualTo(2));
        }

        [Test]
        public void LaValidacionDeLaLibreria_SiAvisaLoQueNoEsDelEsquema()
        {
            // Control del control: si la librería no reportara nada nunca, el
            // test de arriba no probaría nada. Atributo y elemento inventados.
            string td = Path.Combine(_dir, "malo", "TASKDATA");
            Directory.CreateDirectory(td);
            File.WriteAllText(Path.Combine(td, "TASKDATA.XML"),
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ISO11783_TaskData VersionMajor=\"4\" VersionMinor=\"3\" "
                + "ManagementSoftwareManufacturer=\"x\" ManagementSoftwareVersion=\"1\" DataTransferOrigin=\"1\">"
                + "<PFD A=\"PFD1\" C=\"x\" D=\"1\" Z=\"raro\"><GGP A=\"GGP1\"><GPN A=\"GPN1\" C=\"1\"><XYZ/><LSG A=\"5\"><PNT A=\"6\" C=\"-33\" D=\"-61\"/></LSG></GPN></GGP></PFD>"
                + "</ISO11783_TaskData>");

            var iso = ISOXML.Load(td);
            Assert.That(iso.Messages.Count(), Is.GreaterThanOrEqualTo(2));
        }

        // Atributos obligatorios según ISO 11783-10 (la librería no los exige al
        // leer: completa con defaults). Se chequean acá a mano.
        [TestCase(IsoXmlFieldExporter.IsoVersion.V4)]
        [TestCase(IsoXmlFieldExporter.IsoVersion.V3)]
        public void AtributosObligatoriosISO11783_10(IsoXmlFieldExporter.IsoVersion v)
        {
            var r = Exportar(v);
            var doc = new XmlDocument(); doc.Load(r.Archivo);
            var obligatorios = new Dictionary<string, string[]>
            {
                ["ISO11783_TaskData"] = new[] { "VersionMajor", "VersionMinor", "ManagementSoftwareManufacturer", "ManagementSoftwareVersion", "DataTransferOrigin" },
                ["PFD"] = new[] { "A", "C", "D" },
                ["PLN"] = new[] { "A" },
                ["LSG"] = new[] { "A" },
                ["PNT"] = new[] { "A", "C", "D" },
                ["GGP"] = new[] { "A" },
                ["GPN"] = new[] { "A", "C" },
            };
            foreach (XmlElement e in doc.SelectNodes("//*"))
            {
                Assert.That(obligatorios.ContainsKey(e.Name), Is.True, "elemento inesperado " + e.Name);
                foreach (var a in obligatorios[e.Name])
                    Assert.That(e.HasAttribute(a), Is.True, e.Name + " sin " + a);
            }
            if (v == IsoXmlFieldExporter.IsoVersion.V3)
            {
                // Nada de lo que v3 no conoce: GGP/GPN, ids de PLN, PNT tipo 6/7/9.
                Assert.That(doc.SelectNodes("//GGP").Count, Is.EqualTo(0));
                Assert.That(doc.SelectNodes("//PLN[@E]").Count, Is.EqualTo(0));
                Assert.That(doc.SelectNodes("//PNT[@A!='2']").Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void ElegirArchivoEnLaRaizDelPendrive_CreaLaCarpetaTASKDATA()
        {
            // El explorador devuelve "E:\TASKDATA.XML": la carpeta va al lado.
            var r = Exportar(IsoXmlFieldExporter.IsoVersion.V4, Path.Combine(_dir, "TASKDATA.XML"));
            Assert.That(r.Archivo, Is.EqualTo(Path.Combine(_dir, "TASKDATA", "TASKDATA.XML")));

            // Y si eligió adentro de una TASKDATA, no anida otra.
            var r2 = Exportar(IsoXmlFieldExporter.IsoVersion.V4, Path.Combine(_dir, "otro", "TASKDATA", "TASKDATA.XML"));
            Assert.That(r2.Archivo, Is.EqualTo(Path.Combine(_dir, "otro", "TASKDATA", "TASKDATA.XML")));
        }

        [Test]
        public void TASKDATAExistente_NoSePisa_SeApartaEntera()
        {
            string td = Path.Combine(_dir, "TASKDATA");
            Directory.CreateDirectory(td);
            File.WriteAllText(Path.Combine(td, "TASKDATA.XML"), "<viejo/>");
            File.WriteAllText(Path.Combine(td, "TLG00001.BIN"), "x");

            var r = Exportar(IsoXmlFieldExporter.IsoVersion.V4);

            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(r.Apartada, Is.Not.Empty);
            Assert.That(File.ReadAllText(Path.Combine(r.Apartada, "TASKDATA.XML")), Is.EqualTo("<viejo/>"));
            Assert.That(File.Exists(Path.Combine(r.Apartada, "TLG00001.BIN")), Is.True);
            Assert.That(File.Exists(Path.Combine(td, "TLG00001.BIN")), Is.False);
        }

        [Test]
        public void SinLinderoNiGuias_NoExportaNada()
        {
            var r = IsoXmlFieldExporter.Exportar(_dir, "Vacío", new List<CBoundaryList>(), new List<CTrk>(),
                Plano(), IsoXmlFieldExporter.IsoVersion.V4, "1");

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Does.Contain("lindero"));
            Assert.That(Directory.Exists(Path.Combine(_dir, "TASKDATA")), Is.False);
        }

        [Test]
        public void NombresConCaracteresRaros_SeEscapan()
        {
            var g = Guias();
            g[0].name = "AB <1> & \"2\"";
            var r = IsoXmlFieldExporter.Exportar(_dir, "Lote & Cía", Linderos(), g, Plano(),
                IsoXmlFieldExporter.IsoVersion.V4, "1");

            var doc = new XmlDocument(); doc.Load(r.Archivo);   // no tira = XML válido
            Assert.That(doc.SelectSingleNode("/ISO11783_TaskData/PFD/@C").Value, Is.EqualTo("Lote & Cía"));
            var trks = new IsoXmlFieldImporter(PartesDelLote(r.Archivo), ModeloImport()).GetGuidanceLines();
            Assert.That(trks.Any(t => t.name == "AB <1> & \"2\""), Is.True);
        }
    }
}
