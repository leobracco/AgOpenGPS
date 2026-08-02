using AgroParallel.Services.VistaX;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class VxObjetivoDinamicoTests
    {
        // El caso real del banco: motor a 149 pps, 24 sem/vuelta, 600 ppr,
        // 3,52 km/h, 1 surco → 6,1 sem/m (mismo número que mostró PID live).
        [Fact]
        public void CasoRealDelBanco_DaLoQueMuestraPidLive()
        {
            double semM = VxObjetivoDinamico.SemMetro(149, 24, 600, 3.52, 1);
            Assert.InRange(semM, 6.0, 6.2);
        }

        [Fact]
        public void MotorConVariosSurcos_RepartePorSurco()
        {
            // Mismo motor alimentando 7 surcos: el objetivo POR SURCO es 1/7.
            double uno = VxObjetivoDinamico.SemMetro(149, 24, 600, 3.52, 1);
            double siete = VxObjetivoDinamico.SemMetro(149, 24, 600, 3.52, 7);
            Assert.InRange(uno / siete, 6.9, 7.1);
        }

        [Fact]
        public void SinConsigna_DevuelveCero_ParaCaerAlFijo()
        {
            Assert.Equal(0, VxObjetivoDinamico.SemMetro(0, 24, 600, 5, 1));
            Assert.Equal(0, VxObjetivoDinamico.SemMetro(0.4, 24, 600, 5, 1));
        }

        [Fact]
        public void SinCalibrarOParado_DevuelveCero()
        {
            Assert.Equal(0, VxObjetivoDinamico.SemMetro(100, 0, 600, 5, 1));   // sin sem/vuelta
            Assert.Equal(0, VxObjetivoDinamico.SemMetro(100, 24, 0, 5, 1));    // sin ppr
            Assert.Equal(0, VxObjetivoDinamico.SemMetro(100, 24, 600, 0, 1));  // parado
        }
    }
}
