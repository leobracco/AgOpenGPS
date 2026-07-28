// El panel nativo de QuantiX lee estos nombres de campo del JSON. Si el
// serializador los cambia (o alguien renombra una propiedad), el panel no
// falla: muestra "--" y ceros, que en cabina se lee como "el motor no esta
// haciendo nada". Estos tests fijan el contrato del cable.

using System.Collections.Generic;
using System.Text.Json;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class QxRuntimeWireTests
    {
        private static JsonElement SerializarUnMotor(string unidad)
        {
            var cfg = new MotoresConfig
            {
                Nodos = new List<QxNodoConfig>
                {
                    new QxNodoConfig
                    {
                        Uid = "AABBCC",
                        Habilitado = true,
                        Motores = new[]
                        {
                            new QxMotorConfig
                            {
                                Nombre = "Fertilizante",
                                UnidadDosis = unidad,
                                DosisFija = 150,
                                SemillasVuelta = 100,
                                MeterCal = 2.0,
                                DientesEngranaje = 24,
                                MaxHz = 40,
                                Cortes = new List<int> { 1, 2 },
                            },
                        },
                    },
                },
            };
            var snap = QxRuntimeBuilder.Build(cfg, new QxRuntimeContexto
            {
                VelocidadKmh = 7.2,
                AnchoTotalM = 28,
            });

            var json = AgpJson.Serialize(snap);
            return JsonDocument.Parse(json).RootElement
                .GetProperty("motores")[0];
        }

        [Test]
        public void Los_campos_que_lee_el_panel_viajan_en_snake_case()
        {
            var m = SerializarUnMotor("kg_ha");
            foreach (var campo in new[]
            {
                "nodo_uid", "motor_index", "nombre", "dosis_objetivo", "unidad_dosis",
                "target_rpm", "max_rpm", "max_dose_at_current_speed",
            })
            {
                Assert.That(m.TryGetProperty(campo, out _), Is.True,
                    "el panel nativo lee '" + campo + "' — sin el muestra guiones");
            }
        }

        [Test]
        public void La_unidad_viaja_tal_cual_la_compara_el_panel()
        {
            // El panel hace la comparacion contra la cadena "sem_m" para decidir
            // si rotula sem/m o kg/ha.
            Assert.That(SerializarUnMotor("sem_m").GetProperty("unidad_dosis").GetString(),
                Is.EqualTo("sem_m"));
            Assert.That(SerializarUnMotor("kg_ha").GetProperty("unidad_dosis").GetString(),
                Is.EqualTo("kg_ha"));
        }

        [Test]
        public void El_pps_sigue_estando_en_el_cable_pero_no_es_lo_que_se_muestra()
        {
            // Se serializa porque diagnostico y firmware lo usan; la regla de
            // no mostrarlo es de la UI, no del transporte.
            var m = SerializarUnMotor("kg_ha");
            Assert.That(m.TryGetProperty("target_pps", out var pps), Is.True);
            Assert.That(pps.GetDouble(), Is.EqualTo(420).Within(0.01));
        }
    }
}
