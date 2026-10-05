// ============================================================================
// ReporteFallaClient.cs — cliente HTTP del panel "Reportar falla".
//
// La pantalla no habla con el cloud: le pasa al Engine (host local :5180) lo
// que sólo ella tiene — la captura y SUS logs — y el Engine arma, encola y
// sube el ZIP (ReporteFallaService).
//
//   POST /api/soporte/reporte          ← { descripcion, captura_png_b64, logs_pantalla }
//   GET  /api/soporte/reporte/estado?codigo=
//   POST /api/soporte/reporte/pendrive ← { codigo }
//
// Logs de la pantalla que viajan (las colas, no el archivo entero):
//   Logs\errores.log, Logs\salida-anterior.log, Logs\salida.log
// Se leen con FileShare.ReadWrite: salida.log lo está escribiendo el propio
// vigilante mientras tanto.
//
// Mismas trampas del wire que ChatPanelClient: BOM de EmbedIO, snake_case, y
// corte por CancellationToken en vez de Timeout del HttpClient.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class ReporteFallaRespuesta
{
    public bool Ok { get; set; }
    public string? Codigo { get; set; }
    public string? Estado { get; set; }      // "en_cola" | "subido"
    public bool Vinculado { get; set; }
    public string? Ruta { get; set; }
    public string? Error { get; set; }
}

public sealed class ReporteFallaClient
{
    private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    // Armar el ZIP lee logs y comprime: puede tardar unos segundos en una
    // pantalla lenta. El estado y el pendrive son rápidos.
    private static readonly TimeSpan CorteCrear = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CorteCorto = TimeSpan.FromSeconds(15);

    private const int MaxBytesLog = 512 * 1024;

    private readonly string _baseUrl;

    public ReporteFallaClient(string baseUrl = "http://127.0.0.1:5180/")
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                                                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public async Task<ReporteFallaRespuesta> CrearAsync(string descripcion, byte[]? capturaPng, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["descripcion"] = descripcion ?? "",
            ["captura_png_b64"] = capturaPng != null && capturaPng.Length > 0 ? Convert.ToBase64String(capturaPng) : null,
            ["logs_pantalla"] = LogsDeLaPantalla(),
        };
        return await PostAsync("api/soporte/reporte", JsonSerializer.Serialize(body), CorteCrear, ct).ConfigureAwait(false);
    }

    public Task<ReporteFallaRespuesta> PendriveAsync(string codigo, CancellationToken ct = default)
        => PostAsync("api/soporte/reporte/pendrive",
                     JsonSerializer.Serialize(new Dictionary<string, string> { ["codigo"] = codigo ?? "" }),
                     CorteCorto, ct);

    public async Task<ReporteFallaRespuesta> EstadoAsync(string codigo, CancellationToken ct = default)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(CorteCorto);
            using var resp = await _http.GetAsync(_baseUrl + "api/soporte/reporte/estado?codigo="
                                                  + Uri.EscapeDataString(codigo ?? ""), corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return Parsear(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ReporteFallaRespuesta { Ok = false, Error = "cancelado" };
        }
        catch (Exception ex)
        {
            return new ReporteFallaRespuesta { Ok = false, Error = Motivo(ex) };
        }
    }

    private async Task<ReporteFallaRespuesta> PostAsync(string ruta, string body, TimeSpan tope, CancellationToken ct)
    {
        try
        {
            using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
            corte.CancelAfter(tope);
            using var contenido = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_baseUrl + ruta, contenido, corte.Token).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(corte.Token).ConfigureAwait(false);
            return Parsear(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ReporteFallaRespuesta { Ok = false, Error = "cancelado" };
        }
        catch (Exception ex)
        {
            return new ReporteFallaRespuesta { Ok = false, Error = Motivo(ex) };
        }
    }

    private static ReporteFallaRespuesta Parsear(string json)
    {
        var r = new ReporteFallaRespuesta();
        using var doc = JsonDocument.Parse(Limpio(json));
        var root = doc.RootElement;
        r.Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        r.Vinculado = root.TryGetProperty("vinculado", out var v) && v.ValueKind == JsonValueKind.True;
        r.Codigo = Texto(root, "codigo");
        r.Estado = Texto(root, "estado");
        r.Ruta = Texto(root, "ruta");
        r.Error = Texto(root, "error");
        return r;
    }

    private static string? Texto(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string Limpio(string json)
        => string.IsNullOrEmpty(json) ? "{}" : json.TrimStart('﻿');

    private static string Motivo(Exception ex)
    {
        if (ex is OperationCanceledException) return "el motor no respondió a tiempo";
        if (ex is HttpRequestException) return "el motor de PilotX no responde";
        return ex.Message;
    }

    /// <summary>Colas de los logs de la pantalla (al lado del ejecutable, en
    /// Logs\). Lo que no existe simplemente no viaja.</summary>
    public static Dictionary<string, string> LogsDeLaPantalla()
    {
        var res = new Dictionary<string, string>();
        string dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        foreach (var nombre in new[] { "errores.log", "salida-anterior.log", "salida.log" })
        {
            try
            {
                string ruta = Path.Combine(dir, nombre);
                if (!File.Exists(ruta)) continue;
                res[nombre] = LeerCola(ruta, MaxBytesLog);
            }
            catch { /* un log trabado no frena el reporte */ }
        }
        return res;
    }

    private static string LeerCola(string ruta, int max)
    {
        using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long largo = fs.Length;
        if (largo > max) fs.Seek(largo - max, SeekOrigin.Begin);
        var buf = new byte[Math.Min(largo, max)];
        int total = 0;
        while (total < buf.Length)
        {
            int n = fs.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return Encoding.UTF8.GetString(buf, 0, total);
    }
}
