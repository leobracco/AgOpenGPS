// ============================================================================
// ResolveLanIpSubredTests.cs — elegir la IP del PC por la SUBRED del nodo.
//
// El caso real (Las Gringas, 2026-09-25): la tablet del tractor tiene dos
// redes — el hotspot de datos (10.140.241.x) y la Ethernet de la sembradora
// (192.168.5.10). ResolveLanIp devolvía la PRIMERA de la lista, que resultó
// ser la del hotspot, y el .bin se le ofrecía al nodo en una IP a la que el
// nodo no llega. La descarga moría con http_-1, el nodo entraba en panic,
// de ahí a safe_mode, y desde safe_mode rechazaba la OTA siguiente. Costó
// una mañana y hubo que falsear el broker_address de VistaX para desempatar.
//
// El nodo publica su propia IP en el announcement, así que la elección no
// tiene por qué ser una adivinanza: sirve la NIC que comparte subred con él.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class ResolveLanIpSubredTests
    {
        private static List<KeyValuePair<string, int>> Nics(params string[] ipBarraPrefijo)
        {
            var res = new List<KeyValuePair<string, int>>();
            foreach (var s in ipBarraPrefijo)
            {
                var p = s.Split('/');
                res.Add(new KeyValuePair<string, int>(p[0], int.Parse(p[1])));
            }
            return res;
        }

        /// <summary>La tablet de Las Gringas, en el orden en que las enumera
        /// Windows: el hotspot primero.</summary>
        private static List<KeyValuePair<string, int>> TabletGringas()
            => Nics("10.140.241.158/24", "192.168.5.10/24");

        [Test]
        public void ElNodoDelTractorRecibeLaIpDelTractor_NoLaDelHotspot()
        {
            // QX-C45857858428 estaba en 192.168.5.21.
            string elegida = FirmwareOtaClient.ElegirPorSubred(TabletGringas(), "192.168.5.21");

            Assert.That(elegida, Is.EqualTo("192.168.5.10"),
                "tiene que salir la Ethernet de la sembradora, no la primera de la lista");
        }

        [Test]
        public void ElOrdenDeLasNicsNoCambiaLaRespuesta()
        {
            // Justo el bug: antes ganaba la primera. Ahora da igual el orden.
            var alReves = Nics("192.168.5.10/24", "10.140.241.158/24");
            Assert.That(FirmwareOtaClient.ElegirPorSubred(alReves, "192.168.5.21"),
                Is.EqualTo("192.168.5.10"));
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), "192.168.5.21"),
                Is.EqualTo("192.168.5.10"));
        }

        [Test]
        public void UnNodoEnElHotspotRecibeLaIpDelHotspot()
        {
            // La otra cara: si el nodo SÍ está del lado del hotspot, esa es la
            // que sirve. La regla es la subred, no "la Ethernet siempre".
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), "10.140.241.77"),
                Is.EqualTo("10.140.241.158"));
        }

        [Test]
        public void SiNingunaNicComparteSubred_DevuelveNullYSeCaeAlCaminoViejo()
        {
            // Nodo detrás de un router: no hay respuesta correcta por subred, y
            // inventar una sería peor que dejar que decida el método de antes.
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), "172.20.0.5"), Is.Null);
        }

        [Test]
        public void SinIpDelNodo_DevuelveNull()
        {
            // Nodo que todavía no publicó announcement.
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), null), Is.Null);
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), ""), Is.Null);
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), "   "), Is.Null);
        }

        [Test]
        public void IpDelNodoIlegible_NoRompe()
        {
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), "no-es-una-ip"), Is.Null);
            Assert.That(FirmwareOtaClient.ElegirPorSubred(TabletGringas(), "192.168.5"), Is.Null);
        }

        [Test]
        public void ConDosCandidatasGanaLaSubredMasEspecifica()
        {
            // Una /16 que engloba a una /24: la red más chica es la que de
            // verdad llega al nodo, la grande puede ser una VPN que lo tapa.
            var nics = Nics("192.168.0.9/16", "192.168.5.10/24");
            Assert.That(FirmwareOtaClient.ElegirPorSubred(nics, "192.168.5.21"),
                Is.EqualTo("192.168.5.10"));
        }

        [Test]
        public void LaMascaraSeRespeta_NoAlcanzaConQueEmpiecenIgual()
        {
            // 192.168.5.10/24 NO llega a 192.168.6.21: comparar los primeros
            // octetos "a ojo" habría dado un falso positivo.
            var nics = Nics("192.168.5.10/24");
            Assert.That(FirmwareOtaClient.ElegirPorSubred(nics, "192.168.6.21"), Is.Null);

            // Con /16 sí.
            Assert.That(FirmwareOtaClient.ElegirPorSubred(Nics("192.168.5.10/16"), "192.168.6.21"),
                Is.EqualTo("192.168.5.10"));
        }

        [Test]
        public void ListaDeNicsVaciaONula_NoRompe()
        {
            Assert.That(FirmwareOtaClient.ElegirPorSubred(null, "192.168.5.21"), Is.Null);
            Assert.That(FirmwareOtaClient.ElegirPorSubred(new List<KeyValuePair<string, int>>(), "192.168.5.21"),
                Is.Null);
        }

        [Test]
        public void PrefijoFueraDeRango_SeIgnoraEsaNic()
        {
            var nics = Nics("192.168.5.10/0", "192.168.5.11/33", "192.168.5.12/24");
            Assert.That(FirmwareOtaClient.ElegirPorSubred(nics, "192.168.5.21"),
                Is.EqualTo("192.168.5.12"));
        }

        [Test]
        public void Prefijo32_SoloCoincideConsigoMismo()
        {
            var nics = Nics("192.168.5.10/32");
            Assert.That(FirmwareOtaClient.ElegirPorSubred(nics, "192.168.5.10"),
                Is.EqualTo("192.168.5.10"));
            Assert.That(FirmwareOtaClient.ElegirPorSubred(nics, "192.168.5.21"), Is.Null);
        }
    }
}
