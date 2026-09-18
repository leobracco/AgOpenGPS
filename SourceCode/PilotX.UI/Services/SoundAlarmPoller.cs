// ============================================================================
// SoundAlarmPoller.cs — el que hace SONAR la cabina.
//
// Pollea /api/sonidos/estado cada 500 ms y por cada disparo nuevo (seq mayor
// al último visto) baja el wav (cacheado por nombre) y lo entrega al SINK de
// plataforma. El sink lo registra el head: en Windows, PilotX.Desktop lo
// implementa con winmm (PlaySound desde memoria, sin dependencias); un head
// Android lo implementaría con MediaPlayer. Este archivo es net9.0 portable.
//
// Arranca desde el seq ACTUAL (primer GET sin sonar): un reinicio de la UI no
// re-suena el historial. El mute viene del server (config compartida).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class SoundAlarmPoller : IDisposable
{
    /// <summary>Sink de plataforma: recibe el WAV completo en memoria.
    /// Lo registra el head (Desktop: winmm). Null = no suena nada.</summary>
    public static Action<byte[]>? WavSink;

    private sealed class WireDisparo
    {
        public long Seq { get; set; }
        public string? Evento { get; set; }
        public string? Sonido { get; set; }
    }
    private sealed class WireEstado
    {
        public long Seq { get; set; }
        public bool Mute { get; set; }
        /// <summary>Revisión de la carpeta /sounds del server (snake: archivos_rev).
        /// Si se mueve, algún .wav cambió y hay que tirar el cache.</summary>
        public long ArchivosRev { get; set; }
        public List<WireDisparo>? Disparos { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, byte[]> _cacheWav = new();

    private long _ultimoSeq = -1;   // -1 = todavía no sincronizado

    // Revisión de la carpeta de sonidos vista en el último GET. Sin dato
    // todavía = no se toca el cache (server viejo sin archivos_rev: queda el
    // comportamiento de antes, no rompe nada).
    private long _archivosRev;
    private bool _tieneArchivosRev;

    public SoundAlarmPoller(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                string url = _baseUrl + "api/sonidos/estado?desde=" + (_ultimoSeq < 0 ? long.MaxValue : _ultimoSeq);
                var json = await _http.GetStringAsync(url, _cts.Token).ConfigureAwait(false);
                var est = JsonSerializer.Deserialize<WireEstado>(json.TrimStart('﻿'), JsonOpts);
                if (est != null)
                {
                    // El operario pisó un .wav (mismo nombre, otro sonido): sin
                    // esto el cache seguiría entregando el viejo hasta reiniciar
                    // PilotX. Va ANTES de sonar los disparos de este mismo tick.
                    if (_tieneArchivosRev && est.ArchivosRev != _archivosRev)
                        lock (_cacheWav) _cacheWav.Clear();
                    _archivosRev = est.ArchivosRev;
                    _tieneArchivosRev = true;

                    if (_ultimoSeq < 0)
                    {
                        // Primera lectura: anclar sin sonar el pasado.
                        _ultimoSeq = est.Seq;
                    }
                    else
                    {
                        if (est.Disparos != null && !est.Mute && WavSink != null)
                        {
                            foreach (var d in est.Disparos)
                            {
                                if (d == null || d.Seq <= _ultimoSeq) continue;
                                var wav = await ObtenerWav(d.Sonido).ConfigureAwait(false);
                                if (wav != null)
                                {
                                    try { WavSink(wav); }
                                    catch { /* audio best-effort */ }
                                }
                            }
                        }
                        _ultimoSeq = Math.Max(_ultimoSeq, est.Seq);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch { /* motor reiniciando: próximo tick */ }

            try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<byte[]?> ObtenerWav(string? nombre)
    {
        if (string.IsNullOrEmpty(nombre)) return null;
        lock (_cacheWav)
        {
            if (_cacheWav.TryGetValue(nombre, out var hit)) return hit;
        }
        try
        {
            var datos = await _http.GetByteArrayAsync(_baseUrl + "sounds/" + Uri.EscapeDataString(nombre), _cts.Token)
                .ConfigureAwait(false);
            lock (_cacheWav) _cacheWav[nombre] = datos;
            return datos;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
