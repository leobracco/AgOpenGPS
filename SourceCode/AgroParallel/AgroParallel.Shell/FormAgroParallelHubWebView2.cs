// ============================================================================
// FormAgroParallelHubWebView2.cs
// Host WinForms que arranca AgpWebHost + WebView2 y navega a la UI HTML.
// Reemplazo progresivo de FormAgroParallelHub (Fase B placeholder).
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;
using AgroParallel.WebHost;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgroParallel.Shell
{
    public class FormAgroParallelHubWebView2 : Form
    {
        private readonly IAogStateProvider _state;
        private readonly string _wwwroot;
        private readonly int _port;
        private AgpWebHost _webHost;
        private NodoRegistryService _nodos;
        private WebView2 _webView;

        public FormAgroParallelHubWebView2(IAogStateProvider state, string wwwroot, int port = 5180)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _wwwroot = wwwroot;
            _port = port;

            Text = "AgroParallel";
            Width = 1280;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            KeyPreview = true;
            KeyDown += OnKeyDown;
            Load += OnLoad;
            FormClosing += OnClosing;
        }

        // F5  → reload (cache habitual)
        // Ctrl+F5 / Ctrl+R → reload IGNORANDO cache (útil tras editar CSS/JS en wwwroot)
        // F12 → DevTools de WebView2
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                var core = _webView?.CoreWebView2;
                if (core == null) return;

                if (e.KeyCode == Keys.F12)
                {
                    core.OpenDevToolsWindow();
                    e.Handled = true;
                }
                else if ((e.KeyCode == Keys.F5 && e.Control) ||
                         (e.KeyCode == Keys.R && e.Control))
                {
                    core.ExecuteScriptAsync("location.reload(true)");
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F5)
                {
                    core.Reload();
                    e.Handled = true;
                }
            }
            catch { }
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                _nodos = new NodoRegistryService();
                var (broker, brokerPort) = LoadBrokerConfig();
                if (!string.IsNullOrEmpty(broker))
                    _nodos.Start(broker, brokerPort);

                var vistaxCfg = new VistaXConfigService();
                var vistaxLive = new VistaXLiveService(_nodos, vistaxCfg);
                _webHost = new AgpWebHost(
                    _state,
                    new SistemaService(),
                    _nodos,
                    new OrbitXConfigService(),
                    new SectionXConfigService(),
                    new CamarasConfigService(),
                    new QuantiXConfigService(_nodos),
                    vistaxCfg,
                    vistaxLive,
                    new DebugLogService(),
                    _wwwroot,
                    _port);
                _webHost.Start();

                string userData = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "AgroParallel", "WebView2Data");
                Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.AddHostObjectToScript("agp", new ShellBridge(this));
                _webView.CoreWebView2.Navigate(_webHost.Url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fallo al inicializar WebView2: " + ex.Message,
                    "AgroParallel", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            try { _webHost?.Stop(); } catch { }
            try { _nodos?.Stop(); } catch { }
            try { _webView?.Dispose(); } catch { }
            _webHost = null;
            _nodos = null;
            _webView = null;
        }

        // Lee broker/port desde vistaX.json sin depender del proyecto GPS.
        // Si el archivo no existe o es inválido, devuelve (null, 1883) y
        // simplemente no se arranca el descubrimiento MQTT.
        private static (string addr, int port) LoadBrokerConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vistaX.json");
                if (!File.Exists(path)) return (null, 1883);

                string json = File.ReadAllText(path);
                using (var doc = JsonDocument.Parse(json))
                {
                    string addr = null;
                    int port = 1883;
                    if (doc.RootElement.TryGetProperty("BrokerAddress", out var ja))
                        addr = ja.GetString();
                    if (doc.RootElement.TryGetProperty("BrokerPort", out var jp) && jp.ValueKind == JsonValueKind.Number)
                        port = jp.GetInt32();
                    return (addr, port > 0 ? port : 1883);
                }
            }
            catch
            {
                return (null, 1883);
            }
        }
    }
}
