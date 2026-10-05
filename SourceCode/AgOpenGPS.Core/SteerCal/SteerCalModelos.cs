// ============================================================================
// SteerCalModelos.cs — tipos del ASISTENTE DE CALIBRACIÓN DE LA DIRECCIÓN.
//
// Puro a propósito: sin Settings, sin Log, sin host. Lo usa SteerCalWizard
// (máquina de estados) y lo linkean los tests de BenchX para correr el
// asistente contra el actuador simulado. C# 7.3 (AgOpenGPS.Core compila net48).
// ============================================================================

#if NETCOREAPP
#nullable disable
#endif

using System;

namespace AgOpenGPS.SteerCal
{
    /// <summary>
    /// Lo que el asistente puede tocar de la config de dirección. Los campos
    /// "de placa" viajan en los PGN 252/251 (cada envío GRABA la EEPROM de la
    /// AiO: se cuentan); los de PilotX (lookahead/integral/acquire) se quedan
    /// en la PC. Mismas unidades que el wire de /api/steer/config: lookahead
    /// y multiplicador ×10, acquire ×100, integral ×100.
    /// </summary>
    public sealed class SteerCalConfig
    {
        // ---- placa (PGN 252 / 251) ----
        public int Kp;
        public int MinPwm;
        public int HighPwm;
        public int WasOffset;
        public int CountsPerDegree;
        public int Ackerman;
        public bool InvertWas;
        public bool InvertSteer;
        public bool CurrentSensor;
        public int SensorLimit;

        // ---- PilotX (solo lectura para el asistente salvo el ajuste fino) ----
        public double MaxSteerAngle;
        public double WheelbaseM;
        public bool StanleyUsed;
        public int HoldLookAhead;
        public int LookAheadMult;
        public int AcquireFactor;
        public int IntegralPp;

        public SteerCalConfig Clone() { return (SteerCalConfig)MemberwiseClone(); }

        /// <summary>true si lo que viaja a la placa es idéntico.</summary>
        public bool MismaPlaca(SteerCalConfig o)
        {
            if (o == null) return false;
            return Kp == o.Kp && MinPwm == o.MinPwm && HighPwm == o.HighPwm
                && WasOffset == o.WasOffset && CountsPerDegree == o.CountsPerDegree
                && Ackerman == o.Ackerman && InvertWas == o.InvertWas
                && InvertSteer == o.InvertSteer && CurrentSensor == o.CurrentSensor
                && SensorLimit == o.SensorLimit;
        }

        /// <summary>true si no cambió nada de lo que el asistente toca.</summary>
        public bool MismoTodo(SteerCalConfig o)
        {
            return MismaPlaca(o) && HoldLookAhead == o.HoldLookAhead
                && LookAheadMult == o.LookAheadMult && AcquireFactor == o.AcquireFactor
                && IntegralPp == o.IntegralPp;
        }
    }

    /// <summary>Una foto de la máquina, en cada tick (≈10 Hz, al ritmo del GPS).</summary>
    public sealed class SteerCalEntrada
    {
        /// <summary>Segundos, reloj monotónico del host (el mismo que el latido).</summary>
        public double T;
        /// <summary>Segundos desde el último PGN 253 del módulo. null = nunca llegó.</summary>
        public double? Edad253;
        /// <summary>Ángulo real de la rueda que manda la placa (253), en grados.</summary>
        public double AnguloWas;
        /// <summary>PWM que informa la placa (253).</summary>
        public int Pwm;
        /// <summary>Corriente del motor (PGN 250, 0..255). −1 = no llega.</summary>
        public int Corriente = -1;
        /// <summary>Velocidad GPS en km/h (puede venir con signo).</summary>
        public double VelKmh;
        /// <summary>false = el host no sabe la velocidad: todo lo que mueve el motor falla cerrado.</summary>
        public bool HayVelocidad = true;
        public bool FixValido;
        /// <summary>Rumbo del vehículo en radianes (horario, como fixHeading).</summary>
        public double RumboRad;
        /// <summary>Posición del pivote (eje trasero) en metros.</summary>
        public double Este, Norte;
        public bool SeccionesPintando;
        public bool UTurn;
        /// <summary>Switch de dirección de la placa abierto (o cortado por corriente).</summary>
        public bool SwitchAbierto;
        public bool PilotoPuesto;
        public bool ManejoLibre;
        /// <summary>Distancia a la guía en metros (con signo). NaN = sin guía.</summary>
        public double ErrorLineaM = double.NaN;
        public bool HayGuiaRecta;
    }

    public enum SteerCalPaso
    {
        Inactivo = 0,
        Chequeo = 1,        // P0
        SentidoWas = 2,     // P1
        SentidoMotor = 3,   // P2
        CeroWas = 4,        // P3
        CuentasAckermann = 5, // P4
        PwmMinimo = 6,      // P5
        Ganancia = 7,       // P6
        CorteCorriente = 8, // P7
        AjusteFino = 9,     // P8
        Resumen = 10,
        Aplicado = 11,
        Cancelado = 12,
    }

    public enum SteerCalFase
    {
        /// <summary>Explicando qué hacer; espera "Empezar".</summary>
        Instrucciones,
        /// <summary>Midiendo (puede necesitar el hombre muerto).</summary>
        Midiendo,
        /// <summary>Hay una propuesta: el operario acepta o rechaza.</summary>
        Propuesta,
        /// <summary>Paso terminado: "Siguiente".</summary>
        Hecho,
        /// <summary>El paso falló o se abortó: "Repetir" o "Cancelar".</summary>
        Error,
    }

    /// <summary>Lo que el asistente le pide al host que haga con la placa/los settings.</summary>
    public enum SteerCalPedido
    {
        /// <summary>Mandar SteerCalWizard.EnPlaca a la placa (en memoria, sin persistir).</summary>
        EscribirPlaca,
        /// <summary>Volver la placa y los settings a SteerCalWizard.Original (sin persistir).</summary>
        Restaurar,
        /// <summary>Persistir SteerCalWizard.Trabajo (Guardar normal: settings + PGN).</summary>
        AplicarFinal,
        /// <summary>Persistir SteerCalWizard.Original (deshacer después de aplicar).</summary>
        Deshacer,
    }

    /// <summary>Una fila "antes → después" para mostrarle al operario.</summary>
    public sealed class SteerCalCambio
    {
        public string Campo;
        public string Antes;
        public string Despues;

        public SteerCalCambio(string campo, string antes, string despues)
        {
            Campo = campo; Antes = antes; Despues = despues;
        }
    }

    /// <summary>Una línea del chequeo inicial.</summary>
    public sealed class SteerCalChequeo
    {
        public string Texto;
        public bool Ok;

        public SteerCalChequeo(string texto, bool ok) { Texto = texto; Ok = ok; }
    }
}
