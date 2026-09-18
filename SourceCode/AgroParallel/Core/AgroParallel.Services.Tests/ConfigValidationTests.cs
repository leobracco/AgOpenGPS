// ============================================================================
// ConfigValidationTests.cs — Reglas de validación de configs (AGP-CFG-001).
// Por cada regla: al menos un caso válido y uno inválido, con los DTOs reales.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class ConfigValidationTests
    {
        // ---------------- Helpers de armado ----------------

        private static FlowXConfigDto FlowXValido()
        {
            return new FlowXConfigDto
            {
                Nodos = new List<FxNodoConfigDto>
                {
                    new FxNodoConfigDto
                    {
                        Uid = "a1b2c3d4",
                        Habilitado = true,
                        AnchoBarraM = 24,
                        Productos = new List<FxProductoDto> { new FxProductoDto() }
                    }
                }
            };
        }

        private static QxMotoresConfigDto QuantiXValido()
        {
            return new QxMotoresConfigDto
            {
                Nodos = new List<QxNodoConfigDto>
                {
                    new QxNodoConfigDto
                    {
                        Uid = "qx01",
                        Habilitado = true,
                        Motores = new[] { new QxMotorConfigDto() }
                    }
                }
            };
        }

        // ---------------- ValidarRango genérico ----------------

        [Test]
        public void Rango_DentroDeLimites_Pasa()
        {
            Assert.That(ConfigValidation.ValidarRango(5, 0, 10, "x").Ok, Is.True);
        }

        [Test]
        public void Rango_FueraDeLimites_Falla()
        {
            var r = ConfigValidation.ValidarRango(11, 0, 10, "x");
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Errores, Has.Count.EqualTo(1));
        }

        // ---------------- FlowX ----------------

        [Test]
        public void FlowX_ConfigValida_Pasa()
        {
            Assert.That(ConfigValidation.ValidarFlowX(FlowXValido()).Ok, Is.True);
        }

        [Test]
        public void FlowX_PwmMinFueraDeRango_Falla()
        {
            var cfg = FlowXValido();
            cfg.Nodos[0].Productos[0].PwmMin = 5000;
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.False);
        }

        [Test]
        public void FlowX_PwmMinMayorQueMax_Falla()
        {
            var cfg = FlowXValido();
            cfg.Nodos[0].Productos[0].PwmMin = 3000;
            cfg.Nodos[0].Productos[0].PwmMax = 2000;
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.False);
        }

        [Test]
        public void FlowX_PwmMaxCero_EsSinTecho_Pasa()
        {
            // PwmMax 0 = "sin techo extra" según el firmware — no debe rechazarse.
            var cfg = FlowXValido();
            cfg.Nodos[0].Productos[0].PwmMax = 0;
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.True);
        }

        [Test]
        public void FlowX_MeterCalCero_Falla()
        {
            var cfg = FlowXValido();
            cfg.Nodos[0].Productos[0].MeterCal = 0;
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.False);
        }

        [Test]
        public void FlowX_KpNegativo_Falla()
        {
            var cfg = FlowXValido();
            cfg.Nodos[0].Productos[0].Kp = -1;
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.False);
        }

        [Test]
        public void FlowX_AnchoNegativo_Falla()
        {
            var cfg = FlowXValido();
            cfg.Nodos[0].AnchoBarraM = -3;
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.False);
        }

        [Test]
        public void FlowX_AnchoCero_DefaultDeAlta_Pasa()
        {
            // Ancho 0 es el default del nodo recién descubierto — no romper el alta.
            var cfg = FlowXValido();
            cfg.Nodos[0].AnchoBarraM = 0;
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.True);
        }

        [Test]
        public void FlowX_NodoHabilitadoSinUid_Falla()
        {
            var cfg = FlowXValido();
            cfg.Nodos[0].Uid = "";
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.False);
        }

        [Test]
        public void FlowX_UidConEspacios_Falla()
        {
            var cfg = FlowXValido();
            cfg.Nodos[0].Uid = "a1 b2";
            Assert.That(ConfigValidation.ValidarFlowX(cfg).Ok, Is.False);
        }

        // ---------------- QuantiX ----------------

        [Test]
        public void QuantiX_ConfigValida_Pasa()
        {
            Assert.That(ConfigValidation.ValidarQuantiX(QuantiXValido()).Ok, Is.True);
        }

        [Test]
        public void QuantiX_PulsosPorVueltaCero_Falla()
        {
            var cfg = QuantiXValido();
            cfg.Nodos[0].Motores[0].DientesEngranaje = 0;
            Assert.That(ConfigValidation.ValidarQuantiX(cfg).Ok, Is.False);
        }

        [Test]
        public void QuantiX_DosisNegativa_Falla()
        {
            var cfg = QuantiXValido();
            cfg.Nodos[0].Motores[0].DosisFija = -10;
            Assert.That(ConfigValidation.ValidarQuantiX(cfg).Ok, Is.False);
        }

        [Test]
        public void QuantiX_PwmMinMayorQueMax_Falla()
        {
            var cfg = QuantiXValido();
            cfg.Nodos[0].Motores[0].PwmMin = 4095;
            cfg.Nodos[0].Motores[0].PwmMax = 600;
            Assert.That(ConfigValidation.ValidarQuantiX(cfg).Ok, Is.False);
        }

        [Test]
        public void QuantiX_MeterCalCero_Falla()
        {
            var cfg = QuantiXValido();
            cfg.Nodos[0].Motores[0].MeterCal = 0;
            Assert.That(ConfigValidation.ValidarQuantiX(cfg).Ok, Is.False);
        }

        // ---------------- VistaX config ----------------

        [Test]
        public void VistaXConfig_Default_Pasa()
        {
            Assert.That(ConfigValidation.ValidarVistaXConfig(new VistaXConfigDto()).Ok, Is.True);
        }

        [Test]
        public void VistaXConfig_TimeoutMenorA500_Falla()
        {
            var cfg = new VistaXConfigDto { SensorTimeoutMs = 200 };
            Assert.That(ConfigValidation.ValidarVistaXConfig(cfg).Ok, Is.False);
        }

        [Test]
        public void VistaXConfig_IntervaloUiMuyBajo_Falla()
        {
            var cfg = new VistaXConfigDto { UiUpdateIntervalMs = 10 };
            Assert.That(ConfigValidation.ValidarVistaXConfig(cfg).Ok, Is.False);
        }

        // ---------------- VistaX implemento ----------------

        [Test]
        public void VistaXImplemento_Default_Pasa()
        {
            Assert.That(ConfigValidation.ValidarVistaXImplemento(new VistaXImplementoDto()).Ok, Is.True);
        }

        [Test]
        public void VistaXImplemento_SurcosPorTorreFueraDeRango_Falla()
        {
            var imp = new VistaXImplementoDto();
            imp.Setup.SurcosPorTorre = 40;
            Assert.That(ConfigValidation.ValidarVistaXImplemento(imp).Ok, Is.False);
        }

        [Test]
        public void VistaXImplemento_SurcosPorTorreCero_Derivado_Pasa()
        {
            // 0 = derivar TotalSurcos/Torres — default legítimo.
            var imp = new VistaXImplementoDto();
            imp.Setup.SurcosPorTorre = 0;
            Assert.That(ConfigValidation.ValidarVistaXImplemento(imp).Ok, Is.True);
        }

        [Test]
        public void VistaXImplemento_SensorActivoSinUid_Falla()
        {
            var imp = new VistaXImplementoDto();
            imp.MapeoSensores.Add(new VistaXSensorConfigDto { Uid = "", IsActive = true });
            Assert.That(ConfigValidation.ValidarVistaXImplemento(imp).Ok, Is.False);
        }

        [Test]
        public void VistaXImplemento_SurcoDesdeMayorQueHasta_Falla()
        {
            var imp = new VistaXImplementoDto();
            imp.MapeoSensores.Add(new VistaXSensorConfigDto
            {
                Uid = "vx01",
                SurcoDesde = 8,
                SurcoHasta = 4
            });
            Assert.That(ConfigValidation.ValidarVistaXImplemento(imp).Ok, Is.False);
        }

        // ---------------- SectionX ----------------

        [Test]
        public void SectionX_ConfigValida_Pasa()
        {
            var cfg = new SectionXConfigDto
            {
                Nodos = new List<SxNodoConfigDto>
                {
                    new SxNodoConfigDto
                    {
                        Uid = "sx01",
                        Cables = new List<SxCableMapDto> { new SxCableMapDto { Cable = 1, SeccionAOG = 1 } }
                    }
                }
            };
            Assert.That(ConfigValidation.ValidarSectionX(cfg).Ok, Is.True);
        }

        [Test]
        public void SectionX_CableFueraDeRango_Falla()
        {
            var cfg = new SectionXConfigDto
            {
                Nodos = new List<SxNodoConfigDto>
                {
                    new SxNodoConfigDto
                    {
                        Uid = "sx01",
                        Cables = new List<SxCableMapDto> { new SxCableMapDto { Cable = 17, SeccionAOG = 1 } }
                    }
                }
            };
            Assert.That(ConfigValidation.ValidarSectionX(cfg).Ok, Is.False);
        }

        [Test]
        public void SectionX_MasDe16Cables_Falla()
        {
            var cables = new List<SxCableMapDto>();
            for (int i = 1; i <= 17; i++) cables.Add(new SxCableMapDto { Cable = 1, SeccionAOG = i });
            var cfg = new SectionXConfigDto
            {
                Nodos = new List<SxNodoConfigDto> { new SxNodoConfigDto { Uid = "sx01", Cables = cables } }
            };
            Assert.That(ConfigValidation.ValidarSectionX(cfg).Ok, Is.False);
        }

        // ---------------- LineX ----------------

        [Test]
        public void LineX_ConfigValida_Pasa()
        {
            var cfg = new LineXConfigDto
            {
                Nodos = new List<LxNodoConfigDto> { new LxNodoConfigDto { Uid = "lx01" } }
            };
            Assert.That(ConfigValidation.ValidarLineX(cfg).Ok, Is.True);
        }

        [Test]
        public void LineX_SectionCountFueraDeRango_Falla()
        {
            var cfg = new LineXConfigDto
            {
                Nodos = new List<LxNodoConfigDto> { new LxNodoConfigDto { Uid = "lx01", SectionCount = 33 } }
            };
            Assert.That(ConfigValidation.ValidarLineX(cfg).Ok, Is.False);
        }

        [Test]
        public void LineX_SectionCountCero_Falla()
        {
            var cfg = new LineXConfigDto
            {
                Nodos = new List<LxNodoConfigDto> { new LxNodoConfigDto { Uid = "lx01", SectionCount = 0 } }
            };
            Assert.That(ConfigValidation.ValidarLineX(cfg).Ok, Is.False);
        }

        [Test]
        public void LineX_MinUsMayorQueMaxUs_Falla()
        {
            var nodo = new LxNodoConfigDto { Uid = "lx01" };
            nodo.Surcos.Add(new LxSurcoDto { MinUs = 2600, MaxUs = 2500 });
            var cfg = new LineXConfigDto { Nodos = new List<LxNodoConfigDto> { nodo } };
            Assert.That(ConfigValidation.ValidarLineX(cfg).Ok, Is.False);
        }

        [Test]
        public void LineX_TimeoutMenorA500_Falla()
        {
            var cfg = new LineXConfigDto
            {
                Nodos = new List<LxNodoConfigDto> { new LxNodoConfigDto { Uid = "lx01", CommTimeoutMs = 100 } }
            };
            Assert.That(ConfigValidation.ValidarLineX(cfg).Ok, Is.False);
        }

        // ---------------- StormX ----------------

        [Test]
        public void StormX_ConfigValida_Pasa()
        {
            Assert.That(ConfigValidation.ValidarStormX(new StormXConfigDto()).Ok, Is.True);
        }

        [Test]
        public void StormX_LogIntervalCero_NoLoggear_Pasa()
        {
            // 0 = no loggear (documentado en el DTO) — default legítimo.
            var cfg = new StormXConfigDto { LogIntervalSec = 0 };
            Assert.That(ConfigValidation.ValidarStormX(cfg).Ok, Is.True);
        }

        [Test]
        public void StormX_LogIntervalNegativo_Falla()
        {
            var cfg = new StormXConfigDto { LogIntervalSec = -5 };
            Assert.That(ConfigValidation.ValidarStormX(cfg).Ok, Is.False);
        }

        [Test]
        public void StormX_VientoMinMayorQueMax_Falla()
        {
            var cfg = new StormXConfigDto();
            cfg.Limits.WindMinMs = 6.0;
            cfg.Limits.WindMaxMs = 5.5;
            Assert.That(ConfigValidation.ValidarStormX(cfg).Ok, Is.False);
        }

        [Test]
        public void StormX_HumedadFueraDeRango_Falla()
        {
            var cfg = new StormXConfigDto();
            cfg.Limits.HumMinPct = 150;
            Assert.That(ConfigValidation.ValidarStormX(cfg).Ok, Is.False);
        }

        // ---------------- Implemento central ----------------

        [Test]
        public void Implemento_ConfigValida_Pasa()
        {
            var imp = new ImplementoDto { Nombre = "Sembradora 1", AnchoTotalM = 12 };
            Assert.That(ConfigValidation.ValidarImplemento(imp).Ok, Is.True);
        }

        [Test]
        public void Implemento_SinNombre_Falla()
        {
            var imp = new ImplementoDto { Nombre = "  ", AnchoTotalM = 12 };
            Assert.That(ConfigValidation.ValidarImplemento(imp).Ok, Is.False);
        }

        [Test]
        public void Implemento_AnchoNegativo_Falla()
        {
            var imp = new ImplementoDto { Nombre = "Sembradora 1", AnchoTotalM = -1 };
            Assert.That(ConfigValidation.ValidarImplemento(imp).Ok, Is.False);
        }

        [Test]
        public void Implemento_AnchoCero_ReciénCreado_Pasa()
        {
            // Ancho 0 = implemento blank recién creado — no bloquear guardados parciales.
            var imp = new ImplementoDto { Nombre = "Implemento nuevo", AnchoTotalM = 0 };
            Assert.That(ConfigValidation.ValidarImplemento(imp).Ok, Is.True);
        }

        [Test]
        public void Implemento_NodoUidConEspacios_Falla()
        {
            var imp = new ImplementoDto { Nombre = "Sembradora 1", AnchoTotalM = 12 };
            imp.NodosUids.Add("aa bb");
            Assert.That(ConfigValidation.ValidarImplemento(imp).Ok, Is.False);
        }

        // ---------------- Null-safety ----------------

        [Test]
        public void ConfigNull_Falla()
        {
            Assert.That(ConfigValidation.ValidarFlowX(null).Ok, Is.False);
            Assert.That(ConfigValidation.ValidarQuantiX(null).Ok, Is.False);
            Assert.That(ConfigValidation.ValidarSectionX(null).Ok, Is.False);
            Assert.That(ConfigValidation.ValidarLineX(null).Ok, Is.False);
            Assert.That(ConfigValidation.ValidarStormX(null).Ok, Is.False);
            Assert.That(ConfigValidation.ValidarVistaXConfig(null).Ok, Is.False);
            Assert.That(ConfigValidation.ValidarVistaXImplemento(null).Ok, Is.False);
            Assert.That(ConfigValidation.ValidarImplemento(null).Ok, Is.False);
        }

        // ---------------- Trenes del implemento ----------------

        [Test]
        public void Trenes_DistanciaFueraDeRango_Falla()
        {
            var imp = new ImplementoDto();
            imp.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
            imp.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 25 }); // > 20
            Assert.That(ConfigValidation.ValidarTrenes(imp).Ok, Is.False);
        }

        [Test]
        public void Trenes_SurcoConTrenInexistente_Falla()
        {
            var imp = new ImplementoDto();
            imp.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
            imp.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 2 });
            imp.Surcos.Add(new SurcoDto { Numero = 1, TrenId = 9 });
            Assert.That(ConfigValidation.ValidarTrenes(imp).Ok, Is.False);
        }

        [Test]
        public void Trenes_ConfigSana_Pasa()
        {
            var imp = new ImplementoDto();
            imp.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
            imp.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 2.5 });
            imp.Surcos.Add(new SurcoDto { Numero = 1, TrenId = 2 });
            Assert.That(ConfigValidation.ValidarTrenes(imp).Ok, Is.True);
        }

        [Test]
        public void Trenes_IdsDuplicados_Falla()
        {
            var imp = new ImplementoDto();
            imp.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
            imp.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 2 });
            imp.Trenes.Add(new TrenDto { Id = 2, DistanciaM = 5 });
            Assert.That(ConfigValidation.ValidarTrenes(imp).Ok, Is.False);
        }
    }
}
