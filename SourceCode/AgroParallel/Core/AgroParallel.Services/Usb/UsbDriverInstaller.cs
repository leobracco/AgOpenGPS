using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AgroParallel.Usb
{
    // Instala los drivers USB-serial bundleados con pnputil (elevado). La acción
    // la dispara SIEMPRE el operario con un botón + consentimiento UAC; nunca es
    // automática. Idempotente (reinstalar no rompe).
    public static class UsbDriverInstaller
    {
        // Códigos de salida de pnputil que documenta Microsoft como ÉXITO para
        // /add-driver /install: 0 = OK sin más trámite; 259 = ERROR_NO_MORE_ITEMS
        // (ya estaba instalado, nada que agregar); 3010 = ERROR_SUCCESS_REBOOT_REQUIRED
        // (instaló el driver pero pide reinicio — típico en la primera instalación
        // del CP210x/CH340). Cualquier otro código es falla real.
        internal static bool ExitCodeEsExito(int code)
        {
            return code == 0 || code == 259 || code == 3010;
        }

        public static bool Instalar(string engineBaseDir, string driver, out string codigoError)
        {
            codigoError = null;
            var dirs = new List<string>();
            string baseDrv = Path.Combine(engineBaseDir, "tools", "usb-drivers");
            if (driver == "ambos") { dirs.Add(Path.Combine(baseDrv, "cp210x")); dirs.Add(Path.Combine(baseDrv, "ch340")); }
            else dirs.Add(Path.Combine(baseDrv, driver));

            bool algo = false;
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var inf in Directory.GetFiles(d, "*.inf"))
                {
                    algo = true;
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        UseShellExecute = true,   // requerido para Verb=runas (UAC)
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    // netstandard2.0 no expone ProcessStartInfo.ArgumentList (llegó en
                    // .NET Core 2.1). Reusamos el mismo citado a mano que UsbFlashService
                    // (ArmarLineaDeComando, algoritmo PasteArguments del propio runtime)
                    // para no duplicar el algoritmo ni armar la línea con concat insegura.
                    psi.Arguments = UsbFlashService.ArmarLineaDeComando(
                        new[] { "/add-driver", inf, "/install", "/subdirs" });
                    try
                    {
                        using (var p = Process.Start(psi)) { p.WaitForExit(); if (!ExitCodeEsExito(p.ExitCode)) { codigoError = "AGP-USB-006"; return false; } }
                    }
                    catch (Exception) { codigoError = "AGP-USB-006"; return false; }  // UAC rechazado o pnputil ausente
                }
            }
            if (!algo) { codigoError = "AGP-USB-004"; return false; }
            return true;
        }
    }
}
