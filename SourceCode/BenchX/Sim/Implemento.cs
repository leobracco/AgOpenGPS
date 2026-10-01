using System;

namespace BenchX.Sim;

public enum TipoImplemento { Ninguno, Arrastre, TresPuntos }

// Implemento detrás del tractor simulado, con el MISMO modelo de enganche que
// PilotX (AgOpenGPS.Core/Classes/CPositionUpdater.cs CalculatePositionHeading,
// rama tractor no articulado y sin tolva TBT):
//   · enganche = pivote (eje trasero) + EngancheM sobre el rumbo (negativo = atrás),
//   · arrastre: el pivote del implemento "sigue al líder": apunta al enganche
//     y se queda a LargoBarraM de él (si se "acuchilla" > 1,9 rad o en los
//     primeros 50 pasos, se resetea derecho atrás del tractor),
//   · 3 puntos: rígido, en el enganche con el rumbo del tractor.
// Coordenadas locales en metros (este, norte), rumbo en radianes tipo brújula.
//
// La deriva lateral (ladera) es lo que PilotX NO modela: el implemento se
// corre de costado. En arrastre es un empuje por metro avanzado calibrado
// para que en recta el desvío estacionario sea DerivaLateralM; en 3 puntos
// es el juego de los brazos, un corrimiento fijo. Positivo = a la derecha.
public sealed class Implemento
{
    public TipoImplemento Tipo = TipoImplemento.Ninguno;
    public double EngancheM = -1;     // setVehicle_hitchLength (negativo = atrás del eje trasero)
    public double LargoBarraM = 6;    // |setTool_toolTrailingHitchLength|: enganche → pivote del implemento
    public double DerivaLateralM;     // ladera: + derecha, − izquierda

    public double EngancheEste { get; private set; }
    public double EngancheNorte { get; private set; }
    public double Este { get; private set; }     // centro del implemento
    public double Norte { get; private set; }
    public double RumboRad { get; private set; }

    private int _contador;   // startCounter de PilotX

    public void Reiniciar() => _contador = 0;

    // pasoM = metros recorridos en este tick (el distanceCurrentStepFix de PilotX).
    public void Actualizar(double pivotEste, double pivotNorte, double rumboTractor, double pasoM)
    {
        EngancheEste = pivotEste + Math.Sin(rumboTractor) * EngancheM;
        EngancheNorte = pivotNorte + Math.Cos(rumboTractor) * EngancheM;

        if (Tipo == TipoImplemento.TresPuntos)
        {
            RumboRad = rumboTractor;
            Este = EngancheEste + Math.Cos(rumboTractor) * DerivaLateralM;
            Norte = EngancheNorte - Math.Sin(rumboTractor) * DerivaLateralM;
            _contador++;
            return;
        }
        if (Tipo != TipoImplemento.Arrastre)
        {
            RumboRad = rumboTractor; Este = EngancheEste; Norte = EngancheNorte;
            return;
        }

        double largo = -Math.Abs(LargoBarraM);   // trailingHitchLength de PilotX (negativo)
        double rumbo = RumboRad;
        if (pasoM != 0)
        {
            rumbo = Math.Atan2(EngancheEste - Este, EngancheNorte - Norte);
            if (rumbo < 0) rumbo += 2 * Math.PI;
        }

        double over = Math.Abs(Math.PI - Math.Abs(Math.Abs(rumbo - rumboTractor) - Math.PI));
        if (over < 1.9 && _contador > 50)
        {
            Este = EngancheEste + Math.Sin(rumbo) * largo;
            Norte = EngancheNorte + Math.Cos(rumbo) * largo;
        }
        else
        {
            rumbo = rumboTractor;
            Este = EngancheEste + Math.Sin(rumbo) * largo;
            Norte = EngancheNorte + Math.Cos(rumbo) * largo;
        }
        RumboRad = rumbo;

        // Ladera: empuje lateral proporcional al avance. Con paso p y barra L
        // el seguidor recupera p/(L+p) del desvío por paso; empujando
        // d·p/(L+p) el estacionario en recta es exactamente d.
        if (pasoM != 0 && DerivaLateralM != 0 && _contador > 50)
        {
            double p = Math.Abs(pasoM), l = Math.Abs(largo);
            double empuje = DerivaLateralM * p / (l + p);
            Este += Math.Cos(rumbo) * empuje;
            Norte -= Math.Sin(rumbo) * empuje;
        }
        _contador++;
    }

    // Desvío lateral con signo de un punto respecto de una recta (A, rumbo):
    // + a la derecha de la recta mirando en el sentido del rumbo.
    public static double DesvioLateral(double este, double norte, double aEste, double aNorte, double rumboLinea) =>
        (este - aEste) * Math.Cos(rumboLinea) - (norte - aNorte) * Math.Sin(rumboLinea);
}
