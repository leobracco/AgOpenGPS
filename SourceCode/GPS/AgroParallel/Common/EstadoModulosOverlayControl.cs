// ============================================================================
// EstadoModulosOverlayControl.cs
// UserControl con WebView2 que hostea /pages/estado-modulos.html como overlay
// chico SIEMPRE VISIBLE sobre FormGPS — muestra el estado (OK/error/no
// conectado) de CoreX, Motor, GPS, IMU y Machine sobre la pantalla principal
// (mapa nativo), sin depender de que el operario abra el Hub. Mismos datos
// y endpoint que la tira de hub.html (pillCorex/etc + /api/corex-bridge/status).
//
// A diferencia de AlertaNodosWebOverlayControl (oculto hasta postMessage
// 'show'), este arranca Visible=true y no necesita protocolo show/hide —
// el HTML solo lee el estado, nunca pide mostrarse/ocultarse.
//
// Arrastrable DESDE EL HTML: a diferencia de otros overlays (VistaX, que usa
// OverlayDragger.Attach sobre una barrita WinForms separada porque el
// WebView2 "se come" los eventos de mouse del lado nativo), acá el drag se
// implementa del lado del HTML con Pointer Events (funciona igual con mouse
// y con el dedo) y viaja al host por postMessage — más simple que una
// barrita de 12px casi imposible de tocar con el dedo. Ver
// estado-modulos.js: postMessage({type:'drag_start'|'drag'|'drag_end', dx, dy}).
// ============================================================================

using System;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using AgroParallel.Shell; // FormAgroParallelHubWebView2.Prewarm()
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgroParallel.Common
{
    public sealed class EstadoModulosOverlayControl : UserControl
    {
        private const int EdgeMargin = 8;

        private readonly WebView2 _webView;
        private readonly string _hubBaseUrl;
        private bool _navigated;
        private bool _dragging;
        private Point _dragOrigin;

        /// <summary>Disparado al soltar el drag — caller persiste la posición final.</summary>
        public event Action<Point> DragEnded;

        /// <summary>
        /// hubBaseUrl: típicamente "http://127.0.0.1:5180/". El control le
        /// concatena "pages/estado-modulos.html".
        /// </summary>
        public EstadoModulosOverlayControl(string hubBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(hubBaseUrl))
                throw new ArgumentException("hubBaseUrl requerido", nameof(hubBaseUrl));
            _hubBaseUrl = hubBaseUrl;

            Visible = true;
            // Tamaño de arranque aproximado (columna angosta y alta, 5
            // íconos apilados) — el 'resize' que manda estado-modulos.js
            // apenas carga corrige esto al tamaño real, esto solo evita un
            // salto grande mientras el WebView2 todavía no navegó.
            Width = 40;
            Height = 180;
            // El HTML pinta su propia tarjeta blanca con borde/sombra — el
            // control WinForms detrás nunca se ve, pero lo dejamos sólido
            // (no Transparent) para evitar cualquier flash/artefacto GDI
            // mientras el WebView2 todavía no terminó de navegar.
            BackColor = Color.White;
            DoubleBuffered = true;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                AllowExternalDrop = false,
                DefaultBackgroundColor = Color.White
            };
            Controls.Add(_webView);

            HandleCreated += OnHandleCreatedOnce;
        }

        private async void OnHandleCreatedOnce(object sender, EventArgs e)
        {
            HandleCreated -= OnHandleCreatedOnce;
            try
            {
                var env = await FormAgroParallelHubWebView2.Prewarm().ConfigureAwait(true);
                await _webView.EnsureCoreWebView2Async(env);
                if (_webView.CoreWebView2 != null)
                {
                    try { _webView.CoreWebView2.Settings.IsStatusBarEnabled = false; } catch { }
                    _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                    if (!_navigated)
                    {
                        string target = _hubBaseUrl.TrimEnd('/') + "/pages/estado-modulos.html";
                        _webView.CoreWebView2.Navigate(target);
                        _navigated = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[EstadoModulos] init: " + ex.Message);
            }
        }

        // Mensajes desde estado-modulos.js:
        //   {"type":"drag_start"}          → captura Location actual como origen
        //   {"type":"drag","dx":N,"dy":N}  → mueve a origen + delta (clampeado a pantalla)
        //   {"type":"drag_end"}            → dispara DragEnded(Location) para persistir
        //   {"type":"resize","w":N,"h":N}  → ancho/alto reales de #bar (ResizeObserver
        //                                    del lado HTML) — el control nativo hace
        //                                    match 1:1, así nunca queda más grande
        //                                    que las pills.
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = null;
                try { json = e.WebMessageAsJson; } catch { }
                if (string.IsNullOrEmpty(json)) return;

                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "drag_start")
                    {
                        _dragOrigin = Location;
                        _dragging = true;
                    }
                    else if (type == "drag" && _dragging)
                    {
                        double dx = root.TryGetProperty("dx", out var dxEl) ? dxEl.GetDouble() : 0;
                        double dy = root.TryGetProperty("dy", out var dyEl) ? dyEl.GetDouble() : 0;
                        int newX = _dragOrigin.X + (int)dx;
                        int newY = _dragOrigin.Y + (int)dy;
                        ClampToParent(ref newX, ref newY);
                        Location = new Point(newX, newY);
                    }
                    else if (type == "drag_end")
                    {
                        _dragging = false;
                        try { DragEnded?.Invoke(Location); } catch { }
                    }
                    else if (type == "resize")
                    {
                        double w = root.TryGetProperty("w", out var wEl) ? wEl.GetDouble() : 0;
                        double h = root.TryGetProperty("h", out var hEl) ? hEl.GetDouble() : 0;
                        if (w > 0 && h > 0)
                        {
                            int newW = Clamp((int)Math.Ceiling(w), 60, 900);
                            int newH = Clamp((int)Math.Ceiling(h), 18, 200);
                            if (newW != Width || newH != Height)
                            {
                                Width = newW;
                                Height = newH;
                                int x = Location.X, y = Location.Y;
                                ClampToParent(ref x, ref y);
                                Location = new Point(x, y);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[EstadoModulos] msg: " + ex.Message);
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Mismo criterio que OverlayDragger.ClampToParent: deja EdgeMargin px
        // visibles de cada lado, y si el control entra entero en el eje del
        // parent, lo fuerza 100% adentro (no permite ni un pixel de exceso).
        private void ClampToParent(ref int x, ref int y)
        {
            if (Parent == null) return;
            int pw = Parent.ClientSize.Width;
            int ph = Parent.ClientSize.Height;
            int w = Width, h = Height;

            int minX = EdgeMargin - w, maxX = pw - EdgeMargin;
            int minY = EdgeMargin - h, maxY = ph - EdgeMargin;
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            if (y < minY) y = minY;
            if (y > maxY) y = maxY;

            if (w < pw)
            {
                if (x < EdgeMargin) x = EdgeMargin;
                if (x + w > pw - EdgeMargin) x = pw - EdgeMargin - w;
            }
            if (h < ph)
            {
                if (y < EdgeMargin) y = EdgeMargin;
                if (y + h > ph - EdgeMargin) y = ph - EdgeMargin - h;
            }
        }

        /// <summary>Fuerza recarga de la página (útil tras cambios de config).</summary>
        public void Reload()
        {
            try { _webView?.CoreWebView2?.Reload(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_webView != null && _webView.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                }
                catch { }
                try { _webView?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
