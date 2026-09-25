// ============================================================================
// OrbitXRegistroTests.cs — lo que el tractor le manda al cloud para poder
// diagnosticar la siembra sin ir hasta el lote (1.0.84, caso Las Gringas).
//
// Dos cosas se pueden equivocar en silencio y las dos arruinan el diagnóstico:
//
//   1. La traducción de pps a la unidad del operario. Si el cloud calcula
//      distinto que el widget de cabina, el día que no coincidan se discute
//      cuál de los dos miente en vez de mirar el motor.
//
//   2. El partido en líneas del log de eventos. AgLibrary separa los eventos
//      con "\r" PELADO (Log.EventWriter), no con CRLF: partir sólo por '\n'
//      manda la jornada entera como UNA línea y el log deja de ser log.
// ============================================================================

using System.Collections.Generic;
using System.Text;
using AgroParallel.Models;
using AgroParallel.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class OrbitXRegistroTests
    {
        // ---- pps → dosis -----------------------------------------------------

        /// <summary>Motor de semilla de Las Gringas: placa de 24 alvéolos,
        /// transmisión 21/11 → 12,57 semillas por vuelta, un surco por motor.</summary>
        private static QxMotorConfigDto MotorGringas()
        {
            return new QxMotorConfigDto
            {
                Habilitado = true,
                UnidadDosis = "sem_m",
                SemillasVuelta = 12.57,
                DientesEngranaje = 24,
                Cortes = new List<int> { 3 },
            };
        }

        [Test]
        public void SemPorMetro_DaLaDosisQueElOperarioPidio()
        {
            var m = MotorGringas();
            double velMs = 7.0 / 3.6;                 // siembran a 7 km/h

            // pps que hacen falta para 7,5 sem/m (la dosis manual que tienen
            // cargada en los 14 motores).
            double semPorPulso = m.SemillasVuelta / m.DientesEngranaje;
            double pps = 7.5 * velMs / semPorPulso;

            double dosis = OrbitXSync.PpsADosis(pps, m, esSem: true, velMs: velMs, anchoM: 7.28);

            Assert.That(dosis, Is.EqualTo(7.5).Within(0.01),
                "la inversa tiene que devolver la misma dosis que se pidió");
        }

        [Test]
        public void SemPorMetro_DivideEntreLosSurcosDelMotor()
        {
            // Un motor que alimenta 7 surcos entrega 7 veces más pulsos para la
            // misma dosis POR SURCO. Es la misma cuenta que hace el widget de
            // cabina (WidgetQuantiXController): sin dividir por los surcos, el
            // cloud reportaría 7 veces la dosis real.
            var uno = MotorGringas();
            var siete = MotorGringas();
            siete.Cortes = new List<int> { 1, 2, 3, 4, 5, 6, 7 };

            double velMs = 2.0;
            double pps = 100;

            double dosisUno = OrbitXSync.PpsADosis(pps, uno, true, velMs, 7.28);
            double dosisSiete = OrbitXSync.PpsADosis(pps, siete, true, velMs, 7.28);

            Assert.That(dosisSiete, Is.EqualTo(dosisUno / 7.0).Within(1e-9));
        }

        [Test]
        public void KgPorHa_UsaMeterCalYAncho()
        {
            var m = new QxMotorConfigDto
            {
                Habilitado = true,
                UnidadDosis = "kg_ha",
                MeterCal = 50,
            };
            double velMs = 2.0, ancho = 10.0, pps = 4.0;

            // pps × meter_cal × 10 / (ancho × vel) — la inversa del bridge.
            double esperado = pps * 50 * 10.0 / (ancho * velMs);

            Assert.That(OrbitXSync.PpsADosis(pps, m, false, velMs, ancho),
                Is.EqualTo(esperado).Within(1e-9));
        }

        [Test]
        public void ConElTractorQuieto_LaDosisEsCero()
        {
            // Dividir por una velocidad de casi cero da números enormes que
            // después hay que explicarle a alguien. Con la máquina parada la
            // dosis por metro no significa nada.
            var m = MotorGringas();
            Assert.That(OrbitXSync.PpsADosis(50, m, true, velMs: 0.0, anchoM: 7.28), Is.EqualTo(0));
            Assert.That(OrbitXSync.PpsADosis(50, m, true, velMs: 0.05, anchoM: 7.28), Is.EqualTo(0));
        }

        [Test]
        public void SinPulsos_NoInventaDosis()
        {
            var m = MotorGringas();
            Assert.That(OrbitXSync.PpsADosis(0, m, true, 2.0, 7.28), Is.EqualTo(0));
        }

        // ---- log de eventos --------------------------------------------------

        [Test]
        public void ElLogSeParteAunqueAgLibrarySepareConCarriageReturnPelado()
        {
            // Formato real del archivo: Log.EventWriter appendea "\r" al final
            // de cada evento, sin "\n".
            string bloque = "12:07:33-> MQTT: [-] QX-C45857858428 (clientes=2)\r"
                          + "12:07:38-> MQTT: [+] QX-C45857858428 (clientes=3)\r";

            var lineas = OrbitXSync.PartirLineasLog(bloque, 500);

            Assert.That(lineas.Count, Is.EqualTo(2), "dos eventos, dos líneas");
            Assert.That(lineas[0], Does.Contain("[-] QX-C45857858428"));
            Assert.That(lineas[1], Does.Contain("[+] QX-C45857858428"));
        }

        [Test]
        public void ElLogTambienSeParteConCrLf()
        {
            var lineas = OrbitXSync.PartirLineasLog("una\r\ndos\r\n", 500);
            Assert.That(lineas, Is.EqualTo(new[] { "una", "dos" }));
        }

        [Test]
        public void ElLogRespetaElTopeDeLineasPorEnvio()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 50; i++) sb.Append("linea ").Append(i).Append('\r');

            Assert.That(OrbitXSync.PartirLineasLog(sb.ToString(), 10).Count, Is.EqualTo(10));
        }

        [Test]
        public void ElLogRecortaLineasKilometricas()
        {
            // Un stacktrace largo no puede inflar el doc del día. Truncate deja
            // los 1000 primeros caracteres más los puntos suspensivos, que son
            // la marca de que la línea venía cortada.
            var lineas = OrbitXSync.PartirLineasLog(new string('x', 5000) + "\r", 500);
            Assert.That(lineas.Count, Is.EqualTo(1));
            Assert.That(lineas[0].Length, Is.LessThanOrEqualTo(1001));
            Assert.That(lineas[0], Does.EndWith("…"));
        }

        // ---- corte del bloque leído -----------------------------------------

        [Test]
        public void SiQuedaArchivoPorDelante_SeCortaEnElUltimoFinDeLinea()
        {
            // "uno\rdos\rtre" — "tre" está a medio escribir: no se consume.
            byte[] buf = Encoding.UTF8.GetBytes("uno\rdos\rtre");
            int corte = OrbitXSync.CorteHastaFinDeLinea(buf, buf.Length, hastaElFinal: false, maxLineas: 400);

            Assert.That(corte, Is.EqualTo(8), "hasta el \\r que cierra 'dos'");
            Assert.That(Encoding.UTF8.GetString(buf, 0, corte), Is.EqualTo("uno\rdos\r"));
        }

        [Test]
        public void SiElBloqueLlegaAlFinDelArchivo_SeConsumeEntero()
        {
            // La última línea del archivo está completa aunque AgLibrary no le
            // haya puesto separador todavía.
            byte[] buf = Encoding.UTF8.GetBytes("uno\rdos");
            Assert.That(OrbitXSync.CorteHastaFinDeLinea(buf, buf.Length, true, 400),
                Is.EqualTo(buf.Length));
        }

        [Test]
        public void SinNingunFinDeLinea_NoSeConsumeNada()
        {
            // Una línea más larga que el bloque, a medio escribir: esperar al
            // próximo tick es mejor que mandarla partida.
            byte[] buf = Encoding.UTF8.GetBytes("una linea larguisima sin cortar");
            Assert.That(OrbitXSync.CorteHastaFinDeLinea(buf, buf.Length, false, 400),
                Is.EqualTo(0));
        }

        [Test]
        public void ElCorteRespetaElTopeDeLineas_YElOffsetNoSeComeLoQueNoSeMando()
        {
            // El bug que esto fija: se cortaba el bloque por BYTES y recién
            // después se recortaba a N líneas, pero el offset avanzaba sobre el
            // bloque ENTERO. Todo lo que quedaba afuera del tope se perdía sin
            // que nadie se enterara — justo el caso de una pantalla que estuvo
            // horas sin señal y juntó miles de líneas.
            var sb = new StringBuilder();
            for (int i = 0; i < 50; i++) sb.Append("linea ").Append(i).Append('\r');
            byte[] buf = Encoding.UTF8.GetBytes(sb.ToString());

            int corte = OrbitXSync.CorteHastaFinDeLinea(buf, buf.Length, hastaElFinal: true, maxLineas: 10);
            string consumido = Encoding.UTF8.GetString(buf, 0, corte);

            // Lo que se consume del archivo es EXACTAMENTE lo que se manda.
            Assert.That(OrbitXSync.PartirLineasLog(consumido, 10).Count, Is.EqualTo(10));
            Assert.That(consumido, Does.EndWith("linea 9\r"));
            Assert.That(corte, Is.LessThan(buf.Length), "el resto queda para el próximo tick");
        }

        [Test]
        public void CrLfCuentaComoUnSoloFinDeLinea()
        {
            // Con CRLF contado doble, el tope de líneas se alcanzaría a la mitad
            // y cada envío mandaría la mitad de lo que puede.
            byte[] buf = Encoding.UTF8.GetBytes("uno\r\ndos\r\ntres\r\n");
            int corte = OrbitXSync.CorteHastaFinDeLinea(buf, buf.Length, false, maxLineas: 2);

            Assert.That(Encoding.UTF8.GetString(buf, 0, corte), Is.EqualTo("uno\r\ndos\r\n"));
        }

        [Test]
        public void BufferVacio_NoRompe()
        {
            Assert.That(OrbitXSync.CorteHastaFinDeLinea(null, 10, false, 400), Is.EqualTo(0));
            Assert.That(OrbitXSync.CorteHastaFinDeLinea(new byte[0], 0, true, 400), Is.EqualTo(0));
        }
    }
}
