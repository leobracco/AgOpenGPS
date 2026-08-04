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
using System.Text.Json.Serialization;
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
        /// <summary>Revisión de la geometría proyectada (ver ShapeSnapshot.GeomRev).</summary>
        [JsonPropertyName("geom_rev")]
        public string? GeomRev { get; set; }
        public int Count { get; set; }
        public string? StyleField { get; set; }
        public double StyleMin { get; set; }
        public double StyleMax { get; set; }
        public List<WirePolygon>? Polygons { get; set; }
        /// <summary>true = el server confirma que el shape es el mismo del
        /// token enviado: respuesta corta, no viene ningún polígono.</summary>
        public bool Unchanged { get; set; }
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
    // Último SourceToken aplicado: viaja como ?token= y el server contesta
    // "unchanged" de 40 bytes en vez del shapefile entero (que bajábamos y
    // deserializábamos ENTERO cada segundo aunque no cambiara nada).
    private string _lastToken = "";
    // Revisión de la proyección ya aplicada; viaja como ?rev= (ver GeomRev).
    private string _lastRev = "";

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
                string url = _baseUrl + "api/aog/shape" +
                    (string.IsNullOrEmpty(_lastToken) ? "" : "?token=" + Uri.EscapeDataString(_lastToken)
                        + (string.IsNullOrEmpty(_lastRev) ? "" : "&rev=" + Uri.EscapeDataString(_lastRev)));
                using var resp = await _http.GetAsync(url, _cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
                    // El endpoint devuelve null (literal) cuando no hay shape.
                    if (string.IsNullOrWhiteSpace(json) || json == "null")
                    {
                        if (_lastKey != "")
                        {
                            _lastKey = "";
                            _lastToken = "";
                            _onSnapshot(null);
                        }
                    }
                    else
                    {
                        var wire = JsonSerializer.Deserialize<WireSnapshot>(json, JsonOpts);
                        // Respuesta corta (?token= coincidió): mismo shape que
                        // ya está aplicado, no vino ni un polígono — saltear.
                        if (wire != null && wire.Unchanged)
                        {
                            // nada que hacer este tick
                        }
                        else
                        {
                        // La clave de cache incluye GEOMETRÍA (primer vértice y
                        // total de puntos), no solo nombre/cantidad/campo: subir
                        // un shape corregido con el mismo nombre es un caso real
                        // (pasó con el de prueba, desalineado y resubido) y con
                        // la clave vieja el mapa se quedaba dibujando las zonas
                        // anteriores sin ningún error a la vista.
                        string key = wire == null ? "" : ClaveDe(wire);
                        if (wire != null && key != _lastKey)
                        {
                            _lastKey = key;
                            _lastToken = wire.SourceToken ?? "";
                            _lastRev = wire.GeomRev ?? "";
                            _onSnapshot(Convertir(wire));
                        }
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

    private static string ClaveDe(WireSnapshot wire)
    {
        int puntos = 0;
        double e0 = 0, n0 = 0;
        if (wire.Polygons != null)
        {
            foreach (var p in wire.Polygons)
            {
                if (p.Rings == null) continue;
                foreach (var r in p.Rings) puntos += r?.Length ?? 0;
            }
            var pri = wire.Polygons.Count > 0 ? wire.Polygons[0].Rings : null;
            if (pri != null && pri.Count > 0 && pri[0] != null && pri[0].Length >= 2)
            {
                e0 = pri[0][0];
                n0 = pri[0][1];
            }
        }
        return $"{wire.SourceToken}|{wire.Count}|{wire.StyleField}|{puntos}|{e0:F1}|{n0:F1}";
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
            // Piso de alpha para el FILL: el .shp trae ~30% y sobre el fondo
            // oscuro del mapa eso no se distingue — probado en cabina ("no veo
            // fondo"). 55% mantiene legible lo que pasa por arriba (cobertura,
            // lindero, tractor) pero deja la zona claramente pintada.
            float alpha = Math.Max(p.A / 255f, 0.55f);
            var poly = new ShapeMapPolygon
            {
                Rgba = new[] { p.R / 255f, p.G / 255f, p.B / 255f, alpha },
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
