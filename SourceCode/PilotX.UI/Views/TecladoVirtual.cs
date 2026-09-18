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
//     foco — sin WS_EX_NOACTIVATE, sin SendInput, sin devolver foco.
//
// Comportamiento (pedidos 2026-09-01):
//   · campo NUMÉRICO → solo el pad de números (con ⌫ ⏎ y −); campo de texto
//     → pad + QWERTY. La detección: TextInputOptions.ContentType si el panel
//     lo declaró, si no por el contenido actual del campo.
//   · se OCULTA al tocar fuera del campo y fuera del teclado (tocar el mapa,
//     un botón de otra cosa, etc.);
//   · se puede MOVER agarrándolo del asa superior; recuerda dónde lo
//     dejaron mientras la app viva.
//
// Las teclas se escriben DIRECTO en el TextBox enfocado (in-process), con la
// misma inserción determinista sobre .Text + caret que ya probamos en
// TecladoWindow (respeta selección y PasswordChar).
//
// La ventana flotante SIGUE EXISTIENDO para las páginas del Hub (WebView),
// que no tienen TextBox nativo: TecladoWindow.Mostrar consulta ActivoEnShell
// y no se abre si este teclado ya está en pantalla.
//
// Se auto-engancha al TopLevel al entrar al árbol visual: MainWindow solo lo
// declara en el XAML, sin código de wiring.
// ============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PilotX.Desktop.Views
{
    public sealed class TecladoVirtual : Border
    {
        // Paleta oficial (clara/suave/gris/funcional).
        private static readonly IBrush BgCard    = new SolidColorBrush(Color.Parse("#F5F7F4"));
        private static readonly IBrush Borde     = new SolidColorBrush(Color.Parse("#C5CFC5"));
        private static readonly IBrush Texto     = new SolidColorBrush(Color.Parse("#101612"));
        private static readonly IBrush TextoDim  = new SolidColorBrush(Color.Parse("#535E54"));
        private static readonly IBrush BgTecla   = Brushes.White;
        private static readonly IBrush BgModif   = new SolidColorBrush(Color.Parse("#E2E7E2"));
        private static readonly IBrush BgAcento  = new SolidColorBrush(Color.Parse("#4ABA3E"));
        private static readonly IBrush BgShiftOn = new SolidColorBrush(Color.Parse("#1E5E1A"));

        // Cuántos teclados embebidos están visibles ahora (en la práctica, 0 o
        // 1). Lo consulta TecladoWindow.Mostrar para no abrir la ventana
        // flotante encima.
        private static int _visibles;
        public static bool ActivoEnShell => _visibles > 0;

        // Dónde dejó el operario el teclado (offset sobre su posición anclada,
        // abajo-centro). Estático: se recuerda entre aperturas mientras la app
        // viva, igual que hacía la ventana flotante.
        private static Point _corrimiento = default;

        // El campo al que van las teclas: el TextBox que disparó el GotFocus.
        // WeakReference para no retener paneles cerrados.
        private WeakReference<TextBox>? _campo;
        private bool _mayus;
        private bool _numerico;
        private readonly StackPanel _filasQwerty;
        private readonly StackPanel _colNumExtra;
        private readonly DispatcherTimer _timerOcultar;

        // Arrastre por el asa.
        private bool _moviendo;
        private Point _agarre;
        private Point _corrimientoInicial;

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
            RenderTransform = new TranslateTransform();

            // La app corre con el tema Fluent OSCURO: sus estados hover/pressed
            // pintaban la tecla con fondo claro Y texto claro a la vez — al
            // pasar por arriba "se pone todo blanco y no se ve nada" (reporte
            // 2026-09-01). Se fuerzan acá los estados, SOLO dentro del teclado.
            EstadoTecla(null,    ":pointerover", "#EEF2EE", "#101612");
            EstadoTecla(null,    ":pressed",     "#CFD8CF", "#101612");
            // Enter (verde) y Shift activo (verde oscuro): hover/pressed van
            // MÁS oscuros, nunca al gris — si no, el estado se perdía al tocar.
            EstadoTecla("enter", ":pointerover", "#3FA435", "#FFFFFF");
            EstadoTecla("enter", ":pressed",     "#368F2E", "#FFFFFF");
            EstadoTecla("on",    ":pointerover", "#1A5216", "#FFFFFF");
            EstadoTecla("on",    ":pressed",     "#143F11", "#FFFFFF");

            // Gracia antes de ocultar: al saltar de un campo a otro el foco
            // parpadea (LostFocus→GotFocus) y sin esto el teclado titilaba.
            _timerOcultar = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timerOcultar.Tick += (_, __) =>
            {
                _timerOcultar.Stop();
                if (!HayCampoEnfocado()) Ocultar();
            };

            // ── Asa para mover + filas de teclas ────────────────────────────
            var dock = new DockPanel();

            var asa = new Border
            {
                Height = 28,
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(6),
                // Sin fondo no recibe eventos de puntero y el asa no agarra
                // (lección de la barra de TecladoWindow).
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeAll),
                Child = new TextBlock
                {
                    Text = "⠿  teclado",
                    Foreground = TextoDim,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(asa, Dock.Top);
            asa.PointerPressed += AlAgarrarAsa;
            asa.PointerMoved += AlMoverAsa;
            asa.PointerReleased += (_, e) => { _moviendo = false; e.Pointer.Capture(null); };
            dock.Children.Add(asa);

            var raiz = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

            // Pad numérico (siempre visible).
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

            // Columna extra del modo NUMÉRICO: sin el bloque QWERTY, ⌫/−/⏎/⌄
            // tienen que vivir en algún lado.
            _colNumExtra = new StackPanel { Spacing = 6, IsVisible = false };
            foreach (var k in new[] { "⌫", "-", "⏎", "⌄" })
                _colNumExtra.Children.Add(Tecla(k, ancho: 60));
            raiz.Children.Add(_colNumExtra);

            _filasQwerty = new StackPanel { Spacing = 6 };
            raiz.Children.Add(_filasQwerty);

            dock.Children.Add(raiz);
            Child = dock;
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
            // Tunnel: ver el toque ANTES de que alguien lo marque handled —
            // tocar fuera del campo y del teclado lo tiene que ocultar.
            top.AddHandler(InputElement.PointerPressedEvent, AlTocarPantalla, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            var top = TopLevel.GetTopLevel(this);
            if (top != null)
            {
                top.RemoveHandler(InputElement.GotFocusEvent, AlGanarFoco);
                top.RemoveHandler(InputElement.LostFocusEvent, AlPerderFoco);
                top.RemoveHandler(InputElement.PointerPressedEvent, AlTocarPantalla);
            }
            Ocultar();
        }

        private void AlGanarFoco(object? sender, GotFocusEventArgs e)
        {
            if (e.Source is not TextBox tb || tb.IsReadOnly) return;
            _campo = new WeakReference<TextBox>(tb);
            _timerOcultar.Stop();
            ModoNumerico(EsCampoNumerico(tb));
            if (!IsVisible)
            {
                AplicarCorrimiento();   // volver a donde lo dejó el operario
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

        // Tocar FUERA del teclado y fuera de un campo → se oculta (pedido
        // 2026-09-01: "cuando toco fuera del input o fuera del teclado, el
        // teclado debería desaparecer").
        private void AlTocarPantalla(object? sender, PointerPressedEventArgs e)
        {
            if (!IsVisible) return;
            if (e.Source is Visual v)
            {
                foreach (var a in v.GetVisualAncestors())
                {
                    if (a == this) return;               // toque en el teclado
                    if (a is TextBox { IsReadOnly: false }) return; // en un campo
                }
                if (v == this || v is TextBox { IsReadOnly: false }) return;
            }
            Ocultar();
            // Soltar el foco del campo: si quedara enfocado, volver a tocarlo
            // no dispara GotFocus y el teclado no reaparecería.
            try { TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus(); } catch { }
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

        // ── Modo numérico ───────────────────────────────────────────────────

        /// <summary>Campo de números → pad solo. Manda TextInputOptions si el
        /// panel lo declaró; si no, se mira lo que el campo ya tiene escrito
        /// (heurística: los campos numéricos de los editores nunca arrancan
        /// vacíos, traen el valor actual).</summary>
        private static bool EsCampoNumerico(TextBox tb)
        {
            try
            {
                var ct = TextInputOptions.GetContentType(tb);
                if (ct is TextInputContentType.Digits or TextInputContentType.Number) return true;
                if (ct is not TextInputContentType.Normal) return false; // email/url/etc → QWERTY
            }
            catch { }
            var t = tb.Text;
            if (string.IsNullOrWhiteSpace(t)) return false;
            foreach (var c in t)
                if (!char.IsDigit(c) && c is not ('.' or ',' or '-' or '+' or ' ')) return false;
            return true;
        }

        private void ModoNumerico(bool numerico)
        {
            _numerico = numerico;
            _filasQwerty.IsVisible = !numerico;
            _colNumExtra.IsVisible = numerico;
        }

        // ── Arrastre por el asa ─────────────────────────────────────────────
        private void AlAgarrarAsa(object? sender, PointerPressedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            _moviendo = true;
            // Coordenadas del TOPLEVEL, no del propio control: el marco de
            // referencia del control se mueve con él y el arrastre se pelea
            // consigo mismo (lección de TecladoWindow).
            _agarre = e.GetPosition(top);
            _corrimientoInicial = _corrimiento;
            e.Pointer.Capture(sender as IInputElement);
        }

        private void AlMoverAsa(object? sender, PointerEventArgs e)
        {
            if (!_moviendo) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var p = e.GetPosition(top);
            _corrimiento = new Point(
                _corrimientoInicial.X + (p.X - _agarre.X),
                _corrimientoInicial.Y + (p.Y - _agarre.Y));
            AplicarCorrimiento();
        }

        private void AplicarCorrimiento()
        {
            if (RenderTransform is TranslateTransform t)
            {
                t.X = _corrimiento.X;
                t.Y = _corrimiento.Y;
            }
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
            if (etiqueta == "⏎")
            {
                b.Classes.Add("enter");
                b.Background = BgAcento;
                b.Foreground = Brushes.White;
            }
            if (etiqueta == "⇧" && _mayus)
            {
                b.Classes.Add("on");
                b.Background = BgShiftOn;
                b.Foreground = Brushes.White;
            }
            b.Click += (_, __) => Pulsar(etiqueta);
            return b;
        }

        // Fuerza fondo+texto de un estado (hover/pressed) de las teclas, en el
        // ContentPresenter del template — que es donde el tema Fluent los pisa.
        private void EstadoTecla(string? clase, string pseudo, string bg, string fg)
        {
            var st = new Style(x =>
            {
                var sel = x.OfType<Button>();
                if (clase != null) sel = sel.Class(clase);
                return sel.Class(pseudo).Template()
                          .OfType<ContentPresenter>().Name("PART_ContentPresenter");
            });
            st.Setters.Add(new Setter(ContentPresenter.BackgroundProperty,
                new SolidColorBrush(Color.Parse(bg))));
            st.Setters.Add(new Setter(ContentPresenter.ForegroundProperty,
                new SolidColorBrush(Color.Parse(fg))));
            st.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, Borde));
            Styles.Add(st);
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
