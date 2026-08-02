using AgroParallel.Models;
using AgroParallel.Services.Common;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class ImplementoSurcosTests
    {
        [Fact]
        public void Crecer_ConservaAsignacionYHeredaUltimoTren()
        {
            var impl = new ImplementoDto();
            impl.Surcos.Add(new SurcoDto { Numero = 1, TrenId = 1, SeccionPilotX = 1 });
            impl.Surcos.Add(new SurcoDto { Numero = 2, TrenId = 2, SeccionPilotX = 2 });
            ImplementoSurcos.Regenerar(impl, 4);
            Assert.Equal(4, impl.Surcos.Count);
            Assert.Equal(1, impl.Surcos[0].TrenId);
            Assert.Equal(2, impl.Surcos[1].TrenId);
            Assert.Equal(2, impl.Surcos[2].TrenId); // hereda del último
            Assert.Equal(2, impl.Surcos[3].TrenId);
            Assert.Equal(3, impl.Surcos[2].Numero);
            Assert.Equal(3, impl.Surcos[2].SeccionPilotX);
            Assert.Equal(4, impl.NumeroSurcos);
        }

        [Fact]
        public void Achicar_Trunca()
        {
            var impl = new ImplementoDto();
            for (int i = 1; i <= 5; i++)
                impl.Surcos.Add(new SurcoDto { Numero = i, TrenId = i <= 2 ? 1 : 2, SeccionPilotX = i });
            ImplementoSurcos.Regenerar(impl, 2);
            Assert.Equal(2, impl.Surcos.Count);
            Assert.Equal(1, impl.Surcos[1].TrenId);
        }

        [Fact]
        public void DesdeVacio_TodosTren1()
        {
            var impl = new ImplementoDto();
            ImplementoSurcos.Regenerar(impl, 3);
            Assert.Equal(3, impl.Surcos.Count);
            Assert.All(impl.Surcos, s => Assert.Equal(1, s.TrenId));
        }

        // La lista de Secciones del implemento es de donde el write-back
        // central→Tool deriva NumSections: un PUT con la lista vieja pisaba la
        // cantidad de secciones recién configurada (pasó en el equipo real:
        // "puse 14, reinicié y volvió a 3" — un restore por API mandó
        // secciones=3 con numero_surcos=14).
        [Fact]
        public void SincronizarSecciones_RegeneraALaCantidadConservandoLookaheads()
        {
            var impl = new ImplementoDto { NumeroSurcos = 4 };
            impl.Secciones.Add(new SeccionDto { Id = 1, Nombre = "Uno", LookaheadOn = 1.5, LookaheadOff = 0.5 });
            impl.Secciones.Add(new SeccionDto { Id = 2, Nombre = "Dos" });
            ImplementoSurcos.SincronizarSecciones(impl);
            Assert.Equal(4, impl.Secciones.Count);
            Assert.Equal(1.5, impl.Secciones[0].LookaheadOn, 3);   // conserva por índice
            Assert.Equal("Dos", impl.Secciones[1].Nombre);
            Assert.Equal(3, impl.Secciones[2].Id);                  // nuevos con id secuencial
        }

        [Fact]
        public void SincronizarSecciones_Trunca()
        {
            var impl = new ImplementoDto { NumeroSurcos = 2 };
            for (int i = 1; i <= 5; i++) impl.Secciones.Add(new SeccionDto { Id = i });
            ImplementoSurcos.SincronizarSecciones(impl);
            Assert.Equal(2, impl.Secciones.Count);
        }

        // El caso real: Secciones configuradas 14 × 0,52 m (Tool width 7,28)
        // pero el implemento central guardado con los defaults del seed
        // (28 m / 2 m). La geometría del Tool tiene que pisar SIEMPRE.
        [Fact]
        public void AplicarGeometriaDeTool_PisaAnchoYDistanciaConLoDeSecciones()
        {
            var impl = new ImplementoDto { NumeroSurcos = 14, AnchoTotalM = 28, DistanciaEntreSurcosM = 2 };
            for (int i = 1; i <= 14; i++)
                impl.Surcos.Add(new SurcoDto { Numero = i, TrenId = (i % 2) + 1, SeccionPilotX = i });
            var tool = new ToolConfigDto { Width = 7.28, NumSections = 14 };

            ImplementoSurcos.AplicarGeometriaDeTool(impl, tool);

            Assert.Equal(7.28, impl.AnchoTotalM, 3);
            Assert.Equal(0.52, impl.DistanciaEntreSurcosM, 3);
            Assert.Equal(2, impl.Surcos[0].TrenId); // asignación de trenes intacta
        }

        [Fact]
        public void AplicarGeometriaDeTool_AlineaCantidadDeSurcosConservandoTrenes()
        {
            var impl = new ImplementoDto { NumeroSurcos = 3, AnchoTotalM = 28 };
            impl.Surcos.Add(new SurcoDto { Numero = 1, TrenId = 1, SeccionPilotX = 1 });
            impl.Surcos.Add(new SurcoDto { Numero = 2, TrenId = 2, SeccionPilotX = 2 });
            impl.Surcos.Add(new SurcoDto { Numero = 3, TrenId = 2, SeccionPilotX = 3 });
            var tool = new ToolConfigDto { Width = 2.6, NumSections = 5 };

            ImplementoSurcos.AplicarGeometriaDeTool(impl, tool);

            Assert.Equal(5, impl.Surcos.Count);
            Assert.Equal(5, impl.NumeroSurcos);
            Assert.Equal(5, impl.Secciones.Count);
            Assert.Equal(1, impl.Surcos[0].TrenId);           // conserva por índice
            Assert.Equal(2, impl.Surcos[4].TrenId);           // nuevos heredan el último tren
            Assert.Equal(0.52, impl.DistanciaEntreSurcosM, 3); // 2,6 / 5
        }

        [Fact]
        public void AplicarGeometriaDeTool_SinToolOAnchoCero_NoTocaNada()
        {
            var impl = new ImplementoDto { NumeroSurcos = 14, AnchoTotalM = 7.28, DistanciaEntreSurcosM = 0.52 };
            ImplementoSurcos.AplicarGeometriaDeTool(impl, null);
            Assert.Equal(7.28, impl.AnchoTotalM, 3);

            ImplementoSurcos.AplicarGeometriaDeTool(impl, new ToolConfigDto { Width = 0, NumSections = 0 });
            Assert.Equal(7.28, impl.AnchoTotalM, 3);
            Assert.Equal(0.52, impl.DistanciaEntreSurcosM, 3);
        }
    }
}
