// ============================================================================
// PlanimetriaPoller.cs — capa de alturas del mapa GL (planimetría fase 3).
//
// Cada 1,5 s pregunta GET /api/planimetria (liviano). Solo si la función está
// prendida Y la capa visible Y el motor ya tiene mapa, y la revisión cambió,
// baja GET /api/planimetria/capa (grilla + curvas, ya en el plano local del
// lote) y arma ACÁ, en el hilo del poller, la textura RGBA (escala de colores
// o ambientes) y los vértices de las curvas. El mapa solo sube eso a la GPU.
//
// Entrega por Dispatcher.UIThread.Post (regla del mapa: nunca se le empuja
// nada desde otro hilo). Capa apagada → entrega null una vez y no baja nada.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PilotX.Desktop.Services;

public sealed class PlanimetriaMapSnapshot
{
    public int Rev;
    /// <summary>"alturas" | "ambientes".</summary>
    public string Modo = "alturas";
    /// <summary>Textura RGBA Nx×Ny; fila 0 = NORTE. Alpha 0 = sin dato.</summary>
    public byte[] Rgba = Array.Empty<byte>();
    public int W, H;
    /// <summary>Bordes de la grilla en el plano local (m).</summary>
    public double EOeste, EEste, NSur, NNorte;
    /// <summary>Curvas comunes: todas las líneas concatenadas (x,y,…) y su rango (inicio, cantidad de vértices).</summary>
    public float[] Curvas = Array.Empty<float>();
    public List<(int Inicio, int Cantidad)> RangosCurvas = new();
    /// <summary>Curvas maestras (cada 5 intervalos), más marcadas.</summary>
    public float[] Maestras = Array.Empty<float>();
    public List<(int Inicio, int Cantidad)> RangosMaestras = new();
    /// <summary>La curva de la cota elegida para la guía (resaltada).</summary>
    public float[] Guia = Array.Empty<float>();
    public List<(int Inicio, int Cantidad)> RangosGuia = new();
    // Leyenda
    public double ZMin, ZMax, Intervalo;
    public double? CotaGuia;
    public double? CotaBajo, CotaLoma;
}

public sealed class PlanimetriaPoller : IDisposable
{
    private readonly PlanimetriaClient _cli;
    private readonly Action<PlanimetriaMapSnapshot?> _onSnap;
    private readonly Action<PlaniEstado?>? _onEstado;
    private readonly CancellationTokenSource _cts = new();
    private int _revAplicada = int.MinValue;
    private bool _publicadoNull = true;

    /// <param name="onSnapshot">Capa nueva (null = sacarla del mapa). Se llama en el hilo de UI.</param>
    /// <param name="onEstado">Estado de cada consulta (para la leyenda). Hilo de UI.</param>
    public PlanimetriaPoller(string baseUrl, Action<PlanimetriaMapSnapshot?> onSnapshot, Action<PlaniEstado?>? onEstado = null)
    {
        _cli = new PlanimetriaClient(baseUrl, TimeSpan.FromSeconds(4));
        _onSnap = onSnapshot;
        _onEstado = onEstado;
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var est = await _cli.EstadoAsync(_cts.Token).ConfigureAwait(false);
                Post(() => _onEstado?.Invoke(est));
                bool quiere = est != null && est.Ok && est.Habilitada && est.CapaVisible
                    && (est.Estado == "listo" || est.Estado == "calculando") && est.ZMinM.HasValue;
                if (!quiere)
                {
                    if (!_publicadoNull)
                    {
                        _publicadoNull = true;
                        _revAplicada = int.MinValue;
                        Post(() => _onSnap(null));
                    }
                }
                else if (est!.Rev != _revAplicada)
                {
                    var capa = await _cli.CapaAsync(_cts.Token).ConfigureAwait(false);
                    if (capa != null && capa.Ok)
                    {
                        var snap = Armar(capa, est);
                        _revAplicada = capa.Rev;
                        _publicadoNull = false;
                        Post(() => _onSnap(snap));
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch { /* motor reiniciando: se reintenta en el próximo tick */ }

            try { await Task.Delay(1500, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void Post(Action a)
    {
        try { Avalonia.Threading.Dispatcher.UIThread.Post(a); }
        catch { /* cierre de la app */ }
    }

    // ---- armado (hilo del poller) -------------------------------------------

    /// <summary>Escala hipsométrica: bajo azul → verde → amarillo → marrón loma.</summary>
    private static readonly (double T, byte R, byte G, byte B)[] Escala =
    {
        (0.00, 0x2B, 0x6C, 0xB0),
        (0.25, 0x4F, 0xB3, 0xBF),
        (0.50, 0x8B, 0xC3, 0x4A),
        (0.75, 0xF2, 0xC9, 0x4C),
        (1.00, 0xB5, 0x65, 0x1D),
    };

    /// <summary>Colores de ambientes (índice = zona: 1 bajo, 2 media loma, 3 loma).</summary>
    public static readonly (byte R, byte G, byte B)[] ColoresZona =
    {
        (0, 0, 0),
        (0x2F, 0x80, 0xED),
        (0x8B, 0xC3, 0x4A),
        (0xC0, 0x84, 0x3D),
    };

    public const byte AlphaCapa = 150;

    public static (byte R, byte G, byte B) ColorAltura(double t)
    {
        if (double.IsNaN(t)) t = 0;
        t = Math.Clamp(t, 0, 1);
        for (int i = 1; i < Escala.Length; i++)
        {
            if (t <= Escala[i].T)
            {
                var a = Escala[i - 1];
                var b = Escala[i];
                double u = (t - a.T) / (b.T - a.T);
                return ((byte)(a.R + (b.R - a.R) * u), (byte)(a.G + (b.G - a.G) * u), (byte)(a.B + (b.B - a.B) * u));
            }
        }
        var u2 = Escala[^1];
        return (u2.R, u2.G, u2.B);
    }

    public static PlanimetriaMapSnapshot Armar(PlaniCapa c, PlaniEstado? est)
    {
        int nx = c.Nx, ny = c.Ny, N = nx * ny;
        var snap = new PlanimetriaMapSnapshot
        {
            Rev = c.Rev,
            Modo = c.ModoCapa == "ambientes" ? "ambientes" : "alturas",
            W = nx,
            H = ny,
            EOeste = c.EOeste,
            EEste = c.EEste,
            NSur = c.NSur,
            NNorte = c.NNorte,
            ZMin = c.ZMin,
            ZMax = c.ZMax,
            Intervalo = c.IntervaloM,
            CotaGuia = c.CotaGuiaM,
            CotaBajo = est?.Ambientes?.CotaBajoM,
            CotaLoma = est?.Ambientes?.CotaLomaM,
            Rgba = new byte[N * 4],
        };
        byte[] z = string.IsNullOrEmpty(c.ZCm) ? Array.Empty<byte>() : Convert.FromBase64String(c.ZCm);
        byte[] zona = string.IsNullOrEmpty(c.Zona) ? Array.Empty<byte>() : Convert.FromBase64String(c.Zona);
        bool ambientes = snap.Modo == "ambientes" && zona.Length == N;
        double rango = Math.Max(0.01, c.ZMax - c.ZMin);
        for (int i = 0; i < N && 2 * i + 1 < z.Length; i++)
        {
            int q = z[2 * i] | (z[2 * i + 1] << 8);
            if (q == 0xFFFF) continue;          // sin dato: alpha 0 (el shader lo descarta)
            (byte R, byte G, byte B) col;
            if (ambientes)
            {
                int k = zona[i];
                if (k < 1 || k > 3) continue;
                col = ColoresZona[k];
            }
            else
            {
                double zz = c.ZBase + q / 100.0;
                col = ColorAltura((zz - c.ZMin) / rango);
            }
            snap.Rgba[4 * i] = col.R;
            snap.Rgba[4 * i + 1] = col.G;
            snap.Rgba[4 * i + 2] = col.B;
            snap.Rgba[4 * i + 3] = AlphaCapa;
        }

        var comunes = new List<float[]>();
        var maestras = new List<float[]>();
        if (c.Curvas != null)
        {
            foreach (var cv in c.Curvas)
            {
                if (cv.Lineas == null) continue;
                foreach (var l in cv.Lineas) if (l != null && l.Length >= 4) (cv.Maestra ? maestras : comunes).Add(l);
            }
        }
        (snap.Curvas, snap.RangosCurvas) = Concatenar(comunes);
        (snap.Maestras, snap.RangosMaestras) = Concatenar(maestras);
        if (c.CurvaGuia?.Lineas != null) (snap.Guia, snap.RangosGuia) = Concatenar(c.CurvaGuia.Lineas);
        return snap;
    }

    private static (float[], List<(int, int)>) Concatenar(List<float[]> lineas)
    {
        int total = 0;
        foreach (var l in lineas) total += l.Length;
        var arr = new float[total];
        var rangos = new List<(int, int)>(lineas.Count);
        int pos = 0;
        foreach (var l in lineas)
        {
            Array.Copy(l, 0, arr, pos, l.Length);
            rangos.Add((pos / 2, l.Length / 2));
            pos += l.Length;
        }
        return (arr, rangos);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }
}
