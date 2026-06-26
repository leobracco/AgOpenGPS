using AgOpenGPS.Forms;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace AgOpenGPS
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>

        public static readonly string Version = Assembly.GetEntryAssembly().GetName().Version.ToString(3); // Major.Minor.Patch
        public static readonly string SemVer = Application.ProductVersion.Split('+').First();
        public static readonly bool IsPreRelease = Application.ProductVersion.Contains('-');
        public static readonly bool IsDevelopVersion = Application.ProductVersion == "1.0.0.0";

        [STAThread]
        private static void Main(string[] args)
        {
            // Mutex de instancia única.
            using (var mtx = new Mutex(true, "{516-0AC5-B9A1-55fd-A8CE-72F04E6BDE8F}", out bool createdNew))
            {
                if (!createdNew)
                {
                    FormDialog.Show("Warning", "AgOpenGPS is Already Running", DialogSeverity.Warning);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // El teclado virtual ahora es HTML (keyboard.js dentro del hub
                // WebView2). Ya no hookeamos osk/tabtip nativo.

                // Modo AOG normal.
                RegistrySettings.Load();
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(RegistrySettings.culture);
                Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(RegistrySettings.culture);

                int aogExitCode = 0;
                try
                {
                    Application.Run(new FormGPS());
                }
                catch (Exception ex)
                {
                    aogExitCode = 1;
                    // Escribir el crash a un archivo junto al .exe + mostrarlo
                    // en pantalla, así no se pierde y el usuario sabe qué pasó
                    // antes de caer al shell PilotX.
                    try
                    {
                        string exeDir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                        string logPath = System.IO.Path.Combine(exeDir, "AOG-crash.log");
                        string entry = "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\r\n"
                            + ex.GetType().FullName + ": " + ex.Message + "\r\n"
                            + ex.StackTrace + "\r\n";
                        if (ex.InnerException != null)
                            entry += "INNER: " + ex.InnerException + "\r\n";
                        entry += "\r\n";
                        System.IO.File.AppendAllText(logPath, entry);
                    }
                    catch { }
                    try
                    {
                        MessageBox.Show(
                            ex.GetType().Name + ": " + ex.Message
                            + "\r\n\r\nVer AOG-crash.log para más detalle.",
                            "AOG crasheó al iniciar",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    catch { }
                }

                Environment.Exit(aogExitCode);
            }
        }
    }
}