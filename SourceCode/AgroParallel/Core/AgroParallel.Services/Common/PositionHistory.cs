// ============================================================================
// PositionHistory.cs - Historial posición + secciones para desfase tren trasero
// Ubicación: SourceCode/GPS/AgroParallel/Common/PositionHistory.cs
// Target: net48 (C# 7.3)
//
// Captura snapshots (distancia acumulada, sections[]) cada vez que se llama
// Record(...). Luego GetSectionsAtDistanceBack(d) devuelve el snapshot que
// corresponde a "d metros atrás" del estado actual — usado por bridges que
// controlan un tren trasero (SectionX, QuantiX) para reproducir el patrón de
// secciones del tren delantero con el delay físico real.
//
// Filtrado de glitch GPS: saltos > MaxStepMeters (5m por tick ≈ 180 km/h)
// se descartan para no romper el historial con freezes/saltos del receptor.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Common
{
    public class PositionHistory
    {
        public delegate void LogHandler(string msg);

        // Maximo movimiento aceptable entre Records consecutivos. 5m a 100ms ya
        // es 180 km/h — por encima asumimos glitch GPS / freeze y no acumulamos.
        public const double MaxStepMeters = 5.0;

        private readonly LogHandler _log;
        private readonly int _maxRecords;

        private readonly List<PosRecord> _history = new List<PosRecord>();
        private struct PosRecord { public double DistAccum; public bool[] Sections; }

        private double _totalDist;
        private double _lastE, _lastN;
        private bool _hasLast;
        private int _glitchCount;

        public int Count { get { return _history.Count; } }
        public double TotalDistance { get { return _totalDist; } }
        public int GlitchCount { get { return _glitchCount; } }

        public PositionHistory(LogHandler log = null, int maxRecords = 500)
        {
            _log = log;
            _maxRecords = maxRecords > 0 ? maxRecords : 500;
        }

        public void Record(double easting, double northing, bool[] sections)
        {
            if (sections == null) return;

            if (_hasLast)
            {
                double dx = easting - _lastE, dy = northing - _lastN;
                double step = Math.Sqrt(dx * dx + dy * dy);
                if (step <= MaxStepMeters)
                {
                    _totalDist += step;
                }
                else
                {
                    _glitchCount++;
                    if (_log != null)
                        _log(string.Format("GPS glitch ignorado: salto={0:F1}m (count={1})", step, _glitchCount));
                }
            }
            _lastE = easting; _lastN = northing; _hasLast = true;

            bool[] copy = new bool[sections.Length];
            Array.Copy(sections, copy, sections.Length);
            _history.Add(new PosRecord { DistAccum = _totalDist, Sections = copy });
            while (_history.Count > _maxRecords) _history.RemoveAt(0);
        }

        // Devuelve el snapshot de secciones de "dist" metros atrás del estado
        // actual. null si no hay historial suficiente (todavía no avanzó tanto).
        public bool[] GetSectionsAtDistanceBack(double dist)
        {
            double target = _totalDist - dist;
            if (target < 0 || _history.Count < 2) return null;
            for (int i = _history.Count - 1; i >= 0; i--)
                if (_history[i].DistAccum <= target) return _history[i].Sections;
            return _history[0].Sections;
        }

        public void Reset()
        {
            _history.Clear();
            _totalDist = 0;
            _lastE = 0; _lastN = 0;
            _hasLast = false;
            _glitchCount = 0;
        }
    }
}
