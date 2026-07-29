// ============================================================================
// VehicleSpriteClient.cs — trae el sprite del vehículo elegido en Configuración
// y lo entrega listo para subir a GPU (RGBA sin premultiplicar).
//
// Flujo: GET /api/vehicle/sprites → campo "activo" (el archivo elegido) →
// descarga /img/vehiculos/<archivo> → decodifica con Avalonia → RGBA.
//
// Prefiere la variante ".mapa" si existe: el arte trae versiones sin ruedas
// pensadas para verse desde arriba en el mapa (rigido_pauny.mapa.png), que es
// lo que corresponde dibujar sobre el lote.
// ============================================================================

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.UI.Services;

public sealed class VehicleSpriteClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _baseUrl;

    public VehicleSpriteClient(string baseUrl)
    {
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:5180/"
                 : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    }

    public sealed class Sprite
    {
        public byte[] Rgba { get; init; } = Array.Empty<byte>();
        public int Width { get; init; }
        public int Height { get; init; }
        public string Archivo { get; init; } = "";
    }

    /// <summary>
    /// Devuelve el sprite activo, o null si no hay ninguno elegido o falla algo.
    /// Nunca tira: el mapa cae al triángulo y sigue andando.
    /// </summary>
    public async Task<Sprite?> GetActivoAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(_baseUrl + "api/vehicle/sprites", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("activo", out var act)) return null;
            string archivo = act.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(archivo)) return null;

            // Variante ".mapa" primero (vista cenital sin ruedas).
            string sinExt = Path.GetFileNameWithoutExtension(archivo);
            string mapa = sinExt + ".mapa.png";
            byte[]? png = await BajarAsync(mapa, ct).ConfigureAwait(false)
                       ?? await BajarAsync(archivo, ct).ConfigureAwait(false);
            if (png == null) return null;

            return Decodificar(png, archivo);
        }
        catch
        {
            return null;   // sin sprite: el mapa dibuja el triángulo
        }
    }

    /// <summary>
    /// Textura de la rueda delantera (rueda.png). Se dibuja aparte del cuerpo
    /// y girada por el ángulo de dirección — por eso el arte del tractor no
    /// trae ruedas delanteras.
    /// </summary>
    public async Task<Sprite?> GetRuedaAsync(CancellationToken ct = default)
    {
        try
        {
            byte[]? png = await BajarAsync("rueda.png", ct).ConfigureAwait(false);
            if (png == null) return null;
            return Decodificar(png, "rueda.png");
        }
        catch { return null; }
    }

    /// <summary>
    /// Sprite del implemento (sembradora, pulverizadora…). Por ahora va por
    /// convención de nombre en /img/implementos/; cuando haya selector de
    /// implemento se elegirá igual que el vehículo.
    /// </summary>
    public async Task<Sprite?> GetImplementoAsync(string archivo = "sembradora.png", CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "img/implementos/" + archivo, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var png = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Decodificar(png, archivo);
        }
        catch { return null; }
    }

    private static Sprite? Decodificar(byte[] png, string archivo)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        if (w <= 0 || h <= 0) return null;

        int stride = w * 4;
        var buf = new byte[stride * h];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride); }
        finally { handle.Free(); }

        for (int i = 0; i < buf.Length; i += 4)
        {
            byte b = buf[i], g = buf[i + 1], r = buf[i + 2], a = buf[i + 3];
            if (a != 0 && a != 255)
            {
                r = (byte)Math.Min(255, r * 255 / a);
                g = (byte)Math.Min(255, g * 255 / a);
                b = (byte)Math.Min(255, b * 255 / a);
            }
            buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = a;
        }
        return new Sprite { Rgba = buf, Width = w, Height = h, Archivo = archivo };
    }

    private async Task<byte[]?> BajarAsync(string archivo, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_baseUrl + "img/vehiculos/" + archivo, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch { return null; }
    }
}
