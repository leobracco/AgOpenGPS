// ============================================================================
// ErrorDialog.cs — el cartel de error que ve el operario en la cabina.
//
// Reemplaza al diálogo de Windows ("dejó de funcionar / ver detalles /
// continuar / salir"), que en una pantalla táctil en el tractor no le sirve a
// nadie: no dice qué pasó, no se puede copiar, y el operario termina cerrando
// y perdiendo la jornada.
//
// Criterio de diseño:
//   · El CÓDIGO va grande y primero. Es lo único que el operario nos va a
//     poder pasar por teléfono, y con eso soporte ya sabe por dónde arrancar.
//   · Abajo, en criollo, qué conviene hacer.
//   · El detalle técnico existe pero PLEGADO: nunca es lo primero que se ve.
//     Está para copiarlo, no para leerlo en el tractor.
//   · Botones grandes: se usa con guantes y en movimiento.
// ============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PilotX.Desktop.Views
{
    public static class ErrorDialog
    {
        /// <summary>
        /// Muestra el cartel. <paramref name="fatal"/> cambia el texto del
        /// botón: si el programa no puede seguir, no tiene sentido ofrecer
        /// "Continuar" y hacerle creer que se arregló.
        /// </summary>
        public static Window Crear(string codigo, string amigable, string tecnico,
                                   string rutaLog, bool fatal)
        {
            var titulo = new TextBlock
            {
                Text = codigo ?? "AGP-SYS-009",
                FontSize = 34,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#ED4848")),
                TextWrapping = TextWrapping.Wrap,
            };

            var texto = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(amigable) ? "Algo salió mal." : amigable,
                FontSize = 17,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            };

            var donde = new TextBlock
            {
                Text = "El detalle quedó guardado en:\n" + (rutaLog ?? "(sin log)"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#535E54")),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0),
            };

            // Plegado a propósito: el operario no tiene que leer esto.
            var detalle = new Expander
            {
                Header = "Detalle técnico",
                Margin = new Thickness(0, 12, 0, 0),
                Content = new SelectableTextBlock
                {
                    Text = tecnico ?? "",
                    FontFamily = new FontFamily("Consolas, monospace"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            };

            var boton = new Button
            {
                Content = fatal ? "Cerrar PilotX" : "Continuar",
                FontSize = 18,
                Padding = new Thickness(28, 12, 28, 12),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
                Background = new SolidColorBrush(Color.Parse("#4ABA3E")),
                Foreground = Brushes.White,
            };

            var panel = new StackPanel { Margin = new Thickness(26) };
            panel.Children.Add(titulo);
            panel.Children.Add(texto);
            panel.Children.Add(donde);
            panel.Children.Add(detalle);
            panel.Children.Add(boton);

            var win = new Window
            {
                Title = fatal ? "PilotX no puede continuar" : "PilotX — aviso",
                Width = 620,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.Parse("#F5F7F4")),
                Content = new ScrollViewer { Content = panel },
                Topmost = true,   // si tapa algo, que sea esto y no al revés
            };

            boton.Click += (_, _) => win.Close();
            return win;
        }
    }
}
