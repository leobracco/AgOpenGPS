// DemoAuto.cs — la demo que se maneja sola (Expo).
//
// Dos responsabilidades:
//   1. Dejar PilotX listo por su API local: esperar a que levante, abrir el
//      lote, activar la prescripcion y poner las secciones en automatico. Si
//      PilotX se reinicia, lo detecta y vuelve a hacerlo.
//   2. Conducir el tractor simulado por el lote como una sembradora real:
//      N vueltas de cabecera por el perimetro y despues pasadas paralelas
//      norte-sur separadas el ancho de labor, con giro en U en cada cabecera.
//      Al terminar vuelve a empezar (las secciones en automatico no repintan
//      lo pintado; para reiniciar la demo se borra la cobertura).
//
// El lote es un rectangulo de DemoAnchoM x DemoLargoM centrado en la posicion
// de arranque de BenchX (es el mismo rectangulo que se genero como KML para
// PilotX). La geometria se trabaja en metros locales con la misma proyeccion
// equirectangular con la que se armo el KML, asi la ruta cae exactamente
// adentro del lindero.
//
// Control: pure pursuit sobre una lista de puntos. La cinematica del
// simulador es la historica de ModSim (heading += paso * tan(deg*0.02)/2.5),
// asi que la curvatura pedida se convierte a angulo de rueda con esa formula
// y no con Ackermann real.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchX.Config;

namespace BenchX.Sim;

public sealed class DemoAuto : IDisposable
{
    private readonly BenchXConfig _cfg;
    private readonly double _lat0, _lon0, _mLat, _mLon;
    private readonly List<(double x, double y, bool giro)> _ruta = new();
    private int _idx;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _cts;
    private Task? _tarea;

    public bool Activo { get; set; }
    public string Estado { get; private set; } = "inactiva";
    public string EstadoPilotX { get; private set; } = "esperando PilotX";
    public bool PilotXListo { get; private set; }
    public int Vuelta { get; private set; }
    public double SalidaVelocidadKmh { get; private set; }
    public double SalidaAnguloDeg { get; private set; }

    public DemoAuto(BenchXConfig cfg)
    {
        _cfg = cfg;
        _lat0 = cfg.Latitud; _lon0 = cfg.Longitud;
        _mLat = 111320.0;
        _mLon = 111320.0 * Math.Cos(_lat0 * Math.PI / 180.0);
        ArmarRuta();
    }

    // ------------------------------------------------------------------
    //  Ruta
    // ------------------------------------------------------------------
    private void ArmarRuta()
    {
        _ruta.Clear();
        double W = _cfg.DemoAnchoM, H = _cfg.DemoLargoM, a = _cfg.DemoAnchoLaborM;
        if (W < 3 * a || H < 3 * a || a <= 0) { W = 500; H = 400; a = 7.28; }
        double x0 = -W / 2, x1 = W / 2, y0 = -H / 2, y1 = H / 2;

        // 1) Cabecera: vueltas por el perimetro, de afuera hacia adentro,
        //    sentido horario, arrancando en la esquina suroeste yendo al norte.
        int vueltas = Math.Max(0, _cfg.DemoVueltasCabecera);
        for (int k = 0; k < vueltas; k++)
        {
            double d = a / 2 + k * a;      // distancia del centro de la barra al lindero
            Recta(x0 + d, y0 + d, x0 + d, y1 - d);      // oeste, hacia el norte
            Recta(x0 + d, y1 - d, x1 - d, y1 - d);      // norte, hacia el este
            Recta(x1 - d, y1 - d, x1 - d, y0 + d);      // este, hacia el sur
            Recta(x1 - d, y0 + d, x0 + d + a, y0 + d);  // sur, hacia el oeste (sin cerrar del todo)
        }

        // 2) Pasadas norte-sur dentro de la cabecera, de oeste a este.
        double margen = vueltas * a;
        double xi = x0 + margen + a / 2, xf = x1 - margen - a / 2;
        double yi = y0 + margen, yf = y1 - margen;
        bool norte = true;
        for (double x = xi; x <= xf + 0.01; x += a)
        {
            if (norte) Recta(x, yi, x, yf); else Recta(x, yf, x, yi);
            if (x + a <= xf + 0.01)
            {
                // Giro en U hacia la pasada siguiente (semicirculo de radio a/2
                // que se mete unos metros en la cabecera pintada -> corte).
                double cy = norte ? yf : yi;
                ArcoU(x + a / 2, cy, a / 2, norte);
            }
            norte = !norte;
        }
        _idx = 0;
    }

    private void Recta(double ax, double ay, double bx, double by)
    {
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        int n = Math.Max(1, (int)(len / 5.0));
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            _ruta.Add((ax + (bx - ax) * t, ay + (by - ay) * t, false));
        }
    }

    private void ArcoU(double cx, double cy, double r, bool haciaNorte)
    {
        // Semicirculo de 180 grados por el lado de la cabecera. Entrando hacia
        // el norte el arco pasa por encima (y > cy); hacia el sur, por debajo.
        int n = 12;
        for (int i = 1; i < n; i++)
        {
            double ang = Math.PI * i / n;               // 0..pi
            double x = cx - r * Math.Cos(ang);          // de -r a +r
            double y = cy + (haciaNorte ? 1 : -1) * r * Math.Sin(ang);
            _ruta.Add((x, y, true));
        }
    }

    // ------------------------------------------------------------------
    //  Conduccion: se llama en cada tick (100 ms) con la posicion actual
    // ------------------------------------------------------------------
    public void Conducir(double lat, double lon, double headingDeg)
    {
        if (!Activo || _ruta.Count < 2) { SalidaVelocidadKmh = 0; SalidaAnguloDeg = 0; return; }
        if (!PilotXListo) { SalidaVelocidadKmh = 0; SalidaAnguloDeg = 0; Estado = "esperando PilotX"; return; }

        double px = (lon - _lon0) * _mLon, py = (lat - _lat0) * _mLat;

        // Avanzar el indice mientras el punto actual quede atras (a menos de 2 m).
        while (_idx < _ruta.Count - 1 && Dist(px, py, _ruta[_idx]) < 2.0) _idx++;
        if (_idx >= _ruta.Count - 1 && Dist(px, py, _ruta[^1]) < 3.0)
        {
            _idx = 0; Vuelta++;                         // vuelta completa: de nuevo
        }

        // Punto objetivo: el primero a mas de L metros por delante en la ruta.
        double L = 6.0;
        int j = _idx;
        while (j < _ruta.Count - 1 && Dist(px, py, _ruta[j]) < L) j++;
        var obj = _ruta[j];
        bool enGiro = obj.giro || _ruta[_idx].giro;

        // Pure pursuit: curvatura = 2 sin(alfa) / L  (alfa = angulo al objetivo).
        double rumbo = headingDeg * Math.PI / 180.0;    // 0 = norte, horario
        double dx = obj.x - px, dy = obj.y - py;
        double rumboObj = Math.Atan2(dx, dy);           // idem, 0 = norte
        double alfa = rumboObj - rumbo;
        while (alfa > Math.PI) alfa -= 2 * Math.PI;
        while (alfa < -Math.PI) alfa += 2 * Math.PI;
        double d = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
        double kappa = 2 * Math.Sin(alfa) / d;          // 1/m, positiva = a la derecha

        // Cinematica de ModSim: dHeading/dm = tan(deg*0.02)/2.5  ->  deg = atan(2.5*kappa)/0.02
        double grados = Math.Atan(2.5 * kappa) / 0.02;
        SalidaAnguloDeg = Math.Clamp(grados, -40, 40);
        SalidaVelocidadKmh = enGiro ? _cfg.DemoVelocidadGiroKmh : _cfg.DemoVelocidadKmh;
        Estado = (enGiro ? "girando" : "pasada") + $" · punto {_idx + 1}/{_ruta.Count} · vuelta {Vuelta + 1}";
    }

    private static double Dist(double x, double y, (double x, double y, bool giro) p)
        => Math.Sqrt((p.x - x) * (p.x - x) + (p.y - y) * (p.y - y));

    // ------------------------------------------------------------------
    //  PilotX: dejarlo listo y vigilarlo
    // ------------------------------------------------------------------
    public void Start()
    {
        if (_tarea != null) return;
        _cts = new CancellationTokenSource();
        _tarea = Task.Run(() => Vigilar(_cts.Token));
    }

    private async Task Vigilar(CancellationToken ct)
    {
        string api = (_cfg.PilotXApi ?? "http://127.0.0.1:5180").TrimEnd('/');
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Activo) { PilotXListo = false; EstadoPilotX = "demo apagada"; }
                else
                {
                    string cur = await _http.GetStringAsync(api + "/api/lotes/current", ct);
                    string nombre = Campo(cur, "name");
                    bool loteOk = string.Equals(nombre, _cfg.DemoLote, StringComparison.OrdinalIgnoreCase);
                    if (!loteOk)
                    {
                        EstadoPilotX = "abriendo lote " + _cfg.DemoLote;
                        await _http.PostAsync(api + "/api/lotes/open?name=" + Uri.EscapeDataString(_cfg.DemoLote), null, ct);
                        await Task.Delay(2000, ct);
                        cur = await _http.GetStringAsync(api + "/api/lotes/current", ct);
                        loteOk = string.Equals(Campo(cur, "name"), _cfg.DemoLote, StringComparison.OrdinalIgnoreCase);
                    }

                    if (loteOk && !PilotXListo)
                    {
                        // Recien abierto (o PilotX recien levantado): prescripcion + secciones auto.
                        EstadoPilotX = "activando prescripcion";
                        string body = "{\"id\":\"" + _cfg.DemoPrescripcionId + "\",\"propiedad_dosis\":\"" + _cfg.DemoPropiedadDosis + "\"}";
                        await _http.PostAsync(api + "/api/prescripciones/activa", new StringContent(body, Encoding.UTF8, "application/json"), ct);
                        await Task.Delay(500, ct);
                        await _http.PostAsync(api + "/api/aog/guidance/command", new StringContent("{\"cmd\":\"sec_auto\"}", Encoding.UTF8, "application/json"), ct);
                        PilotXListo = true;
                        EstadoPilotX = "PilotX listo: lote abierto, prescripcion activa, secciones auto";
                    }
                    else if (!loteOk)
                    {
                        PilotXListo = false;
                        EstadoPilotX = "no pude abrir el lote " + _cfg.DemoLote;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // API caida (PilotX cerrado o reiniciando): esperar y rehacer todo cuando vuelva.
                PilotXListo = false;
                EstadoPilotX = "esperando PilotX (" + Corto(ex.Message) + ")";
            }
            try { await Task.Delay(PilotXListo ? 10000 : 3000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static string Campo(string json, string campo)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(campo, out var v))
                return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
        }
        catch { }
        return "";
    }

    private static string Corto(string s) => s.Length > 60 ? s.Substring(0, 60) : s;

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tarea?.Wait(1000); } catch { }
        _http.Dispose();
    }
}
