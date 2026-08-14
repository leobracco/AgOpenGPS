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
        private PixelPoint _agarre;
        private bool _moviendo;
        // Ventana que estaba adelante cuando se abrió el teclado: es la que
        // tiene el campo. Se la trae de vuelta antes de cada tecla.
        private static IntPtr _objetivo;

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
                // Recién con el handle creado se puede marcar como no activable
                // (Win32: WS_EX_NOACTIVATE + MA_NOACTIVATE; X11: input hint
                // ICCCM en False — cada OS por su dispatcher).
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                TecladoNativo.HacerNoActivable(hwnd);
                UbicarAbajo();
            };
            var cerrar = this.FindControl<Button>("BtnCerrar");
            if (cerrar != null) cerrar.Click += (_, __) => Cerrar();
            var nums = this.FindControl<Button>("BtnNumeros");
            if (nums != null) nums.Click += (_, __) => { _numerico = !_numerico; Pintar(); };

            // La barra es el asa. Se mueve con coordenadas de PANTALLA: con
            // GetPosition(this) la referencia se mueve junto con la ventana y
            // el arrastre se peleaba consigo mismo (la ventana no iba a ningún
            // lado). BeginMoveDrag tampoco sirve acá: la ventana está marcada
            // como no activable y el gestor de ventanas no le arranca el drag.
            var barra = this.FindControl<Grid>("Barra");
            if (barra != null)
            {
                barra.PointerPressed += (s, e) =>
                {
                    var p = e.GetPosition(this);
                    _agarre = new PixelPoint((int)p.X, (int)p.Y);
                    _moviendo = true;
                    e.Pointer.Capture(barra);
                };
                barra.PointerMoved += (s, e) =>
                {
                    if (!_moviendo) return;
                    // OJO: hay que trabajar en coordenadas de PANTALLA. Con
                    // GetPosition(this) el marco de referencia se mueve junto con
                    // la ventana, así que el desplazamiento daba siempre ~0 y la
                    // ventana no se movía nunca.
                    var enPantalla = this.PointToScreen(e.GetPosition(this));
                    Position = new PixelPoint(enPantalla.X - _agarre.X, enPantalla.Y - _agarre.Y);
                };
                barra.PointerReleased += (s, e) =>
                {
                    _moviendo = false;
                    e.Pointer.Capture(null);
                };
            }
            Pintar();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>Abre el teclado (o lo trae al frente si ya estaba).</summary>
        public static void Mostrar(bool numerico, string? titulo = null)
        {
            // Antes de mostrar nada: la ventana de adelante ahora es la que
            // tiene el campo enfocado. Si se capturara después, podría ser ya
            // la del propio teclado. (Windows: GetForegroundWindow; Linux:
            // xdotool getactivewindow — mismo contrato.)
            var frente = TecladoNativo.VentanaAlFrente();
            if (frente != IntPtr.Zero && (_abierta == null || frente != _abierta.TryGetPlatformHandle()?.Handle))
                _objetivo = frente;

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
                // Sin Focusable=false, tocar una tecla la enfoca — y para
                // enfocarse Avalonia activa su ventana, que es exactamente lo
                // que le robaba el foco al campo que se estaba editando.
                Focusable = false,
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
            // La tecla viaja por DOS caminos, a propósito:
            //  · publicada en el engine → la página la aplica sobre el campo que
            //    tenía, sin depender de quién tenga el foco de Windows (es lo
            //    único que aguanta: el WebView pierde el foco interno al tocar
            //    otra ventana);
            //  · SendInput → para los campos NATIVOS de la pantalla, que no
            //    pasan por ninguna página.
            Publicar(k);

            // Estado local del teclado (mayús / layout): corre en cualquier OS.
            switch (k)
            {
                case "⇧": _mayus = !_mayus; Pintar(); return;
                case "123": _numerico = true; Pintar(); return;
                case "ABC": _numerico = false; Pintar(); return;
            }

            // Camino nativo (SendInput en Windows, xdotool/XTEST en Linux):
            // escribe en los campos de la UI nativa, que no pasan por página.
            TecladoNativo.DevolverFoco(_objetivo);
            switch (k)
            {
                case "⌫": TecladoNativo.Backspace(_objetivo); return;
                case "⏎": TecladoNativo.Enter(_objetivo); return;
                case "espacio": TecladoNativo.EscribirTexto(_objetivo, " "); return;
            }
            var texto = (_mayus && k.Length == 1 && char.IsLetter(k[0])) ? k.ToUpperInvariant() : k;
            TecladoNativo.EscribirTexto(_objetivo, texto);
            if (_mayus) { _mayus = false; Pintar(); }
        }

        // Los cambios de layout y de mayúsculas son cosa de esta ventana: no se
        // publican, si no la página intentaría escribir '123' en el campo.
        private static readonly System.Net.Http.HttpClient _http =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        private void Publicar(string k)
        {
            if (k is "⇧" or "123" or "ABC") return;
            string tecla = k switch
            {
                "⌫" => "back",
                "⏎" => "enter",
                "espacio" => " ",
                _ => (_mayus && k.Length == 1 && char.IsLetter(k[0])) ? k.ToUpperInvariant() : k
            };
            try
            {
                var url = App.TargetUrl.TrimEnd('/') + "/api/teclado/tecla";
                var cuerpo = new System.Net.Http.StringContent(
                    "{\"tecla\":" + System.Text.Json.JsonSerializer.Serialize(tecla) + "}",
                    System.Text.Encoding.UTF8, "application/json");
                _ = _http.PostAsync(url, cuerpo);   // fire and forget: no bloquear el toque
            }
            catch { }
        }
    }
}
