// ============================================================================
// AgpStepper.cs — réplica nativa del stepper [−] valor [+] de js/steps.js.
//
// QUÉ QUEDÓ NATIVO: el control táctil que usan las tabs del editor QuantiX
// (Motores, PID, Calibración) para todos los campos numéricos.
// QUÉ SIGUE EN HTML: js/steps.js, porque la PWA del celular sigue usando
// pages/quantix.html.
//
// Dos modos, igual que AGPSteps:
//   · Int → paso fijo (PWM, pulsos por vuelta, filtro, vueltas, surcos…)
//   · Pid → paso ADAPTATIVO sobre el valor actual: v<1 → 0.01, v<10 → 0.1,
//           v<100 → 1, v≥100 → 5. Así se afina Kd=0.5 y Kp=120 con el mismo
//           control sin cambiar nada.
//
// Los botones son de 44x40: en la pantalla del tractor con guante, los
// sliders no se pueden tocar. Sin atajos de teclado (regla del repo).
// ============================================================================

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PilotX.Desktop.Views.Controls;

public enum AgpStepperModo { Int, Pid }

public sealed class AgpStepper : Border
{
    private static readonly IBrush BgFila = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Borde  = new SolidColorBrush(Color.Parse("#C5CFC5"));
    private static readonly IBrush Texto  = new SolidColorBrush(Color.Parse("#101612"));
    private static readonly IBrush BgBoton = new SolidColorBrush(Color.Parse("#EEF2EE"));

    private readonly TextBlock _lbl;
    private double _valor;

    public AgpStepperModo Modo { get; set; } = AgpStepperModo.Int;
    public double Paso { get; set; } = 1;
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>Se dispara con cada toque de [−]/[+] y con SetValor(x, true).</summary>
    public event Action<double>? ValorCambiado;

    public double Valor
    {
        get => _valor;
        set => SetValor(value, false);
    }

    public AgpStepper(double valor, AgpStepperModo modo = AgpStepperModo.Int,
                      double paso = 1, double? min = null, double? max = null)
    {
        Modo = modo;
        Paso = paso;
        Min = min;
        Max = max;
        _valor = valor;

        Background = BgFila;
        BorderBrush = Borde;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(2);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var menos = Boton("−");
        menos.Click += (_, __) => Bump(-1);
        Grid.SetColumn(menos, 0);
        g.Children.Add(menos);

        _lbl = new TextBlock
        {
            Text = Formatear(_valor),
            Foreground = Texto,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 56,
            TextAlignment = TextAlignment.Center,
        };
        Grid.SetColumn(_lbl, 1);
        g.Children.Add(_lbl);

        var mas = Boton("+");
        mas.Click += (_, __) => Bump(+1);
        Grid.SetColumn(mas, 2);
        g.Children.Add(mas);

        Child = g;
    }

    private static Button Boton(string txt) => new Button
    {
        Content = txt,
        Width = 44,
        Height = 40,
        FontSize = 19,
        FontWeight = FontWeight.Bold,
        Background = BgBoton,
        Foreground = Texto,
        BorderBrush = Borde,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(0),
    };

    /// <summary>Paso vigente. En modo Pid depende del valor ACTUAL.</summary>
    public double PasoActual()
    {
        if (Modo != AgpStepperModo.Pid) return Paso <= 0 ? 1 : Paso;
        double v = Math.Abs(_valor);
        if (v < 1)   return 0.01;
        if (v < 10)  return 0.1;
        if (v < 100) return 1;
        return 5;
    }

    private void Bump(int dir)
    {
        double paso = PasoActual();
        double next = _valor + dir * paso;
        // Snap al múltiplo del paso: sin esto los toques sucesivos acumulan
        // 5.0000001 de coma flotante.
        next = Math.Round(next / paso) * paso;
        next = Math.Round(next, 6);
        SetValor(next, true);
    }

    public void SetValor(double v, bool notificar)
    {
        if (Min.HasValue && v < Min.Value) v = Min.Value;
        if (Max.HasValue && v > Max.Value) v = Max.Value;
        _valor = v;
        _lbl.Text = Formatear(v);
        if (notificar) ValorCambiado?.Invoke(v);
    }

    private string Formatear(double v)
    {
        double paso = PasoActual();
        int dec = paso >= 1 ? 0 : paso >= 0.1 ? 1 : paso >= 0.01 ? 2 : 3;
        return v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Valor redondeado a entero (los campos que el firmware
    /// consume como int: PWM, pulsos por vuelta, filtro).</summary>
    public int ValorInt => (int)Math.Round(_valor);
}
