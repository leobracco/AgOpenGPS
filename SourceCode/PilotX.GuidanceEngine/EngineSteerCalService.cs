// ============================================================================
// EngineSteerCalService.cs — el ASISTENTE DE CALIBRACIÓN DE LA DIRECCIÓN atado
// al motor headless.
//
// Dos caras sobre UNA máquina de estados (AgOpenGPS.SteerCal.SteerCalWizard):
//   · ISteerCalService       → la pantalla (HTTP /api/steer/cal/*);
//   · IAsistenteDireccionMotor → CAutoSteerUpdater, en cada PGN 254: arma la
//     foto (253/250/GPS/secciones/U-turn/switch), avanza el asistente y dice
//     si el 254 lleva su setpoint.
//
// Qué hace con los pedidos del asistente:
//   · EscribirPlaca / Restaurar → pone los campos de placa en
//     Settings.Default EN MEMORIA (sin Save) y manda 252/251 (SendSettings).
//     Así una prueba no queda persistida si algo se corta a la mitad.
//   · AplicarFinal / Deshacer → Guardar normal por ISteerConfigService (el
//     mismo camino que el botón Guardar de Dirección): persiste + PGN.
//
// Candado de pantalla: si con el asistente en curso la pantalla deja de
// consultar 5 s (WebView/Desktop caído, red cortada), se cancela solo y la
// placa vuelve a la config previa.
//
// Sin uso no cambia nada: el asistente arranca INACTIVO y en ese estado
// QuiereMotor siempre devuelve false (el PGN 254 sale como siempre).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgLibrary.Logging;
using AgOpenGPS.SteerCal;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgOpenGPS
{
    public sealed class EngineSteerCalService : ISteerCalService, IAsistenteDireccionMotor
    {
        /// <summary>Sin consultas de la pantalla por más de esto, se cancela solo.</summary>
        public const double PantallaMudaS = 5.0;

        private readonly GuidanceEngineHost _host;
        private readonly ISteerConfigService _steer;
        private readonly SteerCalWizard _wiz = new SteerCalWizard();
        private readonly object _lock = new object();
        private readonly Stopwatch _reloj = Stopwatch.StartNew();
        private double _ultimaPantalla;
        private double _ultimoTickPgn = double.NegativeInfinity;

        public EngineSteerCalService(GuidanceEngineHost host, ISteerConfigService steer)
        {
            _host = host;
            _steer = steer;
        }

        private static global::AgOpenGPS.Properties.Settings S => global::AgOpenGPS.Properties.Settings.Default;

        private double Ahora => _reloj.Elapsed.TotalSeconds;

        // ---------------------------------------------------------------------
        // ISteerCalService (pantalla)
        // ---------------------------------------------------------------------
        public SteerCalEstadoDto Estado()
        {
            lock (_lock)
            {
                _ultimaPantalla = Ahora;
                Avanzar(desdePantalla: true);
                return Dto(true, null);
            }
        }

        public SteerCalEstadoDto Iniciar()
        {
            lock (_lock)
            {
                _ultimaPantalla = Ahora;
                if (_wiz.Activo) return Dto(false, "en-curso");
                bool ok = _wiz.Iniciar(LeerConfig(), Ahora);
                Avanzar(desdePantalla: true);
                return Dto(ok, ok ? null : "no-corresponde");
            }
        }

        public SteerCalEstadoDto Accion(string accion)
        {
            lock (_lock)
            {
                _ultimaPantalla = Ahora;
                bool ok = _wiz.Accion(accion);
                Procesar();
                Avanzar(desdePantalla: true);
                return Dto(ok, ok ? null : "no-corresponde");
            }
        }

        public SteerCalEstadoDto Latido(bool apretado)
        {
            lock (_lock)
            {
                _ultimaPantalla = Ahora;
                _wiz.Latido(apretado, Ahora);
                return Dto(true, null);
            }
        }

        // ---------------------------------------------------------------------
        // IAsistenteDireccionMotor (CAutoSteerUpdater, en cada PGN 254)
        // ---------------------------------------------------------------------
        public bool QuiereMotor(out double setpointGrados, out double velocidadKmh)
        {
            lock (_lock)
            {
                _ultimoTickPgn = Ahora;
                Avanzar(desdePantalla: false);
                setpointGrados = _wiz.Setpoint;
                velocidadKmh = _wiz.VelocidadPgnKmh;
                return _wiz.MotorActivo;
            }
        }

        // ---------------------------------------------------------------------
        /// <summary>
        /// El asistente avanza al ritmo del PGN 254 (un tick por fix: el yaw y
        /// la distancia salen de fix a fix). La pantalla solo lo avanza si no
        /// hay PGN hace más de 1 s (sin GPS), para que el chequeo muestre qué falta.
        /// </summary>
        private void Avanzar(bool desdePantalla)
        {
            if (!_wiz.Activo) return;
            if (Ahora - _ultimaPantalla > PantallaMudaS)
            {
                _wiz.Cancelar("la pantalla dejó de responder");
                Procesar();
                return;
            }
            if (desdePantalla && Ahora - _ultimoTickPgn < 1.0) return;
            try { _wiz.Tick(Foto()); }
            catch (Exception ex)
            {
                // Falla cerrado: cualquier cosa rara corta el asistente y restaura.
                Log.EventWriter("Asistente de direccion: error en el tick, se cancela: " + ex.Message);
                _wiz.Cancelar("error interno");
            }
            Procesar();
        }

        private void Procesar()
        {
            foreach (var p in _wiz.TomarPedidos())
            {
                try
                {
                    switch (p)
                    {
                        case SteerCalPedido.EscribirPlaca: EnMemoria(_wiz.EnPlaca); break;
                        case SteerCalPedido.Restaurar: EnMemoria(_wiz.Original); break;
                        case SteerCalPedido.AplicarFinal: Persistir(_wiz.Trabajo); break;
                        case SteerCalPedido.Deshacer: Persistir(_wiz.Original); break;
                    }
                }
                catch (Exception ex)
                {
                    Log.EventWriter("Asistente de direccion: no se pudo " + p + ": " + ex.Message);
                }
            }
            foreach (string e in _wiz.TomarEventos()) Log.EventWriter(e);
        }

        private SteerCalEntrada Foto()
        {
            var mc = _host.Mc;
            double? edad = null;
            long t253 = _host.ultimoPgn253Ticks;
            if (t253 != 0)
                edad = (Stopwatch.GetTimestamp() - t253) / (double)Stopwatch.Frequency;

            bool fix = _host.Pn != null && _host.Pn.fixQuality > 0
                       && _host.lastFixUtc != default(DateTime)
                       && (DateTime.UtcNow - _host.lastFixUtc).TotalSeconds < 1.0;

            bool pintando = false;
            if (_host.IsJobStarted && _host.Tool != null)
            {
                int n = Math.Min(_host.Tool.numOfSections, _host.Sections.Length);
                for (int i = 0; i < n; i++)
                    if (_host.Sections[i] != null && _host.Sections[i].isSectionOn) { pintando = true; break; }
            }

            bool recta = false;
            var trk = _host.Trk;
            if (trk != null && trk.gArr != null && trk.idx >= 0 && trk.idx < trk.gArr.Count)
                recta = trk.gArr[trk.idx].mode == TrackMode.AB;

            short off = _host.guidanceLineDistanceOff;

            return new SteerCalEntrada
            {
                T = Ahora,
                Edad253 = edad,
                AnguloWas = mc.actualSteerAngleDegrees,
                Pwm = mc.pwmDisplay,
                Corriente = mc.sensorData,
                VelKmh = _host.avgSpeed,
                HayVelocidad = true,
                FixValido = fix,
                RumboRad = _host.fixHeading,
                Este = _host.pivotAxlePos.easting,
                Norte = _host.pivotAxlePos.northing,
                SeccionesPintando = pintando,
                UTurn = _host.Yt != null && _host.Yt.isYouTurnTriggered,
                SwitchAbierto = mc.steerSwitchHigh,
                PilotoPuesto = _host.isBtnAutoSteerOn,
                ManejoLibre = _host.Vehicle != null && _host.Vehicle.isInFreeDriveMode,
                ErrorLineaM = Math.Abs((int)off) < 29000 ? off / 1000.0 : double.NaN,
                HayGuiaRecta = recta,
            };
        }

        // ---------------------------------------------------------------------
        // Config ↔ Settings
        // ---------------------------------------------------------------------
        private static SteerCalConfig LeerConfig()
        {
            var s = S;
            int set0 = s.setArdSteer_setting0, set1 = s.setArdSteer_setting1;
            return new SteerCalConfig
            {
                Kp = s.setAS_Kp,
                MinPwm = s.setAS_minSteerPWM,
                HighPwm = s.setAS_highSteerPWM,
                WasOffset = s.setAS_wasOffset,
                CountsPerDegree = s.setAS_countsPerDegree,
                Ackerman = s.setAS_ackerman,
                InvertWas = (set0 & 1) != 0,
                InvertSteer = (set0 & 4) != 0,
                CurrentSensor = (set1 & 4) != 0,
                SensorLimit = s.setArdSteer_maxPulseCounts,
                MaxSteerAngle = s.setVehicle_maxSteerAngle,
                WheelbaseM = s.setVehicle_wheelbase,
                StanleyUsed = s.setVehicle_isStanleyUsed,
                HoldLookAhead = (int)Math.Round(s.setVehicle_goalPointLookAheadHold * 10.0),
                LookAheadMult = (int)Math.Round(s.setVehicle_goalPointLookAheadMult * 10.0),
                AcquireFactor = (int)Math.Round(s.setVehicle_goalPointAcquireFactor * 100.0),
                IntegralPp = (int)Math.Round(s.purePursuitIntegralGainAB * 100.0),
            };
        }

        /// <summary>Campos de placa a Settings EN MEMORIA + PGN 252/251. Sin Save().</summary>
        private void EnMemoria(SteerCalConfig c)
        {
            var s = S;
            s.setAS_Kp = B(c.Kp);
            s.setAS_minSteerPWM = B(c.MinPwm);
            s.setAS_highSteerPWM = B(c.HighPwm);
            s.setAS_lowSteerPWM = B(c.HighPwm / 3);     // misma derivación que el form nativo
            s.setAS_wasOffset = Math.Max(-4000, Math.Min(4000, c.WasOffset));
            s.setAS_countsPerDegree = B(c.CountsPerDegree);
            s.setAS_ackerman = B(c.Ackerman);

            int set0 = s.setArdSteer_setting0;
            set0 = c.InvertWas ? (set0 | 1) : (set0 & ~1);
            set0 = c.InvertSteer ? (set0 | 4) : (set0 & ~4);
            int set1 = s.setArdSteer_setting1;
            if (c.CurrentSensor)
            {
                // Los sensores de corte son excluyentes (mismo criterio que Guardar).
                set0 &= ~128;
                set1 = (set1 & ~2) | 4;
                s.setArdSteer_maxPulseCounts = B(c.SensorLimit);
            }
            s.setArdSteer_setting0 = (byte)set0;
            s.setArdSteer_setting1 = (byte)set1;

            _host.SettingsSender.SendSettings();
        }

        /// <summary>Guardar normal (persiste + aplica + PGN) con los valores dados.</summary>
        private void Persistir(SteerCalConfig c)
        {
            if (_steer == null) { EnMemoria(c); S.Save(); return; }
            SteerConfigDto d = _steer.Get();
            d.ProportionalGain = c.Kp;
            d.MinPwm = c.MinPwm;
            d.HighSteerPwm = c.HighPwm;
            d.WasOffset = c.WasOffset;
            d.CountsPerDegree = c.CountsPerDegree;
            d.Ackerman = c.Ackerman;
            d.InvertWas = c.InvertWas;
            d.InvertSteer = c.InvertSteer;
            if (c.CurrentSensor)
            {
                d.CurrentSensor = true;
                d.PressureSensor = false;
                d.Encoder = false;
                d.SensorLimit = c.SensorLimit;
            }
            d.HoldLookAhead = c.HoldLookAhead;
            d.AcquireFactor = c.AcquireFactor;
            d.IntegralPp = c.IntegralPp;
            _steer.Save(d);
        }

        private static byte B(int v) => (byte)Math.Max(0, Math.Min(255, v));

        // ---------------------------------------------------------------------
        // DTO
        // ---------------------------------------------------------------------
        private SteerCalEstadoDto Dto(bool ok, string error)
        {
            var mc = _host.Mc;
            var d = new SteerCalEstadoDto
            {
                Ok = ok,
                Error = error,
                Activo = _wiz.Activo,
                Paso = PasoId(_wiz.Paso),
                Numero = SteerCalWizard.NumeroDe(_wiz.Paso),
                Total = SteerCalWizard.TotalPasos,
                Fase = FaseId(_wiz.Fase),
                Titulo = SteerCalWizard.TituloDe(_wiz.Paso),
                Mensaje = _wiz.Mensaje,
                MensajeError = _wiz.MensajeEsError,
                Medicion = _wiz.Medicion,
                Progreso = _wiz.Progreso,
                PuedeEmpezar = _wiz.PuedeEmpezar,
                PuedeSiguiente = _wiz.PuedeSiguiente,
                PuedeAceptar = _wiz.PuedeAceptar,
                PuedeRepetir = _wiz.PuedeRepetir,
                PuedeSaltar = _wiz.PuedeSaltar,
                PuedeAplicar = _wiz.PuedeAplicar,
                PuedeDeshacer = _wiz.PuedeDeshacer,
                HombreMuerto = _wiz.NecesitaHombreMuerto,
                MotorActivo = _wiz.MotorActivo,
                Apretado = _wiz.Apretado,
                Setpoint = _wiz.Setpoint,
                Angulo = mc != null ? mc.actualSteerAngleDegrees : 0,
                Velocidad = Math.Abs(_host.avgSpeed),
                Corriente = mc != null ? mc.sensorData : -1,
                Escrituras = _wiz.EscriturasTotal,
            };
            foreach (var c in _wiz.Chequeos) d.Chequeos.Add(new SteerCalChequeoDto { Texto = c.Texto, Ok = c.Ok });
            foreach (var c in _wiz.Propuesta)
                d.Cambios.Add(new SteerCalCambioDto { Campo = c.Campo, Antes = c.Antes, Despues = c.Despues });
            return d;
        }

        private static string PasoId(SteerCalPaso p)
        {
            switch (p)
            {
                case SteerCalPaso.Chequeo: return "chequeo";
                case SteerCalPaso.SentidoWas: return "sentido_was";
                case SteerCalPaso.SentidoMotor: return "sentido_motor";
                case SteerCalPaso.CeroWas: return "cero_was";
                case SteerCalPaso.CuentasAckermann: return "cuentas_ackermann";
                case SteerCalPaso.PwmMinimo: return "pwm_minimo";
                case SteerCalPaso.Ganancia: return "ganancia";
                case SteerCalPaso.CorteCorriente: return "corte_corriente";
                case SteerCalPaso.AjusteFino: return "ajuste_fino";
                case SteerCalPaso.Resumen: return "resumen";
                case SteerCalPaso.Aplicado: return "aplicado";
                case SteerCalPaso.Cancelado: return "cancelado";
                default: return "inactivo";
            }
        }

        private static string FaseId(SteerCalFase f)
        {
            switch (f)
            {
                case SteerCalFase.Midiendo: return "midiendo";
                case SteerCalFase.Propuesta: return "propuesta";
                case SteerCalFase.Hecho: return "hecho";
                case SteerCalFase.Error: return "error";
                default: return "instrucciones";
            }
        }
    }
}
