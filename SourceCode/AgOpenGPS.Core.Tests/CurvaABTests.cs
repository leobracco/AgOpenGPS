// ============================================================================
// CurvaABTests.cs — guía A-B en curva (CABCurve).
//
// La curva activa (curList) se recalcula en un Task y se publica desde la
// continuación del await. En el Engine headless NO hay SynchronizationContext,
// así que esa continuación corre en un hilo del pool, en paralelo con
// GetCurrentCurveLine (hilo del GPS). Estos tests fijan lo que tiene que
// aguantar el seguidor de curva cuando la lista cambia por debajo, y que una
// construcción cancelada no publique una curva a medias.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using AgOpenGPS;
using AgOpenGPS.Core;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests
{
    [TestFixture]
    public class CurvaABTests
    {
        private sealed class VehiculoHostFalso : IVehicleHost
        {
            public CModuleComm Mc => null;
            public CSim Sim => null;
            public CTool Tool => null;
            public CBoundary Bnd => null;
            public double AvgSpeed { get; set; }
            public double FixHeading { get; set; }
            public bool IsFirstHeadingSet => true;
            public string HeadingFromSource => "Fix";
            public bool IsSimEnabled => false;
            public double CamSetDistance => 0;
            public bool IsSvennArrowOn => false;
            public int ABLineWidth => 2;
        }

        private sealed class CurvaHostFalso : IABCurveHost
        {
            public readonly VehiculoHostFalso VehiculoHost = new VehiculoHostFalso();

            public CurvaHostFalso()
            {
                Tool = new CTool(null) { width = 6, overlap = 0, offset = 0 };
                Bnd = new CBoundary(null);
                Vehicle = new CVehicle(VehiculoHost)
                {
                    goalPointLookAheadHold = 4,
                    goalPointLookAheadMult = 1,
                    goalPointAcquireFactor = 1.5,
                    maxSteerAngle = 35,
                };
                Ahrs = new CAHRS();
                ABLine = new CABLine(this);
            }

            public CTool Tool { get; }
            public CTram Tram => null;
            public CVehicle Vehicle { get; }
            public CBoundary Bnd { get; }
            public CModuleComm Mc => null;
            public CAHRS Ahrs { get; }
            public int TrackIdx { get; set; }
            public List<CTrk> Tracks { get; } = new List<CTrk>();
            public bool IsYouTurnTriggered => false;
            public bool YouTurnDistanceFromYouTurnLine() => false;
            public double YouTurnSteerAngle => 0;
            public double YouTurnDistanceFromCurrentLine => 0;
            public vec2 YouTurnGoalPoint => new vec2();
            public vec2 YouTurnRadiusPoint => new vec2();
            public double YouTurnPpRadius => 0;
            public void DrawYouTurn() { }
            public double SideHillCompFactor => 0;
            public void StanleyGuidanceABLine(vec3 curPtA, vec3 curPtB, vec3 pivot, vec3 steer) { }
            public double SecondsSinceStart => 0;
            public bool IsBtnAutoSteerOn => false;
            public vec2 GuidanceLookPos => new vec2();
            public bool IsStanleyUsed => false;
            public bool IsReverse => false;
            public double FixHeading => VehiculoHost.FixHeading;
            public double AvgSpeed => VehiculoHost.AvgSpeed;
            public double CamHeading => 0;
            public bool IsSideGuideLines => false;
            public double CamSetDistance => 0;
            public short GuidanceLineDistanceOff { set { } }
            public short GuidanceLineSteerAngle { set { } }
            public CABLine ABLine { get; }
            public vec3 PivotAxlePos => new vec3();
            public vec3 SteerAxlePos => new vec3();
            public void PerformAutoSteerClick() { }
            public void TimedMessageBox(int timeout, string title, string message) { }
            public void StanleyGuidanceCurve(vec3 pivot, vec3 steer, ref List<vec3> curList) { }
        }

        /// <summary>Recta hacia el norte (rumbo 0) de <paramref name="metros"/> m, un punto por metro.</summary>
        private static List<vec3> RectaNorte(int metros)
        {
            var lista = new List<vec3>();
            for (int i = 0; i <= metros; i++) lista.Add(new vec3(0, i, 0));
            return lista;
        }

        private static CurvaHostFalso HostConCurva(List<vec3> refPts)
        {
            var host = new CurvaHostFalso();
            var trk = new CTrk { mode = TrackMode.Curve };
            trk.curvePts.AddRange(refPts);
            host.Tracks.Add(trk);
            host.TrackIdx = 0;
            host.VehiculoHost.AvgSpeed = 8;
            return host;
        }

        [Test]
        public void ConstruccionCancelada_NoDevuelveCurvaAMedias()
        {
            var host = HostConCurva(RectaNorte(400));
            var curva = new CABCurve(host);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var resultado = curva.BuildNewOffsetList(3, host.Tracks[0], cts.Token);
                Assert.That(resultado, Is.Null,
                    "una construcción cancelada tiene que descartarse, no publicarse como curva activa");
            }
        }

        [Test]
        public void ConstruccionNormal_DevuelveLaParalelaCompleta()
        {
            var host = HostConCurva(RectaNorte(400));
            var curva = new CABCurve(host);

            var resultado = curva.BuildNewOffsetList(3, host.Tracks[0]);

            Assert.That(resultado, Is.Not.Null);
            Assert.That(resultado.Count, Is.GreaterThan(350));
            foreach (var p in resultado)
                Assert.That(Math.Abs(p.easting), Is.EqualTo(3).Within(0.01),
                    "la paralela a 3 m de una recta tiene que quedar toda a 3 m");
        }

        [Test]
        public void CambioDeListaPorDebajo_NoRevientaPorIndiceViejo()
        {
            var host = HostConCurva(RectaNorte(400));
            var curva = new CABCurve(host);

            // Primera pasada: el seguidor se "engancha" a la curva larga con un
            // índice alto (pivote a 300 m del inicio).
            curva.curList = RectaNorte(400);
            curva.GetCurrentCurveLine(new vec3(0.2, 300, 0), new vec3(0.2, 303, 0));
            Assume.That(curva.currentLocationIndex, Is.GreaterThan(200));

            // El hilo de construcción publica una lista más corta sin pasar por
            // el hilo del GPS (lo que pasa en el Engine headless).
            curva.curList = RectaNorte(50);

            Assert.DoesNotThrow(() => curva.GetCurrentCurveLine(new vec3(0.2, 40, 0), new vec3(0.2, 43, 0)));
        }

        [Test]
        public void CercaDelFinal_ElPuntoObjetivoNoSaltaAlPrincipio()
        {
            var host = HostConCurva(RectaNorte(400));
            var curva = new CABCurve(host);
            curva.curList = RectaNorte(100);

            // Pivote a 1 m del final, mirando al norte: la distancia de mira
            // es mayor que lo que queda de curva.
            curva.GetCurrentCurveLine(new vec3(0, 99, 0), new vec3(0, 102, 0));

            // Hoy da la vuelta: interpola entre el último punto y el PRIMERO y
            // el objetivo cae ATRÁS del tractor (~95 m con el pivote en 99 m).
            Assert.That(curva.goalPointCu.northing, Is.GreaterThanOrEqualTo(99.0),
                "en una curva abierta el punto objetivo no puede quedar atrás del tractor");
            Assert.That(curva.goalPointCu.northing, Is.LessThanOrEqualTo(100.0 + 1e-6));
        }
    }
}
