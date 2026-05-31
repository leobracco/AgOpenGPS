// ============================================================================
// AlertaNodosWebOverlayControl.cs
// UserControl con WebView2 que hostea /pages/cabina-alarmas.html como overlay
// FULL-BLEED sobre FormGPS. Aparece SOLO cuando hay nodos del implemento
// activo offline.
//
// Diseño:
//   · Por defecto Visible = false — el control no ocupa lugar visual ni se
//     dibuja sobre el mapa hasta que el HTML lo pida.
//   · El HTML hace polling de /api/nodos/unified cada 2s y, cuando detecta
//     offlines del implemento activo, envía postMessage({type:"show", count}).
//     Este control escucha CoreWebView2.WebMessageReceived y hace Visible=true.
//     Cuando vuelve a estar todo OK, manda {type:"hide"} y volvemos a ocultar.
//   · Avalonia-friendly: toda la UI está en HTML. Cuando AgValoniaGPS reemplace
//     a FormGPS, sólo hay que cambiar este host (UserControl→Avalonia.WebView)
//     — el HTML/JS y el contrato de mensajes se mantienen.
//
// El AgpWebHost ya debe estar corriendo cuando navegamos (lo levanta
// AgpWebHostBootstrap durante el arranque de FormGPS).
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;
using AgroParallel.Shell; // FormAgroParallelHubWebView2.Prewarm()
using Microsoft.Web.WebView2.WinForms;

namespace AgroParallel.Common
{
    public sealed class AlertaNodosWebOverlayControl : UserControl
    {
        private readonly WebView2 _webView;
        private readonly string _hubBaseUrl;
        private bool _navigated;

        /// <summary>
        /// Disparado cuando el HTML pide mostrar el banner. count = nodos offline.
        /// Útil si el caller quiere loggear o telemetrizar la transición.
        /// </summary>
        public event Action<int> ShowRequested;

        /// <summary>Disparado cuando el HTML pide ocultar (todo OK).</summary>
        public event Action HideRequested;

        /// <summary>Disparado cuando el operario aprieta "Silenciar 10 min".</summary>
        public event Action<DateTimeOffset> Silenced;

        /// <summary>
        /// hubBaseUrl: típicamente "http://127.0.0.1:5180/". El control le
        /// concatena "pages/cabina-alarmas.html".
        /// </summary>
        public AlertaNodosWebOverlayControl(string hubBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(hubBaseUrl))
                throw new ArgumentException("hubBaseUrl requerido", nameof(hubBaseUrl));
            _hubBaseUrl = hubBaseUrl;

            // Por defecto oculto: hasta que el HTML mande "show" no debemos
            // tapar ni un pixel del mapa.
            Visible = false;

            // Altura razonable para un banner top — el HTML usa min-height 64px
            // y crece con el contenido; le dejamos margen para 2 líneas.
            Height = 88;
            BackColor = Color.Transparent;
            DoubleBuffered = true;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                AllowExternalDrop = false,
                DefaultBackgroundColor = Color.Transparent
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
                    // Background transparente del WebView2 — la página decide qué
                    // pintar (banner rojo) y cuándo (body.hidden).
                    try { _webView.CoreWebView2.Settings.IsStatusBarEnabled = false; } catch { }
                    _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                    if (!_navigated)
                    {
                        string target = _hubBaseUrl.TrimEnd('/') + "/pages/cabina-alarmas.html";
                        _webView.CoreWebView2.Navigate(target);
                        _navigated = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AlertaNodos] init: " + ex.Message);
            }
        }

        // Mensajes que llegan desde la página HTML:
        //   {"type":"show","count":N}    → mostrar el banner (Visible=true)
        //   {"type":"hide"}              → ocultar
        //   {"type":"silenced","until":ms}→ operario silenció por 10 min
        //
        // Parsing manual con búsqueda de substrings: evitamos depender de
        // System.Text.Json o Newtonsoft sólo para 3 campos triviales. Si llega
        // basura (campo faltante, JSON inválido), simplemente ignoramos.
        private void OnWebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = null;
                try { json = e.WebMessageAsJson; } catch { }
                if (string.IsNullOrEmpty(json)) return;

                string type = ExtractStringField(json, "type");
                if (string.IsNullOrEmpty(type)) return;

                if (type == "show")
                {
                    int count = ExtractIntField(json, "count");
                    if (!Visible) Visible = true;
                    BringToFront();
                    try { ShowRequested?.Invoke(count); } catch { }
                }
                else if (type == "hide")
                {
                    if (Visible) Visible = false;
                    try { HideRequested?.Invoke(); } catch { }
                }
                else if (type == "silenced")
                {
                    long until = ExtractLongField(json, "until");
                    var when = until > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(until)
                        : DateTimeOffset.UtcNow.AddMinutes(10);
                    try { Silenced?.Invoke(when); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AlertaNodos] msg: " + ex.Message);
            }
        }

        // ---- Mini JSON helpers (sólo para los 3 campos esperados) ----------

        private static string ExtractStringField(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static int ExtractIntField(string json, string key)
        {
            long v = ExtractLongField(json, key);
            if (v < int.MinValue) return int.MinValue;
            if (v > int.MaxValue) return int.MaxValue;
            return (int)v;
        }

        private static long ExtractLongField(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return 0;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return 0;
            int j = colon + 1;
            while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
            int start = j;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
            if (j == start) return 0;
            long n;
            return long.TryParse(json.Substring(start, j - start), out n) ? n : 0;
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
