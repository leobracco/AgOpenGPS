// GeneradorSemillas.cs — semillas de estadística CONOCIDA para emular el
// firmware vistax-node v3.1 (campo "dt" de la telemetría agp.vistax.telemetry/2).
//
// Cada surco emulado tiene su generador. Con la tasa nominal del surco
// (sem/s que salen del motor QuantiX emulado) el espaciamiento ideal es
// T = 1 / tasa. Cada semilla nueva cierra un intervalo con el que la PC
// clasifica (ISO 7256-1) contra Xref = T:
//   · con probabilidad PDoble   → intervalo T·U(0,05 ; 0,45)   (doble)
//   · con probabilidad PFalla   → intervalo T·U(1,6 ; 2,4)     (falla)
//   · si no                     → T·N(1, Cv) recortada a (0,5 ; 1,5] (simple)
// Así el %Dobles, %Fallas y el CV que calcula PilotX tienen que volver a
// lo que se configuró en la UI de BenchX (±1 punto con unos miles de semillas).
// El recorte a (0,5 ; 1,5] achica el desvío de la normal (con CV 25 % la
// recortada da ~22 %): el generador usa el σ que, recortado, da justo el CV
// pedido (SigmaParaCv). Tope físico: 28,8 % (recortada → uniforme).
//
// Igual que el firmware (Network.cpp / popIntervalos):
//   · el intervalo va en unidades de 0,1 ms (uint16); ≥ 6,5535 s = 0xFFFF
//     (hueco: parada, cabecera, primera semilla);
//   · buffer circular de 64 intervalos por cable; lo que no entra se cuenta
//     como perdido y sale en "dt_lost" con el próximo mensaje;
//   · cada mensaje lleva como mucho 24 intervalos (MAX_DT_POR_MSG);
//   · sin broker, el nodo descarta los intervalos y los cuenta perdidos.
// Clase pura (sin reloj, sin MQTT, Random con semilla): GeneradorSemillasTests.
using System;
using System.Collections.Generic;

namespace BenchX.Sim;

public sealed class GeneradorSemillas
{
    public const int DtHueco = 0xFFFF;
    public const int MaxDtPorMsg = 24;
    public const int CapacidadBuffer = 64;
    /// <summary>Por debajo de esta tasa el surco no siembra (motor parado).</summary>
    public const double TasaMinima = 0.05;

    private readonly Random _rnd;
    private readonly Queue<int> _buffer = new();
    private double _tDesdeUltima = 1000;   // la primera semilla sale como hueco
    private double _proximo = double.PositiveInfinity;
    private uint _perdidos;

    public GeneradorSemillas(int semilla) { _rnd = new Random(semilla); }

    /// <summary>Probabilidad de doble por semilla (0..1).</summary>
    public double PDoble { get; set; }
    /// <summary>Probabilidad de falla por semilla (0..1).</summary>
    public double PFalla { get; set; }
    /// <summary>CV de los espacios simples (0..1, ej. 0,18 = 18 %).</summary>
    public double Cv { get; set; }

    /// <summary>Semillas generadas desde el arranque (el "acum" del firmware).</summary>
    public long Acumulado { get; private set; }

    public int EnBuffer => _buffer.Count;

    /// <summary>
    /// Avanza <paramref name="dtSeg"/> segundos a <paramref name="semPorSeg"/>
    /// semillas por segundo nominales. Devuelve cuántas semillas cayeron.
    /// </summary>
    public int Avanzar(double dtSeg, double semPorSeg)
    {
        if (!(dtSeg > 0)) return 0;
        if (!(semPorSeg >= TasaMinima) || double.IsInfinity(semPorSeg))
        {
            // Parado: el tiempo corre y el próximo intervalo va a ser un hueco.
            _tDesdeUltima += dtSeg;
            _proximo = double.PositiveInfinity;
            return 0;
        }
        double T = 1.0 / semPorSeg;
        if (double.IsPositiveInfinity(_proximo))
            _proximo = _tDesdeUltima + SiguienteIntervalo(T);   // arranque: el hueco incluye la parada
        _tDesdeUltima += dtSeg;
        int n = 0;
        while (_tDesdeUltima >= _proximo)
        {
            Encolar(_proximo);
            _tDesdeUltima -= _proximo;
            Acumulado++;
            n++;
            _proximo = SiguienteIntervalo(T);
        }
        return n;
    }

    /// <summary>Intervalo (s) de la próxima semilla con espaciamiento ideal T.</summary>
    public double SiguienteIntervalo(double T)
    {
        double u = _rnd.NextDouble();
        double pd = Math.Clamp(PDoble, 0, 1), pf = Math.Clamp(PFalla, 0, 1 - pd);
        if (u < pd) return T * (0.05 + _rnd.NextDouble() * 0.40);
        if (u < pd + pf) return T * (1.6 + _rnd.NextDouble() * 0.8);
        double cv = Math.Max(0, Cv);
        if (cv != _cvCacheado) { _cvCacheado = cv; _sigma = SigmaParaCv(cv); }
        if (!double.IsPositiveInfinity(_sigma))
        {
            for (int i = 0; i < 50; i++)
            {
                double v = 1.0 + _sigma * Normal();
                if (v > 0.5 && v <= 1.5) return T * v;
            }
        }
        // CV al tope (o 50 rechazos seguidos): uniforme en (0,5 ; 1,5].
        return T * (1.5 - _rnd.NextDouble());
    }

    private double _cvCacheado = -1, _sigma;

    /// <summary>Máximo CV alcanzable con simples en (0,5 ; 1,5]: uniforme.</summary>
    public static readonly double CvMaximo = 0.5 / Math.Sqrt(3.0);

    /// <summary>
    /// σ de la normal N(1, σ) que, recortada a ±0,5, tiene desvío
    /// <paramref name="cv"/>. Bisección sobre el desvío de la normal truncada:
    /// σ_t² = σ²·(1 − 2a·φ(a)/(2Φ(a) − 1)), con a = 0,5/σ.
    /// </summary>
    public static double SigmaParaCv(double cv)
    {
        if (!(cv > 0)) return 0;
        if (cv >= CvMaximo * 0.98) return double.PositiveInfinity;   // uniforme
        double lo = cv, hi = 5;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (DesvioRecortado(mid) < cv) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>Desvío de N(0, σ) recortada a ±0,5.</summary>
    public static double DesvioRecortado(double sigma)
    {
        if (!(sigma > 0)) return 0;
        double a = 0.5 / sigma;
        double phi = Math.Exp(-0.5 * a * a) / Math.Sqrt(2 * Math.PI);
        double masa = Erf(a / Math.Sqrt(2));       // 2Φ(a) − 1
        if (masa < 1e-12) return 0.5 / Math.Sqrt(3.0);
        double f = 1 - 2 * a * phi / masa;
        return sigma * Math.Sqrt(Math.Max(0, f));
    }

    // Abramowitz-Stegun 7.1.26 (error < 1,5e-7): de sobra para un banco.
    private static double Erf(double x)
    {
        double t = 1 / (1 + 0.3275911 * Math.Abs(x));
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x >= 0 ? y : -y;
    }

    /// <summary>Saca hasta <paramref name="max"/> intervalos (0,1 ms) para el
    /// próximo mensaje, y los perdidos desde el último (popIntervalos).</summary>
    public int Pop(int max, List<int> destino, out uint perdidos)
    {
        int n = 0;
        while (n < max && _buffer.Count > 0) { destino.Add(_buffer.Dequeue()); n++; }
        perdidos = _perdidos;
        _perdidos = 0;
        return n;
    }

    /// <summary>MQTT caído: se tiran los intervalos y se cuentan perdidos
    /// (descartarIntervalos del firmware).</summary>
    public void Descartar()
    {
        _perdidos += (uint)_buffer.Count;
        _buffer.Clear();
    }

    /// <summary>Segundos → unidades de 0,1 ms del firmware (1..0xFFFF).</summary>
    public static int A01ms(double seg)
    {
        double u = Math.Round(seg * 10000.0);
        if (u >= DtHueco) return DtHueco;
        return u < 1 ? 1 : (int)u;
    }

    private void Encolar(double seg)
    {
        if (_buffer.Count >= CapacidadBuffer) { _perdidos++; return; }
        _buffer.Enqueue(A01ms(seg));
    }

    private double Normal()
    {
        double u1 = 1.0 - _rnd.NextDouble(), u2 = _rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
