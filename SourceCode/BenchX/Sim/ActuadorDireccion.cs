using System;
using System.Collections.Generic;

namespace BenchX.Sim;

// Dirección "de verdad" para el modo Física realista: la ECU AiO (Keya.hex)
// cerrando el lazo contra el WAS analógico y el motor Keya moviendo la rueda.
//
// Lazo de la ECU — port de AIO_v4 Keya CANBUS (solo referencia, no se toca):
//   · Autosteer.ino ~481-487: corte si el WAS lee fuera de ±50° o > 25 km/h.
//   · Autosteer.ino ~509-513: error = actual − setpoint; < 0,2 km/h → error 0.
//   · AutosteerPID.ino: P puro, tope lowPWM..highPWM en la banda de 1,5°,
//     + minPWM para vencer la fricción, MotorDriveDirection invierte.
//   · Autosteer.ino ~386-397: lectura de corriente filtrada 0,7/0,3 y corte
//     (steerSwitch = 1) si supera PulseCountMax con "sensor de corriente" on.
// Convención del firmware: PWM positivo lleva la rueda hacia la IZQUIERDA
// (baja el ángulo), porque el error es actual − setpoint.
//
// La ECU corre a 100 Hz (paso de 10 ms); el tick de BenchX es de 100 ms, así
// que Avanzar() hace 10 sub-pasos.
//
// Motor (lo físico, oculto para PilotX): no se mueve hasta superar un PWM
// mínimo REAL (fricción/estática), después gira en °/s proporcional al PWM
// hasta un tope, con retardo puro + primer orden.
public sealed class ActuadorDireccion
{
    public const double PasoEcuS = 0.01;
    public const double LimiteWasGrados = 50;    // inputWAS[0] / inputWAS[20]
    public const double VelocidadCorteKmh = 25;
    public const double VelocidadMinimaKmh = 0.2;
    public const double BandaLowHighGrados = 1.5; // LOW_HIGH_DEGREES

    public SensorWas Was { get; }

    // --- motor físico (parámetros "ocultos" del banco) ---
    public double PwmMinimoReal = 22;           // por debajo, el motor no vence la fricción
    public double VelocidadMaxGradosS = 35;     // °/s de rueda con PWM 255
    public double RetardoMs = 50;               // retardo puro CAN + motor
    public double ConstanteTiempoS = 0.08;      // inercia (primer orden)
    public double TopeMecanicoGrados = 45;      // topes de la dirección
    public bool MotorInvertido;                 // cableado/montaje al revés

    // --- corriente ---
    public double CorrientePorPwm = 0.1;        // lectura de la ECU por punto de PWM aplicado (esfuerzo)
    public double PicoCorriente = 150;          // suma cuando el operario agarra el volante
    public bool AgarrarVolante;                 // el operario frena el volante con la mano

    // --- estado ---
    public double AnguloFisico { get; private set; }   // rueda real (lo que usa la cinemática)
    public double AnguloWas { get; private set; }      // lo que lee la ECU (viaja en el PGN 253)
    public int PwmDrive { get; private set; }          // salida del lazo, con signo
    public int PwmDisplay => Math.Min(255, Math.Abs(PwmDrive));
    public int PwmAplicado { get; private set; }       // lo que llega al motor después del retardo
    public double VelocidadGradosS { get; private set; }
    public double LecturaCorriente { get; private set; } // 0..255, lo que compara la ECU
    public string? UltimoCorte { get; private set; }
    public int Cortes { get; private set; }

    private readonly Queue<int> _cola = new();

    public ActuadorDireccion(SensorWas? was = null) => Was = was ?? new SensorWas();

    // La ECU maneja el motor solo con el piloto puesto desde PilotX y el
    // switch de dirección abajo (watchdog del 254 en Autosteer.ino ~620).
    public static bool Enganchado(PgnProcessor p) => (p.GuidanceStatus & 1) != 0 && p.SteerSwitch == 0;

    // Volante movido a mano (slider) con el piloto suelto.
    public void PonerAnguloManual(double grados)
    {
        AnguloFisico = Math.Clamp(grados, -TopeMecanicoGrados, TopeMecanicoGrados);
        VelocidadGradosS = 0;
    }

    public void Reiniciar(double anguloFisico = 0)
    {
        PonerAnguloManual(anguloFisico);
        _cola.Clear();
        PwmDrive = 0; PwmAplicado = 0; LecturaCorriente = 0; UltimoCorte = null; Cortes = 0;
    }

    // controlarMotor = false: motor real en el banco (Motor apagado en BenchX);
    // el WAS y los cortes siguen, la rueda queda donde la puso el slider.
    public void Avanzar(double dt, PgnProcessor p, bool controlarMotor = true)
    {
        int n = Math.Max(1, (int)Math.Round(dt / PasoEcuS));
        double h = dt / n;
        for (int i = 0; i < n; i++) PasoEcu(h, p, controlarMotor);
    }

    private void PasoEcu(double h, PgnProcessor p, bool controlarMotor)
    {
        AnguloWas = SensorWas.AnguloEcu(Was.LeerCrudo(AnguloFisico), p);
        double vel = p.GpsSpeedPilotX;

        if (AnguloWas < -LimiteWasGrados || AnguloWas > LimiteWasGrados)
            Cortar(p, "WAS fuera de ±50°");
        else if (vel > VelocidadCorteKmh)
            Cortar(p, "más de 25 km/h");
        if (p.CurrentSensor != 0 && LecturaCorriente >= p.PulseCountMax)
            Cortar(p, "corriente del motor (volante agarrado)");

        PwmDrive = controlarMotor && Enganchado(p)
            ? CalcularPwm(AnguloWas, p.SteerAngleSetPoint, vel, p)
            : 0;

        if (controlarMotor) AvanzarMotor(h, PwmDrive);
        else { _cola.Clear(); PwmAplicado = 0; VelocidadGradosS = 0; }

        // Corriente: esfuerzo del motor + pico si lo están frenando a mano.
        // Sin PWM el motor no empuja: agarrar el volante no genera corriente.
        double i = CorrientePorPwm * Math.Abs(PwmAplicado)
                   + (AgarrarVolante && PwmAplicado != 0 ? PicoCorriente : 0);
        LecturaCorriente = Math.Min(255, LecturaCorriente * 0.7 + i * 0.3);
    }

    // calcSteeringPID() de AutosteerPID.ino, tal cual (truncamientos int16 incluidos).
    public static int CalcularPwm(double anguloWas, double setPoint, double velKmh, PgnProcessor p)
    {
        double error = anguloWas - setPoint;
        if (velKmh < VelocidadMinimaKmh) error = 0;

        int pwm = (int)(p.Kp * error);
        double errorAbs = Math.Abs(error);
        double low = p.LowPwm, high = p.HighPwm;
        int newMax = errorAbs < BandaLowHighGrados
            ? (int)(errorAbs * ((high - low) / BandaLowHighGrados) + low)
            : (int)high;

        if (pwm < 0) pwm -= p.MinPwm;
        else if (pwm > 0) pwm += p.MinPwm;

        if (pwm > newMax) pwm = newMax;
        if (pwm < -newMax) pwm = -newMax;

        if (p.MotorDir != 0) pwm = -pwm;
        return pwm;
    }

    // Motor solo, con un PWM dado (público para probarlo aislado).
    public void AvanzarMotor(double h, int pwm)
    {
        int pasosRetardo = Math.Max(0, (int)Math.Round(RetardoMs / 1000.0 / PasoEcuS));
        _cola.Enqueue(pwm);
        while (_cola.Count > pasosRetardo + 1) _cola.Dequeue();
        PwmAplicado = _cola.Count > pasosRetardo ? _cola.Peek() : 0;

        double objetivo = 0;
        int mag = Math.Abs(PwmAplicado);
        if (mag > PwmMinimoReal)
        {
            double rango = Math.Max(1, 255 - PwmMinimoReal);
            objetivo = Math.Min(VelocidadMaxGradosS, (mag - PwmMinimoReal) / rango * VelocidadMaxGradosS);
            objetivo *= -Math.Sign(PwmAplicado);      // PWM + → rueda a la izquierda
            if (MotorInvertido) objetivo = -objetivo;
        }

        double alfa = ConstanteTiempoS <= h ? 1.0 : h / ConstanteTiempoS;
        VelocidadGradosS += (objetivo - VelocidadGradosS) * alfa;
        if (AgarrarVolante) VelocidadGradosS = 0;   // el operario lo frena

        AnguloFisico += VelocidadGradosS * h;
        if (AnguloFisico > TopeMecanicoGrados) { AnguloFisico = TopeMecanicoGrados; VelocidadGradosS = 0; }
        if (AnguloFisico < -TopeMecanicoGrados) { AnguloFisico = -TopeMecanicoGrados; VelocidadGradosS = 0; }
    }

    private void Cortar(PgnProcessor p, string motivo)
    {
        if (p.SteerSwitch != 0) return;   // ya estaba suelto
        p.SteerSwitch = 1;                // como el firmware: steerSwitch = 1 → PilotX desengancha
        UltimoCorte = motivo;
        Cortes++;
    }
}
