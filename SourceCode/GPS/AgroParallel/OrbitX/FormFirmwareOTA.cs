// ============================================================================
// FormFirmwareOTA.cs - Firmware Manager UI
//
// Permite seleccionar un producto, ver versiones disponibles en el cache local
// (servidas por FirmwareLanServer) y disparar OTA a un nodo por MQTT.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgroParallel.OrbitX
{
    public class FormFirmwareOTA : Form
    {
        private ComboBox _cbProducto;
        private TextBox  _tbUid;
        private ListBox  _lbVersiones;
        private TextBox  _tbChangelog;
        private Button   _btnRefrescar;
        private Button   _btnDisparar;
        private Button   _btnSyncCloud;
        private TextBox  _txtLog;
        private Label    _lblEstado;

        private readonly OrbitXConfig _cfg;
        private readonly OrbitXSync _sync;       // para invocar FirmwareSync manual
        private FirmwareOtaClient _ota;
        private List<FirmwareCatalogItem> _firmwares = new List<FirmwareCatalogItem>();

        private static readonly string[] PRODUCTOS = new[]
        {
            "VistaX", "QuantiX", "SectionX", "FlowX", "StormX",
            "SoilX", "SignalX", "CowX", "LineX",
        };

        public FormFirmwareOTA(OrbitXConfig cfg, OrbitXSync sync)
        {
            _cfg = cfg ?? OrbitXConfig.Load();
            _sync = sync;
            BuildUI();
        }

        private void BuildUI()
        {
            Text = "OrbitX — Firmware Manager (OTA)";
            Width = 760;
            Height = 560;
            StartPosition = FormStartPosition.CenterParent;

            var lblP = new Label { Text = "Producto:", Left = 12, Top = 16, Width = 70 };
            _cbProducto = new ComboBox { Left = 86, Top = 12, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var p in PRODUCTOS) _cbProducto.Items.Add(p);
            _cbProducto.SelectedIndex = 1;
            _cbProducto.SelectedIndexChanged += (s, e) => RefrescarVersiones();

            var lblUid = new Label { Text = "UID nodo:", Left = 260, Top = 16, Width = 70 };
            _tbUid = new TextBox { Left = 332, Top = 12, Width = 160 };

            _btnRefrescar = new Button { Text = "↻ Refrescar", Left = 506, Top = 10, Width = 110 };
            _btnRefrescar.Click += async (s, e) => { _btnRefrescar.Enabled = false; RefrescarVersiones(); await Task.Yield(); _btnRefrescar.Enabled = true; };

            _btnSyncCloud = new Button { Text = "⤵ Sync cloud", Left = 622, Top = 10, Width = 110 };
            _btnSyncCloud.Click += async (s, e) => await SyncCloud();

            var lblV = new Label { Text = "Versiones disponibles:", Left = 12, Top = 50, Width = 200 };
            _lbVersiones = new ListBox { Left = 12, Top = 70, Width = 360, Height = 220 };
            _lbVersiones.SelectedIndexChanged += (s, e) => MostrarChangelog();

            var lblCL = new Label { Text = "Changelog:", Left = 384, Top = 50, Width = 100 };
            _tbChangelog = new TextBox { Left = 384, Top = 70, Width = 348, Height = 220,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

            _btnDisparar = new Button { Text = "🚀 Actualizar nodo", Left = 12, Top = 300, Width = 200, Height = 32 };
            _btnDisparar.Click += async (s, e) => await DispararOTA();

            _lblEstado = new Label { Left = 220, Top = 308, Width = 510, ForeColor = Color.DarkSlateBlue };

            var lblLog = new Label { Text = "Log:", Left = 12, Top = 340, Width = 60 };
            _txtLog = new TextBox { Left = 12, Top = 360, Width = 720, Height = 150,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };

            Controls.AddRange(new Control[] {
                lblP, _cbProducto, lblUid, _tbUid, _btnRefrescar, _btnSyncCloud,
                lblV, _lbVersiones, lblCL, _tbChangelog,
                _btnDisparar, _lblEstado,
                lblLog, _txtLog,
            });

            FormClosing += (s, e) =>
            {
                try { _ota?.Dispose(); } catch { }
            };

            Load += async (s, e) => { RefrescarVersiones(); await ConectarOTA(); };
        }

        private async Task ConectarOTA()
        {
            try
            {
                _ota = new FirmwareOtaClient();
                _ota.OnLog += (s, msg) => Log(msg);
                _ota.OnProgress += (s, p) =>
                {
                    Invoke(new Action(() =>
                    {
                        Log($"[{p.Producto}/{p.Uid}] {p.Status} v{p.Version} {(string.IsNullOrEmpty(p.Detalle) ? "" : "· " + p.Detalle)}");
                        _lblEstado.Text = $"Último: {p.Producto}/{p.Uid} → {p.Status} v{p.Version}";
                    }));
                };
                await _ota.ConnectAsync();
            }
            catch (Exception ex)
            {
                Log("MQTT no conectado: " + ex.Message);
            }
        }

        // ── Refrescar lista local ───────────────────────────────────────────
        private void RefrescarVersiones()
        {
            _lbVersiones.Items.Clear();
            _tbChangelog.Text = "";
            string producto = _cbProducto.SelectedItem?.ToString() ?? "";
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    int port = _cfg.FirmwareHttpPort > 0 ? _cfg.FirmwareHttpPort : 8088;
                    string url = $"http://localhost:{port}/firmware/list/{Uri.EscapeDataString(producto)}";
                    var json = http.GetStringAsync(url).Result;
                    _firmwares = JsonSerializer.Deserialize<List<FirmwareCatalogItem>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new List<FirmwareCatalogItem>();
                }
                _firmwares.Sort((a, b) => string.Compare(b.version, a.version, StringComparison.Ordinal));
                foreach (var fw in _firmwares)
                {
                    string mark = fw.local ? "✓" : "⤓";
                    _lbVersiones.Items.Add($"{mark}  v{fw.version}    ({fw.tamano_bytes / 1024} KB)");
                }
                if (_lbVersiones.Items.Count > 0) _lbVersiones.SelectedIndex = 0;
                Log($"Listado {producto}: {_firmwares.Count} versiones");
            }
            catch (Exception ex)
            {
                Log("Refrescar falló: " + ex.Message);
            }
        }

        private void MostrarChangelog()
        {
            int i = _lbVersiones.SelectedIndex;
            if (i < 0 || i >= _firmwares.Count) { _tbChangelog.Text = ""; return; }
            var fw = _firmwares[i];
            _tbChangelog.Text = $"Producto: {fw.producto}\r\nVersión:  {fw.version}\r\n" +
                                $"SHA256:   {fw.hash_sha256}\r\nTamaño:   {fw.tamano_bytes:N0} bytes\r\n" +
                                $"Local:    {(fw.local ? "sí (en cache)" : "no — falta descargar del cloud")}\r\n\r\n" +
                                $"{fw.changelog}";
        }

        // ── Forzar sync con cloud ───────────────────────────────────────────
        private async Task SyncCloud()
        {
            _btnSyncCloud.Enabled = false;
            try
            {
                if (_sync == null) { Log("OrbitXSync no disponible"); return; }
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    var r = await FirmwareMirror.SyncAsync(http, _cfg, msg => Log(msg));
                    Log($"Cloud sync: {r.CatalogCount} en catálogo · {r.Descargados} bajados · {r.Errores} errores");
                }
                RefrescarVersiones();
            }
            catch (Exception ex)
            {
                Log("Cloud sync falló: " + ex.Message);
            }
            finally { _btnSyncCloud.Enabled = true; }
        }

        // ── Disparar OTA ────────────────────────────────────────────────────
        private async Task DispararOTA()
        {
            int i = _lbVersiones.SelectedIndex;
            if (i < 0 || i >= _firmwares.Count) { Log("Elegí una versión"); return; }
            string uid = _tbUid.Text.Trim();
            if (string.IsNullOrEmpty(uid)) { Log("Ingresá UID del nodo"); return; }

            var fw = _firmwares[i];
            if (!fw.local)
            {
                if (MessageBox.Show("Esta versión NO está en cache local. Descargar del cloud primero?",
                        "Firmware faltante", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                await SyncCloud();
                if (i < _firmwares.Count) fw = _firmwares[i];
                if (!fw.local) { Log("No se pudo descargar la versión"); return; }
            }

            int port = _cfg.FirmwareHttpPort > 0 ? _cfg.FirmwareHttpPort : 8088;
            string url = FirmwareOtaClient.BuildFirmwareUrl(fw.producto, fw.version, port);

            string msg = $"Disparar OTA en {fw.producto}/{uid} → v{fw.version}\n\nURL:\n{url}";
            if (MessageBox.Show(msg, "Confirmar OTA", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            try
            {
                if (_ota == null) await ConectarOTA();
                bool ok = await _ota.SendOtaAsync(fw.producto, uid, url, fw.version);
                Log(ok ? $"OTA enviada a {fw.producto}/{uid} v{fw.version}" : "OTA publish falló");
            }
            catch (Exception ex)
            {
                Log("OTA error: " + ex.Message);
            }
        }

        private void Log(string msg)
        {
            try
            {
                if (InvokeRequired) { Invoke(new Action(() => Log(msg))); return; }
                _txtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\r\n");
            }
            catch { }
        }
    }
}
