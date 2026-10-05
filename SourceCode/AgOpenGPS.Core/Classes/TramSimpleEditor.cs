// ============================================================================
// TramSimpleEditor.cs — panel simple de tramlines (port de FormGPS.TramSimple).
//
// Version reducida del constructor de tramlines: genera las huellas desde la
// guia ACTIVA con pasadas configurables, sin la edicion por cortes del editor
// completo. Es lo que abre el menu de config de tram.
//
// Era `partial class FormGPS`: solo existia bajo WinForms y /api/tram-simple
// daba 404. Cuarto de docs/RETIRAR-WINFORMS.md. Cero dependencias reales de
// WinForms; movido tal cual, host -> campos y callbacks. FormGPS delega.
// ============================================================================

using System;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS
{
    public sealed class TramSimpleEditor
    {
        private readonly CTrack _trk;
        private readonly CTram _tram;
        private readonly CABLine _abLine;
        private readonly CTool _tool;
        private readonly CBoundary _bnd;
        private readonly CABCurve _curve;
        private readonly CVehicle _vehicle;

        private readonly Action _guardarTram;
        private readonly Action _guardarGuias;
        private readonly Func<string> _unidadesFn;
        private readonly Func<double> _m2DisplayFn;
        private readonly Func<bool> _hayLoteFn;
        private readonly Func<double> _maxDiagonalFn;
        private readonly Func<int> _pasadasGet;
        private readonly Action<int> _pasadasSet;
        private readonly Func<double> _alphaGet;
        private readonly Action<double> _alphaSet;
        /// <summary>Cerrar ventanas flotantes del host (FormSteer, etc.).
        /// Solo WinForms; en el motor queda no-op.</summary>
        private readonly Action _cerrarVentanasFlotantes;

        private readonly Action _refrescarModo;
        private readonly Action _refrescarUi;

        public TramSimpleEditor(
            CTrack trk, CTram tram, CABLine abLine, CTool tool, CBoundary bnd,
            CABCurve curve, CVehicle vehicle,
            Action guardarTram, Action guardarGuias,
            Func<string> unidades = null, Func<double> m2Display = null,
            Func<bool> hayLote = null, Func<double> maxDiagonal = null,
            Func<int> pasadasGet = null, Action<int> pasadasSet = null,
            Func<double> alphaGet = null, Action<double> alphaSet = null,
            Action cerrarVentanasFlotantes = null,
            Action refrescarModo = null, Action refrescarUi = null)
        {
            _trk = trk; _tram = tram; _abLine = abLine; _tool = tool; _bnd = bnd;
            _curve = curve; _vehicle = vehicle;
            _guardarTram = guardarTram ?? (() => { });
            _guardarGuias = guardarGuias ?? (() => { });
            _unidadesFn = unidades ?? (() => "m");
            _m2DisplayFn = m2Display ?? (() => 1.0);
            _hayLoteFn = hayLote ?? (() => false);
            _maxDiagonalFn = maxDiagonal ?? (() => 2000.0);
            _pasadasGet = pasadasGet ?? (() => 2);
            _pasadasSet = pasadasSet ?? (_ => { });
            _alphaGet = alphaGet ?? (() => 0.5);
            _alphaSet = alphaSet ?? (_ => { });
            _cerrarVentanasFlotantes = cerrarVentanasFlotantes ?? (() => { });
            _refrescarModo = refrescarModo ?? (() => { });
            _refrescarUi = refrescarUi ?? (() => { });
        }

        private string _unidades => _unidadesFn();
        private double _m2Display => _m2DisplayFn();
        private bool _hayLote => _hayLoteFn();
        private double _maxDiagonalLote => _maxDiagonalFn();

        private int _pasadasCfg { get => _pasadasGet(); set => _pasadasSet(value); }
        private double _alphaCfg { get => _alphaGet(); set => _alphaSet(value); }

        // Guía activa válida (hay al menos una y el índice apunta a ella).
        public bool TramSimple_HasTrack()
        {
            return _trk != null && _trk.gArr != null
                   && _trk.idx >= 0 && _trk.idx < _trk.gArr.Count;
        }

        public bool TramSimple_HasBoundary()
        {
            return _bnd != null && _bnd.bndList.Count > 0;
        }

        // Curva vs AB: define qué builder usar (igual que el ctor de FormTram).
        public bool TramSimple_IsCurve()
        {
            return TramSimple_HasTrack() && _trk.gArr[_trk.idx].mode != TrackMode.AB;
        }

        // Reconstruye el _tram en memoria y deja la preview visible (displayMode=All),
        // exactamente como FormTram.MoveBuildTramLine(0). Sin guía activa no hay nada
        // que construir (el launcher garantiza _trk.idx != -1, pero /state se puede
        // pedir sin lote cargado: no reventamos).
        private void TramSimple_Rebuild()
        {
            if (!TramSimple_HasTrack()) return;
            _tram.displayMode = TramMode.All;
            if (TramSimple_IsCurve()) _curve.BuildTram();
            else _abLine.BuildTram();
        }

        // Réplica de FormTram_Load: elige generateMode según lo que ya exista,
        // fuerza FillTracks sin contorno, y construye si todavía no hay _tram.
        public TramSimpleStateSnapshot TramSimple_Open()
        {
            _tool.halfWidth = (_tool.width - _tool.overlap) / 2.0;

            _tram.generateMode = TramMode.All;
            if (_tram.tramList.Count > 0 && _tram.tramBndOuterArr.Count > 0)
                _tram.generateMode = TramMode.All;
            else if (_tram.tramBndOuterArr.Count == 0)
                _tram.generateMode = TramMode.FillTracks;
            else if (_tram.tramList.Count == 0)
                _tram.generateMode = TramMode.BoundaryTracks;
            else
                _tram.generateMode = TramMode.All;

            if (_bnd.bndList.Count == 0) _tram.generateMode = TramMode.FillTracks;

            _cerrarVentanasFlotantes();

            if (_tram.tramList.Count > 0 || _tram.tramBndOuterArr.Count > 0)
            {
                // Ya hay _tram: solo asegurar que la preview esté prendida.
                _tram.displayMode = TramMode.All;
            }
            else
            {
                TramSimple_Rebuild();
            }

            return TramSimple_Snapshot();
        }

        public TramSimpleStateSnapshot TramSimple_SetPasses(int passes)
        {
            if (passes < 1) passes = 1;
            _tram.passes = passes;
            _pasadasCfg = passes;

            TramSimple_Rebuild();
            return TramSimple_Snapshot();
        }

        public TramSimpleStateSnapshot TramSimple_SetAlpha(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            _tram.alpha = percent * 0.01;
            return TramSimple_Snapshot();
        }

        public TramSimpleStateSnapshot TramSimple_SetMode(string mode)
        {
            TramMode m;
            switch (mode)
            {
                case "FillTracks": m = TramMode.FillTracks; break;
                case "BoundaryTracks": m = TramMode.BoundaryTracks; break;
                case "All": m = TramMode.All; break;
                default: m = _tram.generateMode; break;
            }
            // Sin contorno no hay tracks de borde: FormTram deshabilita el botón.
            if (_bnd.bndList.Count == 0) m = TramMode.FillTracks;
            _tram.generateMode = m;
            TramSimple_Rebuild();
            return TramSimple_Snapshot();
        }

        // Invierte la dirección de la guía activa (idéntico a FormTram.btnSwapAB_Click).
        public TramSimpleStateSnapshot TramSimple_SwapAB()
        {
            if (!TramSimple_HasTrack()) return TramSimple_Snapshot();

            if (_trk.gArr[_trk.idx].mode == TrackMode.AB)
            {
                vec2 bob = _trk.gArr[_trk.idx].ptA;
                _trk.gArr[_trk.idx].ptA = _trk.gArr[_trk.idx].ptB;
                _trk.gArr[_trk.idx].ptB = new vec2(bob);

                _trk.gArr[_trk.idx].heading += Math.PI;
                if (_trk.gArr[_trk.idx].heading < 0) _trk.gArr[_trk.idx].heading += glm.twoPI;
                if (_trk.gArr[_trk.idx].heading > glm.twoPI) _trk.gArr[_trk.idx].heading -= glm.twoPI;

                double abHeading = _trk.gArr[_trk.idx].heading;
                _trk.gArr[_trk.idx].endPtA.easting = _trk.gArr[_trk.idx].ptA.easting - (Math.Sin(abHeading) * _abLine.abLength);
                _trk.gArr[_trk.idx].endPtA.northing = _trk.gArr[_trk.idx].ptA.northing - (Math.Cos(abHeading) * _abLine.abLength);

                _trk.gArr[_trk.idx].endPtB.easting = _trk.gArr[_trk.idx].ptB.easting + (Math.Sin(abHeading) * _abLine.abLength);
                _trk.gArr[_trk.idx].endPtB.northing = _trk.gArr[_trk.idx].ptB.northing + (Math.Cos(abHeading) * _abLine.abLength);
            }
            else
            {
                int cnt = _trk.gArr[_trk.idx].curvePts.Count;
                if (cnt > 0)
                {
                    _trk.gArr[_trk.idx].curvePts.Reverse();

                    vec3[] arr = new vec3[cnt];
                    cnt--;
                    _trk.gArr[_trk.idx].curvePts.CopyTo(arr);
                    _trk.gArr[_trk.idx].curvePts.Clear();

                    _trk.gArr[_trk.idx].heading += Math.PI;
                    if (_trk.gArr[_trk.idx].heading < 0) _trk.gArr[_trk.idx].heading += glm.twoPI;
                    if (_trk.gArr[_trk.idx].heading > glm.twoPI) _trk.gArr[_trk.idx].heading -= glm.twoPI;

                    for (int i = 1; i < cnt; i++)
                    {
                        vec3 pt3 = arr[i];
                        pt3.heading += Math.PI;
                        if (pt3.heading > glm.twoPI) pt3.heading -= glm.twoPI;
                        if (pt3.heading < 0) pt3.heading += glm.twoPI;
                        _trk.gArr[_trk.idx].curvePts.Add(pt3);
                    }

                    vec2 temp = new vec2(_trk.gArr[_trk.idx].ptA);
                    _trk.gArr[_trk.idx].ptA = new vec2(_trk.gArr[_trk.idx].ptB);
                    _trk.gArr[_trk.idx].ptB = new vec2(temp);
                }
            }

            _guardarGuias();

            _tram.tramArr?.Clear();
            _tram.tramList?.Clear();
            _tram.tramBndOuterArr?.Clear();
            _tram.tramBndInnerArr?.Clear();

            TramSimple_Rebuild();
            return TramSimple_Snapshot();
        }

        // Cierra el editor (equivalente al FormClosing de FormTram).
        public void TramSimple_Commit(bool save)
        {
            if (!save)
            {
                _tram.tramArr?.Clear();
                _tram.tramList?.Clear();
                _tram.tramBndOuterArr?.Clear();
                _tram.tramBndInnerArr?.Clear();
                _tram.displayMode = 0;
            }

            _guardarTram();
            _refrescarUi();
            _refrescarModo();

            _alphaCfg = _tram.alpha;
            _pasadasCfg = _tram.passes;

        }

        // Snapshot plano del estado (lo mapea el adapter al DTO).
        public TramSimpleStateSnapshot TramSimple_Snapshot()
        {
            return new TramSimpleStateSnapshot
            {
                HasTrack = TramSimple_HasTrack(),
                HasBoundary = TramSimple_HasBoundary(),
                IsCurve = TramSimple_IsCurve(),
                Passes = _tram.passes,
                AlphaPercent = (int)Math.Round(_tram.alpha * 100.0),
                Mode = _tram.generateMode.ToString(),
                ToolWidthDisplay = _tool.width * _m2Display,
                TramWidthDisplay = _tram.tramWidth * _m2Display,
                TrackWidthDisplay = _vehicle.VehicleConfig.TrackWidth * _m2Display,
                Units = (_unidades ?? "m").Trim()
            };
        }

        // POCO intermedio para no acoplar el partial (assembly GPS) al DTO de Models
        // por conversión implícita; el adapter lo copia campo a campo.
        public sealed class TramSimpleStateSnapshot
        {
            public bool HasTrack;
            public bool HasBoundary;
            public bool IsCurve;
            public int Passes;
            public int AlphaPercent;
            public string Mode;
            public double ToolWidthDisplay;
            public double TramWidthDisplay;
            public double TrackWidthDisplay;
            public string Units;
        }
    }
}
