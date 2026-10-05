// ============================================================================
// VistaXRegistroLote.cs — registro de VistaX por lote (informe y mapa por surco).
//
// Reemplaza al camino viejo SeedMonitor → VistaXFieldLogger, que ya no se
// instanciaba en ningún lado. Una vez por segundo:
//   1. mira el lote abierto (IAogStateProvider): sin lote no graba nada;
//   2. pide el snapshot de VistaXLiveService (sem/m por surco, "sembrando");
//   3. arma una muestra con la posición y la pasa a VxRegistroTramos;
//   4. cuando se completa un tramo (~10 m) pide los dobles/fallas/CV del
//      tramo (TomarTramoEspaciamiento) y lo escribe en <lote>/VistaX/Surcos/.
// Al cambiar o cerrar el lote (y al apagar): cierra el pedazo final, guarda
// resumen.json y regenera el SHP del mapa por surco.
//
// Nada se crea en disco hasta que hay un tramo con datos: un lote trabajado
// sin VistaX no queda con una carpeta vacía.
//
// La posición es la del GPS (lat/lon del snapshot); la distancia se mide con
// la posición local de la herramienta. Con implementos de arrastre largos el
// mapa queda corrido unos metros hacia adelante — anotado como pendiente.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services.VistaX
{
    public sealed class VistaXRegistroLote : IDisposable
    {
        /// <summary>Cada cuántos tramos se reescribe resumen.json (≈1 min).</summary>
        public const int TramosEntreResumenes = 12;

        private readonly IAogStateProvider _state;
        private readonly IVistaXLiveService _live;
        private readonly IVistaXTramosEspaciamiento _tramos;
        private readonly Func<DateTime> _reloj;
        private readonly object _lock = new object();
        private readonly VxRegistroTramos _registro = new VxRegistroTramos();

        private Timer _timer;
        private int _enTick;
        private string _loteNombre;
        private VxRegistroArchivo _archivo;
        private int _tramosDesdeResumen;

        public VistaXRegistroLote(IAogStateProvider state, IVistaXLiveService live,
            IVistaXTramosEspaciamiento tramos, Func<DateTime> relojUtc = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _live = live ?? throw new ArgumentNullException(nameof(live));
            _tramos = tramos;
            _reloj = relojUtc ?? (() => DateTime.UtcNow);
        }

        /// <summary>Tramos escritos desde que arrancó (diagnóstico).</summary>
        public int TramosEscritos { get; private set; }

        /// <summary>Carpeta del registro del lote abierto (null sin lote).</summary>
        public string CarpetaActual { get { lock (_lock) return _archivo != null ? _archivo.Dir : null; } }

        public void Start(int periodoMs = 1000)
        {
            if (_timer != null) return;
            _timer = new Timer(_ => TickSeguro(), null, periodoMs, periodoMs);
        }

        public void Stop()
        {
            var t = _timer;
            _timer = null;
            try { t?.Dispose(); } catch { }
            lock (_lock) CerrarLote();
        }

        public void Dispose() { Stop(); }

        private void TickSeguro()
        {
            if (Interlocked.Exchange(ref _enTick, 1) == 1) return;
            try { Tick(); }
            catch (Exception ex) { AgpLog.Warn("VistaXRegistro", "tick", ex); }
            finally { Interlocked.Exchange(ref _enTick, 0); }
        }

        /// <summary>Un paso del registro. Público para los tests.</summary>
        public void Tick()
        {
            lock (_lock)
            {
                AogStateSnapshot st = null;
                try { st = _state.GetSnapshot(); } catch { }
                string lote = st?.CurrentFieldDirectory ?? "";
                string loteDir = (!string.IsNullOrEmpty(lote) && !string.IsNullOrEmpty(st?.FieldsDirectory))
                    ? Path.Combine(st.FieldsDirectory, lote) : null;
                if (loteDir != null && !Directory.Exists(loteDir)) loteDir = null;

                if (!string.Equals(loteDir == null ? null : lote, _loteNombre, StringComparison.OrdinalIgnoreCase))
                {
                    CerrarLote();
                    if (loteDir != null)
                    {
                        _loteNombre = lote;
                        _archivo = new VxRegistroArchivo(VxRegistroArchivo.DirDeLote(loteDir), lote);
                    }
                }
                if (_archivo == null || !_live.IsRunning) return;

                VistaXLiveSnapshotDto vx = null;
                try { vx = _live.GetSnapshot(); } catch { }
                if (vx == null) return;

                var m = ArmarMuestra(st, vx, _reloj());
                if (_registro.Agregar(m))
                {
                    CerrarTramo();
                }
                else if (!m.Sembrando)
                {
                    // Lo que se haya clasificado sin tramo abierto (pedazo
                    // descartado) no se arrastra al próximo tramo.
                    TomarEspaciamiento();
                }
            }
        }

        /// <summary>Muestra a partir del estado de PilotX y del snapshot VistaX.
        /// Solo surcos de SEMILLA (dobles/fallas no aplican a fertilizante).</summary>
        public static VxMuestra ArmarMuestra(AogStateSnapshot st, VistaXLiveSnapshotDto vx, DateTime utc)
        {
            var m = new VxMuestra
            {
                Utc = utc,
                Sembrando = vx != null && vx.MonitoreoActivo,
                VelKmh = vx != null ? vx.Velocidad : 0,
                DistEntreSurcosM = vx != null ? vx.DistanciaEntreSurcos : 0,
            };
            if (st != null)
            {
                m.Lat = st.Latitude;
                m.Lon = st.Longitude;
                bool herramienta = Math.Abs(st.ToolEasting) > 1e-9 || Math.Abs(st.ToolNorthing) > 1e-9;
                m.E = herramienta ? st.ToolEasting : st.PivotEasting;
                m.N = herramienta ? st.ToolNorthing : st.PivotNorthing;
                m.RumboRad = herramienta ? st.ToolHeading : st.Heading;
            }
            if (vx?.Trenes == null) return m;
            foreach (var tren in vx.Trenes)
            {
                if (tren?.Surcos == null) continue;
                foreach (var s in tren.Surcos)
                {
                    if (s == null || s.Bajada <= 0) continue;
                    if (!string.Equals(s.Tipo, VistaXSensorTypes.Semilla, StringComparison.OrdinalIgnoreCase)) continue;
                    string est = s.Estado ?? "";
                    bool valida = !s.SeccionCortada && !s.Muted
                                  && est != "no-data" && est != "muted" && est != "seccion-off";
                    m.Surcos.Add(new VxMuestraSurco { Tren = s.Tren, Bajada = s.Bajada, SemM = s.SemM, Valida = valida });
                }
            }
            return m;
        }

        // ── internos (con _lock tomado) ─────────────────────────────────────

        private List<VxIndicesSurco> TomarEspaciamiento()
        {
            try { return _tramos != null ? _tramos.TomarTramoEspaciamiento() : null; }
            catch { return null; }
        }

        private void CerrarTramo()
        {
            var t = _registro.Cerrar(TomarEspaciamiento());
            if (t == null || _archivo == null) return;
            try
            {
                _archivo.Escribir(t);
                TramosEscritos++;
                if (++_tramosDesdeResumen >= TramosEntreResumenes)
                {
                    _tramosDesdeResumen = 0;
                    _archivo.GuardarResumen(_reloj());
                }
            }
            catch (Exception ex) { AgpLog.Warn("VistaXRegistro", "escribir tramo", ex); }
        }

        private void CerrarLote()
        {
            if (_archivo != null)
            {
                if (_registro.HayTramoEnCurso) CerrarTramo();
                try
                {
                    if (_archivo.TramosEscritos > 0)
                    {
                        _archivo.GuardarResumen(_reloj());
                        int puntos = _archivo.ExportarShp();
                        AgpLog.Info("VistaXRegistro", "lote " + _loteNombre + ": " + _archivo.TramosEscritos
                            + " tramos nuevos, SHP con " + puntos + " puntos");
                    }
                }
                catch (Exception ex) { AgpLog.Warn("VistaXRegistro", "cerrar lote", ex); }
            }
            _registro.Descartar();
            _archivo = null;
            _loteNombre = null;
            _tramosDesdeResumen = 0;
        }
    }
}
