// ============================================================================
// EngineToolGeometryCalculator.cs — adaptador IToolGeometryCalculator sobre
// GuidanceEngineHost. Gemelo headless de FormGpsToolGeometryCalculator: barra
// del implemento (N secciones) con leftPoint/rightPoint en coords mundo +
// estado vivo de cada sección para colorearlas en el render GL de PilotX.Desktop.
// Cambios vs FormGPS: tool -> Tool, section -> Sections, isJobStarted -> IsJobStarted.
//
// TRENES DE SIEMBRA: si el implemento central tiene un tren trasero con
// distancia real, las secciones de ese tren se dibujan DONDE ESTÁN de verdad:
// la barra que la herramienta ocupó hace N metros (ring de barras por
// distancia recorrida). Eso mantiene la relación de aspecto y las curvas, y
// el on/off que se muestra es el de aquel momento — el mismo criterio de
// corte retardado que aplican QuantiX/SectionX con GetSectionsAtDistanceBack.
// El cliente GL no cambia: recibe secciones con coords y estado, y las dibuja.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.Common;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineToolGeometryCalculator : IToolGeometryCalculator
    {
        private readonly GuidanceEngineHost _host;

        /// <summary>Implemento central (trenes + surco→tren). Opcional: sin él,
        /// todas las secciones se reportan como tren 1 sin desplazar — el
        /// comportamiento de siempre.</summary>
        public Func<ImplementoDto> ImplementoProvider { get; set; }

        public EngineToolGeometryCalculator(GuidanceEngineHost host) { _host = host; }

        // ---- ring de barras por distancia recorrida --------------------------
        // Cada muestra guarda la barra completa (left/right por sección) y su
        // estado. A 4 Hz y 8 km/h son ~0,55 m por muestra; con tope de 25 m de
        // distancia y 300 muestras el peor caso es trivial en memoria.
        private sealed class Muestra
        {
            public double DistAcum;
            public double[] LE, LN, RE, RN;
            public bool[] On, Mapping;
        }

        private readonly List<Muestra> _ring = new List<Muestra>();
        private double _distAcum;
        private double _prevCx, _prevCy;
        private bool _tienePrev;
        // Dirección de avance unitaria (para desplazar el tren trasero en
        // rígido). Se actualiza solo con pasos válidos: parado conserva la
        // última conocida y la barra no salta.
        private double _dirE, _dirN;
        private bool _tieneDir;
        private const double MaxRingDist = 25.0;   // > tope de distancia de tren (20 m)
        private const int MaxRingCount = 300;
        private const double PasoMinimo = 0.02;    // parado no acumula muestras
        private const double PasoMaximo = 5.0;     // glitch GPS: no acumular (mismo criterio PositionHistory)

        public ToolGeometrySnapshot GetGeometry()
        {
            var snap = new ToolGeometrySnapshot
            {
                NumSections = 0,
                IsValid = false,
                Sections = new List<ToolSectionGeometry>()
            };
            if (_host == null) return snap;

            try
            {
                if (_host.Tool == null || _host.Sections == null) return snap;
                if (!_host.IsJobStarted) return snap;

                int n = _host.Tool.numOfSections;
                if (n <= 0 || n > _host.Sections.Length) return snap;

                snap.NumSections = n;
                snap.IsValid = true;

                for (int i = 0; i < n; i++)
                {
                    var s = _host.Sections[i];
                    if (s == null) continue;

                    int btn;
                    switch (s.sectionBtnState)
                    {
                        case btnStates.Off: btn = 0; break;
                        case btnStates.Auto: btn = 1; break;
                        case btnStates.On: btn = 2; break;
                        default: btn = 0; break;
                    }

                    snap.Sections.Add(new ToolSectionGeometry
                    {
                        Index = i,
                        LeftE = s.leftPoint.easting,
                        LeftN = s.leftPoint.northing,
                        RightE = s.rightPoint.easting,
                        RightN = s.rightPoint.northing,
                        IsOn = s.isSectionOn,
                        IsMapping = s.isMappingOn,
                        BtnState = btn,
                        TrenId = 1
                    });
                }

                AplicarTrenes(snap);
            }
            catch (Exception)
            {
                snap.IsValid = false;
                snap.Sections.Clear();
                snap.NumSections = 0;
            }

            return snap;
        }

        // Desplaza las secciones de trenes traseros a la barra que la
        // herramienta ocupó hace `distancia_m` metros. Nunca tira: ante
        // cualquier cosa rara deja la geometría como estaba.
        private void AplicarTrenes(ToolGeometrySnapshot snap)
        {
            try
            {
                GrabarMuestra(snap);

                ImplementoDto impl = null;
                try { if (ImplementoProvider != null) impl = ImplementoProvider(); } catch { }
                if (impl == null || impl.Trenes == null || impl.Trenes.Count < 2) return;

                var surcosPorSeccion = SurcosPorSeccion.Construir(impl);
                if (surcosPorSeccion == null) return;

                for (int i = 0; i < snap.Sections.Count; i++)
                {
                    var sec = snap.Sections[i];
                    List<int> surcos;
                    if (!surcosPorSeccion.TryGetValue(sec.Index + 1, out surcos)) continue;
                    var tr = TrenResolver.Resolver(impl, surcos);
                    if (tr == null) continue;

                    sec.TrenId = tr.TrenId;
                    if (tr.DistanciaM <= 0.05) continue;

                    // GEOMETRÍA: chasis rígido — el tren trasero va SIEMPRE
                    // paralelo al delantero, desplazado hacia atrás a lo largo
                    // de la dirección de avance. (La primera versión dibujaba
                    // la barra histórica de hace N metros: en curva quedaba
                    // girada respecto del implemento, y un doble tren rígido
                    // no hace eso.)
                    if (_tieneDir)
                    {
                        double dxAtras = -_dirE * tr.DistanciaM;
                        double dyAtras = -_dirN * tr.DistanciaM;
                        sec.LeftE += dxAtras; sec.LeftN += dyAtras;
                        sec.RightE += dxAtras; sec.RightN += dyAtras;
                    }

                    // ESTADO: retardado de verdad — lo que la sección hacía
                    // hace N metros (mismo criterio que el corte del fierro).
                    var m = MuestraHaceMetros(tr.DistanciaM);
                    if (m == null || sec.Index >= m.On.Length) continue;
                    sec.IsOn = m.On[sec.Index];
                    sec.IsMapping = m.Mapping[sec.Index];
                }
            }
            catch { /* dibujo: nunca puede voltear el endpoint */ }
        }

        private void GrabarMuestra(ToolGeometrySnapshot snap)
        {
            int n = snap.Sections.Count;
            if (n == 0) return;

            // Centro de la barra: promedio de los extremos.
            double cx = (snap.Sections[0].LeftE + snap.Sections[n - 1].RightE) * 0.5;
            double cy = (snap.Sections[0].LeftN + snap.Sections[n - 1].RightN) * 0.5;

            if (_tienePrev)
            {
                double dx = cx - _prevCx, dy = cy - _prevCy;
                double paso = Math.Sqrt(dx * dx + dy * dy);
                if (paso < PasoMinimo) return;            // parado: no acumular
                if (paso > PasoMaximo) { _ring.Clear(); _distAcum = 0; } // salto GPS: historia inválida
                else
                {
                    _distAcum += paso;
                    _dirE = dx / paso; _dirN = dy / paso; _tieneDir = true;
                }
            }
            _prevCx = cx; _prevCy = cy; _tienePrev = true;

            var m = new Muestra
            {
                DistAcum = _distAcum,
                LE = new double[n],
                LN = new double[n],
                RE = new double[n],
                RN = new double[n],
                On = new bool[n],
                Mapping = new bool[n]
            };
            for (int i = 0; i < n; i++)
            {
                var s = snap.Sections[i];
                m.LE[i] = s.LeftE; m.LN[i] = s.LeftN;
                m.RE[i] = s.RightE; m.RN[i] = s.RightN;
                m.On[i] = s.IsOn; m.Mapping[i] = s.IsMapping;
            }
            _ring.Add(m);

            // Recorte por distancia y por cantidad.
            while (_ring.Count > MaxRingCount ||
                   (_ring.Count > 1 && _distAcum - _ring[0].DistAcum > MaxRingDist))
                _ring.RemoveAt(0);
        }

        /// <summary>La muestra más nueva cuya distancia quede a `dist` metros o
        /// más por detrás de la posición actual. Con historia corta (recién
        /// abierto el lote) devuelve la más vieja disponible: la barra trasera
        /// aparece más cerca hasta que se acumulan los metros — preferible a no
        /// dibujarla o a inventar una posición.</summary>
        private Muestra MuestraHaceMetros(double dist)
        {
            if (_ring.Count == 0) return null;
            double objetivo = _distAcum - dist;
            for (int i = _ring.Count - 1; i >= 0; i--)
                if (_ring[i].DistAcum <= objetivo) return _ring[i];
            return _ring[0];
        }
    }
}
