// ============================================================================
// MainWindow.AutoridadControl.cs — la cabina en la autoridad de control.
//
// 1) LATE cada 1,5 s (POST /api/control/latido): mientras esta pantalla esté
//    viva, un celular no puede tomar el control sin que el operario lo ceda.
// 2) Muestra el indicador ControlIndicador (chico, arriba-derecha) SOLO cuando
//    hay algo que decir: un pedido de control, un remoto con el control, o
//    (en modo solo registro) que alguien accionó desde la red hace poco.
// Contra un motor viejo sin /api/control el latido da null y el indicador
// queda escondido: no rompe nada.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using PilotX.Desktop.Services;

namespace PilotX.Desktop;

public partial class MainWindow
{
    private const int ControlLatidoMs = 1500;
    /// <summary>Cuánto queda a la vista el "accionó desde la red" (modo registro).</summary>
    private const long ControlAvisoAccionMs = 15000;

    private ControlClient? _controlCli;
    private CancellationTokenSource? _controlCts;
    private Border? _controlIndicador;
    private TextBlock? _controlTexto;
    private StackPanel? _controlBotones;
    private Button? _controlBtnCeder, _controlBtnNo, _controlBtnRecuperar;
    private bool _controlEraRemoto;

    private void ArrancarAutoridadControl()
    {
        _controlIndicador = this.FindControl<Border>("ControlIndicador");
        _controlTexto = this.FindControl<TextBlock>("ControlIndicadorTexto");
        _controlBotones = this.FindControl<StackPanel>("ControlIndicadorBotones");
        _controlBtnCeder = this.FindControl<Button>("ControlBtnCeder");
        _controlBtnNo = this.FindControl<Button>("ControlBtnNo");
        _controlBtnRecuperar = this.FindControl<Button>("ControlBtnRecuperar");

        _controlCli = new ControlClient(DeriveOrigin(App.TargetUrl));
        _controlCts = new CancellationTokenSource();
        var ct = _controlCts.Token;
        Closed += (_, _) => { try { _controlCts?.Cancel(); } catch { } };

        if (_controlBtnCeder != null) _controlBtnCeder.Click += async (_, __) => PintarControl(await _controlCli.CederAsync(ct));
        if (_controlBtnNo != null) _controlBtnNo.Click += async (_, __) => PintarControl(await _controlCli.RechazarAsync(ct));
        if (_controlBtnRecuperar != null) _controlBtnRecuperar.Click += async (_, __) => PintarControl(await _controlCli.RecuperarAsync(ct));

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                ControlEstadoWire? e = null;
                try { e = await _controlCli.LatidoAsync(ct).ConfigureAwait(false); }
                catch { /* el host local puede no estar listo todavía */ }

                try { await Dispatcher.UIThread.InvokeAsync(() => PintarControl(e)); }
                catch { }

                try { await Task.Delay(ControlLatidoMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    private void PintarControl(ControlEstadoWire? e)
    {
        if (_controlIndicador == null || _controlTexto == null) return;
        var tr = PilotX.Cockpit.Bars.Traductor.T;

        if (e == null || !e.Ok)
        {
            _controlIndicador.IsVisible = false;
            return;
        }

        // El control volvió solo a la cabina (el remoto dejó de responder o lo
        // soltó): decirlo una vez. Si además se soltó el piloto, el cartel del
        // piloto lo dice aparte (AvisarPiloto en el motor).
        if (_controlEraRemoto && e.DuenoEsCabina)
            MostrarToast(tr("El control volvió a esta pantalla."));
        _controlEraRemoto = !e.DuenoEsCabina;

        bool verCeder = false, verRecuperar = false;
        string? texto = null;

        if (e.HayPedido)
        {
            texto = Quien(e.PedidoNombre, e.PedidoIp) + " " + tr("pide el control");
            verCeder = true;
        }
        else if (!e.DuenoEsCabina)
        {
            texto = tr("Control") + ": " + Quien(e.DuenoNombre, e.DuenoIp);
            verRecuperar = true;
        }
        else if (e.UltimaAccionRemota != null && e.UltimaAccionRemota.HaceMs < ControlAvisoAccionMs)
        {
            texto = tr("Accionó desde la red") + ": " + Quien(e.UltimaAccionRemota.Nombre, e.UltimaAccionRemota.Ip);
        }

        if (texto == null)
        {
            _controlIndicador.IsVisible = false;
            return;
        }

        _controlTexto.Text = texto;
        if (_controlBotones != null) _controlBotones.IsVisible = verCeder || verRecuperar;
        if (_controlBtnCeder != null) _controlBtnCeder.IsVisible = verCeder;
        if (_controlBtnNo != null) _controlBtnNo.IsVisible = verCeder;
        if (_controlBtnRecuperar != null) _controlBtnRecuperar.IsVisible = verRecuperar;
        _controlIndicador.IsVisible = true;
    }

    private static string Quien(string? nombre, string? ip)
    {
        string n = string.IsNullOrWhiteSpace(nombre) ? "?" : nombre!.Trim();
        if (string.IsNullOrWhiteSpace(ip) || n.Contains(ip!)) return n;
        return n + " (" + ip + ")";
    }
}
