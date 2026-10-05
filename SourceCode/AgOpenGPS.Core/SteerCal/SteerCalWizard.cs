// ============================================================================
// SteerCalWizard.cs — ASISTENTE DE CALIBRACIÓN GUIADA DE LA DIRECCIÓN.
//
// Máquina de estados PURA (sin Settings, sin Log, sin host, sin reloj propio):
// el host le pasa una foto de la máquina en cada tick (SteerCalEntrada), las
// acciones del operario (Accion) y el hombre muerto (Latido). El asistente
// contesta con:
//   · MotorActivo / Setpoint / VelocidadPgnKmh → lo que tiene que salir en el
//     PGN 254 (solo mientras MotorActivo; si no, el PGN sale como siempre);
//   · pedidos (TomarPedidos) → escribir la placa, restaurar, aplicar, deshacer;
//   · eventos (TomarEventos) → texto para Log.EventWriter, con antes/después.
//
// Hardware para el que está pensado (no lo discutas): AiO con Keya.hex, Keya
// SOLO motor por CAN, ángulo SIEMPRE del WAS analógico (ADS1115 single), corte
// al agarrar el volante por corriente del motor (PGN 250 / PulseCountMax).
//
// Trabaja sobre una COPIA (Trabajo); Original no se toca. Lo que se prueba en
// la placa se manda en memoria (EnPlaca) y se cuenta: cada PGN 252/251 graba
// la EEPROM de la AiO, así que hay tope de escrituras por paso. Cancelar en
// cualquier momento devuelve la placa a Original. Aplicar persiste Trabajo, y
// después se puede Deshacer.
//
// Pasos: P0 chequeo → P1 sentido del WAS → P2 sentido del motor → P3 cero del
// WAS (en marcha) → P4 cuentas/grado y Ackermann (círculos) → P5 PWM mínimo →
// P6 ganancia y PWM alto (andando a 1–2 km/h) → P7 corte por corriente →
// P8 ajuste fino (3 pasadas) → resumen.
//
// Candados del motor (todos fallan cerrado):
//   · hombre muerto: el motor se mueve SOLO mientras el operario mantiene
//     apretado; latido cada ≤ 500 ms o se corta;
//   · "parado" = velocidad GPS < 0,5 km/h Y fix válido; sin velocidad no hay
//     motor; en los pasos parados el 254 lleva 0,5 km/h falsos (la placa con
//     < 0,2 no mueve), nunca los 8 del manejo libre;
//   · setpoints acotados a ±5° en P2/P6/P7;
//   · abortar si el ángulo pasa el tope + 5° o si el error crece 300 ms;
//   · nada de motor con el piloto puesto, el manejo libre prendido, secciones
//     pintando, U-turn en curso, switch de dirección abierto o sin PGN 253.
//
// C# 7.3 a propósito: AgOpenGPS.Core compila net48/netstandard2.0.
// ============================================================================

#if NETCOREAPP
#nullable disable
#endif

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AgOpenGPS.SteerCal
{
    public sealed class SteerCalWizard
    {
        // ---------------------------------------------------------------------
        // Constantes (candados y criterios). Públicas para los tests.
        // ---------------------------------------------------------------------
        public const double LatidoMaxS = 0.5;
        public const double ParadoMaxKmh = 0.5;
        public const double VelocidadFalsaKmh = 0.5;
        public const double SetpointMaxGrados = 5.0;
        public const double MargenTopeGrados = 5.0;
        public const double CrecimientoErrorS = 0.3;
        public const double CrecimientoErrorMinGrados = 0.5;
        public const double Edad253MaxS = 0.5;
        public const double Chequeo253MaxS = 1.0;

        public const double AndandoMinKmh = 0.8;   // "1-2 km/h" con tolerancia de manejo
        public const double AndandoMaxKmh = 2.5;

        public const double CeroMinKmh = 3, CeroMaxKmh = 6, CeroDistanciaM = 50, CeroYawMaxGradS = 0.6;
        public const double CirculoMinKmh = 2, CirculoMaxKmh = 7, CirculoVentanaS = 4;
        public const double CirculoMinGrados = 8, CirculoSaltoMaxGrados = 1.5;
        public const double FinoMinKmh = 6, FinoMaxKmh = 10, FinoPasadaM = 100;
        public const int FinoPasadas = 3;

        public const double SobrepicoMaxPct = 10;
        public const double EscalonS = 2.5;
        public const double CentrarToleranciaGrados = 1.0, CentrarQuietoS = 0.5, CentrarTimeoutS = 6;
        public const int PwmAltoMax = 220, PwmAltoPaso = 30;
        public const int PwmMinimoBajo = 20, PwmMinimoAlto = 25, PwmMinimoPropuesto = 22;

        public const double AgarreTimeoutS = 20, AgarreCambioS = 2;
        public const int CiclosCorriente = 2;
        public const int UmbralCorrienteMax = 240;

        /// <summary>Ganancias que se prueban en P6, de menor a mayor.</summary>
        public static readonly int[] CandidatosKp = { 30, 45, 60, 80, 100, 130 };

        /// <summary>Tope de grabaciones de placa (PGN 252/251 = EEPROM) por paso.
        /// Las restauraciones (volver a lo de antes) siempre pasan, pero cuentan.</summary>
        public static int TopeEscrituras(SteerCalPaso p)
        {
            switch (p)
            {
                case SteerCalPaso.SentidoWas: return 1;
                case SteerCalPaso.SentidoMotor: return 1;
                case SteerCalPaso.CeroWas: return 1;
                case SteerCalPaso.CuentasAckermann: return 2;
                case SteerCalPaso.PwmMinimo: return 1;
                case SteerCalPaso.Ganancia: return 10;
                case SteerCalPaso.CorteCorriente: return 4;
                default: return 0;
            }
        }

        // ---------------------------------------------------------------------
        // Estado visible
        // ---------------------------------------------------------------------
        public SteerCalPaso Paso { get; private set; } = SteerCalPaso.Inactivo;
        public SteerCalFase Fase { get; private set; } = SteerCalFase.Instrucciones;
        /// <summary>Qué hacer ahora, o el resultado, en criollo.</summary>
        public string Mensaje { get; private set; } = "";
        public bool MensajeEsError { get; private set; }
        /// <summary>Números en vivo del paso (lo que se está midiendo).</summary>
        public string Medicion { get; private set; } = "";
        public double Progreso { get; private set; }
        public List<SteerCalCambio> Propuesta { get; } = new List<SteerCalCambio>();
        public List<SteerCalChequeo> Chequeos { get; } = new List<SteerCalChequeo>();

        public SteerCalConfig Original { get; private set; }
        public SteerCalConfig Trabajo { get; private set; }
        /// <summary>Lo que tiene la placa ahora (lo último que se le mandó).</summary>
        public SteerCalConfig EnPlaca { get; private set; }

        public bool MotorActivo { get; private set; }
        public double Setpoint { get; private set; }
        public double VelocidadPgnKmh { get; private set; }

        public int EscriturasPaso { get; private set; }
        public int EscriturasTotal { get; private set; }

        public bool Activo => Paso >= SteerCalPaso.Chequeo && Paso <= SteerCalPaso.Resumen;

        /// <summary>El operario tiene apretado el hombre muerto y el latido está fresco.</summary>
        public bool Apretado => _apretado && !double.IsNaN(_ultimoLatido) && _tAhora - _ultimoLatido <= LatidoMaxS + 1e-9;

        public bool NecesitaHombreMuerto => Fase == SteerCalFase.Midiendo &&
            (Paso == SteerCalPaso.SentidoMotor || Paso == SteerCalPaso.Ganancia || Paso == SteerCalPaso.CorteCorriente);

        public bool PuedeEmpezar => Fase == SteerCalFase.Instrucciones && PasoConMedicion(Paso);

        public bool PuedeSiguiente => Fase == SteerCalFase.Hecho && Paso >= SteerCalPaso.Chequeo && Paso <= SteerCalPaso.AjusteFino;

        public bool PuedeAceptar => Fase == SteerCalFase.Propuesta;

        public bool PuedeRepetir => Fase == SteerCalFase.Error && PasoConMedicion(Paso)
                                    && !(Paso == SteerCalPaso.CeroWas && _ceroGrabado);

        /// <summary>Se pueden saltear del cero en adelante; el chequeo y los
        /// sentidos de WAS y motor no (sin ellos el motor puede escaparse).</summary>
        public bool PuedeSaltar => Paso >= SteerCalPaso.CeroWas && Paso <= SteerCalPaso.AjusteFino
                                   && Fase != SteerCalFase.Midiendo;

        public bool PuedeAplicar => Paso == SteerCalPaso.Resumen;
        public bool PuedeDeshacer => Paso == SteerCalPaso.Aplicado && _huboAplicacion;

        public static string TituloDe(SteerCalPaso p)
        {
            switch (p)
            {
                case SteerCalPaso.Chequeo: return "Chequeo antes de empezar";
                case SteerCalPaso.SentidoWas: return "Sentido del sensor de ángulo";
                case SteerCalPaso.SentidoMotor: return "Sentido del motor";
                case SteerCalPaso.CeroWas: return "Cero del sensor";
                case SteerCalPaso.CuentasAckermann: return "Cuentas por grado y Ackermann";
                case SteerCalPaso.PwmMinimo: return "PWM mínimo";
                case SteerCalPaso.Ganancia: return "Ganancia y fuerza máxima";
                case SteerCalPaso.CorteCorriente: return "Corte al agarrar el volante";
                case SteerCalPaso.AjusteFino: return "Ajuste fino en el lote";
                case SteerCalPaso.Resumen: return "Resumen";
                case SteerCalPaso.Aplicado: return "Listo";
                case SteerCalPaso.Cancelado: return "Cancelado";
                default: return "Asistente de dirección";
            }
        }

        /// <summary>Número de paso para mostrar ("Paso 3 de 9"); 0 fuera de los pasos.</summary>
        public static int NumeroDe(SteerCalPaso p)
        {
            return p >= SteerCalPaso.Chequeo && p <= SteerCalPaso.AjusteFino ? (int)p : 0;
        }

        public const int TotalPasos = 9;

        // ---------------------------------------------------------------------
        // Salidas hacia el host
        // ---------------------------------------------------------------------
        private readonly List<SteerCalPedido> _pedidos = new List<SteerCalPedido>();
        private readonly List<string> _eventos = new List<string>();

        public List<SteerCalPedido> TomarPedidos()
        {
            var r = new List<SteerCalPedido>(_pedidos);
            _pedidos.Clear();
            return r;
        }

        public List<string> TomarEventos()
        {
            var r = new List<string>(_eventos);
            _eventos.Clear();
            return r;
        }

        // ---------------------------------------------------------------------
        // Estado interno
        // ---------------------------------------------------------------------
        private SteerCalEntrada _ultima;
        private double _tAhora;
        private bool _apretado;
        private double _ultimoLatido = double.NaN;
        private bool _huboAplicacion;

        // derivadas (rumbo/distancia)
        private double _tPrev = double.NaN, _rumboPrev, _estePrev, _nortePrev;
        private double _yawGradS = double.NaN;
        private double _distTick, _dtTick;
        private double _rumboAcum, _distAcum;

        // vigilancia del error que crece
        private double _crecErrPrev = double.NaN, _crecTPrev, _crecErrIni, _crecTIni = double.NaN;

        // P1
        private double _p1Base, _p1Inicio, _p1Desde = double.NaN;
        private bool _p1Verificando;
        // P2
        private bool _p2Corriendo, _p2Verificando;
        private double _p2Base, _p2Inicio, _p2Sp;
        // P3
        private double _p3Suma, _p3Dist;
        private int _p3N;
        private bool _ceroGrabado;
        private int _p3Propuesto;
        private double _p3Promedio;
        // P4
        private int _p4Lado = 1;
        private readonly List<double[]> _p4Ventana = new List<double[]>();   // t, ángulo, rumboAcum, distAcum
        private double _p4WasDer = double.NaN, _p4RefDer, _p4WasIzq = double.NaN, _p4RefIzq;
        private int _p4Cuentas, _p4Ack;
        // secuencia de escalones (P6 y P7)
        private enum Sub { Centrar, Escalon }
        private Sub _sub;
        private int _subEscalon;            // 0 = +5, 1 = −5
        private double _subT0 = double.NaN, _subQuieto = double.NaN, _subInicio;
        private readonly List<double> _escT = new List<double>(), _escA = new List<double>();
        private readonly List<int> _escPwm = new List<int>();
        private RespuestaEscalon _rPos, _rNeg;
        // P6
        private int _p6Idx, _p6Mejor;
        private bool _p6ValidandoAlto;
        private int _p6AltoNuevo;
        private RespuestaEscalon _p6MejorPos, _p6MejorNeg;
        // P7
        private int _p7Etapa;               // 0 medir, 1 agarre
        private int _p7Max, _p7Ciclos, _p7Propuesto;
        private double _p7SinCorriente, _p7Activo, _p7T0;
        private int _p7Propuesta;           // 1 umbral medido, 2 subir tras corte, 3 bajar tras no cortar
        // P8
        private readonly List<double> _p8T = new List<double>(), _p8E = new List<double>();
        private double _p8Dist;
        private readonly List<AnalisisPasadas> _p8Pasadas = new List<AnalisisPasadas>();
        private readonly List<double> _p8TodoE = new List<double>();
        private SteerCalConfig _p8Nueva;

        // ---------------------------------------------------------------------
        // API
        // ---------------------------------------------------------------------

        /// <summary>Arranca con la config actual. Devuelve false si ya hay uno en curso.</summary>
        public bool Iniciar(SteerCalConfig actual, double t)
        {
            if (Activo || actual == null) return false;
            Original = actual.Clone();
            Trabajo = actual.Clone();
            EnPlaca = actual.Clone();
            EscriturasTotal = 0;
            _huboAplicacion = false;
            _ceroGrabado = false;
            _p1Verificando = _p2Verificando = false;
            _tAhora = t;
            _apretado = false;
            _ultimoLatido = double.NaN;
            _tPrev = double.NaN;
            Evento("Asistente de dirección: arranca. Config: " + Resumir(Original));
            IrA(SteerCalPaso.Chequeo);
            return true;
        }

        /// <summary>Hombre muerto. apretado=true recarga el latido; false lo suelta ya.</summary>
        public void Latido(bool apretado, double t)
        {
            _apretado = apretado;
            if (apretado) _ultimoLatido = t;
            if (t > _tAhora) _tAhora = t;
            if (!apretado && MotorActivo) PararMotor();
        }

        /// <summary>Acción del operario. Devuelve false si no corresponde en este momento.</summary>
        public bool Accion(string accion)
        {
            if (string.IsNullOrEmpty(accion)) return false;
            switch (accion.Trim().ToLowerInvariant())
            {
                case "cancelar": return Cancelar("el operario canceló");
                case "empezar": return Empezar();
                case "siguiente": return Siguiente();
                case "saltar": return Saltar();
                case "repetir": return Repetir();
                case "aceptar": return Aceptar();
                case "rechazar": return Rechazar();
                case "aplicar": return Aplicar();
                case "deshacer": return Deshacer();
                default: return false;
            }
        }

        /// <summary>Corte externo (pantalla que dejó de latir, host que se apaga).</summary>
        public bool Cancelar(string motivo)
        {
            if (!Activo) return false;
            PararMotor();
            if (EscriturasTotal > 0 || !EnPlaca.MismaPlaca(Original))
                _pedidos.Add(SteerCalPedido.Restaurar);
            EnPlaca = Original.Clone();
            Evento("Asistente de dirección: cancelado (" + motivo + ") en '" + TituloDe(Paso)
                   + "'. Se vuelve a la config anterior: " + Resumir(Original));
            Paso = SteerCalPaso.Cancelado;
            Fase = SteerCalFase.Hecho;
            Propuesta.Clear();
            Mensaje = "Cancelado: la dirección quedó como estaba antes del asistente.";
            MensajeEsError = false;
            Medicion = "";
            return true;
        }

        /// <summary>Un tick con la foto de la máquina (≈10 Hz).</summary>
        public void Tick(SteerCalEntrada e)
        {
            if (e == null) return;
            if (e.T > _tAhora) _tAhora = e.T;
            Derivadas(e);
            _ultima = e;

            if (!Activo) { PararMotor(); return; }

            bool motorAntes = MotorActivo;
            switch (Paso)
            {
                case SteerCalPaso.Chequeo: TickChequeo(e); break;
                case SteerCalPaso.SentidoWas: TickSentidoWas(e); break;
                case SteerCalPaso.SentidoMotor: TickSentidoMotor(e); break;
                case SteerCalPaso.CeroWas: TickCero(e); break;
                case SteerCalPaso.CuentasAckermann: TickCirculos(e); break;
                case SteerCalPaso.Ganancia: TickGanancia(e); break;
                case SteerCalPaso.CorteCorriente: TickCorte(e, motorAntes); break;
                case SteerCalPaso.AjusteFino: TickFino(e); break;
            }

            // Fuera de "Midiendo" el motor no se mueve nunca.
            if (Fase != SteerCalFase.Midiendo && MotorActivo) PararMotor();
        }

        // ---------------------------------------------------------------------
        // Navegación
        // ---------------------------------------------------------------------
        private static bool PasoConMedicion(SteerCalPaso p)
        {
            return p == SteerCalPaso.SentidoWas || p == SteerCalPaso.SentidoMotor || p == SteerCalPaso.CeroWas
                || p == SteerCalPaso.CuentasAckermann || p == SteerCalPaso.Ganancia
                || p == SteerCalPaso.CorteCorriente || p == SteerCalPaso.AjusteFino;
        }

        private void IrA(SteerCalPaso p)
        {
            PararMotor();
            bool otroPaso = p != Paso;
            Paso = p;
            if (otroPaso) EscriturasPaso = 0;
            Fase = SteerCalFase.Instrucciones;
            Propuesta.Clear();
            Chequeos.Clear();
            MensajeEsError = false;
            Medicion = "";
            Progreso = 0;

            switch (p)
            {
                case SteerCalPaso.Chequeo:
                    Mensaje = "Revisando que esté todo listo…";
                    break;
                case SteerCalPaso.SentidoWas:
                    Mensaje = "Con el tractor PARADO y las ruedas derechas, tocá Empezar. Después girá el volante A MANO "
                            + "hacia la DERECHA (unos 10°) y dejalo ahí. El motor no se mueve en este paso.";
                    break;
                case SteerCalPaso.SentidoMotor:
                    _p2Corriendo = false;
                    Mensaje = "Con el tractor PARADO y las ruedas derechas, tocá Empezar y MANTENÉ APRETADO el botón: "
                            + "el volante va a ir 3° a la derecha. Si soltás, se frena.";
                    break;
                case SteerCalPaso.CeroWas:
                    Mensaje = "Andá DERECHO a 3–6 km/h, con el piloto apagado y manejando vos, por un tramo recto de "
                            + "50 m o más. Tocá Empezar y mantené el volante quieto. El cero se graba UNA sola vez.";
                    break;
                case SteerCalPaso.CuentasAckermann:
                    _p4Lado = 1;
                    _p4WasDer = _p4WasIzq = double.NaN;
                    _p4Ventana.Clear();
                    Mensaje = InstruccionCirculo();
                    break;
                case SteerCalPaso.PwmMinimo:
                    EvaluarPwmMinimo();
                    break;
                case SteerCalPaso.Ganancia:
                    Mensaje = "Andá DERECHO a 1–2 km/h, con el piloto apagado. Tocá Empezar y MANTENÉ APRETADO el botón: "
                            + "el volante va a dar saltos de 5° a cada lado mientras se prueban ganancias de menor a mayor. "
                            + "Soltá para frenar en cualquier momento.";
                    break;
                case SteerCalPaso.CorteCorriente:
                    _p7Etapa = 0;
                    _p7Max = -1;
                    _p7Propuesta = 0;
                    Mensaje = "Primero se mide la corriente NORMAL del motor. Andá DERECHO a 1–2 km/h, piloto apagado, "
                            + "tocá Empezar y MANTENÉ APRETADO: el volante va a dar saltos de 5° a cada lado.";
                    break;
                case SteerCalPaso.AjusteFino:
                    if (Trabajo.StanleyUsed)
                    {
                        Fase = SteerCalFase.Hecho;
                        Mensaje = "El ajuste fino automático es para Pure Pursuit y estás en Stanley: se saltea.";
                    }
                    else
                    {
                        Mensaje = "Necesitás una guía RECTA (AB) activa. Andá a 6–10 km/h, enganchá el piloto sobre la "
                                + "línea y hacé 3 pasadas de 100 m. Tocá Empezar.";
                    }
                    break;
                case SteerCalPaso.Resumen:
                    ArmarResumen();
                    break;
            }

            if (otroPaso) Evento("Asistente de dirección: paso '" + TituloDe(p) + "'");
        }

        private static SteerCalPaso Proximo(SteerCalPaso p)
        {
            return p >= SteerCalPaso.AjusteFino ? SteerCalPaso.Resumen : (SteerCalPaso)((int)p + 1);
        }

        private bool Siguiente()
        {
            if (!PuedeSiguiente) return false;
            IrA(Proximo(Paso));
            return true;
        }

        private bool Saltar()
        {
            if (!PuedeSaltar) return false;
            // Lo que se probó en la placa durante el paso vuelve a lo aceptado.
            RestaurarPlacaATrabajo("se saltea el paso");
            Evento("Asistente de dirección: se saltea '" + TituloDe(Paso) + "'");
            IrA(Proximo(Paso));
            return true;
        }

        private bool Repetir()
        {
            if (!PuedeRepetir) return false;
            if (Paso == SteerCalPaso.SentidoMotor) _p2Corriendo = false;
            IrA(Paso);
            return true;
        }

        private bool Empezar()
        {
            if (!PuedeEmpezar) return false;
            SteerCalEntrada e = _ultima ?? new SteerCalEntrada { T = _tAhora };

            switch (Paso)
            {
                case SteerCalPaso.SentidoWas:
                    if (!Parado(e)) return Aviso("Pará el tractor: este paso es con el tractor quieto.");
                    _p1Base = e.AnguloWas;
                    _p1Inicio = e.T;
                    _p1Desde = double.NaN;
                    Mensaje = "Ahora girá el volante A MANO hacia la DERECHA, unos 10°, y dejalo quieto.";
                    break;

                case SteerCalPaso.SentidoMotor:
                    if (!Parado(e)) return Aviso("Pará el tractor: este paso es con el tractor quieto.");
                    if (Math.Abs(e.AnguloWas) > 2.0)
                        return Aviso("Enderezá las ruedas (estás en " + G(e.AnguloWas) + ") y tocá Empezar.");
                    _p2Corriendo = false;
                    Mensaje = "MANTENÉ APRETADO el botón: el volante va 3° a la derecha.";
                    break;

                case SteerCalPaso.CeroWas:
                    ReiniciarRecta();
                    Mensaje = "Andá derecho a 3–6 km/h con el volante quieto…";
                    break;

                case SteerCalPaso.CuentasAckermann:
                    _p4Ventana.Clear();
                    Mensaje = "Mantené el círculo con el volante quieto…";
                    break;

                case SteerCalPaso.Ganancia:
                    _p6Idx = 0;
                    _p6Mejor = -1;
                    _p6ValidandoAlto = false;
                    _p6AltoNuevo = Trabajo.HighPwm;
                    _p6MejorPos = _p6MejorNeg = null;
                    Fase = SteerCalFase.Midiendo;
                    if (!ProbarCandidato()) return true;   // ProbarCandidato ya dejó el error
                    Mensaje = "MANTENÉ APRETADO el botón y seguí derecho a 1–2 km/h.";
                    return true;

                case SteerCalPaso.CorteCorriente:
                    if (_p7Etapa == 0)
                    {
                        _p7Max = -1;
                        _p7Ciclos = 0;
                        _p7SinCorriente = 0;
                        ReiniciarSecuencia();
                        Mensaje = "MANTENÉ APRETADO el botón y seguí derecho a 1–2 km/h.";
                    }
                    else
                    {
                        if (!Parado(e)) return Aviso("Pará el tractor: la prueba de agarrar el volante es con el tractor quieto.");
                        _p7Activo = 0;
                        _p7T0 = e.T;
                        Mensaje = "MANTENÉ APRETADO el botón con una mano y con la otra AGARRÁ FUERTE el volante. Tiene que cortar.";
                    }
                    break;

                case SteerCalPaso.AjusteFino:
                    _p8T.Clear(); _p8E.Clear(); _p8Dist = 0;
                    _p8Pasadas.Clear(); _p8TodoE.Clear();
                    Mensaje = "Pasada 1 de 3: piloto enganchado, 6–10 km/h, 100 m sobre la guía recta.";
                    break;
            }

            Fase = SteerCalFase.Midiendo;
            MensajeEsError = false;
            return true;
        }

        private bool Aviso(string texto)
        {
            Mensaje = texto;
            MensajeEsError = true;
            return false;
        }

        private bool Aceptar()
        {
            if (!PuedeAceptar) return false;
            SteerCalConfig t = Trabajo.Clone();

            switch (Paso)
            {
                case SteerCalPaso.SentidoWas:
                    t.InvertWas = !t.InvertWas;
                    if (!EscribirPlaca(t, "invertir sensor")) return true;
                    Cambio("Invertir sensor", Trabajo.InvertWas, t.InvertWas);
                    Trabajo = t;
                    _p1Verificando = true;
                    IrA(Paso);
                    Mensaje = "Listo, invertido. Para confirmar: ruedas derechas, tocá Empezar y girá a mano a la DERECHA.";
                    return true;

                case SteerCalPaso.SentidoMotor:
                    t.InvertSteer = !t.InvertSteer;
                    if (!EscribirPlaca(t, "invertir motor")) return true;
                    Cambio("Invertir motor", Trabajo.InvertSteer, t.InvertSteer);
                    Trabajo = t;
                    _p2Verificando = true;
                    IrA(Paso);
                    Mensaje = "Listo, invertido. Para confirmar: ruedas derechas, tocá Empezar y mantené apretado.";
                    return true;

                case SteerCalPaso.CeroWas:
                    if (_ceroGrabado) return false;
                    t.WasOffset = _p3Propuesto;
                    if (!EscribirPlaca(t, "cero del sensor")) return true;
                    Evento("Asistente de dirección: cero del sensor — offset " + Trabajo.WasOffset + " → " + t.WasOffset
                           + " (corrimiento medido " + G(_p3Promedio) + " en " + F(_p3Dist, 0) + " m)");
                    Trabajo = t;
                    _ceroGrabado = true;
                    Terminar("Cero grabado. Queda fijo: no se vuelve a tocar en marcha.");
                    return true;

                case SteerCalPaso.CuentasAckermann:
                    t.CountsPerDegree = _p4Cuentas;
                    t.Ackerman = _p4Ack;
                    if (!EscribirPlaca(t, "cuentas/grado y Ackermann")) return true;
                    Evento("Asistente de dirección: cuentas/grado " + Trabajo.CountsPerDegree + " → " + t.CountsPerDegree
                           + ", Ackermann " + Trabajo.Ackerman + " → " + t.Ackerman);
                    Trabajo = t;
                    Terminar("Cuentas y Ackermann aplicados.");
                    return true;

                case SteerCalPaso.PwmMinimo:
                    t.MinPwm = PwmMinimoPropuesto;
                    if (!EscribirPlaca(t, "PWM mínimo")) return true;
                    Cambio("PWM mínimo", Trabajo.MinPwm, t.MinPwm);
                    Trabajo = t;
                    Terminar("PWM mínimo en " + t.MinPwm + ".");
                    return true;

                case SteerCalPaso.Ganancia:
                    t.Kp = CandidatosKp[_p6Mejor];
                    t.HighPwm = _p6AltoNuevo;
                    Cambio("Ganancia (Kp)", Trabajo.Kp, t.Kp);
                    Cambio("PWM alto", Trabajo.HighPwm, t.HighPwm);
                    Trabajo = t;
                    RestaurarPlacaATrabajo("ganancia elegida");
                    Terminar("Ganancia " + t.Kp + " y PWM alto " + t.HighPwm + " aplicados.");
                    return true;

                case SteerCalPaso.CorteCorriente:
                    t.SensorLimit = _p7Propuesto;
                    t.CurrentSensor = true;
                    if (!EscribirPlaca(t, "umbral de corte por corriente")) return true;
                    Cambio("Umbral de corte por corriente", Trabajo.SensorLimit, t.SensorLimit);
                    Trabajo = t;
                    if (_p7Propuesta == 2)
                    {
                        IrA(Paso);
                        Mensaje = "Umbral subido a " + t.SensorLimit + ". Volvé a armar el switch de dirección y medí de nuevo.";
                    }
                    else
                    {
                        _p7Etapa = 1;
                        Fase = SteerCalFase.Instrucciones;
                        Propuesta.Clear();
                        MensajeEsError = false;
                        Mensaje = "Ahora la prueba: con el tractor PARADO, tocá Empezar, MANTENÉ APRETADO con una mano y "
                                + "con la otra AGARRÁ FUERTE el volante. Tiene que cortar.";
                    }
                    return true;

                case SteerCalPaso.AjusteFino:
                    if (_p8Nueva == null) return false;
                    Cambio("Mirada adelante (×0,1 s)", Trabajo.HoldLookAhead, _p8Nueva.HoldLookAhead);
                    Cambio("Entrada a la línea (×0,01)", Trabajo.AcquireFactor, _p8Nueva.AcquireFactor);
                    Cambio("Integral PP", Trabajo.IntegralPp, _p8Nueva.IntegralPp);
                    t.HoldLookAhead = _p8Nueva.HoldLookAhead;
                    t.AcquireFactor = _p8Nueva.AcquireFactor;
                    t.IntegralPp = _p8Nueva.IntegralPp;
                    Trabajo = t;
                    Terminar("Aceptado. Se guarda al final, con Aplicar.");
                    return true;
            }
            return false;
        }

        private bool Rechazar()
        {
            if (!PuedeAceptar) return false;
            switch (Paso)
            {
                case SteerCalPaso.SentidoWas:
                    Fallar("Sin invertir el sensor no se puede seguir: el piloto empujaría para el lado contrario.");
                    return true;
                case SteerCalPaso.SentidoMotor:
                    Fallar("Sin invertir el motor no se puede seguir: el volante se iría al tope.");
                    return true;
                case SteerCalPaso.Ganancia:
                    RestaurarPlacaATrabajo("se rechaza la ganancia propuesta");
                    Terminar("Sin cambios: quedó la ganancia " + Trabajo.Kp + ".");
                    return true;
                case SteerCalPaso.CorteCorriente:
                    if (_p7Propuesta == 3)
                    {
                        Fallar("El corte al agarrar el volante NO quedó probado. No salgas al lote así.");
                        return true;
                    }
                    if (_p7Propuesta == 2)
                    {
                        Fallar("El corte salta andando normal con el umbral " + Trabajo.SensorLimit
                               + ": así el piloto se va a soltar solo.");
                        return true;
                    }
                    // Umbral medido rechazado: igual se prueba el corte con el que hay.
                    _p7Etapa = 1;
                    Fase = SteerCalFase.Instrucciones;
                    Propuesta.Clear();
                    MensajeEsError = false;
                    Mensaje = "Queda el umbral " + Trabajo.SensorLimit + ". Igual hay que probarlo: PARADO, tocá Empezar, "
                            + "mantené apretado y AGARRÁ FUERTE el volante.";
                    return true;
                default:
                    Terminar("Sin cambios.");
                    return true;
            }
        }

        private bool Aplicar()
        {
            if (!PuedeAplicar) return false;
            PararMotor();
            bool hayAlgo = !Trabajo.MismoTodo(Original) || EscriturasTotal > 0;
            if (hayAlgo)
            {
                _pedidos.Add(SteerCalPedido.AplicarFinal);
                EnPlaca = Trabajo.Clone();
                _huboAplicacion = true;
                Evento("Asistente de dirección: APLICADO. Antes: " + Resumir(Original) + " | Después: " + Resumir(Trabajo));
                Mensaje = "Aplicado y guardado ✔. Si algo no te convence, tocá Deshacer.";
            }
            else
            {
                Evento("Asistente de dirección: terminado sin cambios.");
                Mensaje = "Terminado: no quedó nada para cambiar.";
            }
            Paso = SteerCalPaso.Aplicado;
            Fase = SteerCalFase.Hecho;
            MensajeEsError = false;
            return true;
        }

        private bool Deshacer()
        {
            if (!PuedeDeshacer) return false;
            _pedidos.Add(SteerCalPedido.Deshacer);
            EnPlaca = Original.Clone();
            _huboAplicacion = false;
            Evento("Asistente de dirección: DESHECHO. Se vuelve a: " + Resumir(Original));
            Paso = SteerCalPaso.Cancelado;
            Fase = SteerCalFase.Hecho;
            Propuesta.Clear();
            Mensaje = "Deshecho: la dirección volvió a la config de antes del asistente.";
            MensajeEsError = false;
            return true;
        }

        // ---------------------------------------------------------------------
        // P0 — chequeo
        // ---------------------------------------------------------------------
        private void TickChequeo(SteerCalEntrada e)
        {
            Chequeos.Clear();
            Chequeos.Add(new SteerCalChequeo("Llega el ángulo de la placa (PGN 253)",
                e.Edad253.HasValue && e.Edad253.Value <= Chequeo253MaxS));
            Chequeos.Add(new SteerCalChequeo("GPS con fix válido y velocidad", e.FixValido && e.HayVelocidad));
            Chequeos.Add(new SteerCalChequeo("Corte por corriente prendido (Dirección › Módulo)", Trabajo.CurrentSensor));
            Chequeos.Add(new SteerCalChequeo("Piloto apagado", !e.PilotoPuesto));
            Chequeos.Add(new SteerCalChequeo("Manejo libre apagado", !e.ManejoLibre));
            Chequeos.Add(new SteerCalChequeo("Secciones sin pintar", !e.SeccionesPintando));
            Chequeos.Add(new SteerCalChequeo("Sin vuelta en U en curso", !e.UTurn));
            Chequeos.Add(new SteerCalChequeo("Switch de dirección armado (cerrado)", !e.SwitchAbierto));

            bool todo = true;
            foreach (var c in Chequeos) todo &= c.Ok;
            Fase = todo ? SteerCalFase.Hecho : SteerCalFase.Instrucciones;
            Mensaje = todo ? "Todo listo. Tocá Siguiente." : "Resolvé lo que está en rojo para seguir.";
            MensajeEsError = false;
            Progreso = 0;
        }

        // ---------------------------------------------------------------------
        // P1 — sentido del WAS (parado, motor quieto, gira el operario)
        // ---------------------------------------------------------------------
        private void TickSentidoWas(SteerCalEntrada e)
        {
            if (Fase != SteerCalFase.Midiendo) return;
            if (!Parado(e))
            {
                Mensaje = "Pará el tractor: este paso es con el tractor quieto.";
                MensajeEsError = true;
                _p1Desde = double.NaN;
                return;
            }

            double d = e.AnguloWas - _p1Base;
            Medicion = "Giro que marca el sensor: " + G(d);
            Progreso = Math.Min(1.0, Math.Abs(d) / 5.0);

            if (Math.Abs(d) >= 5.0)
            {
                if (double.IsNaN(_p1Desde)) _p1Desde = e.T;
                else if (e.T - _p1Desde >= 0.5 - 1e-9) { ConcluirSentidoWas(d); return; }
            }
            else _p1Desde = double.NaN;

            if (e.T - _p1Inicio > 40)
                Fallar("El sensor no se movió: ¿está conectado? Revisá el cable del WAS y que la placa mande el PGN 253.");
        }

        private void ConcluirSentidoWas(double d)
        {
            if (d > 0)
            {
                Evento("Asistente de dirección: sentido del sensor OK (girando a la derecha marcó " + G(d) + ")");
                Terminar(_p1Verificando
                    ? "Ahora sí: girando a la derecha el sensor marca derecha."
                    : "Bien: girando a la derecha el sensor marca derecha.");
                return;
            }
            if (_p1Verificando)
            {
                Fallar("Sigue marcando al revés aun invertido: revisá el cableado del sensor.");
                return;
            }
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            Propuesta.Add(new SteerCalCambio("Invertir sensor (WAS)", SiNo(Trabajo.InvertWas), SiNo(!Trabajo.InvertWas)));
            Mensaje = "Girando a la derecha el sensor marcó IZQUIERDA (" + G(d) + "). Hay que invertirlo.";
            MensajeEsError = false;
            Evento("Asistente de dirección: sensor invertido (girando a la derecha marcó " + G(d) + ")");
        }

        // ---------------------------------------------------------------------
        // P2 — sentido del motor (parado, hombre muerto, +3°)
        // ---------------------------------------------------------------------
        private void TickSentidoMotor(SteerCalEntrada e)
        {
            if (Fase != SteerCalFase.Midiendo) return;

            string b = BloqueoMotor(e, true);
            if (b != null)
            {
                PararMotor();
                _p2Corriendo = false;
                Mensaje = b;
                MensajeEsError = false;
                return;
            }

            if (!_p2Corriendo)
            {
                _p2Corriendo = true;
                _p2Base = e.AnguloWas;
                _p2Inicio = e.T;
                _p2Sp = Acotar(_p2Base + 3.0, SetpointMaxGrados);
            }

            MoverMotor(e, _p2Sp, true);
            Mensaje = "Mantené apretado: el volante va 3° a la derecha.";
            MensajeEsError = false;
            Medicion = "Objetivo " + G(_p2Sp) + " — ángulo " + G(e.AnguloWas);
            Progreso = Math.Max(0, Math.Min(1, (e.AnguloWas - _p2Base) / Math.Max(0.5, _p2Sp - _p2Base)));

            var c = RevisarCandados(e, true);
            if (c == Candado.Escape) { AbortarPaso(TextoEscape(e)); return; }
            // Se fue más de 1,5° para el lado contrario, o el error crece 300 ms: motor invertido.
            if (c == Candado.Crece || e.AnguloWas - _p2Base < -1.5)
            {
                PararMotor();
                MotorInvertidoDetectado(e);
                return;
            }

            double err = Math.Abs(_p2Sp - e.AnguloWas);
            if (err <= 0.8 && e.AnguloWas - _p2Base > 1.0)
            {
                PararMotor();
                Evento("Asistente de dirección: sentido del motor OK (de " + G(_p2Base) + " a " + G(e.AnguloWas) + ")");
                Terminar("Bien: el motor gira para el lado correcto. Podés soltar.");
                return;
            }
            if (e.T - _p2Inicio > 4 && Math.Abs(e.AnguloWas - _p2Base) < 0.3)
            {
                AbortarPaso("El motor no mueve el volante: revisá la alimentación del Keya, el CAN y el PWM mínimo.");
                return;
            }
            if (e.T - _p2Inicio > 8)
                AbortarPaso("No llegó a los 3° en 8 segundos: el motor está flojo o algo lo frena.");
        }

        private void MotorInvertidoDetectado(SteerCalEntrada e)
        {
            Evento("Asistente de dirección: motor invertido detectado (objetivo " + G(_p2Sp) + ", fue a " + G(e.AnguloWas) + ")");
            if (_p2Verificando)
            {
                AbortarPaso("Sigue yendo al revés aun invertido: revisá el sentido del sensor (paso anterior) y el cableado del motor.");
                return;
            }
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            Propuesta.Add(new SteerCalCambio("Invertir motor", SiNo(Trabajo.InvertSteer), SiNo(!Trabajo.InvertSteer)));
            Mensaje = "El volante se fue para el lado contrario: el motor está invertido. Se cortó en el acto.";
            MensajeEsError = false;
        }

        // ---------------------------------------------------------------------
        // P3 — cero del WAS (en marcha, recta, una sola vez)
        // ---------------------------------------------------------------------
        private void ReiniciarRecta()
        {
            _p3Suma = 0; _p3N = 0; _p3Dist = 0;
        }

        private void TickCero(SteerCalEntrada e)
        {
            if (Fase != SteerCalFase.Midiendo) return;

            string motivo = null;
            double v = Math.Abs(e.VelKmh);
            if (e.PilotoPuesto) motivo = "Apagá el piloto: el cero se mide manejando vos.";
            else if (!e.FixValido || !e.HayVelocidad) motivo = "Sin GPS válido.";
            else if (v < CeroMinKmh || v > CeroMaxKmh) motivo = "Andá entre 3 y 6 km/h (vas a " + F(v, 1) + " km/h).";
            else if (double.IsNaN(_yawGradS) || Math.Abs(_yawGradS) > CeroYawMaxGradS) motivo = "Seguí DERECHO: el tractor está doblando.";

            if (motivo != null)
            {
                Mensaje = motivo + (_p3Dist > 1 ? " Se reinicia la recta." : "");
                MensajeEsError = false;
                ReiniciarRecta();
                Progreso = 0;
                return;
            }

            _p3Suma += e.AnguloWas;
            _p3N++;
            _p3Dist += _distTick;
            double prom = _p3Suma / _p3N;
            Progreso = Math.Min(1.0, _p3Dist / CeroDistanciaM);
            Mensaje = "Seguí derecho con el volante quieto…";
            MensajeEsError = false;
            Medicion = "Recta: " + F(_p3Dist, 0) + " de " + F(CeroDistanciaM, 0) + " m — el sensor marca " + G(prom, 2);

            if (_p3Dist >= CeroDistanciaM) ConcluirCero(prom);
        }

        private void ConcluirCero(double prom)
        {
            _p3Promedio = prom;
            if (Math.Abs(prom) < 0.1)
            {
                Evento("Asistente de dirección: cero OK (corrimiento " + G(prom, 2) + "), no se graba");
                Terminar("El cero ya está bien (corrido " + G(prom, 2) + "). No se toca.");
                return;
            }
            int nuevo = Calibracion.OffsetParaCero(Trabajo, prom);
            if (Math.Abs(nuevo) > Calibracion.OffsetMax)
            {
                Fallar("El cero quedaría fuera de rango (" + nuevo + " cuentas): revisá el montaje del sensor.");
                return;
            }
            _p3Propuesto = nuevo;
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            Propuesta.Add(new SteerCalCambio("Cero del sensor (cuentas)", Trabajo.WasOffset.ToString(CultureInfo.InvariantCulture),
                nuevo.ToString(CultureInfo.InvariantCulture)));
            Mensaje = "Andando derecho el sensor marca " + G(prom, 2) + ". Grabar el cero lo deja en 0°. Se graba UNA vez.";
            MensajeEsError = false;
        }

        // ---------------------------------------------------------------------
        // P4 — cuentas/grado y Ackermann (círculos, δ = atan(L·ω/v))
        // ---------------------------------------------------------------------
        private string InstruccionCirculo()
        {
            string lado = _p4Lado > 0 ? "DERECHA" : "IZQUIERDA";
            return "Andando a 3–6 km/h, hacé un círculo a la " + lado + " con el volante QUIETO (ruedas a 10–25°), "
                 + "piloto apagado. Tocá Empezar y mantené el círculo unos segundos.";
        }

        private void TickCirculos(SteerCalEntrada e)
        {
            if (Fase != SteerCalFase.Midiendo) return;

            double v = Math.Abs(e.VelKmh);
            double tope = Tope();
            string motivo = null;
            if (e.PilotoPuesto) motivo = "Apagá el piloto: el círculo lo hacés vos.";
            else if (!e.FixValido || !e.HayVelocidad) motivo = "Sin GPS válido.";
            else if (v < CirculoMinKmh || v > CirculoMaxKmh) motivo = "Andá entre 3 y 6 km/h (vas a " + F(v, 1) + " km/h).";
            else if (Math.Sign(e.AnguloWas) != _p4Lado || Math.Abs(e.AnguloWas) < CirculoMinGrados)
                motivo = "Doblá más a la " + (_p4Lado > 0 ? "derecha" : "izquierda") + " (ruedas a 10–25°).";
            else if (Math.Abs(e.AnguloWas) > tope - 2) motivo = "Doblá un poco menos: estás cerca del tope.";

            if (motivo != null)
            {
                Mensaje = motivo;
                MensajeEsError = false;
                _p4Ventana.Clear();
                return;
            }

            // Volante quieto: si el ángulo se va más de 1,5° del promedio, se reinicia.
            if (_p4Ventana.Count > 0)
            {
                double suma = 0;
                foreach (var m in _p4Ventana) suma += m[1];
                if (Math.Abs(e.AnguloWas - suma / _p4Ventana.Count) > CirculoSaltoMaxGrados)
                {
                    _p4Ventana.Clear();
                    Mensaje = "Mantené el volante QUIETO: se reinicia la medida.";
                }
            }
            _p4Ventana.Add(new[] { e.T, e.AnguloWas, _rumboAcum, _distAcum });

            double dur = e.T - _p4Ventana[0][0];
            Progreso = (_p4Lado > 0 ? 0 : 0.5) + 0.5 * Math.Min(1, dur / CirculoVentanaS);
            Medicion = "Midiendo el círculo a la " + (_p4Lado > 0 ? "derecha" : "izquierda") + ": " + F(dur, 1) + " de "
                     + F(CirculoVentanaS, 0) + " s — sensor " + G(e.AnguloWas);
            if (dur < CirculoVentanaS) return;

            double[] ini = _p4Ventana[0], fin = _p4Ventana[_p4Ventana.Count - 1];
            double dt = fin[0] - ini[0];
            double omega = (fin[2] - ini[2]) / dt;           // rad/s
            double vel = (fin[3] - ini[3]) / dt;             // m/s
            double referencia = Calibracion.AnguloBicicletaGrados(Trabajo.WheelbaseM, omega, vel);
            double sumaA = 0;
            foreach (var m in _p4Ventana) sumaA += m[1];
            double was = sumaA / _p4Ventana.Count;
            _p4Ventana.Clear();

            if (double.IsNaN(referencia) || Math.Sign(referencia) != _p4Lado || Math.Abs(referencia) < 5)
            {
                Fallar("La medida no cierra: el tractor giró para el otro lado de lo que marca el sensor, o muy poco. "
                     + "Repetí el círculo (si gira al revés, revisá el sentido del sensor).");
                return;
            }

            Evento("Asistente de dirección: círculo a la " + (_p4Lado > 0 ? "derecha" : "izquierda") + " — sensor " + G(was, 2)
                   + ", real " + G(referencia, 2) + " (L " + F(Trabajo.WheelbaseM, 2) + " m, v " + F(vel * 3.6, 1) + " km/h)");

            if (_p4Lado > 0)
            {
                _p4WasDer = was; _p4RefDer = referencia;
                _p4Lado = -1;
                Fase = SteerCalFase.Instrucciones;
                Mensaje = "Derecha medida. " + InstruccionCirculo();
                Progreso = 0.5;
                return;
            }

            _p4WasIzq = was; _p4RefIzq = referencia;
            ConcluirCirculos();
        }

        private void ConcluirCirculos()
        {
            double rDer = _p4WasDer / _p4RefDer, rIzq = _p4WasIzq / _p4RefIzq;
            if (rDer < 0.5 || rDer > 2 || rIzq < 0.5 || rIzq > 2)
            {
                Fallar("La medida es rara (el sensor marca " + F(rDer * 100, 0) + " % / " + F(rIzq * 100, 0)
                     + " % de lo real): revisá la distancia entre ejes y repetí con el volante más quieto.");
                return;
            }
            _p4Cuentas = Calibracion.CuentasNuevas(Trabajo.CountsPerDegree, _p4WasDer, _p4RefDer);
            _p4Ack = Calibracion.AckermannNuevo(Trabajo.Ackerman, Trabajo.CountsPerDegree, _p4Cuentas, _p4WasIzq, _p4RefIzq);
            Medicion = "Derecha: sensor " + G(_p4WasDer, 1) + " / real " + G(_p4RefDer, 1)
                     + " — Izquierda: sensor " + G(_p4WasIzq, 1) + " / real " + G(_p4RefIzq, 1);
            Progreso = 1;

            if (_p4Cuentas == Trabajo.CountsPerDegree && _p4Ack == Trabajo.Ackerman)
            {
                Terminar("Las cuentas por grado y el Ackermann ya están bien.");
                return;
            }
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            Propuesta.Add(new SteerCalCambio("Cuentas por grado", Trabajo.CountsPerDegree.ToString(CultureInfo.InvariantCulture),
                _p4Cuentas.ToString(CultureInfo.InvariantCulture)));
            Propuesta.Add(new SteerCalCambio("Ackermann (%)", Trabajo.Ackerman.ToString(CultureInfo.InvariantCulture),
                _p4Ack.ToString(CultureInfo.InvariantCulture)));
            Mensaje = "Con estos números el sensor marca lo mismo que gira el tractor de verdad.";
            MensajeEsError = false;
        }

        // ---------------------------------------------------------------------
        // P5 — PWM mínimo (no se barre: 20–25 anda)
        // ---------------------------------------------------------------------
        private void EvaluarPwmMinimo()
        {
            int m = Trabajo.MinPwm;
            Medicion = "PWM mínimo actual: " + m;
            if (m >= PwmMinimoBajo && m <= PwmMinimoAlto)
            {
                Fase = SteerCalFase.Hecho;
                Mensaje = "El PWM mínimo está en " + m + ": dentro de lo que anda (20–25).";
                return;
            }
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            Propuesta.Add(new SteerCalCambio("PWM mínimo", m.ToString(CultureInfo.InvariantCulture),
                PwmMinimoPropuesto.ToString(CultureInfo.InvariantCulture)));
            Mensaje = "El PWM mínimo está en " + m + ", fuera de 20–25. Se propone " + PwmMinimoPropuesto + ".";
        }

        // ---------------------------------------------------------------------
        // Secuencia de escalones (P6 y P7): centrar → +5 → centrar → −5
        // ---------------------------------------------------------------------
        private void ReiniciarSecuencia()
        {
            _sub = Sub.Centrar;
            _subEscalon = 0;
            _subT0 = double.NaN;
            _subQuieto = double.NaN;
            _escT.Clear(); _escA.Clear(); _escPwm.Clear();
            _rPos = _rNeg = null;
        }

        /// <summary>
        /// Avanza la secuencia un tick. Devuelve true cuando terminó el par
        /// de escalones (_rPos y _rNeg cargados). Mueve el motor (andando).
        /// </summary>
        private bool PasoSecuencia(SteerCalEntrada e, bool vigilarCrecimiento, out bool abortado)
        {
            abortado = false;
            if (_sub == Sub.Centrar)
            {
                MoverMotor(e, 0, false);
                if (double.IsNaN(_subT0)) _subT0 = e.T;
                if (Math.Abs(e.AnguloWas) < CentrarToleranciaGrados)
                {
                    if (double.IsNaN(_subQuieto)) _subQuieto = e.T;
                    else if (e.T - _subQuieto >= CentrarQuietoS - 1e-9)
                    {
                        _sub = Sub.Escalon;
                        _subInicio = e.AnguloWas;
                        _subT0 = e.T;
                        _escT.Clear(); _escA.Clear(); _escPwm.Clear();
                    }
                }
                else _subQuieto = double.NaN;

                if (_sub == Sub.Centrar && e.T - _subT0 > CentrarTimeoutS)
                {
                    AbortarPaso("El volante no vuelve al centro: revisá el PWM mínimo y que nada lo trabe.");
                    abortado = true;
                    return false;
                }
            }
            else
            {
                double sp = _subEscalon == 0 ? SetpointMaxGrados : -SetpointMaxGrados;
                MoverMotor(e, sp, false);
                _escT.Add(e.T - _subT0);
                _escA.Add(e.AnguloWas);
                _escPwm.Add(e.Pwm);
                if (e.T - _subT0 >= EscalonS - 1e-9)
                {
                    int alto = EnPlaca.HighPwm;
                    var r = RespuestaEscalon.Analizar(_escT, _escA, _escPwm, _subInicio, sp, alto);
                    if (_subEscalon == 0)
                    {
                        _rPos = r;
                        _subEscalon = 1;
                        _sub = Sub.Centrar;
                        _subT0 = double.NaN;
                        _subQuieto = double.NaN;
                    }
                    else
                    {
                        _rNeg = r;
                        _sub = Sub.Centrar;
                        _subEscalon = 0;
                        _subT0 = double.NaN;
                        _subQuieto = double.NaN;
                        Candado c0 = RevisarCandados(e, vigilarCrecimiento);
                        if (c0 == Candado.Escape) { AbortarPaso(TextoEscape(e)); abortado = true; return false; }
                        return true;
                    }
                }
            }

            var c = RevisarCandados(e, vigilarCrecimiento);
            if (c == Candado.Escape) { AbortarPaso(TextoEscape(e)); abortado = true; }
            else if (c == Candado.Crece)
            {
                AbortarPaso("El error crece en vez de achicarse: el volante se va para el otro lado. Se cortó. "
                          + "Revisá los sentidos del sensor y del motor.");
                abortado = true;
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // P6 — ganancia (Kp) y PWM alto, ANDANDO a 1–2 km/h
        // ---------------------------------------------------------------------
        private int KpEnPrueba => _p6ValidandoAlto ? CandidatosKp[_p6Mejor] : CandidatosKp[_p6Idx];
        private int AltoEnPrueba => _p6ValidandoAlto ? Math.Min(PwmAltoMax, Trabajo.HighPwm + PwmAltoPaso) : Trabajo.HighPwm;

        private bool ProbarCandidato()
        {
            SteerCalConfig c = Trabajo.Clone();
            c.Kp = KpEnPrueba;
            c.HighPwm = AltoEnPrueba;
            if (!EscribirPlaca(c, "prueba Kp " + c.Kp + " / PWM alto " + c.HighPwm)) return false;
            ReiniciarSecuencia();
            return true;
        }

        private void TickGanancia(SteerCalEntrada e)
        {
            if (Fase != SteerCalFase.Midiendo) return;

            string b = BloqueoMotor(e, false);
            if (b != null)
            {
                bool estabaEnPrueba = MotorActivo || _sub != Sub.Centrar || _subEscalon != 0;
                PararMotor();
                if (estabaEnPrueba) ReiniciarSecuencia();   // se repite la ganancia en curso, sin regrabar
                Mensaje = b;
                MensajeEsError = false;
                return;
            }

            bool abortado;
            bool listo = PasoSecuencia(e, true, out abortado);
            if (abortado) return;

            double frac = _sub == Sub.Escalon ? (_subEscalon + Math.Min(1, (e.T - _subT0) / EscalonS)) / 2.0 : _subEscalon / 2.0;
            Progreso = Math.Min(1.0, (_p6Idx + frac) / (CandidatosKp.Length + 1.0));
            Mensaje = "Mantené apretado y seguí derecho a 1–2 km/h…";
            MensajeEsError = false;
            Medicion = "Probando ganancia " + KpEnPrueba + " (PWM alto " + AltoEnPrueba + ")"
                     + (_p6Mejor >= 0 ? " — la mejor hasta ahora: " + CandidatosKp[_p6Mejor] : "");

            if (listo) EvaluarCandidato();
        }

        private void EvaluarCandidato()
        {
            bool bueno = _rPos != null && _rNeg != null && _rPos.Bueno && _rNeg.Bueno;
            Evento("Asistente de dirección: Kp " + KpEnPrueba + " / PWM alto " + AltoEnPrueba + " → "
                   + (bueno ? "BIEN" : "NO") + " (+5°: " + DescribirEscalon(_rPos) + "; −5°: " + DescribirEscalon(_rNeg) + ")");

            if (_p6ValidandoAlto)
            {
                if (bueno) { _p6AltoNuevo = AltoEnPrueba; _p6MejorPos = _rPos; _p6MejorNeg = _rNeg; }
                FinGanancia();
                return;
            }

            if (bueno)
            {
                _p6Mejor = _p6Idx;
                _p6MejorPos = _rPos;
                _p6MejorNeg = _rNeg;
                _p6Idx++;
                if (_p6Idx < CandidatosKp.Length)
                {
                    ProbarCandidato();
                    return;
                }
            }

            if (_p6Mejor < 0)
            {
                AbortarPaso("Ni la ganancia más baja (" + CandidatosKp[0] + ") anda sin pasarse u oscilar: "
                          + "revisá el PWM mínimo y que el motor no patine.");
                return;
            }

            bool lento = _p6MejorPos != null && _p6MejorNeg != null
                && Promedio(_p6MejorPos.SubidaS, _p6MejorNeg.SubidaS) > 1.0
                && (_p6MejorPos.FraccionSaturado + _p6MejorNeg.FraccionSaturado) / 2 > 0.6
                && Trabajo.HighPwm < PwmAltoMax;
            if (lento)
            {
                _p6ValidandoAlto = true;
                ProbarCandidato();
                return;
            }
            FinGanancia();
        }

        private void FinGanancia()
        {
            PararMotor();
            int kp = CandidatosKp[_p6Mejor];
            Medicion = "Con ganancia " + kp + ": +5° " + DescribirEscalon(_p6MejorPos) + "; −5° " + DescribirEscalon(_p6MejorNeg);
            Progreso = 1;
            if (kp == Trabajo.Kp && _p6AltoNuevo == Trabajo.HighPwm)
            {
                RestaurarPlacaATrabajo("la ganancia actual es la mejor");
                Terminar("La ganancia actual (" + kp + ") ya es la mejor. No se toca.");
                return;
            }
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            Propuesta.Add(new SteerCalCambio("Ganancia (Kp)", Trabajo.Kp.ToString(CultureInfo.InvariantCulture),
                kp.ToString(CultureInfo.InvariantCulture)));
            if (_p6AltoNuevo != Trabajo.HighPwm)
                Propuesta.Add(new SteerCalCambio("PWM alto", Trabajo.HighPwm.ToString(CultureInfo.InvariantCulture),
                    _p6AltoNuevo.ToString(CultureInfo.InvariantCulture)));
            Mensaje = "La ganancia más alta que no se pasa (menos del 10 %) ni oscila es " + kp + ".";
            MensajeEsError = false;
        }

        private static string DescribirEscalon(RespuestaEscalon r)
        {
            if (r == null) return "—";
            if (!r.Llego) return "no llegó";
            return "subida " + F(r.SubidaS, 1) + " s, sobrepico " + F(r.SobrepicoPct, 0) + " %" + (r.Oscila ? ", oscila" : "");
        }

        // ---------------------------------------------------------------------
        // P7 — umbral de corte por corriente + prueba de agarrar el volante
        // ---------------------------------------------------------------------
        private void TickCorte(SteerCalEntrada e, bool motorAntes)
        {
            if (Fase != SteerCalFase.Midiendo) return;
            if (_p7Etapa == 0) TickCorteMedir(e, motorAntes);
            else TickCorteAgarre(e, motorAntes);
        }

        private void TickCorteMedir(SteerCalEntrada e, bool motorAntes)
        {
            // La placa cortó andando normal: el umbral es bajo.
            if (motorAntes && e.SwitchAbierto)
            {
                PararMotor();
                int sube = Math.Min(UmbralCorrienteMax, Math.Max(Trabajo.SensorLimit + 20,
                    (int)Math.Round(Trabajo.SensorLimit * 1.3)));
                Evento("Asistente de dirección: el corte por corriente saltó andando normal (umbral " + Trabajo.SensorLimit
                       + ", corriente vista " + _p7Max + ")");
                if (sube <= Trabajo.SensorLimit)
                {
                    AbortarPaso("El corte salta andando normal y el umbral ya está al máximo: revisá el sensor de corriente.");
                    return;
                }
                _p7Propuesto = sube;
                _p7Propuesta = 2;
                Fase = SteerCalFase.Propuesta;
                Propuesta.Clear();
                Propuesta.Add(new SteerCalCambio("Umbral de corte por corriente",
                    Trabajo.SensorLimit.ToString(CultureInfo.InvariantCulture), sube.ToString(CultureInfo.InvariantCulture)));
                Mensaje = "Cortó andando normal, sin que nadie agarre el volante: el umbral " + Trabajo.SensorLimit
                        + " es bajo. ¿Lo subimos?";
                MensajeEsError = false;
                return;
            }

            string b = BloqueoMotor(e, false);
            if (b != null)
            {
                bool estabaEnPrueba = MotorActivo || _sub != Sub.Centrar || _subEscalon != 0;
                PararMotor();
                if (estabaEnPrueba) ReiniciarSecuencia();
                Mensaje = b;
                MensajeEsError = false;
                return;
            }

            if (e.Corriente >= 0) { if (e.Corriente > _p7Max) _p7Max = e.Corriente; }
            else
            {
                _p7SinCorriente += _dtTick;
                if (_p7SinCorriente > 3)
                {
                    AbortarPaso("La placa no manda la corriente del motor (PGN 250): revisá que el corte por corriente esté prendido en la placa.");
                    return;
                }
            }

            bool abortado;
            bool ciclo = PasoSecuencia(e, true, out abortado);
            if (abortado) return;

            Mensaje = "Mantené apretado y seguí derecho a 1–2 km/h…";
            MensajeEsError = false;
            Medicion = "Corriente normal máxima: " + (_p7Max < 0 ? "—" : _p7Max.ToString(CultureInfo.InvariantCulture))
                     + " (umbral actual " + Trabajo.SensorLimit + ")";
            Progreso = Math.Min(1.0, (_p7Ciclos + (_subEscalon / 2.0)) / CiclosCorriente);

            if (!ciclo) return;
            _p7Ciclos++;
            if (_p7Ciclos < CiclosCorriente) return;

            PararMotor();
            if (_p7Max < 0)
            {
                AbortarPaso("No llegó ninguna lectura de corriente (PGN 250).");
                return;
            }
            int u = Calibracion.UmbralCorriente(_p7Max);
            Evento("Asistente de dirección: corriente normal máxima " + _p7Max + " → umbral propuesto " + u
                   + " (antes " + Trabajo.SensorLimit + ")");
            if (u > UmbralCorrienteMax)
            {
                Fallar("La corriente normal ya es muy alta (" + _p7Max + "): no queda margen para cortar. Revisá el motor.");
                return;
            }
            _p7Propuesto = u;
            _p7Propuesta = 1;
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            Propuesta.Add(new SteerCalCambio("Umbral de corte por corriente",
                Trabajo.SensorLimit.ToString(CultureInfo.InvariantCulture), u.ToString(CultureInfo.InvariantCulture)));
            Mensaje = "Andando normal el motor llega a " + _p7Max + ". Con margen, el corte va en " + u + ".";
            MensajeEsError = false;
        }

        private void TickCorteAgarre(SteerCalEntrada e, bool motorAntes)
        {
            if (motorAntes && e.SwitchAbierto)
            {
                PararMotor();
                Evento("Asistente de dirección: corte al agarrar el volante PROBADO (umbral " + Trabajo.SensorLimit + ")");
                Terminar("¡Cortó al agarrar el volante! ✔ Volvé a armar el switch de dirección (o el botón del volante).");
                return;
            }

            string b = BloqueoMotor(e, true);
            if (b != null)
            {
                PararMotor();
                Mensaje = b;
                MensajeEsError = false;
                return;
            }

            double sp = Math.Floor((e.T - _p7T0) / AgarreCambioS) % 2 == 0 ? SetpointMaxGrados : -SetpointMaxGrados;
            MoverMotor(e, sp, true);
            // Acá el operario PELEA el volante a propósito: el error que crece
            // es lo esperado, no se vigila (el escape por tope sí).
            if (RevisarCandados(e, false) == Candado.Escape) { AbortarPaso(TextoEscape(e)); return; }

            _p7Activo += _dtTick;
            Progreso = Math.Min(1.0, _p7Activo / AgarreTimeoutS);
            Mensaje = "Mantené apretado y AGARRÁ FUERTE el volante: tiene que cortar.";
            MensajeEsError = false;
            Medicion = "Corriente: " + (e.Corriente < 0 ? "—" : e.Corriente.ToString(CultureInfo.InvariantCulture))
                     + " — corta en " + Trabajo.SensorLimit;

            if (_p7Activo >= AgarreTimeoutS)
            {
                PararMotor();
                int piso = _p7Max > 0 ? _p7Max + 10 : 20;
                int baja = Math.Max(piso, (int)Math.Round(Trabajo.SensorLimit * 0.8));
                Evento("Asistente de dirección: NO cortó al agarrar el volante (umbral " + Trabajo.SensorLimit + ")");
                if (baja >= Trabajo.SensorLimit)
                {
                    Fallar("No cortó y no se puede bajar más el umbral sin que corte andando normal: revisá el sensor de corriente.");
                    return;
                }
                _p7Propuesto = baja;
                _p7Propuesta = 3;
                Fase = SteerCalFase.Propuesta;
                Propuesta.Clear();
                Propuesta.Add(new SteerCalCambio("Umbral de corte por corriente",
                    Trabajo.SensorLimit.ToString(CultureInfo.InvariantCulture), baja.ToString(CultureInfo.InvariantCulture)));
                Mensaje = "No cortó en " + F(AgarreTimeoutS, 0) + " s. ¿Bajamos el umbral y probamos de nuevo?";
            }
        }

        // ---------------------------------------------------------------------
        // P8 — ajuste fino: 3 pasadas de 100 m con el piloto
        // ---------------------------------------------------------------------
        private void TickFino(SteerCalEntrada e)
        {
            if (Fase != SteerCalFase.Midiendo) return;

            double v = Math.Abs(e.VelKmh);
            string motivo = null;
            if (!e.HayGuiaRecta) motivo = "Activá una guía RECTA (AB).";
            else if (!e.PilotoPuesto) motivo = "Enganchá el piloto sobre la guía.";
            else if (e.UTurn) motivo = "Terminá la vuelta en U.";
            else if (!e.FixValido || !e.HayVelocidad) motivo = "Sin GPS válido.";
            else if (v < FinoMinKmh || v > FinoMaxKmh) motivo = "Andá entre 6 y 10 km/h (vas a " + F(v, 1) + " km/h).";
            else if (double.IsNaN(e.ErrorLineaM)) motivo = "Sin distancia a la guía.";

            if (motivo != null)
            {
                Mensaje = motivo + (_p8Dist > 1 ? " La pasada en curso se descarta: arrancá otra." : "");
                MensajeEsError = false;
                _p8T.Clear(); _p8E.Clear(); _p8Dist = 0;
                return;
            }

            _p8T.Add(e.T);
            _p8E.Add(e.ErrorLineaM);
            _p8Dist += _distTick;
            Progreso = Math.Min(1.0, (_p8Pasadas.Count + _p8Dist / FinoPasadaM) / FinoPasadas);
            Mensaje = "Pasada " + (_p8Pasadas.Count + 1) + " de " + FinoPasadas + ": seguí con el piloto…";
            MensajeEsError = false;
            Medicion = F(_p8Dist, 0) + " de " + F(FinoPasadaM, 0) + " m — a la guía " + F(e.ErrorLineaM * 100, 1) + " cm";

            if (_p8Dist < FinoPasadaM) return;

            var a = AnalisisPasadas.Analizar(_p8T, _p8E, _p8Dist);
            _p8Pasadas.Add(a);
            _p8TodoE.AddRange(_p8E);
            Evento("Asistente de dirección: pasada " + _p8Pasadas.Count + " — RMS " + F(a.RmsM * 100, 1) + " cm, sesgo "
                   + F(a.SesgoM * 100, 1) + " cm, período " + (double.IsNaN(a.PeriodoS) ? "—" : F(a.PeriodoS, 0) + " s"));
            _p8T.Clear(); _p8E.Clear(); _p8Dist = 0;

            if (_p8Pasadas.Count < FinoPasadas)
            {
                Mensaje = "Pasada " + _p8Pasadas.Count + " lista. Hacé la siguiente (podés girar en la cabecera).";
                return;
            }
            ConcluirFino();
        }

        private void ConcluirFino()
        {
            // Combinado: sesgo/RMS/desvío sobre todas las muestras; período y
            // cruces, promedio de las pasadas (los huecos entre pasadas no cuentan).
            var total = new AnalisisPasadas();
            double s = 0, s2 = 0;
            foreach (double x in _p8TodoE) { s += x; s2 += x * x; }
            int n = Math.Max(1, _p8TodoE.Count);
            total.SesgoM = s / n;
            total.RmsM = Math.Sqrt(s2 / n);
            total.DesvioM = Math.Sqrt(Math.Max(0, s2 / n - total.SesgoM * total.SesgoM));
            double sumP = 0, sumC = 0; int nP = 0;
            foreach (var p in _p8Pasadas)
            {
                sumC += p.CrucesPor100m;
                if (!double.IsNaN(p.PeriodoS)) { sumP += p.PeriodoS; nP++; }
            }
            total.CrucesPor100m = sumC / _p8Pasadas.Count;
            total.PeriodoS = nP > 0 ? sumP / nP : double.NaN;

            Medicion = "RMS " + F(total.RmsM * 100, 1) + " cm — sesgo " + F(total.SesgoM * 100, 1) + " cm — "
                     + (double.IsNaN(total.PeriodoS) ? "sin oscilación" : "oscila cada " + F(total.PeriodoS, 0) + " s");
            Progreso = 1;

            string motivo;
            _p8Nueva = Calibracion.ProponerAjusteFino(Trabajo, total, out motivo);
            if (_p8Nueva == null)
            {
                Terminar("Anda fino (RMS " + F(total.RmsM * 100, 1) + " cm): no hace falta tocar nada.");
                return;
            }
            Fase = SteerCalFase.Propuesta;
            Propuesta.Clear();
            if (_p8Nueva.HoldLookAhead != Trabajo.HoldLookAhead)
                Propuesta.Add(new SteerCalCambio("Qué tan adelante mira", F(Trabajo.HoldLookAhead * 0.1, 1) + " s",
                    F(_p8Nueva.HoldLookAhead * 0.1, 1) + " s"));
            if (_p8Nueva.AcquireFactor != Trabajo.AcquireFactor)
                Propuesta.Add(new SteerCalCambio("Entrada a la línea", F(Trabajo.AcquireFactor * 0.01, 2),
                    F(_p8Nueva.AcquireFactor * 0.01, 2)));
            if (_p8Nueva.IntegralPp != Trabajo.IntegralPp)
                Propuesta.Add(new SteerCalCambio("Integral (PP)", Trabajo.IntegralPp.ToString(CultureInfo.InvariantCulture),
                    _p8Nueva.IntegralPp.ToString(CultureInfo.InvariantCulture)));
            Mensaje = "Propuesta: " + motivo + ".";
            MensajeEsError = false;
        }

        // ---------------------------------------------------------------------
        // Resumen
        // ---------------------------------------------------------------------
        private void ArmarResumen()
        {
            Propuesta.Clear();
            Fila("Invertir sensor (WAS)", SiNo(Original.InvertWas), SiNo(Trabajo.InvertWas));
            Fila("Invertir motor", SiNo(Original.InvertSteer), SiNo(Trabajo.InvertSteer));
            Fila("Cero del sensor (cuentas)", Original.WasOffset, Trabajo.WasOffset);
            Fila("Cuentas por grado", Original.CountsPerDegree, Trabajo.CountsPerDegree);
            Fila("Ackermann (%)", Original.Ackerman, Trabajo.Ackerman);
            Fila("PWM mínimo", Original.MinPwm, Trabajo.MinPwm);
            Fila("Ganancia (Kp)", Original.Kp, Trabajo.Kp);
            Fila("PWM alto", Original.HighPwm, Trabajo.HighPwm);
            Fila("Umbral de corte por corriente", Original.SensorLimit, Trabajo.SensorLimit);
            Fila("Qué tan adelante mira (×0,1 s)", Original.HoldLookAhead, Trabajo.HoldLookAhead);
            Fila("Entrada a la línea (×0,01)", Original.AcquireFactor, Trabajo.AcquireFactor);
            Fila("Integral (PP)", Original.IntegralPp, Trabajo.IntegralPp);
            // Hecho (no Propuesta): acá se decide con Aplicar / Cancelar, no con
            // Aceptar / Rechazar. Las filas viajan igual en Propuesta.
            Fase = SteerCalFase.Hecho;
            Mensaje = Propuesta.Count == 0
                ? "No quedó nada para cambiar. Tocá Aplicar para terminar."
                : "Revisá los cambios. Aplicar los guarda; Cancelar deja todo como estaba.";
        }

        private void Fila(string campo, int antes, int despues)
        {
            if (antes != despues)
                Propuesta.Add(new SteerCalCambio(campo, antes.ToString(CultureInfo.InvariantCulture),
                    despues.ToString(CultureInfo.InvariantCulture)));
        }

        private void Fila(string campo, string antes, string despues)
        {
            if (antes != despues) Propuesta.Add(new SteerCalCambio(campo, antes, despues));
        }

        // ---------------------------------------------------------------------
        // Motor y candados
        // ---------------------------------------------------------------------
        private enum Candado { Ok, Escape, Crece }

        private static bool Parado(SteerCalEntrada e)
        {
            return e.HayVelocidad && e.FixValido && Math.Abs(e.VelKmh) < ParadoMaxKmh;
        }

        /// <summary>Por qué NO se puede mover el motor ahora (null = se puede).</summary>
        private string BloqueoMotor(SteerCalEntrada e, bool parado)
        {
            if (!Apretado) return "Mantené apretado el botón para mover el volante.";
            if (!e.HayVelocidad) return "PilotX no informa velocidad: sin eso el motor no se mueve.";
            if (!e.FixValido) return "Sin GPS válido: sin eso el motor no se mueve.";
            if (!e.Edad253.HasValue || e.Edad253.Value > Edad253MaxS) return "No llega el ángulo de la placa (PGN 253).";
            if (e.PilotoPuesto) return "Apagá el piloto.";
            if (e.ManejoLibre) return "Apagá el manejo libre.";
            if (e.SeccionesPintando) return "Apagá las secciones: no se calibra pintando.";
            if (e.UTurn) return "Hay una vuelta en U en curso.";
            if (e.SwitchAbierto) return "El switch de dirección está abierto: armalo (o tocá el botón del volante).";
            double v = Math.Abs(e.VelKmh);
            if (parado && v >= ParadoMaxKmh) return "Pará el tractor (vas a " + F(v, 1) + " km/h).";
            if (!parado && (v < AndandoMinKmh || v > AndandoMaxKmh))
                return "Andá entre 1 y 2 km/h (vas a " + F(v, 1) + " km/h).";
            return null;
        }

        private void MoverMotor(SteerCalEntrada e, double setpoint, bool parado)
        {
            setpoint = Acotar(setpoint, SetpointMaxGrados);
            if (!MotorActivo || Math.Abs(setpoint - Setpoint) > 1e-9) ResetCrecimiento();
            MotorActivo = true;
            Setpoint = setpoint;
            VelocidadPgnKmh = parado ? VelocidadFalsaKmh : Math.Abs(e.VelKmh);
        }

        private void PararMotor()
        {
            MotorActivo = false;
            Setpoint = 0;
            VelocidadPgnKmh = 0;
            ResetCrecimiento();
        }

        private double Tope()
        {
            double t = Trabajo != null ? Trabajo.MaxSteerAngle : 0;
            return t > 0 ? t : 40;
        }

        private Candado RevisarCandados(SteerCalEntrada e, bool vigilarCrecimiento)
        {
            if (!MotorActivo) return Candado.Ok;
            if (Math.Abs(e.AnguloWas) > Tope() + MargenTopeGrados) return Candado.Escape;
            if (vigilarCrecimiento && ErrorCrece(Math.Abs(Setpoint - e.AnguloWas), e.T)) return Candado.Crece;
            return Candado.Ok;
        }

        private string TextoEscape(SteerCalEntrada e)
        {
            return "El ángulo pasó el tope (" + G(e.AnguloWas) + " con tope " + F(Tope(), 0) + "°): se cortó el motor.";
        }

        private void ResetCrecimiento()
        {
            _crecErrPrev = double.NaN;
            _crecTIni = double.NaN;
        }

        /// <summary>true si el error lleva 300 ms creciendo y creció al menos 0,5°.</summary>
        private bool ErrorCrece(double err, double t)
        {
            if (double.IsNaN(_crecErrPrev))
            {
                _crecErrPrev = err;
                _crecTPrev = t;
                return false;
            }
            if (err > _crecErrPrev + 0.05)
            {
                if (double.IsNaN(_crecTIni)) { _crecTIni = _crecTPrev; _crecErrIni = _crecErrPrev; }
            }
            else if (err < _crecErrPrev - 0.05)
            {
                _crecTIni = double.NaN;
            }
            _crecErrPrev = err;
            _crecTPrev = t;
            return !double.IsNaN(_crecTIni) && t - _crecTIni >= CrecimientoErrorS - 1e-9
                   && err - _crecErrIni >= CrecimientoErrorMinGrados;
        }

        // ---------------------------------------------------------------------
        // Placa
        // ---------------------------------------------------------------------
        /// <summary>Manda una config de prueba a la placa, respetando el tope del paso.</summary>
        private bool EscribirPlaca(SteerCalConfig cfg, string motivo)
        {
            if (cfg.MismaPlaca(EnPlaca)) { EnPlaca = cfg.Clone(); return true; }
            if (EscriturasPaso >= TopeEscrituras(Paso))
            {
                AbortarPaso("Se llegó al tope de grabaciones en la placa para este paso (" + TopeEscrituras(Paso)
                          + "): cada grabación gasta la memoria de la placa. Cancelá y volvé a empezar más tarde.");
                return false;
            }
            Grabar(cfg, motivo);
            return true;
        }

        /// <summary>Devuelve la placa a lo aceptado (Trabajo). Pasa siempre, aunque esté el tope.</summary>
        private void RestaurarPlacaATrabajo(string motivo)
        {
            if (Trabajo == null || Trabajo.MismaPlaca(EnPlaca)) return;
            Grabar(Trabajo, "vuelve a lo aceptado: " + motivo);
        }

        private void Grabar(SteerCalConfig cfg, string motivo)
        {
            string antes = Resumir(EnPlaca);
            EnPlaca = cfg.Clone();
            EscriturasPaso++;
            EscriturasTotal++;
            _pedidos.Add(SteerCalPedido.EscribirPlaca);
            Evento("Asistente de dirección: graba placa (" + motivo + ") — " + antes + " → " + Resumir(EnPlaca)
                   + " [" + EscriturasPaso + "/" + TopeEscrituras(Paso) + " en el paso, " + EscriturasTotal + " en total]");
        }

        // ---------------------------------------------------------------------
        // Cierre de paso
        // ---------------------------------------------------------------------
        private void Terminar(string texto)
        {
            PararMotor();
            Fase = SteerCalFase.Hecho;
            Propuesta.Clear();
            Mensaje = texto;
            MensajeEsError = false;
            Progreso = 1;
        }

        private void Fallar(string texto)
        {
            PararMotor();
            Fase = SteerCalFase.Error;
            Propuesta.Clear();
            Mensaje = texto;
            MensajeEsError = true;
            Evento("Asistente de dirección: '" + TituloDe(Paso) + "' — " + texto);
        }

        /// <summary>Corta el motor, devuelve la placa a lo aceptado y deja el paso en error.</summary>
        private void AbortarPaso(string texto)
        {
            PararMotor();
            Fallar(texto);
            RestaurarPlacaATrabajo("paso abortado");
        }

        // ---------------------------------------------------------------------
        // Derivadas: yaw y distancia (del rumbo y la posición del pivote)
        // ---------------------------------------------------------------------
        private void Derivadas(SteerCalEntrada e)
        {
            _distTick = 0;
            _dtTick = 0;
            if (!double.IsNaN(_tPrev) && e.T > _tPrev)
            {
                double dt = e.T - _tPrev;
                double dr = e.RumboRad - _rumboPrev;
                while (dr > Math.PI) dr -= 2 * Math.PI;
                while (dr < -Math.PI) dr += 2 * Math.PI;
                double yaw = dr / dt * 180.0 / Math.PI;
                _yawGradS = double.IsNaN(_yawGradS) ? yaw : _yawGradS * 0.6 + yaw * 0.4;
                double de = e.Este - _estePrev, dn = e.Norte - _nortePrev;
                double d = Math.Sqrt(de * de + dn * dn);
                if (d > 20) d = 0;   // salto de posición (GPS que vuelve): no es distancia andada
                _distTick = d;
                _dtTick = dt;
                _rumboAcum += dr;
                _distAcum += d;
            }
            _tPrev = e.T;
            _rumboPrev = e.RumboRad;
            _estePrev = e.Este;
            _nortePrev = e.Norte;
        }

        // ---------------------------------------------------------------------
        // Texto
        // ---------------------------------------------------------------------
        private void Evento(string s) { _eventos.Add(s); }

        private void Cambio(string campo, int antes, int despues)
        {
            if (antes != despues) Evento("Asistente de dirección: " + campo + " " + antes + " → " + despues);
        }

        private void Cambio(string campo, bool antes, bool despues)
        {
            if (antes != despues) Evento("Asistente de dirección: " + campo + " " + SiNo(antes) + " → " + SiNo(despues));
        }

        private static string SiNo(bool b) { return b ? "Sí" : "No"; }

        private static double Acotar(double v, double max) { return v > max ? max : (v < -max ? -max : v); }

        private static double Promedio(double a, double b)
        {
            if (double.IsNaN(a)) return b;
            if (double.IsNaN(b)) return a;
            return (a + b) / 2;
        }

        /// <summary>Número con coma decimal (criollo).</summary>
        public static string F(double v, int dec)
        {
            if (double.IsNaN(v)) return "—";
            return v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).Replace('.', ',');
        }

        /// <summary>Grados con signo: "+3,0°".</summary>
        public static string G(double v, int dec = 1)
        {
            if (double.IsNaN(v)) return "—";
            return (v > 0 ? "+" : "") + F(v, dec) + "°";
        }

        public static string Resumir(SteerCalConfig c)
        {
            if (c == null) return "—";
            return "Kp " + c.Kp + ", PWM " + c.MinPwm + "/" + c.HighPwm + ", cero " + c.WasOffset
                 + ", cuentas " + c.CountsPerDegree + ", Ackermann " + c.Ackerman
                 + ", inv. WAS " + SiNo(c.InvertWas) + ", inv. motor " + SiNo(c.InvertSteer)
                 + ", corte corriente " + (c.CurrentSensor ? c.SensorLimit.ToString(CultureInfo.InvariantCulture) : "no")
                 + ", mira " + F(c.HoldLookAhead * 0.1, 1) + " s, acquire " + F(c.AcquireFactor * 0.01, 2)
                 + ", integral " + c.IntegralPp;
        }
    }
}
