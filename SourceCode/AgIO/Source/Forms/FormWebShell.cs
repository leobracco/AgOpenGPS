using System;
using System.IO;
using System.Windows.Forms;
using AgLibrary.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgIO
{
    /// <summary>
    /// Ventana web de CoreX: WebView2 fullscreen contra el host local :5181.
    /// Convive con FormLoop (UI vieja) durante la migración — spec decisión 4.
    /// </summary>
    public sealed class FormWebShell : Form
    {
        private readonly WebView2 _web = new WebView2();

        public FormWebShell()
        {
            Text = "CoreX";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1100;
            Height = 720;
            // Mismo ícono que el exe (isotipo Agro Parallel).
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { /* sin ícono no es fatal */ }

            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);

            Load += async (s, e) =>
            {
                try
                {
                    // User data folder explícito (mismo enfoque que el shell de PilotX):
                    // sin esto WebView2 escribe junto al exe y falla desde Program Files.
                    string userData = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "AgroParallel", "WebView2Data");
                    try { Directory.CreateDirectory(userData); } catch { }
                    var env = await CoreWebView2Environment.CreateAsync(null, userData);
                    await _web.EnsureCoreWebView2Async(env);
                    _web.CoreWebView2.Navigate(CoreXWebHost.Url);
                }
                catch (Exception ex)
                {
                    // WebView2 runtime ausente: no rompemos CoreX, queda la UI vieja.
                    Log.EventWriter("FormWebShell sin WebView2: " + ex.Message);
                    Close();
                }
            };
        }
    }
}
