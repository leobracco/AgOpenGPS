// ============================================================================
// FreeDriveTests.cs — candados del MANEJO LIBRE (free drive).
//
// Prendido, el PGN 254 sale con status=1 y un ángulo puesto a mano: el módulo
// mueve el volante SIN guía. Es la función más peligrosa de la pantalla
// Dirección, así que sus dos candados se prueban acá:
//   · no se deja prender con el tractor andando;
//   · no se deja prender si el host no sabe informar velocidad ("no sé" tiene
//     que fallar cerrado).
// El tercer candado —el watchdog que lo apaga solo en cada PGN— vive en
// CAutoSteerUpdater y se valida en cabina: necesita el host entero.
// ============================================================================

using AgOpenGPS;
using AgOpenGPS.Core;
using AgroParallel.Adapters;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class FreeDriveTests
    {
        /// <summary>Lo mínimo que CVehicle le pide al host. Nada de esto lo usa
        /// el manejo libre: solo hace falta para construir el vehículo.</summary>
        private sealed class HostFalso : IVehicleHost
        {
            public CModuleComm Mc => null;
            public CSim Sim => null;
            public CTool Tool => null;
            public CBoundary Bnd => null;
            public double AvgSpeed => 0;
            public double FixHeading => 0;
            public bool IsFirstHeadingSet => false;
            public string HeadingFromSource => "Fix";
            public bool IsSimEnabled => false;
            public double CamSetDistance => 0;
            public bool IsSvennArrowOn => false;
            public int ABLineWidth => 2;
        }

        private CVehicle _vehicle;
        private double _velocidad;

        [SetUp]
        public void SetUp()
        {
            _vehicle = new CVehicle(new HostFalso())
            {
                functionSpeedLimit = 7.0,
                maxSteerAngle = 30.0,
                isInFreeDriveMode = false,
                driveFreeSteerAngle = 0,
            };
            _velocidad = 0;
        }

        private SteerConfigService Servicio(bool conVelocidad = true)
        {
            return new SteerConfigService(
                _vehicle,
                actualSteerAngleDegrees: () => 0,
                sendSettings: null,
                applyLive: null,
                avgSpeed: conVelocidad ? (System.Func<double>)(() => _velocidad) : null);
        }

        [Test]
        public void Prender_ConElTractorParado_Anda()
        {
            var r = Servicio().SetFreeDrive(true);

            Assert.That(r.Ok, Is.True);
            Assert.That(r.On, Is.True);
            Assert.That(r.Angle, Is.EqualTo(0));
            Assert.That(_vehicle.isInFreeDriveMode, Is.True);
        }

        [Test]
        public void Prender_EnMovimiento_SeRechaza()
        {
            _velocidad = 9.0;   // límite 7

            var r = Servicio().SetFreeDrive(true);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("velocidad"));
            Assert.That(r.On, Is.False);
            Assert.That(_vehicle.isInFreeDriveMode, Is.False, "el modo NO puede quedar prendido");
        }

        [Test]
        public void Prender_MarchaAtras_TambienSeRechaza()
        {
            // La velocidad llega con signo según el sentido: el candado mira el
            // valor absoluto, si no yendo para atrás pasaba de largo.
            _velocidad = -9.0;

            var r = Servicio().SetFreeDrive(true);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("velocidad"));
            Assert.That(_vehicle.isInFreeDriveMode, Is.False);
        }

        [Test]
        public void Prender_SinSaberLaVelocidad_FallaCerrado()
        {
            var r = Servicio(conVelocidad: false).SetFreeDrive(true);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("sin-velocidad"));
            Assert.That(_vehicle.isInFreeDriveMode, Is.False);
        }

        [Test]
        public void Apagar_ValeSiempre_YDejaElAnguloEnCero()
        {
            var svc = Servicio();
            svc.SetFreeDrive(true);
            svc.NudgeFreeDrive(1);
            _velocidad = 20.0;   // apagar no depende de la velocidad

            var r = svc.SetFreeDrive(false);

            Assert.That(r.Ok, Is.True);
            Assert.That(r.On, Is.False);
            Assert.That(r.Angle, Is.EqualTo(0));
            Assert.That(_vehicle.driveFreeSteerAngle, Is.EqualTo(0));
        }

        [Test]
        public void Angulo_Apagado_NoHaceNada()
        {
            var r = Servicio().NudgeFreeDrive(1);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("apagado"));
            Assert.That(_vehicle.driveFreeSteerAngle, Is.EqualTo(0));
        }

        [Test]
        public void Angulo_CorreDeAUnGradoParaCadaLado()
        {
            var svc = Servicio();
            svc.SetFreeDrive(true);

            Assert.That(svc.NudgeFreeDrive(1).Angle, Is.EqualTo(1));
            Assert.That(svc.NudgeFreeDrive(1).Angle, Is.EqualTo(2));
            Assert.That(svc.NudgeFreeDrive(-1).Angle, Is.EqualTo(1));
            Assert.That(svc.NudgeFreeDrive(-1).Angle, Is.EqualTo(0));
            Assert.That(svc.NudgeFreeDrive(-1).Angle, Is.EqualTo(-1));
        }

        [Test]
        public void Angulo_NoPasaElMaximoDelVehiculo()
        {
            var svc = Servicio();
            svc.SetFreeDrive(true);

            AgroParallel.Models.FreeDriveStateDto r = null;
            for (int i = 0; i < 60; i++) r = svc.NudgeFreeDrive(1);
            Assert.That(r.Angle, Is.EqualTo(30), "maxSteerAngle del vehículo");

            for (int i = 0; i < 120; i++) r = svc.NudgeFreeDrive(-1);
            Assert.That(r.Angle, Is.EqualTo(-30));
        }

        [Test]
        public void Angulo_ElTopeNuncaSupera40()
        {
            // Un vehículo mal configurado (80° de ángulo máximo) no debe poder
            // pedirle al módulo más de lo que el FormSteer nativo permitía.
            _vehicle.maxSteerAngle = 80.0;
            var svc = Servicio();
            svc.SetFreeDrive(true);

            AgroParallel.Models.FreeDriveStateDto r = null;
            for (int i = 0; i < 100; i++) r = svc.NudgeFreeDrive(1);

            Assert.That(r.Angle, Is.EqualTo(40));
        }

        [Test]
        public void Cero_AlternaEntre0y5()
        {
            var svc = Servicio();
            svc.SetFreeDrive(true);

            Assert.That(svc.ToggleFreeDriveZero().Angle, Is.EqualTo(5));
            Assert.That(svc.ToggleFreeDriveZero().Angle, Is.EqualTo(0));
        }

        [Test]
        public void Cero_Apagado_NoHaceNada()
        {
            var r = Servicio().ToggleFreeDriveZero();

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("apagado"));
            Assert.That(_vehicle.driveFreeSteerAngle, Is.EqualTo(0));
        }

        [Test]
        public void Watchdog_PrenderDesdeLaPantalla_LoDejaArmado()
        {
            Servicio().SetFreeDrive(true);

            Assert.That(_vehicle.freeDriveWatchdog, Is.GreaterThan(0),
                "prendido desde la pantalla tiene que quedar vigilado");
        }

        [Test]
        public void Watchdog_CadaConsultaDeLaPantallaLoRecarga()
        {
            var svc = Servicio();
            svc.SetFreeDrive(true);
            int armado = _vehicle.freeDriveWatchdog;

            // El motor lo va descontando en cada PGN…
            _vehicle.freeDriveWatchdog = 3;
            // …y el latido de la pantalla lo vuelve a llenar.
            svc.GetFreeDrive();

            Assert.That(_vehicle.freeDriveWatchdog, Is.EqualTo(armado));
        }

        [Test]
        public void Watchdog_Apagado_NoQuedaVigilandoNada()
        {
            var svc = Servicio();
            svc.SetFreeDrive(true);
            svc.SetFreeDrive(false);

            Assert.That(_vehicle.freeDriveWatchdog, Is.EqualTo(-1));

            // Y consultar con el modo apagado no lo revive.
            svc.GetFreeDrive();
            Assert.That(_vehicle.freeDriveWatchdog, Is.EqualTo(-1));
        }

        [Test]
        public void Watchdog_ElModoNativoNoEntraAlVigilado()
        {
            // El FormSteer nativo prende el modo tocando el vehículo directo:
            // su ventana vive mientras el modo vive, así que NO tiene que
            // apagarse por falta de latidos.
            _vehicle.isInFreeDriveMode = true;

            Assert.That(_vehicle.freeDriveWatchdog, Is.EqualTo(-1));
        }

        [Test]
        public void Estado_TraeVelocidadYLimiteParaQueLaPantallaExplique()
        {
            _velocidad = 12.5;

            var r = Servicio().GetFreeDrive();

            Assert.That(r.Speed, Is.EqualTo(12.5));
            Assert.That(r.SpeedLimit, Is.EqualTo(7.0));
            Assert.That(r.MaxAngle, Is.EqualTo(30.0));
        }
    }
}
