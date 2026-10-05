// ============================================================================
// QuantiXRuntimeService.cs — adaptador delgado: estado de PilotX + el archivo
// de motores → snapshot de runtime para el widget/panel de QuantiX.
//
// Toda la lógica vive en QxRuntimeBuilder (función pura, con tests). Acá solo
// se lee el estado y se carga la config, para que exista UNA implementación
// en vez de una copia por host (WinForms / Android / motor headless).
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class QuantiXRuntimeService : IQuantiXRuntimeService
    {
        private readonly IAogStateProvider _state;
        private readonly Func<MotoresConfig> _cargarMotores;
        private readonly Func<ImplementoDto> _cargarImplemento;

        /// <param name="cargarMotores">Cómo obtener la config de motores. Por
        /// defecto, el mismo archivo que lee el bridge (quantiX_motores.json).</param>
        /// <param name="cargarImplemento">Cómo obtener el implemento central
        /// (surcos por sección → ancho/surcos reales de cada motor). null =
        /// sin implemento: el builder cae al fallback proporcional, igual que
        /// el bridge sin ImplementoProvider.</param>
        public QuantiXRuntimeService(IAogStateProvider state, Func<MotoresConfig> cargarMotores = null,
                                     Func<ImplementoDto> cargarImplemento = null)
        {
            _state = state;
            _cargarMotores = cargarMotores ?? MotoresConfig.Load;
            _cargarImplemento = cargarImplemento;
        }

        public QuantiXRuntimeSnapshot GetSnapshot()
        {
            AogStateSnapshot aog = null;
            try { aog = _state != null ? _state.GetSnapshot() : null; }
            catch { /* sin estado: se reporta velocidad 0, no se rompe el panel */ }

            var ctx = new QxRuntimeContexto
            {
                VelocidadKmh = aog != null ? aog.AvgSpeed : 0,
                AnchoTotalM = aog != null && aog.ToolWidth > 0 ? aog.ToolWidth : 0,
                // Mapa global del tick: solo cuenta si el tractor está adentro
                // de la prescripción, igual que el bridge.
                DosisMapaGlobal = (aog != null && aog.ShapeIsInside) ? aog.ShapeCurrentDose : 0,
                CampoLookup = campo =>
                {
                    try { return _state != null ? _state.GetShapeFieldDose(campo) : 0; }
                    catch { return 0; }
                },
                TotalSecciones = aog != null ? aog.NumSections : 0,
            };
            // Implemento central: misma fuente que el bridge. Si el provider
            // tira, el snapshot sale igual con el fallback proporcional.
            if (_cargarImplemento != null)
            {
                try { ctx.Implemento = _cargarImplemento(); } catch { }
            }

            MotoresConfig cfg;
            try { cfg = _cargarMotores(); }
            catch { cfg = null; }

            return QxRuntimeBuilder.Build(cfg, ctx);
        }
    }
}
