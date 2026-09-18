// ============================================================================
// VistaXCalibracionService.cs — implementación in-memory de la ventana de
// captura. No mantiene timer propio; toma muestras pull-driven cuando la UI
// llama GetState() (polling 200-300 ms desde el JS). Eso evita un thread
// adicional y mantiene el flujo simple: ventana arranca → UI hace polling
// → al cerrarse el tiempo, GetState() devuelve "listo_para_aplicar".
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class VistaXCalibracionService : IVistaXCalibracionService
    {
        private readonly IVistaXLiveService _live;
        private readonly IVistaXConfigService _vxCfg;
        private readonly IInsumoCatalogService _insumos;

        private readonly object _lock = new object();

        private bool _running;
        private string _insumoId = "";
        private string _modo = "objetivo";
        private double _segundosTotal;
        private DateTime _startedAtUtc;

        // Acumulador: por surco mantenemos sumatoria y conteo para sacar promedio.
        // Surco = "bajada" del implemento. Solo participan sensores tipo "semilla".
        private readonly Dictionary<int, RunningAvg> _porSurco = new Dictionary<int, RunningAvg>();
        private int _muestras;
        private double _maxSemMObservado;
        private bool _listoParaAplicar;
        private double _valorFinal;

        public VistaXCalibracionService(IVistaXLiveService live,
                                        IVistaXConfigService vxCfg,
                                        IInsumoCatalogService insumos)
        {
            _live = live;
            _vxCfg = vxCfg;
            _insumos = insumos;
        }

        public bool Start(VistaXCalibracionStartDto req)
        {
            if (req == null) return false;
            string insumoId = (req.InsumoId ?? "").Trim();
            if (string.IsNullOrEmpty(insumoId))
            {
                var activo = _insumos?.GetActivo();
                if (activo == null) return false;
                insumoId = activo.Id;
            }

            double segs = req.Segundos;
            if (!(segs > 0.5) || !(segs < 60)) segs = 5; // sanity

            lock (_lock)
            {
                _running = true;
                _insumoId = insumoId;
                _modo = string.Equals(req.Modo, "saturado", StringComparison.OrdinalIgnoreCase)
                            ? "saturado" : "objetivo";
                _segundosTotal = segs;
                _startedAtUtc = DateTime.UtcNow;
                _porSurco.Clear();
                _muestras = 0;
                _maxSemMObservado = 0;
                _listoParaAplicar = false;
                _valorFinal = 0;
            }
            return true;
        }

        public VistaXCalibracionStateDto GetState()
        {
            lock (_lock)
            {
                // Si está corriendo, agregar la muestra "ahora" del live snapshot.
                if (_running)
                {
                    var snap = _live?.GetSnapshot();
                    AccumularDesdeSnapshot(snap);

                    double elapsed = (DateTime.UtcNow - _startedAtUtc).TotalSeconds;
                    if (elapsed >= _segundosTotal)
                    {
                        _running = false;
                        _listoParaAplicar = true;
                        _valorFinal = PromedioActual();
                    }
                }

                double promedio = _running ? PromedioActual() : _valorFinal;
                double maxSensor = _vxCfg?.GetImplemento()?.Setup?.MaxDensidadSensor ?? 20.0;
                bool saturado = _maxSemMObservado >= maxSensor || promedio >= maxSensor;

                double restantes = 0;
                if (_running)
                {
                    restantes = _segundosTotal - (DateTime.UtcNow - _startedAtUtc).TotalSeconds;
                    if (restantes < 0) restantes = 0;
                }

                return new VistaXCalibracionStateDto
                {
                    Running = _running,
                    InsumoId = _insumoId,
                    Modo = _modo,
                    SegundosTotal = _segundosTotal,
                    SegundosRestantes = restantes,
                    SemMActual = Math.Round(promedio, 2),
                    Saturado = saturado,
                    Muestras = _muestras,
                    Surcos = _porSurco.Keys.OrderBy(k => k).ToList(),
                    ListoParaAplicar = _listoParaAplicar,
                    ValorFinalSemM = Math.Round(_valorFinal, 2)
                };
            }
        }

        public bool Apply(VistaXCalibracionApplyDto req)
        {
            if (req == null || !req.Aceptar) { Cancel(); return false; }

            string insumoId;
            string modo;
            double valor;
            lock (_lock)
            {
                if (!_listoParaAplicar) return false;
                insumoId = _insumoId;
                modo = _modo;
                valor = req.ValorOverride > 0 ? req.ValorOverride : _valorFinal;
            }
            if (string.IsNullOrEmpty(insumoId) || valor <= 0) return false;

            var cat = _insumos?.Load();
            if (cat == null || cat.Items == null) return false;
            var item = cat.Items.Find(i => i != null && i.Id == insumoId);
            if (item == null) return false;

            if (string.Equals(modo, "saturado", StringComparison.OrdinalIgnoreCase))
                item.DensidadAsumidaSaturadoSemM = Math.Round(valor, 2);
            else
                item.DensidadObjetivoSemM = Math.Round(valor, 2);

            _insumos.Save(cat);

            // Reset post-apply para no quedar "listo_para_aplicar" trabado.
            lock (_lock) { _listoParaAplicar = false; }
            return true;
        }

        public void Cancel()
        {
            lock (_lock)
            {
                _running = false;
                _listoParaAplicar = false;
                _valorFinal = 0;
                _porSurco.Clear();
                _muestras = 0;
            }
        }

        // ---- helpers ---------------------------------------------------------

        private void AccumularDesdeSnapshot(VistaXLiveSnapshotDto snap)
        {
            if (snap?.Trenes == null) return;
            bool agregada = false;
            foreach (var tren in snap.Trenes)
            {
                if (tren?.Surcos == null) continue;
                foreach (var s in tren.Surcos)
                {
                    if (s == null) continue;
                    if (!VistaXSensorTypes.IsSemilla(s.Tipo)) continue;
                    if (s.Muted) continue;
                    if (s.Estado == "no-data") continue;
                    // s.Valor está en la unidad cruda que reporta el firmware
                    // (sem/m según pipeline VistaX). Si no está disponible, usamos
                    // s.Spm / 60 * (1/velocidad) — pero el live ya hace el join
                    // contra velocidad para emitir un Valor consistente.
                    double semM = s.Valor;
                    if (!(semM > 0)) continue;

                    if (!_porSurco.TryGetValue(s.Bajada, out var avg))
                    {
                        avg = new RunningAvg();
                        _porSurco[s.Bajada] = avg;
                    }
                    avg.Add(semM);
                    if (semM > _maxSemMObservado) _maxSemMObservado = semM;
                    agregada = true;
                }
            }
            if (agregada) _muestras++;
        }

        private double PromedioActual()
        {
            if (_porSurco.Count == 0) return 0;
            double sum = 0; int n = 0;
            foreach (var kv in _porSurco)
            {
                if (kv.Value.Count == 0) continue;
                sum += kv.Value.Mean();
                n++;
            }
            return n == 0 ? 0 : sum / n;
        }

        private sealed class RunningAvg
        {
            public double Sum;
            public int Count;
            public void Add(double v) { Sum += v; Count++; }
            public double Mean() => Count == 0 ? 0 : Sum / Count;
        }
    }
}
