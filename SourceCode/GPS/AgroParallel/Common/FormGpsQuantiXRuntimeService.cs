// ============================================================================
// FormGpsQuantiXRuntimeService.cs
// Adaptador IQuantiXRuntimeService → PilotX.
//
// La lógica vivía acá copiada (y desincronizada del bridge: la dosis fija le
// ganaba al mapa, y las sembradoras se calculaban con la fórmula de kg/ha).
// Ahora delega en QuantiXRuntimeService/QxRuntimeBuilder — la MISMA pieza que
// usan el motor headless y el bridge que comanda el motor. Si el widget y la
// sembradora muestran números distintos, es un bug, ya no una copia vieja.
//
// Se mantiene el tipo porque FormGPS lo construye con el form, y porque el
// null-check del form es lo que evita servir runtime antes de que PilotX esté
// levantado.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsQuantiXRuntimeService : IQuantiXRuntimeService
    {
        private readonly FormGPS _form;
        private readonly QuantiXRuntimeService _inner;

        public FormGpsQuantiXRuntimeService(FormGPS form, IAogStateProvider state)
        {
            _form = form;
            _inner = new QuantiXRuntimeService(state);
        }

        public QuantiXRuntimeSnapshot GetSnapshot()
        {
            if (_form == null)
            {
                return new QuantiXRuntimeSnapshot
                {
                    Motores = new List<QuantiXMotorRuntime>(),
                    CurrentSpeedKmh = 0,
                    CurrentToolWidthM = 0
                };
            }
            return _inner.GetSnapshot();
        }
    }
}
