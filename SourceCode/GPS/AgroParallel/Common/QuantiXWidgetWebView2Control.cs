// ============================================================================
// QuantiXWidgetWebView2Control.cs
// UserControl reemplazo del WinForms ShapefileLegendControl. Hostea un WebView2
// que navega a /pages/widget-quantix.html y conversa con el backend
// IQuantiXRuntimeService + INodoRegistryService vía REST.
//
// Diseño:
//   · Mismo tamaño nominal que el legacy (220x240) — sirve como drop-in.
//   · Se agrega como hijo de FormGPS (mismo z-order que shapefileLegend antes).
//   · Reusa el environment WebView2 pre-warmed por FormAgroParallelHubWebView2.
//   · Toda la lógica MAN/AUTO, dosis, modal de confirmación y teclado numérico
//     vive en JS — este control sólo es el shell.
//   · La selección de nodo se persiste en localStorage del WebView2 (no hay
//     SelectedNodoUid público — el shell ya no necesita ese estado, porque
//     FormGPS dejó de pushear datos con SetMotorDosis).
//
// El AgpWebHost ya debe estar corriendo cuando se navega (lo levanta
// AgpWebHostBootstrap durante el arranque de FormGPS).
// ============================================================================

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgroParallel.Shell; // FormAgroParallelHubWebView2.Prewarm()
using Microsoft.Web.WebView2.WinForms;

namespace AgroParallel.Common
{
    public sealed class QuantiXWidgetWebView2Control : UserControl
    {
        private readonly WebView2 _webView;
        private readonly string _hubBaseUrl;
        private bool _navigated;

        /// <summary>
        /// hubBaseUrl: la Url base del AgpWebHost (típicamente
        /// "http://127.0.0.1:5180/"). El control le concatena "pages/widget-quantix.html".
        /// </summary>
        public QuantiXWidgetWebView2Control(string hubBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(hubBaseUrl))
                throw new ArgumentException("hubBaseUrl requerido", nameof(hubBaseUrl));
            _hubBaseUrl = hubBaseUrl;

            // Drop-in: mismo tamaño que el legacy ShapefileLegendControl.
            Width = 220;
            Height = 240;
            BackColor = Color.Black; // fondo opaco mientras carga la página
            DoubleBuffered = true;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                AllowExternalDrop = false
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
                NavigateIfNeeded();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[QxWidget] init: " + ex.Message);
            }
        }

        private void NavigateIfNeeded()
        {
            if (_navigated || _webView.CoreWebView2 == null) return;
            string target = _hubBaseUrl.TrimEnd('/') + "/pages/widget-quantix.html";
            _webView.CoreWebView2.Navigate(target);
            _navigated = true;
        }

        /// <summary>
        /// Compat con el caller histórico (ShapefileLegendControl.SetConnected).
        /// El widget HTML ya determina conectividad por su cuenta — este método
        /// queda como no-op para no tocar todas las llamadas en FormGPS.
        /// </summary>
        public void SetConnected(bool _) { }

        /// <summary>
        /// Compat: HasData ahora siempre true (el widget se autogestiona vacío).
        /// </summary>
        public bool HasData => true;

        /// <summary>Compat — ya no se usa, el HTML lo decide.</summary>
        public void Clear() { }

        /// <summary>Compat — leyenda del shape no aplica al widget HTML.</summary>
        public void SetLegend(string field, double min, double max) { }

        /// <summary>Compat — valor actual del shape no aplica al widget HTML.</summary>
        public void SetCurrent(double value, bool hasValue) { }

        /// <summary>Compat — el widget HTML lee la config y muestra los nodos.</summary>
        public string SelectedNodoUid => null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _webView?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
