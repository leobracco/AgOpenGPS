// ============================================================================
// TecladoPoller.cs — abre y cierra la ventana del teclado según lo que pase en
// las páginas del Hub.
//
// Las páginas corren en un WebView embebido y no tienen línea directa con el
// shell, así que el estado del teclado viaja por el engine
// (TecladoController). Este poller:
//
//   · late contra /api/teclado/presente, que es como el shell dice "yo tengo
//     el teclado nativo" — si deja de latir (PilotX cerrado, o el Hub abierto
//     desde el celular), la página vuelve sola a su teclado HTML;
//   · abre/oculta TecladoWindow cuando cambia el estado.
//
// El latido y la lectura son la MISMA llamada: la respuesta de /presente ya
// trae el estado, así que es un request por ciclo y no dos.
// ============================================================================

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PilotX.Desktop
{
    public sealed class TecladoPoller
    {
        private readonly HttpClient _http;
        private readonly string _url;
        private CancellationTokenSource? _cts;
        private long _ultimaSeq = -1;
        // Generación de "ocultar": cada cambio de estado la incrementa. Un ocultar
        // pendiente solo se ejecuta si su generación sigue siendo la vigente; si
        // el teclado se reabre antes (parpadeo de foco al teclear), se cancela.
        private int _genOcultar;

        public TecladoPoller(string baseUrl)
        {
            _url = baseUrl.TrimEnd('/') + "/api/teclado/presente";
            // Timeout corto: es un latido, si no contesta se reintenta en el
            // próximo ciclo. Sin esto un cuelgue del engine congela el loop.
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }

        public void Stop()
        {
            var c = _cts; _cts = null;
            try { c?.Cancel(); c?.Dispose(); } catch { }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            // ESPERAR a que Avalonia esté levantada antes de tocar nada suyo.
            // Dispatcher.UIThread, leído desde un hilo del pool antes de que la
            // app arranque, se queda con ESE hilo como "hilo de UI": después el
            // bucle principal explota con PlatformNotSupportedException y la
            // pantalla no abre. Costó un arranque en blanco descubrirlo.
            while (!ct.IsCancellationRequested && Avalonia.Application.Current == null)
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var body = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var res = await _http.PostAsync(_url, body, ct).ConfigureAwait(false);
                    var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var raiz = doc.RootElement;
                    bool abierto = raiz.TryGetProperty("abierto", out var a) && a.GetBoolean();
                    bool numerico = raiz.TryGetProperty("numerico", out var n) && n.GetBoolean();
                    string titulo = raiz.TryGetProperty("titulo", out var t) ? (t.GetString() ?? "") : "";
                    long seq = raiz.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;

                    if (seq != _ultimaSeq)
                    {
                        _ultimaSeq = seq;
                        if (abierto)
                        {
                            // Reabrir cancela cualquier ocultar pendiente.
                            Interlocked.Increment(ref _genOcultar);
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                try { TecladoWindow.Mostrar(numerico, titulo); }
                                catch (Exception ex)
                                {
                                    // Sin esto, un error al construir la ventana (un XAML
                                    // que no parsea, por ejemplo) se perdía y el síntoma
                                    // era "el teclado no aparece", sin ninguna pista.
                                    System.Diagnostics.Debug.WriteLine("[teclado] " + ex);
                                    Console.WriteLine("[teclado] no pude mostrar la ventana: " + ex.Message);
                                }
                            });
                        }
                        else
                        {
                            // NO ocultar en el acto: al tocar una tecla el campo
                            // parpadea el foco (LostFocus→GotFocus) y manda
                            // cerrar+abrir; ocultar ya haría titilar el teclado en
                            // cada tecla y lo devolvería a su posición inicial.
                            // Esperamos una gracia > intervalo de poll; si se
                            // reabre, esta generación queda vieja y no oculta.
                            int gen = Interlocked.Increment(ref _genOcultar);
                            _ = Task.Run(async () =>
                            {
                                try { await Task.Delay(700, ct).ConfigureAwait(false); }
                                catch { return; }
                                if (gen != Volatile.Read(ref _genOcultar)) return;
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    try { TecladoWindow.Ocultar(); } catch { }
                                });
                            });
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch
                {
                    // Engine caído o arrancando: se reintenta. El teclado nativo
                    // nunca puede tirar abajo la pantalla.
                }
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
