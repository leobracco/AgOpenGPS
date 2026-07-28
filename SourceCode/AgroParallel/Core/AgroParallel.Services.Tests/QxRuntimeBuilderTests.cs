// Lo que el operario VE de QuantiX tiene que coincidir con lo que el motor
// HACE. Estos tests fijan esa coincidencia: la misma cascada de dosis y la
// misma formula de pulsos que usa el bridge. Cuando se desincronizaron, el
// widget mostraba un objetivo y la sembradora aplicaba otro.

using System.Collections.Generic;
using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class QxRuntimeBuilderTests
    {
        // Fertilizadora: 1 motor, 2 g/pulso, 24 dientes, 40 Hz de techo.
        private static MotoresConfig Fertilizadora(double dosisFija = 0, string campo = null)
        {
            var m = new QxMotorConfig
            {
                Nombre = "Fertilizante",
                UnidadDosis = "kg_ha",
                DosisFija = dosisFija,
                CampoDosis = campo,
                MeterCal = 2.0,
                DientesEngranaje = 24,
                MaxHz = 40,
                Cortes = new List<int> { 1, 2, 3, 4 },
            };
            return Uno(m);
        }

        // Sembradora: plato de 100 semillas/vuelta, 24 dientes, 12 surcos.
        private static MotoresConfig Sembradora(double dosisFija = 0)
        {
            var m = new QxMotorConfig
            {
                Nombre = "Semilla",
                UnidadDosis = "sem_m",
                DosisFija = dosisFija,
                SemillasVuelta = 100,
                MeterCal = 2.0,          // presente pero NO aplica a sem/m
                DientesEngranaje = 24,
                MaxHz = 40,
                Cortes = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
            };
            return Uno(m);
        }

        private static MotoresConfig Uno(QxMotorConfig m) => new MotoresConfig
        {
            Nodos = new List<QxNodoConfig>
            {
                new QxNodoConfig { Uid = "AABBCC", Habilitado = true, Motores = new[] { m } },
            },
        };

        private static QxRuntimeContexto Ctx(double vel, double ancho = 28, double mapa = 0) =>
            new QxRuntimeContexto { VelocidadKmh = vel, AnchoTotalM = ancho, DosisMapaGlobal = mapa };

        // ---- La coincidencia con el bridge -----------------------------------

        [Test]
        public void El_mapa_le_gana_a_la_dosis_fija()
        {
            // El bridge aplica "mapa manda". Si el widget muestra la fija, el
            // operario cree que esta tirando 150 y en realidad tira 200.
            var snap = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(8, mapa: 200));
            Assert.That(snap.Motores[0].DosisObjetivo, Is.EqualTo(200));
        }

        [Test]
        public void Sin_mapa_cae_a_la_dosis_fija()
        {
            var snap = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(8, mapa: 0));
            Assert.That(snap.Motores[0].DosisObjetivo, Is.EqualTo(150));
        }

        [Test]
        public void Manual_pisa_todo()
        {
            var cfg = Fertilizadora(dosisFija: 150);
            cfg.Nodos[0].Motores[0].ManualMode = true;
            cfg.Nodos[0].Motores[0].ManualDosis = 90;
            var snap = QxRuntimeBuilder.Build(cfg, Ctx(8, mapa: 200));
            Assert.That(snap.Motores[0].DosisObjetivo, Is.EqualTo(90),
                "el override manual del widget gana sobre el mapa y sobre la fija");
        }

        [Test]
        public void Campo_del_shapefile_se_consulta_por_nombre()
        {
            var ctx = Ctx(8, mapa: 200);
            ctx.CampoLookup = campo => campo == "DOSIS_N" ? 310 : 0;
            var snap = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150, campo: "DOSIS_N"), ctx);
            Assert.That(snap.Motores[0].DosisObjetivo, Is.EqualTo(310));
        }

        [Test]
        public void Los_pulsos_son_los_mismos_que_calcula_el_bridge()
        {
            // 150 kg/ha, 28 m, 7,2 km/h, 2 g/pulso -> 420 pps (verificado a mano).
            var snap = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(7.2));
            Assert.That(snap.Motores[0].TargetPps, Is.EqualTo(420).Within(0.01));
            Assert.That(snap.Motores[0].TargetPps,
                Is.EqualTo(QxPulseCalculator.Pps(new QxPulseInput
                {
                    Dosis = 150, VelocidadKmh = 7.2, SeccionOn = true,
                    AnchoM = 28, MeterCal = 2.0,
                })).Within(1e-9));
        }

        // ---- Sembradoras: la formula que faltaba -----------------------------

        [Test]
        public void Sembradora_usa_semillas_por_metro_no_kilos_por_hectarea()
        {
            // 6 sem/m, 12 surcos, 7,2 km/h -> 34,56 pps. Con la formula de
            // kg/ha daria 420: dos ordenes de magnitud de diferencia.
            var snap = QxRuntimeBuilder.Build(Sembradora(dosisFija: 6), Ctx(7.2));
            Assert.That(snap.Motores[0].TargetPps, Is.EqualTo(34.56).Within(0.01));
        }

        [Test]
        public void Sembradora_el_techo_se_mide_en_semillas_por_metro()
        {
            // 40 Hz con 100 sem/vuelta y 24 dientes = 4,1667 sem/pulso ->
            // 166,67 sem/s. A 7,2 km/h (2 m/s) y 12 surcos = 6,94 sem/m.
            var snap = QxRuntimeBuilder.Build(Sembradora(dosisFija: 6), Ctx(7.2));
            Assert.That(snap.Motores[0].MaxDoseAtCurrentSpeed, Is.EqualTo(6.944).Within(0.01));
        }

        [Test]
        public void Sembradora_sin_calibrar_el_plato_no_tiene_techo_conocido()
        {
            var cfg = Sembradora(dosisFija: 6);
            cfg.Nodos[0].Motores[0].SemillasVuelta = 0;
            var snap = QxRuntimeBuilder.Build(cfg, Ctx(7.2));
            Assert.That(snap.Motores[0].TargetPps, Is.Zero, "sin plato calibrado, motor quieto");
            Assert.That(snap.Motores[0].MaxDoseAtCurrentSpeed, Is.EqualTo(-1),
                "-1 es 'no se': la UI muestra un guion, un cero seria mentira");
        }

        // ---- Techo operativo -------------------------------------------------

        [Test]
        public void El_techo_de_dosis_baja_al_acelerar()
        {
            // Es la pregunta de campo: "hasta que velocidad puedo ir sin
            // quedarme corto de fertilizante".
            var lento = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(6));
            var rapido = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(12));
            Assert.That(rapido.Motores[0].MaxDoseAtCurrentSpeed,
                Is.EqualTo(lento.Motores[0].MaxDoseAtCurrentSpeed / 2).Within(0.01));
        }

        [Test]
        public void Parado_el_techo_es_desconocido_no_cero()
        {
            var snap = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(0));
            Assert.That(snap.Motores[0].TargetPps, Is.Zero);
            Assert.That(snap.Motores[0].MaxDoseAtCurrentSpeed, Is.EqualTo(-1));
        }

        [Test]
        public void La_curva_de_techo_trae_las_velocidades_tipicas_y_es_decreciente()
        {
            var m = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(8)).Motores[0];
            Assert.That(m.MaxDoseCurve, Has.Count.EqualTo(5));
            for (int i = 1; i < m.MaxDoseCurve.Count; i++)
                Assert.That(m.MaxDoseCurve[i].MaxDose, Is.LessThan(m.MaxDoseCurve[i - 1].MaxDose));
        }

        [Test]
        public void Motor_sin_techo_medido_no_inventa_curva()
        {
            var cfg = Fertilizadora(dosisFija: 150);
            cfg.Nodos[0].Motores[0].MaxHz = 0;         // nunca se midio
            var m = QxRuntimeBuilder.Build(cfg, Ctx(8)).Motores[0];
            Assert.That(m.MaxDoseCurve, Is.Empty);
            Assert.That(m.MaxRpm, Is.Zero);
            Assert.That(m.MaxDoseAtCurrentSpeed, Is.EqualTo(-1));
        }

        // ---- Lo que ve el operario -------------------------------------------

        [Test]
        public void Se_reporta_rpm_ademas_del_pps_interno()
        {
            // 420 pps con 24 dientes = 1050 rpm. El pps queda para el firmware.
            var m = QxRuntimeBuilder.Build(Fertilizadora(dosisFija: 150), Ctx(7.2)).Motores[0];
            Assert.That(m.TargetRpm, Is.EqualTo(1050).Within(0.01));
            Assert.That(m.MaxRpm, Is.EqualTo(100).Within(0.01));   // 40 Hz, 24 dientes
        }

        // ---- Que motores entran ---------------------------------------------

        [Test]
        public void Nodo_deshabilitado_no_aparece()
        {
            var cfg = Fertilizadora(dosisFija: 150);
            cfg.Nodos[0].Habilitado = false;
            Assert.That(QxRuntimeBuilder.Build(cfg, Ctx(8)).Motores, Is.Empty);
        }

        [Test]
        public void Nodo_sin_uid_no_aparece()
        {
            // Un nodo sin UID no se puede comandar por MQTT: mostrarlo seria
            // prometer un motor que no existe.
            var cfg = Fertilizadora(dosisFija: 150);
            cfg.Nodos[0].Uid = "";
            Assert.That(QxRuntimeBuilder.Build(cfg, Ctx(8)).Motores, Is.Empty);
        }

        [Test]
        public void Sin_configuracion_devuelve_vacio_sin_romper()
        {
            var snap = QxRuntimeBuilder.Build(null, Ctx(8));
            Assert.That(snap.Motores, Is.Empty);
            Assert.That(snap.CurrentSpeedKmh, Is.EqualTo(8));
        }
    }
}
