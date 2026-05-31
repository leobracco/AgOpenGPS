// ============================================================================
// VistaXWebOverlayPanel.cs
// UserControl con WebView2 que hostea las páginas HTML de VistaX como overlay
// sobre el mapa de PilotX (FormGPS).
//
// Se usa dos veces:
//   · Strip inferior (vistax-live.html) → ancho según cantidad de sensores.
//   · Stats superior (vistax-stats.html) → ventana arriba-derecha con KPIs.
//
// Estructura del control:
//   ┌───────────────────────────────┐
//   │ ≡  título               ✕    │  ← drag bar (22 px, NO cubierta por WebView2)
//   ├───────────────────────────────┤
//   │  WebView2 (contenido HTML)    │
//   │                            ◢  │  ← grip de resize (16 px)
//   └───────────────────────────────┘
//
// Por qué la drag bar es necesaria: WebView2 corre en una HWND nativa que
// intercepta TODOS los eventos del mouse — los handlers de WinForms del
// UserControl nunca los reciben. Por eso el OverlayDragger genérico no
// funciona si se ata al WebView2 mismo. La barra arriba (Panel WinForms
// puro, fuera del rectángulo de la WebView) sí recibe MouseDown/Move/Up.
//
// El AgpWebHost ya debe estar levantado (AgpWebHostBootstrap.Url ≠ null).
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;
using AgroParallel.Shell;
using Microsoft.Web.WebView2.WinForms;

namespace AgroParallel.Common
{
    public sealed class VistaXWebOverlayPanel : UserControl
    {
        private const int GripSize = 16;
        private const int DragBarHeight = 22;
        private const int CloseBtnSize = 18;

        private readonly WebView2 _webView;
        private readonly Panel _dragBar;
        private readonly Label _titleLbl;
        private readonly Button _closeBtn;
        private readonly Panel _resizeGrip;
        private readonly string _url;
        private readonly Size _minSize;
        private bool _navigated;

        // Estado del drag de resize.
        private bool _resizing;
        private Point _resizeStartScreen;
        private Size _resizeStartSize;

        /// <summary>Disparado al soltar el resize grip — caller persiste W/H.</summary>
        public event Action<Size> ResizedByUser;

        /// <summary>Disparado al hacer click en la X — caller cierra el overlay.</summary>
        public event Action CloseRequested;

        /// <summary>
        /// Panel WinForms puro (fuera del WebView2) donde se recibe el drag.
        /// El caller debe atachar OverlayDragger a este control, no al UserControl
        /// completo — sino el WebView2 se come los eventos.
        /// </summary>
        public Control DragHandle => _dragBar;

        public VistaXWebOverlayPanel(string hubBaseUrl, string pagePath, Size defaultSize, Size minSize, string title = "VistaX")
        {
            if (string.IsNullOrWhiteSpace(hubBaseUrl))
                throw new ArgumentException("hubBaseUrl requerido", nameof(hubBaseUrl));
            if (string.IsNullOrWhiteSpace(pagePath))
                throw new ArgumentException("pagePath requerido", nameof(pagePath));

            _url = hubBaseUrl.TrimEnd('/') + "/" + pagePath.TrimStart('/');
            _minSize = new Size(
                Math.Max(120, minSize.Width),
                Math.Max(60 + DragBarHeight, minSize.Height + DragBarHeight));

            Width = Math.Max(_minSize.Width, defaultSize.Width);
            Height = Math.Max(_minSize.Height, defaultSize.Height + DragBarHeight);
            BackColor = Color.Black;
            DoubleBuffered = true;

            // --- Drag bar (Panel WinForms puro) ---
            _dragBar = new Panel
            {
                Height = DragBarHeight,
                Cursor = Cursors.SizeAll,
                BackColor = Color.FromArgb(28, 36, 30) // --agp-surface
            };
            _dragBar.Paint += OnPaintDragBar;
            Controls.Add(_dragBar);

            _titleLbl = new Label
            {
                Text = "≡  " + title,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(220, 224, 220),
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            _dragBar.Controls.Add(_titleLbl);

            _closeBtn = new Button
            {
                Text = "×",
                FlatStyle = FlatStyle.Flat,
                Size = new Size(CloseBtnSize, CloseBtnSize),
                BackColor = Color.FromArgb(28, 36, 30),
                ForeColor = Color.FromArgb(180, 180, 180),
                TabStop = false,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _closeBtn.FlatAppearance.BorderSize = 0;
            _closeBtn.Click += (s, e) => { try { CloseRequested?.Invoke(); } catch { } };
            _dragBar.Controls.Add(_closeBtn);

            // --- WebView2 (host del HTML) ---
            _webView = new WebView2
            {
                AllowExternalDrop = false,
                DefaultBackgroundColor = Color.FromArgb(16, 22, 18) // --agp-bg
            };
            Controls.Add(_webView);

            // --- Resize grip ---
            _resizeGrip = new Panel
            {
                Size = new Size(GripSize, GripSize),
                Cursor = Cursors.SizeNWSE,
                BackColor = Color.FromArgb(60, 67, 62)
            };
            _resizeGrip.Paint += OnPaintGrip;
            _resizeGrip.MouseDown += OnGripMouseDown;
            _resizeGrip.MouseMove += OnGripMouseMove;
            _resizeGrip.MouseUp += OnGripMouseUp;
            Controls.Add(_resizeGrip);

            LayoutChildren();
            HandleCreated += OnHandleCreatedOnce;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        // Dispone la WebView2 ocupando todo menos:
        //   · franja superior de 22 px = drag bar
        //   · cuadrado 16×16 en la esquina inf-der = grip
        // Lo hacemos manualmente porque WebView2 no se lleva del todo bien
        // con Dock=Fill + sibling overlapping.
        private void LayoutChildren()
        {
            if (_dragBar != null)
            {
                _dragBar.Bounds = new Rectangle(0, 0, Width, DragBarHeight);
                if (_titleLbl != null)
                    _titleLbl.Bounds = new Rectangle(6, 0, Width - CloseBtnSize - 14, DragBarHeight);
                if (_closeBtn != null)
                    _closeBtn.Location = new Point(Width - CloseBtnSize - 3, (DragBarHeight - CloseBtnSize) / 2);
                _dragBar.BringToFront();
            }
            if (_webView != null)
            {
                _webView.Bounds = new Rectangle(0, DragBarHeight,
                    Math.Max(1, Width),
                    Math.Max(1, Height - DragBarHeight));
            }
            if (_resizeGrip != null)
            {
                _resizeGrip.Bounds = new Rectangle(
                    Width - GripSize, Height - GripSize, GripSize, GripSize);
                _resizeGrip.BringToFront();
            }
        }

        private void OnPaintDragBar(object sender, PaintEventArgs e)
        {
            // Borde inferior sutil para separar del WebView2.
            using (var p = new Pen(Color.FromArgb(60, 67, 62)))
            {
                e.Graphics.DrawLine(p, 0, DragBarHeight - 1, _dragBar.Width, DragBarHeight - 1);
            }
        }

        private void OnPaintGrip(object sender, PaintEventArgs e)
        {
            // Tres puntos diagonales para indicar "resize".
            using (var b = new SolidBrush(Color.FromArgb(220, 220, 220)))
            {
                int r = 2;
                e.Graphics.FillEllipse(b, GripSize - 4 - r, GripSize - 4 - r, r * 2, r * 2);
                e.Graphics.FillEllipse(b, GripSize - 8 - r, GripSize - 4 - r, r * 2, r * 2);
                e.Graphics.FillEllipse(b, GripSize - 4 - r, GripSize - 8 - r, r * 2, r * 2);
            }
        }

        private void OnGripMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _resizing = true;
            _resizeStartScreen = Control.MousePosition;
            _resizeStartSize = this.Size;
        }

        private void OnGripMouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            var cur = Control.MousePosition;
            int dx = cur.X - _resizeStartScreen.X;
            int dy = cur.Y - _resizeStartScreen.Y;

            int newW = Math.Max(_minSize.Width, _resizeStartSize.Width + dx);
            int newH = Math.Max(_minSize.Height, _resizeStartSize.Height + dy);

            if (Parent != null)
            {
                int maxW = Parent.ClientSize.Width - Left - 4;
                int maxH = Parent.ClientSize.Height - Top - 4;
                if (maxW > _minSize.Width) newW = Math.Min(newW, maxW);
                if (maxH > _minSize.Height) newH = Math.Min(newH, maxH);
            }

            if (newW != Width || newH != Height)
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left;
                Size = new Size(newW, newH);
            }
        }

        private void OnGripMouseUp(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            ResizedByUser?.Invoke(this.Size);
        }

        private async void OnHandleCreatedOnce(object sender, EventArgs e)
        {
            HandleCreated -= OnHandleCreatedOnce;
            try
            {
                var env = await FormAgroParallelHubWebView2.Prewarm().ConfigureAwait(true);
                await _webView.EnsureCoreWebView2Async(env);
                if (!_navigated && _webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.Navigate(_url);
                    _navigated = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VxOverlay] init: " + ex.Message);
            }
        }

        /// <summary>Recarga la página (útil después de cambios de config).</summary>
        public void Reload()
        {
            try { _webView?.CoreWebView2?.Reload(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _webView?.Dispose(); } catch { }
                try { _dragBar?.Dispose(); } catch { }
                try { _resizeGrip?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
