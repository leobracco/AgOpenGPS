// ============================================================================
// FxOtaTab.cs — pantalla "Firmware" (actualización OTA del nodo).
//
// QUE QUEDO NATIVO: la pestana 5 de pages/flowx.html — URL del .bin, version,
// SHA-256 opcional y el envio del comando ota con su confirmacion.
// QUE SIGUE EN HTML: la misma pestana en pages/flowx.html, para la PWA, y el
// Hub de firmwares (pages/firmwares.html) donde se cargan los .bin desde USB.
// QUE NO SE PORTA: la pestana "Próximamente" (lista de roadmap sin funcion).
//
// El .bin lo sirve la propia pantalla; el nodo lo baja por HTTP en la LAN y
// verifica el SHA-256 si se lo damos. El firmware cierra secciones y frena la
// bomba antes de reflashear, pero igual se pregunta antes: en medio de una
// pulverizacion esto para la maquina.
// ============================================================================

using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using PilotX.Desktop.Services;

namespace PilotX.Desktop.Views.FlowXEditor;

public sealed class FxOtaTab : FxTab
{
    private static readonly Regex Sha256 = new Regex("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    private TextBox? _url;
    private TextBox? _version;
    private TextBox? _sha;

    public FxOtaTab(FxCtx c) : base(c) { }

    public override void Rebuild()
    {
        Children.Clear();

        var n = C.NodoActual();
        if (n == null)
        {
            Children.Add(FxUi.Sub("Elegí un nodo arriba para actualizarle el firmware."));
            return;
        }

        Children.Add(FxUi.Sub("Le dice al nodo que baje un archivo de firmware servido por la pantalla "
                            + "y se actualice. Verifica el SHA-256 si se lo cargás. Aborta cualquier "
                            + "calibración o auto-tune en curso."));

        var g = FxUi.Grilla();
        _url = FxUi.Entrada(C.Client, _url?.Text ?? "", false, "URL del archivo de firmware", 380);
        _url.Watermark = "http://192.168.5.10:5180/api/firmwares/flowx/1.9.0/firmware.bin";
        g.Children.Add(FxUi.Campo("URL del archivo (.bin)", _url, 390));

        _version = FxUi.Entrada(C.Client, _version?.Text ?? "", false, "Versión", 120);
        _version.Watermark = "1.9.0";
        g.Children.Add(FxUi.Campo("Versión", _version, 130));

        _sha = FxUi.Entrada(C.Client, _sha?.Text ?? "", false, "SHA-256 (opcional)", 380);
        _sha.Watermark = PilotX.Cockpit.Bars.Traductor.T("64 caracteres, opcional");
        g.Children.Add(FxUi.Campo("SHA-256 (opcional)", _sha, 390));

        Children.Add(g);
        Children.Add(FxUi.Boton("Actualizar firmware del nodo", () => _ = OtaAsync(n), peligro: true));
    }

    private async Task OtaAsync(FlowXNodoConfig n)
    {
        if (C.Alertar == null || C.Confirmar == null) return;
        string uid = n.Uid ?? "";
        if (string.IsNullOrEmpty(uid)) return;

        string url = (_url?.Text ?? "").Trim();
        string version = (_version?.Text ?? "").Trim();
        string sha = (_sha?.Text ?? "").Trim();

        if (url.Length == 0 || version.Length == 0)
        {
            await C.Alertar("Faltan datos",
                "La dirección del archivo y la versión son obligatorias.").ConfigureAwait(true);
            return;
        }
        if (sha.Length > 0 && !Sha256.IsMatch(sha))
        {
            await C.Alertar("SHA-256 inválido",
                "Tiene que ser de 64 caracteres, o dejarlo vacío para no verificar.").ConfigureAwait(true);
            return;
        }

        bool ok = await C.Confirmar("Confirmar actualización",
            "El nodo va a cerrar las secciones, frenar la bomba y actualizarse a la versión "
          + version + ".\n\n¿Continuar?").ConfigureAwait(true);
        if (!ok) return;

        C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Enviando la actualización…"), "");
        object payload = sha.Length > 0
            ? new { url, version, sha256 = sha }
            : (object)new { url, version };
        var r = await C.Client.SendCmdAsync(uid, "ota", payload, C.Ct).ConfigureAwait(true);
        if (r.Ok)
            C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T(
                "Actualización enviada — esperá a que el nodo se reinicie."), "ok");
        else
            C.Estado?.Invoke(PilotX.Cockpit.Bars.Traductor.T("Error") + ": " + r.Texto(), "err");
    }
}
