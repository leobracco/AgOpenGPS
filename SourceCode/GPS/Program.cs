using AgOpenGPS.Forms;
using AgroParallel.Sistema;
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
            bool shellMode = args != null && args.Length > 0 &&
                (args[0] == "--shell" || args[0] == "-shell" || args[0] == "/shell");

            // Mutex distinto para cada modo, así no se pisa con el otro proceso.
            string mutexName = shellMode
                ? "{516-0AC5-B9A1-55fd-A8CE-72F04E6BDE8F}-shell"
                : "{516-0AC5-B9A1-55fd-A8CE-72F04E6BDE8F}";
            using (var mtx = new Mutex(true, mutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    if (!shellMode) FormDialog.Show("Warning", "AgOpenGPS is Already Running", DialogSeverity.Warning);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (shellMode)
                {
                    // Modo shell de fallback (relanzado desde AOG al cerrarse).
                    using (var shell = new FormPilotXShell())
                    {
                        Application.Run(shell);
                        if (shell.Result == FormPilotXShell.ShellAction.Reabrir)
                        {
                            try { Process.Start(Application.ExecutablePath); } catch { }
                        }
                    }
                    return;
                }

                // Modo AOG normal.
                RegistrySettings.Load();
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(RegistrySettings.culture);
                Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(RegistrySettings.culture);

                int aogExitCode = 0;
                try
                {
                    Application.Run(new FormGPS());
                }
                catch (Exception)
                {
                    aogExitCode = 1;
                }

                // Al cerrarse AOG (X, Alt+F4, Application.Exit, crash), relanzamos
                // el mismo .exe en modo --shell. Lo hacemos ANTES de salir y en
                // un proceso nuevo para esquivar el flag de "exiting" del Application.
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--shell")
                    {
                        UseShellExecute = false
                    });
                }
                catch { }
                Environment.Exit(aogExitCode);
            }
        }
    }
}