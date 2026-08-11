using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace BenchX;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.ReinicioPedido += Reiniciar;
        Closing += (_, _) => _vm.Cerrar();
    }

    private void CeroVelocidad_Click(object? s, RoutedEventArgs e) => _vm.CeroVelocidad();
    private void CeroAngulo_Click(object? s, RoutedEventArgs e) => _vm.CeroAngulo();
    private void CeroRoll_Click(object? s, RoutedEventArgs e) => _vm.CeroRoll();
    private void GuardarPosicion_Click(object? s, RoutedEventArgs e) => _vm.GuardarPosicion();
    private void BotonDireccion_Click(object? s, RoutedEventArgs e) => _vm.BotonDireccionRemoto();

    // PGN 201: la config ya quedó guardada; relanzar el proceso con la subred nueva.
    private void Reiniciar(string mensaje)
    {
        _vm.Cerrar();
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Close();
    }
}
