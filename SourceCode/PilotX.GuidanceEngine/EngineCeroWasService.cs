// ============================================================================
// EngineCeroWasService.cs — CERO AUTOMÁTICO DEL WAS atado al motor headless.
//
// Criterio del usuario (no negociable): el cero del WAS se calibra una vez y
// queda fijo; NUNCA se autocorrige en silencio. Por eso:
//   · apagado (setAS_ceroWasAuto = false, el default) no mide nada;
//   · prendido, GuidanceEngineHost.TickCeroWas alimenta CeroWasEstadistico en
//     cada fix y esto solo muestra el estado y la PROPUESTA;
//   · el offset cambia únicamente con Aplicar (y queda Deshacer).
//
// Cuándo deja aplicar — lo más seguro:
//   · piloto DESENGANCHADO: con el piloto puesto, cambiar el offset mueve en el
//     acto el ángulo que la placa cree tener; el lazo lo corrige girando la
//     rueda el sesgo entero (1–2°) de golpe → el tractor se cruza de la línea.
//     Suelto, la placa no está cerrando el lazo con el WAS y nada se mueve.
//     No se pide además "parado": suelto no hay lazo, y el operario puede
//     aplicar en la cabecera sin frenar.
//   · sin manejo libre ni asistente de calibración en curso (los dos mueven
//     el volante contra el WAS);
//   · con una propuesta vigente calculada con el offset que la placa tiene HOY.
//
// Candado de la EEPROM: cada Aplicar/Deshacer es UNA sola escritura (PGN
// 252/251, el mismo camino que el cero manual: SteerConfigService), y entre
// dos escrituras tiene que haber al menos 3 s (un doble toque no graba dos
// veces). Después de aplicar o deshacer la medición arranca de cero: las
// muestras viejas se tomaron con el offset anterior.
// ============================================================================

using System;
using System.Diagnostics;
using AgLibrary.Logging;
using AgOpenGPS.SteerCal;
using AgroParallel.Adapters;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgOpenGPS
{
    public sealed class EngineCeroWasService : ICeroWasService
    {
        /// <summary>Mínimo entre dos escrituras de offset a la placa.</summary>
        public const double SegundosEntreEscrituras = 3.0;
        /// <summary>Desde acá la propuesta se considera de confianza alta.</summary>
        public const double ConfianzaAlta = 75.0;

        private readonly GuidanceEngineHost _host;
        private readonly SteerConfigService _steer;
        private readonly Func<bool> _asistenteEnCurso;
        private readonly object _lock = new object();
        private readonly Stopwatch _reloj = Stopwatch.StartNew();
        private double _ultimaEscritura = double.NegativeInfinity;

        private bool _hayDeshacer;
        private int _offsetPrevio;
        private int _offsetAplicado;

        public EngineCeroWasService(GuidanceEngineHost host, SteerConfigService steer, Func<bool> asistenteEnCurso = null)
        {
            _host = host;
            _steer = steer;
            _asistenteEnCurso = asistenteEnCurso;
        }

        private static global::AgOpenGPS.Properties.Settings S => global::AgOpenGPS.Properties.Settings.Default;

        // ---------------------------------------------------------------------
        public CeroWasEstadoDto Estado()
        {
            lock (_lock) return Dto(true, null);
        }

        public CeroWasEstadoDto Activar(bool on)
        {
            lock (_lock)
            {
                if (S.setAS_ceroWasAuto != on)
                {
                    S.setAS_ceroWasAuto = on;
                    S.Save();
                    Log.EventWriter("Cero automatico del WAS: " + (on ? "prendido (solo mide y propone)" : "apagado"));
                }
                // Prender o apagar arranca de cero: nada medido antes vale.
                lock (_host.CeroWasLock) _host.CeroWasMedidor.Reiniciar();
                return Dto(true, null);
            }
        }

        public CeroWasEstadoDto Aplicar()
        {
            lock (_lock)
            {
                string bloqueo = BloqueoAplicar(out ResultadoCeroWas r);
                if (bloqueo != null) return Dto(false, bloqueo);

                int previo = S.setAS_wasOffset;
                SteerZeroWasResult res = _steer.AplicarWasOffset(r.OffsetPropuesto);
                if (!res.Ok) return Dto(false, res.Error ?? "no-aplicado");

                _ultimaEscritura = _reloj.Elapsed.TotalSeconds;
                _hayDeshacer = true;
                _offsetPrevio = previo;
                _offsetAplicado = r.OffsetPropuesto;
                lock (_host.CeroWasLock) _host.CeroWasMedidor.Reiniciar();
                Log.EventWriter("Cero automatico del WAS: el operario aplico la propuesta. Offset "
                    + previo + " -> " + r.OffsetPropuesto + " (sesgo " + r.SesgoGrados.ToString("F2")
                    + " grados, confianza " + r.Confianza.ToString("F0") + ", " + r.Muestras + " muestras)");
                return Dto(true, null);
            }
        }

        public CeroWasEstadoDto Deshacer()
        {
            lock (_lock)
            {
                string bloqueo = BloqueoDeshacer();
                if (bloqueo != null) return Dto(false, bloqueo);

                SteerZeroWasResult res = _steer.AplicarWasOffset(_offsetPrevio);
                if (!res.Ok) return Dto(false, res.Error ?? "no-aplicado");

                _ultimaEscritura = _reloj.Elapsed.TotalSeconds;
                Log.EventWriter("Cero automatico del WAS: el operario deshizo. Offset "
                    + _offsetAplicado + " -> " + _offsetPrevio);
                _hayDeshacer = false;
                lock (_host.CeroWasLock) _host.CeroWasMedidor.Reiniciar();
                return Dto(true, null);
            }
        }

        public CeroWasEstadoDto Reiniciar()
        {
            lock (_lock)
            {
                lock (_host.CeroWasLock) _host.CeroWasMedidor.Reiniciar();
                return Dto(true, null);
            }
        }

        // ---------------------------------------------------------------------
        private ResultadoCeroWas Leer()
        {
            lock (_host.CeroWasLock) return _host.CeroWasMedidor.Resultado();
        }

        /// <summary>Lo que traba el volante para que el offset no se toque.</summary>
        private string BloqueoComun()
        {
            if (_host.isBtnAutoSteerOn) return "piloto-enganchado";
            if (_host.Vehicle != null && _host.Vehicle.isInFreeDriveMode) return "manejo-libre";
            if (_asistenteEnCurso != null && _asistenteEnCurso()) return "asistente";
            if (_reloj.Elapsed.TotalSeconds - _ultimaEscritura < SegundosEntreEscrituras) return "muy-seguido";
            return null;
        }

        private string BloqueoAplicar(out ResultadoCeroWas r)
        {
            r = Leer();
            if (!S.setAS_ceroWasAuto) return "apagado";
            if (r.Estado != EstadoCeroWas.Propuesta || !r.HayPropuesta) return "sin-propuesta";
            // La propuesta se calculó con OTRO offset (alguien ceró a mano y el
            // fix todavía no reinició la medición): no se aplica algo viejo.
            if (r.OffsetActual != S.setAS_wasOffset) return "offset-cambiado";
            if (Math.Abs(r.OffsetPropuesto) > CeroWasEstadistico.OffsetLimite) return "fuera-de-rango";
            return BloqueoComun();
        }

        private string BloqueoDeshacer()
        {
            if (!_hayDeshacer) return "sin-deshacer";
            // Alguien cambió el offset después de aplicar (cero manual, Guardar):
            // deshacer pisaría ese cambio.
            if (S.setAS_wasOffset != _offsetAplicado) return "offset-cambiado";
            return BloqueoComun();
        }

        private CeroWasEstadoDto Dto(bool ok, string error)
        {
            bool activo = S.setAS_ceroWasAuto;
            string bloqueo = BloqueoAplicar(out ResultadoCeroWas r);
            bool puedeDeshacer = BloqueoDeshacer() == null;
            if (_hayDeshacer && S.setAS_wasOffset != _offsetAplicado) _hayDeshacer = false;   // ya no hay a qué volver

            return new CeroWasEstadoDto
            {
                Ok = ok,
                Error = error,
                Activo = activo,
                Estado = activo ? EstadoId(r.Estado) : "apagado",
                CondicionesOk = activo && r.CondicionesOk,
                Motivo = activo ? r.Motivo : null,
                Muestras = r.Muestras,
                Segundos = r.SegundosEfectivos,
                SegundosRequeridos = r.SegundosRequeridos,
                SesgoGrados = r.SesgoGrados,
                DispersionGrados = r.DispersionGrados,
                Confianza = r.Confianza,
                ConfianzaAlta = r.Confianza >= ConfianzaAlta,
                OffsetActual = S.setAS_wasOffset,
                OffsetPropuesto = r.OffsetPropuesto,
                HayPropuesta = activo && r.Estado == EstadoCeroWas.Propuesta && r.HayPropuesta,
                PuedeAplicar = bloqueo == null,
                BloqueoAplicar = bloqueo,
                PuedeDeshacer = puedeDeshacer,
                OffsetPrevio = _offsetPrevio,
            };
        }

        private static string EstadoId(EstadoCeroWas e)
        {
            switch (e)
            {
                case EstadoCeroWas.CeroBien: return "cero_bien";
                case EstadoCeroWas.Propuesta: return "propuesta";
                case EstadoCeroWas.Inestable: return "inestable";
                case EstadoCeroWas.SesgoExcesivo: return "sesgo_excesivo";
                case EstadoCeroWas.FueraDeRango: return "fuera_de_rango";
                default: return "juntando";
            }
        }
    }
}
