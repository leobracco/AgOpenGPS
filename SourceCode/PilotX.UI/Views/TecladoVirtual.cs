// ============================================================================
// TecladoVirtual.cs — el teclado en pantalla EMBEBIDO en el shell nativo.
//
// Reemplaza a la ventana flotante (TecladoWindow) para los campos NATIVOS de
// Avalonia. Por qué embebido y no ventana aparte:
//
//   · aparece AL INSTANTE con el foco (GotFocus directo, sin el viaje
//     POST /api/teclado/abrir → poller 400 ms → ventana), que era la mitad
//     de la sensación de "teclado malo";
//   · no hay gimnasia de foco: el control vive en la misma ventana que el
//     campo, los botones son Focusable=false y el TextBox nunca pierde el
//     foco — sin WS_EX_NOACTIVATE, sin SendInput, sin devolver foco;
//   · no flota ni se corre de lugar: anclado abajo-centro, siempre igual.
//
// Las teclas se escriben DIRECTO en el TextBox enfocado (in-process), con la
// misma inserción determinista sobre .Text + caret que ya probamos en
// TecladoWindow (respeta selección y PasswordChar).
//
// La ventana flotante SIGUE EXISTIENDO para las páginas del Hub (WebView),
// que no tienen TextBox nativo: TecladoWindow.Mostrar consulta ActivoEnShell
// y no se abre si este teclado ya está en pantalla — sin eso aparecían los
// dos teclados a la vez (los paneles nativos siguen posteando
// /api/teclado/abrir y el poller reaccionaba igual).
//
// Se auto-engancha al TopLevel al entrar al árbol visual: MainWindow solo lo
// declara en el XAML, sin código de wiring.
// ============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PilotX.Desktop.Views
{
    public sealed class TecladoVirtual : Border
    {
        // Paleta oficial (clara/suave/gris/funcional).
        private static readonly IBrush BgCard    = new SolidColorBrush(Color.Parse("#F5F7F4"));
        private static readonly IBrush Borde     = new SolidColorBrush(Color.Parse("#C5CFC5"));
        private static readonly IBrush Texto     = new SolidColorBrush(Color.Parse("#101612"));
        private static readonly IBrush BgTecla   = Brushes.White;
        private static readonly IBrush BgModif   = new SolidColorBrush(Color.Parse("#E2E7E2"));
        private static readonly IBrush BgAcento  = new SolidColorBrush(Color.Parse("#4ABA3E"));
        private static readonly IBrush BgShiftOn = new SolidColorBrush(Color.Parse("#1E5E1A"));

        // Cuántos teclados embebidos están visibles ahora (en la práctica, 0 o
        // 1). Lo consulta TecladoWindow.Mostrar para no abrir la ventana
        // flotante encima.
        private static int _visibles;
        public static bool ActivoEnShell => _visibles > 0;

        // El campo al que van las teclas: el TextBox que disparó el GotFocus.
        // WeakReference para no retener paneles cerrados.
        private WeakReference<TextBox>? _campo;
        private bool _mayus;
        private readonly StackPanel _filasQwerty;
        private readonly DispatcherTimer _timerOcultar;
        private Button? _btnShift;

        public TecladoVirtual()
        {
            Background = BgCard;
            BorderBrush = Borde;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(12);
            Padding = new Thickness(10);
            IsVisible = false;
            Focusable = false;
            // Sombra suave para despegarlo del mapa/panel de atrás.
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetY = 4, Blur = 18, Color = Color.Parse("#33101612")
            });

            // Gracia antes de ocultar: al saltar de un campo a otro el foco
            // parpadea (LostFocus→GotFocus) y sin esto el teclado titilaba.
            _timerOcultar = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timerOcultar.Tick += (_, __) =>
            {
                _timerOcultar.Stop();
                if (!HayCampoEnfocado()) Ocultar();
            };

            // ── Layout: numérico a la izquierda + QWERTY a la derecha ───────
            var raiz = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

            var pad = new StackPanel { Spacing = 6 };
            foreach (var fila in new[]
            {
                new[] { "7", "8", "9" },
                new[] { "4", "5", "6" },
                new[] { "1", "2", "3" },
                new[] { "0", ".", "," }
            })
            {
                var f = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var k in fila) f.Children.Add(Tecla(k, ancho: 60));
                pad.Children.Add(f);
            }
            raiz.Children.Add(pad);

            _filasQwerty = new StackPanel { Spacing = 6 };
            raiz.Children.Add(_filasQwerty);

            Child = raiz;
            PintarQwerty();
        }

        // ── Enganche automático al TopLevel ─────────────────────────────────
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            // Bubble: alcanza cualquier TextBox de la ventana, presente o futuro.
            top.AddHandler(InputElement.GotFocusEvent, AlGanarFoco, RoutingStrategies.Bubble);
            top.AddHandler(InputElement.LostFocusEvent, AlPerderFoco, RoutingStrategies.Bubble);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            var top = TopLevel.GetTopLevel(this);
            if (top != null)
            {
                top.RemoveHandler(InputElement.GotFocusEvent, AlGanarFoco);
                top.RemoveHandler(InputElement.LostFocusEvent, AlPerderFoco);
            }
            Ocultar();
        }

        private void AlGanarFoco(object? sender, GotFocusEventArgs e)
        {
            if (e.Source is not TextBox tb || tb.IsReadOnly) return;
            _campo = new WeakReference<TextBox>(tb);
            _timerOcultar.Stop();
            if (!IsVisible)
            {
                IsVisible = true;
                _visibles++;
            }
        }

        private void AlPerderFoco(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not TextBox) return;
            _timerOcultar.Stop();
            _timerOcultar.Start();
        }

        private bool HayCampoEnfocado()
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                return top?.FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false };
            }
            catch { return false; }
        }

        private void Ocultar()
        {
            if (IsVisible)
            {
                IsVisible = false;
                _visibles = Math.Max(0, _visibles - 1);
            }
            if (_mayus) { _mayus = false; PintarQwerty(); }
        }

        // ── Teclas ──────────────────────────────────────────────────────────
        private static readonly string[][] QWERTY =
        {
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "ñ" },
            new[] { "⇧", "z", "x", "c", "v", "b", "n", "m", "⌫" },
            new[] { "@", "-", "_", "espacio", ".", "⏎", "⌄" }
        };

        private void PintarQwerty()
        {
            _filasQwerty.Children.Clear();
            _btnShift = null;
            foreach (var fila in QWERTY)
            {
                var f = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                foreach (var k in fila) f.Children.Add(Tecla(k));
                _filasQwerty.Children.Add(f);
            }
        }

        private Button Tecla(string etiqueta, int ancho = 0)
        {
            bool espacio = etiqueta == "espacio";
            bool modificador = etiqueta is "⇧" or "⌫" or "⏎" or "⌄";
            var b = new Button
            {
                Content = MostrarEtiqueta(etiqueta),
                Height = 52,
                MinWidth = ancho > 0 ? ancho : (espacio ? 220 : (modificador ? 76 : 56)),
                // Sin Focusable=false, tocar la tecla se robaría el foco del
                // campo que se está editando — es TODO el truco del teclado.
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 20,
                Background = modificador ? BgModif : BgTecla,
                // Foreground explícito: el tema de la app es oscuro y las
                // teclas heredaban texto claro (blanco sobre blanco).
                Foreground = Texto,
                BorderBrush = Borde,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };
            if (etiqueta == "⏎") { b.Background = BgAcento; b.Foreground = Brushes.White; }
            if (etiqueta == "⇧")
            {
                _btnShift = b;
                if (_mayus) { b.Background = BgShiftOn; b.Foreground = Brushes.White; }
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
                case "⇧": _mayus = !_mayus; PintarQwerty(); return;
                case "⌄": Ocultar(); return;
            }

            var tb = CampoDestino();
            if (tb == null) { Ocultar(); return; }

            switch (k)
            {
                case "⌫": Borrar(tb); break;
                case "⏎":
                    tb.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.Enter
                    });
                    break;
                case "espacio": Insertar(tb, " "); break;
                default:
                    Insertar(tb, _mayus && k.Length == 1 && char.IsLetter(k[0])
                        ? k.ToUpperInvariant() : k);
                    break;
            }
            // Mayús de un solo tiro, como el teclado del celular.
            if (_mayus && k != "⌫") { _mayus = false; PintarQwerty(); }
        }

        private TextBox? CampoDestino()
        {
            // Preferimos el campo que disparó el GotFocus (exacto); el foco
            // vivo del FocusManager queda de respaldo por si el árbol cambió.
            if (_campo != null && _campo.TryGetTarget(out var tb) && tb.IsEffectivelyVisible)
                return tb;
            try
            {
                var top = TopLevel.GetTopLevel(this);
                return top?.FocusManager?.GetFocusedElement() as TextBox;
            }
            catch { return null; }
        }

        // Inserción/borrado deterministas sobre .Text + caret (misma lógica
        // probada de TecladoWindow): respetan la selección y el PasswordChar.
        private static void Insertar(TextBox tb, string texto)
        {
            var t = tb.Text ?? "";
            int a = Math.Min(tb.SelectionStart, tb.SelectionEnd);
            int b = Math.Max(tb.SelectionStart, tb.SelectionEnd);
            a = Math.Clamp(a, 0, t.Length);
            b = Math.Clamp(b, 0, t.Length);
            if (b > a) t = t.Remove(a, b - a);
            tb.Text = t.Insert(a, texto);
            int caret = a + texto.Length;
            tb.CaretIndex = caret;
            tb.SelectionStart = tb.SelectionEnd = caret;
        }

        private static void Borrar(TextBox tb)
        {
            var t = tb.Text ?? "";
            int a = Math.Min(tb.SelectionStart, tb.SelectionEnd);
            int b = Math.Max(tb.SelectionStart, tb.SelectionEnd);
            a = Math.Clamp(a, 0, t.Length);
            b = Math.Clamp(b, 0, t.Length);
            if (b > a)
            {
                tb.Text = t.Remove(a, b - a);
                tb.CaretIndex = a;
                tb.SelectionStart = tb.SelectionEnd = a;
                return;
            }
            int caret = Math.Clamp(tb.CaretIndex, 0, t.Length);
            if (caret > 0)
            {
                tb.Text = t.Remove(caret - 1, 1);
                tb.CaretIndex = caret - 1;
                tb.SelectionStart = tb.SelectionEnd = caret - 1;
            }
        }
    }
}
