// ============================================================================
// RustDeskIntegracion — soporte remoto integrado a PilotX, cero instalación
// manual.
//
// El paquete de PilotX trae el cliente RustDesk preconfigurado (server y clave
// pública viajan en el NOMBRE del exe, en la carpeta RustDesk\ junto a la
// app). Al arrancar PilotX:
//
//   · Si RustDesk ya está instalado → no hace nada (el heartbeat de OrbitX
//     ya reporta el ID al CRM).
//   · Si no está y el paquete lo trae → dispara UNA elevación (UAC) que
//     instala el servicio y fija la clave de acceso desatendido. Es el único
//     clic que existe, la primera vez y nunca más — Windows no permite
//     instalar un servicio sin él.
//   · Si el operario rechaza el UAC → no insiste en esta sesión; reintenta
//     en el próximo arranque.
//
// Con esto: copiar PilotX + arrancar = pantalla con soporte remoto, ID
// visible en el CRM, sin instalar nada a mano.
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;

namespace PilotX.Desktop;

public static class RustDeskIntegracion
{
    public static void AsegurarEnSegundoPlano() => Task.Run(Asegurar);

    private static void Asegurar()
    {
        try
        {
            string instalado = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "RustDesk", "rustdesk.exe");
            if (File.Exists(instalado)) return;   // ya está: nada que hacer

            // El cliente empaquetado: RustDesk\rustdesk-host=<server>,key=<...>.exe
            // (el nombre ES la configuración; no renombrar).
            string dir = Path.Combine(AppContext.BaseDirectory, "RustDesk");
            if (!Directory.Exists(dir)) return;
            string? bundled = null;
            foreach (var f in Directory.GetFiles(dir, "rustdesk-host=*.exe"))
            {
                bundled = f;
                break;
            }
            if (bundled == null) return;

            // La clave de acceso desatendido viaja en RustDesk\clave.txt del
            // paquete (Build está fuera de git: la clave NUNCA va al repo).
            // Sin archivo no hay clave que fijar: se instala igual, pero el
            // acceso desatendido queda sin configurar.
            string clave = null;
            string claveTxt = Path.Combine(dir, "clave.txt");
            if (File.Exists(claveTxt)) clave = File.ReadAllText(claveTxt).Trim();

            // Un .cmd temporal corre TODO el setup dentro de una sola elevación:
            // instalar servicio + fijar la clave desatendida.
            string cmd = Path.Combine(Path.GetTempPath(), "pilotx-rustdesk-setup.cmd");
            File.WriteAllText(cmd,
                "@echo off\r\n" +
                "\"" + bundled + "\" --silent-install\r\n" +
                "timeout /t 10 /nobreak >nul\r\n" +
                (string.IsNullOrEmpty(clave) ? "" :
                    "\"" + instalado + "\" --password " + clave + "\r\n"));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cmd,
                UseShellExecute = true,          // necesario para Verb=runas
                Verb = "runas",                  // el ÚNICO UAC, primera vez
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(180000);
            try { File.Delete(cmd); } catch { }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC rechazado (1223) o sin permisos: no insistir en esta sesión.
            // El próximo arranque vuelve a ofrecer la elevación.
        }
        catch
        {
            // Best-effort absoluto: el soporte remoto jamás puede frenar
            // el arranque de la pantalla.
        }
    }
}
