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
    }
}
