// ============================================================================
// TecladoWindow.axaml.cs — el teclado en pantalla, en su PROPIA ventana.
//
// Antes el teclado era HTML y vivía dentro de la misma página: por más que se
// lo moviera, siempre quedaba encima de algo, y además solo servía en las
// pantallas web (en la UI nativa no había teclado). Como ventana aparte:
//
//   · no le come lugar al contenido de la página;
//   · el operario la corre a donde quiera, sobre el mapa incluso;
//   · sirve para TODOS los campos —nativos y web— porque las teclas se
//     mandan a la ventana enfocada con SendInput.
//
// La ventana no se activa nunca (WS_EX_NOACTIVATE): si robara el foco, el
// campo que se está editando lo perdería y no se escribiría nada.
// ============================================================================

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace PilotX.Desktop
{
    public partial class TecladoWindow : Window
    {
        private static TecladoWindow? _abierta;
        private bool _numerico;
        private bool _mayus;
        private Point _agarre;
        private bool _moviendo;

        private static readonly string[][] LETRAS =
        {
            new[] { "1","2","3","4","5","6","7","8","9","0" },
            new[] { "q","w","e","r","t","y","u","i","o","p" },
            new[] { "a","s","d","f","g","h","j","k","l","ñ" },
            new[] { "⇧","z","x","c","v","b","n","m",",",".","⌫" },
            new[] { "123","-","_","espacio","@",".","⏎" }
        };

        private static readonly string[][] NUMEROS =
        {
            new[] { "7","8","9","⌫" },
            new[] { "4","5","6","-" },
            new[] { "1","2","3","," },
            new[] { "ABC","0",".","⏎" }
        };

        public TecladoWindow()
        {
            InitializeComponent();
            Opened += (_, __) =>
            {
                // Recién con el handle creado se puede marcar como no activable.
                try { TecladoWin32.HacerNoActivable(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero); } catch { }
                UbicarAbajo();
            };
            var cerrar = this.FindControl<Button>("BtnCerrar");
            if (cerrar != null) cerrar.Click += (_, __) => Cerrar();
            var nums = this.FindControl<Button>("BtnNumeros");
            if (nums != null) nums.Click += (_, __) => { _numerico = !_numerico; Pintar(); };

            // La barra es el asa: arrastrar mueve la ventana.
            var barra = this.FindControl<Grid>("Barra");
            if (barra != null)
            {
                barra.PointerPressed += (s, e) =>
                {
                    _agarre = e.GetPosition(this);
                    _moviendo = true;
                };
                barra.PointerMoved += (s, e) =>
                {
                    if (!_moviendo) return;
                    var p = e.GetPosition(this);
                    Position = new PixelPoint(
                        Position.X + (int)(p.X - _agarre.X),
                        Position.Y + (int)(p.Y - _agarre.Y));
                };
                barra.PointerReleased += (s, e) => _moviendo = false;
            }
            Pintar();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>Abre el teclado (o lo trae al frente si ya estaba).</summary>
        public static void Mostrar(bool numerico, string? titulo = null)
        {
            if (_abierta == null)
            {
                _abierta = new TecladoWindow();
                _abierta.Closed += (_, __) => _abierta = null;
                _abierta._numerico = numerico;
                _abierta.Pintar();
                _abierta.Show();
            }
            else
            {
                if (_abierta._numerico != numerico) { _abierta._numerico = numerico; _abierta.Pintar(); }
                if (!_abierta.IsVisible) _abierta.Show();
            }
            var t = _abierta.FindControl<TextBlock>("Titulo");
            if (t != null) t.Text = string.IsNullOrWhiteSpace(titulo) ? "Teclado" : titulo;
        }

        public static void Ocultar()
        {
            try { _abierta?.Hide(); } catch { }
        }

        public static bool EstaAbierto => _abierta != null && _abierta.IsVisible;

        private void Cerrar() => Hide();

        // Arranca abajo y centrado, que es donde menos estorba de entrada; de
        // ahí el operario la corre a gusto.
        private void UbicarAbajo()
        {
            try
            {
                var s = Screens?.Primary?.WorkingArea;
                if (s == null) return;
                Position = new PixelPoint(
                    s.Value.X + (s.Value.Width - (int)Width) / 2,
                    s.Value.Y + s.Value.Height - (int)Height - 8);
            }
            catch { }
        }

        private void Pintar()
        {
            var cont = this.FindControl<StackPanel>("Filas");
            if (cont == null) return;
            cont.Children.Clear();
            var filas = _numerico ? NUMEROS : LETRAS;
            foreach (var fila in filas)
            {
                var f = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var k in fila) f.Children.Add(Tecla(k));
                cont.Children.Add(f);
            }
        }

        private Button Tecla(string etiqueta)
        {
            bool ancha = etiqueta == "espacio";
            bool modificador = etiqueta is "⇧" or "⌫" or "⏎" or "123" or "ABC";
            var b = new Button
            {
                Content = MostrarEtiqueta(etiqueta),
                Height = 48,
                MinWidth = ancha ? 220 : (modificador ? 74 : 56),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 18,
                Background = modificador ? Brush.Parse("#E2E7E2") : Brushes.White,
                // Foreground explícito: sin esto hereda el del tema, que es
                // claro, y las teclas salían en blanco sobre blanco.
                Foreground = Brush.Parse("#101612"),
                BorderBrush = Brush.Parse("#C5CFC5"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };
            if (etiqueta == "⏎")
            {
                b.Background = Brush.Parse("#4ABA3E");
                b.Foreground = Brushes.White;
            }
            b.Click += (_, __) => Pulsar(etiqueta);
            return b;
        }

        private string MostrarEtiqueta(string k)
        {
            if (k == "espacio") return "espacio";
            if (k.Length == 1 && _mayus && char.IsLetter(k[0])) return k.ToUpperInvariant();
            return k;
        }

        private void Pulsar(string k)
        {
            switch (k)
            {
                case "⌫": TecladoWin32.EscribirTeclaVirtual(TecladoWin32.VK_BACK); return;
                case "⏎": TecladoWin32.EscribirTeclaVirtual(TecladoWin32.VK_RETURN); return;
                case "⇧": _mayus = !_mayus; Pintar(); return;
                case "123": _numerico = true; Pintar(); return;
                case "ABC": _numerico = false; Pintar(); return;
                case "espacio": TecladoWin32.EscribirTexto(" "); return;
            }
            var texto = (_mayus && k.Length == 1 && char.IsLetter(k[0])) ? k.ToUpperInvariant() : k;
            TecladoWin32.EscribirTexto(texto);
            // Shift es de un solo uso, como en el teclado del celular.
            if (_mayus) { _mayus = false; Pintar(); }
        }
    }
}
