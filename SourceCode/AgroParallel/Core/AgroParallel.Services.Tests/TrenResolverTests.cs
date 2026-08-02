using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Common;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class TrenResolverTests
    {
        private static ImplementoDto Impl2Trenes()
        {
            var impl = new ImplementoDto();
            impl.Trenes.Add(new TrenDto { Id = 1, Nombre = "Delantero", DistanciaM = 0 });
            impl.Trenes.Add(new TrenDto { Id = 2, Nombre = "Trasero", DistanciaM = 2.5 });
            for (int i = 1; i <= 14; i++)
                impl.Surcos.Add(new SurcoDto { Numero = i, TrenId = (i % 2 == 1) ? 1 : 2, SeccionPilotX = i });
            return impl; // impares delanteros, pares traseros
        }

        [Fact]
        public void SurcosDeUnTren_DevuelveEseTrenSinConflicto()
        {
            var r = TrenResolver.Resolver(Impl2Trenes(), new[] { 2, 4, 6 });
            Assert.NotNull(r);
            Assert.Equal(2, r.TrenId);
            Assert.Equal(2.5, r.DistanciaM, 3);
            Assert.False(r.Conflicto);
        }

        [Fact]
        public void SurcosDelantero_DistanciaCero()
        {
            var r = TrenResolver.Resolver(Impl2Trenes(), new[] { 1, 3 });
            Assert.NotNull(r);
            Assert.Equal(1, r.TrenId);
            Assert.Equal(0, r.DistanciaM, 3);
        }

        [Fact]
        public void SurcosMezclados_ConflictoYGanaElPrimero()
        {
            var r = TrenResolver.Resolver(Impl2Trenes(), new[] { 2, 3 });
            Assert.NotNull(r);
            Assert.True(r.Conflicto);
            Assert.Equal(2, r.TrenId); // el del primer surco pedido
        }

        [Fact]
        public void SinTrenesUtiles_DevuelveNull_ParaFallback()
        {
            Assert.Null(TrenResolver.Resolver(null, new[] { 1 }));
            Assert.Null(TrenResolver.Resolver(new ImplementoDto(), new[] { 1 })); // sin trenes
            var unSolo = new ImplementoDto();
            unSolo.Trenes.Add(new TrenDto { Id = 1, DistanciaM = 0 });
            Assert.Null(TrenResolver.Resolver(unSolo, new[] { 1 })); // 1 tren = nada que derivar
        }

        [Fact]
        public void SurcosDesconocidos_SeIgnoran_TodosDesconocidosEsNull()
        {
            var impl = Impl2Trenes();
            var r = TrenResolver.Resolver(impl, new[] { 99, 2 });
            Assert.NotNull(r);
            Assert.Equal(2, r.TrenId);
            Assert.Null(TrenResolver.Resolver(impl, new[] { 99, 100 }));
        }
    }
}
