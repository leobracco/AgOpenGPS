// ============================================================================
// ShapeGeometryPoller.cs — cliente + poller de /api/aog/shape para el mapa GL.
//
// La prescripción (.shp) es la base de dosificación de QuantiX y FlowX: si no
// se VE sobre el mapa, el operario no puede contrastar lo que la máquina está
// aplicando con la zona en la que está parado.
//
// Cadencia 1 Hz, igual que tram/paths: el shape solo cambia al subir otro o
// al cambiar el campo de dosis. Se filtra por (source_token, count,
// style_field): si no cambió nada, no se re-triangula ni se retoca el mapa.
//
// La triangulación (ear clipping, el MISMO EarClipper que usa el WinForms,
// linkeado) se hace acá en el hilo del poller, no en el hilo GL: con cientos
// de zonas es trabajo de una sola vez por shape y el render no tiene por qué
// pagarlo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class ShapeMapPolygon
{
    /// <summary>Triángulos del anillo exterior, listos para GL: x,y,x,y…</summary>
    public float[] TriVerts = Array.Empty<float>();

    /// <summary>Anillos (exterior + agujeros) para el contorno: x,y,x,y…</summary>
    public List<float[]> Rings = new();

    /// <summary>RGBA 0..1 del relleno (color por dosis si hay campo).</summary>
    public float[] Rgba = { 0f, 0.78f, 1f, 0.31f };
}

public sealed class ShapeMapSnapshot
{
    public string SourceToken = "";
    public string? StyleField;
    public double StyleMin, StyleMax;
    public List<ShapeMapPolygon> Polygons = new();
}

public sealed class ShapeGeometryPoller : IDisposable
{
    private sealed class WirePolygon
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }
        public List<double[]>? Rings { get; set; }
    }

    private sealed class WireSnapshot
    {
        public string? SourceToken { get; set; }
        public int Count { get; set; }
        public string? StyleField { get; set; }
        public double StyleMin { get; set; }
        public double StyleMax { get; set; }
        public List<WirePolygon>? Polygons { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Action<ShapeMapSnapshot?> _onSnapshot;
    private readonly CancellationTokenSource _cts = new();

    private string _lastKey = "";

    public ShapeGeometryPoller(string baseUrl, Action<ShapeMapSnapshot?> onSnapshot)
    {
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _onSnapshot = onSnapshot;
        // Timeout corto y propio: el poller no puede colgar el resto si el
        // motor está ocupado (ver feedback HttpClient.Timeout).
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(_baseUrl + "api/aog/shape", _cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
                    // El endpoint devuelve null (literal) cuando no hay shape.
                    if (string.IsNullOrWhiteSpace(json) || json == "null")
                    {
                        if (_lastKey != "")
                        {
                            _lastKey = "";
                            _onSnapshot(null);
                        }
                    }
                    else
                    {
                        var wire = JsonSerializer.Deserialize<WireSnapshot>(json, JsonOpts);
                        string key = wire == null ? "" : $"{wire.SourceToken}|{wire.Count}|{wire.StyleField}";
                        if (wire != null && key != _lastKey)
                        {
                            _lastKey = key;
                            _onSnapshot(Convertir(wire));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch { /* motor reiniciando: se reintenta en el próximo tick */ }

            try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static ShapeMapSnapshot Convertir(WireSnapshot wire)
    {
        var snap = new ShapeMapSnapshot
        {
            SourceToken = wire.SourceToken ?? "",
            StyleField = wire.StyleField,
            StyleMin = wire.StyleMin,
            StyleMax = wire.StyleMax,
        };
        if (wire.Polygons == null) return snap;

        foreach (var p in wire.Polygons)
        {
            if (p.Rings == null || p.Rings.Count == 0) continue;
            var poly = new ShapeMapPolygon
            {
                Rgba = new[] { p.R / 255f, p.G / 255f, p.B / 255f, p.A / 255f },
            };

            foreach (var ring in p.Rings)
            {
                if (ring == null || ring.Length < 6) continue;
                var f = new float[ring.Length];
                for (int i = 0; i < ring.Length; i++) f[i] = (float)ring[i];
                poly.Rings.Add(f);
            }
            if (poly.Rings.Count == 0) continue;

            // Triangular SOLO el exterior, igual que el fill del WinForms: los
            // agujeros quedan como contorno. Ear clipping compartido.
            var outer = poly.Rings[0];
            int n = outer.Length / 2;
            var pts = new System.Drawing.PointF[n];
            for (int i = 0; i < n; i++) pts[i] = new System.Drawing.PointF(outer[i * 2], outer[i * 2 + 1]);
            try
            {
                var idx = AgroParallel.Common.EarClipper.Triangulate(pts);
                var tv = new float[idx.Count * 2];
                for (int i = 0; i < idx.Count; i++)
                {
                    tv[i * 2] = pts[idx[i]].X;
                    tv[i * 2 + 1] = pts[idx[i]].Y;
                }
                poly.TriVerts = tv;
            }
            catch { /* triangulación atascada: queda solo el contorno */ }

            snap.Polygons.Add(poly);
        }
        return snap;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
