// ============================================================================
// MapGlSurface.Planimetria.cs — capa de alturas del lote (planimetría fase 3).
//
// Una textura (la grilla del mapa de alturas, un texel por celda, coloreada
// en el poller) estirada sobre el rectángulo de la grilla en el plano local,
// y encima las curvas de nivel (comunes finas, maestras más marcadas y la de
// la cota elegida para la guía resaltada en magenta).
//
// Va DESPUÉS de la cobertura (si no, a la tarde el lote pintado la taparía
// entera) y ANTES de tram, guías, lindero y tractor: es fondo de referencia.
// Translúcida (alpha 150/255) para que lo pintado se siga leyendo abajo.
//
// Reglas del mapa que respeta:
//   · OnPlanimetria solo guarda y pide frame por Dispatcher (lo llaman desde
//     el hilo de UI; nada de GL fuera del hilo GL);
//   · la subida a GPU entra en el presupuesto de UNA categoría por frame;
//   · la textura y el VBO se sueltan en Deinit y se rehacen tras perder el
//     contexto (TDR) con la última capa guardada.
// ============================================================================

using System;
using Avalonia.Threading;
using PilotX.Desktop.Services;
using Silk.NET.OpenGL;

namespace PilotX.Desktop.Views;

public sealed partial class MapGlSurface
{
    private volatile PlanimetriaMapSnapshot? _pendingPlani;
    private volatile bool _planiQuitar;
    private PlanimetriaMapSnapshot? _plani;         // la que está en GPU
    private PlanimetriaMapSnapshot? _ultimoPlani;   // para rehacer tras perder el contexto
    private uint _planiTex;
    private uint _planiVbo;
    private int _planiVboCapFloats;
    private bool _planiListo;
    private int _planiOffMaestras, _planiOffGuia;   // offsets en VÉRTICES dentro del VBO

    private static readonly float[] ColCurvaDia = { 0.30f, 0.20f, 0.10f, 0.50f };
    private static readonly float[] ColMaestraDia = { 0.22f, 0.13f, 0.05f, 0.85f };
    private static readonly float[] ColCurvaNoche = { 0.88f, 0.84f, 0.74f, 0.40f };
    private static readonly float[] ColMaestraNoche = { 0.95f, 0.92f, 0.82f, 0.75f };
    private static readonly float[] ColCurvaGuia = { 0.86f, 0.12f, 0.58f, 1.00f };

    /// <summary>Capa nueva del poller (null = sacarla). Hilo de UI.</summary>
    public void OnPlanimetria(PlanimetriaMapSnapshot? snap)
    {
        if (snap == null)
        {
            _pendingPlani = null;
            _planiQuitar = true;
        }
        else
        {
            _planiQuitar = false;
            _pendingPlani = snap;
        }
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    /// <summary>Sube (si hay pendiente y queda presupuesto) y dibuja. Hilo GL.</summary>
    private void DibujarPlanimetria(ref bool subioAlgo)
    {
        if (_gl == null || _texProgram == 0) return;
        if (_planiQuitar)
        {
            _planiQuitar = false;
            _plani = null;
            _ultimoPlani = null;
            _planiListo = false;
        }
        var p = _pendingPlani;
        if (p != null && !subioAlgo)
        {
            _pendingPlani = null;
            SubirPlanimetria(p);
            subioAlgo = true;
        }
        if (!_planiListo || _plani == null) return;
        DibujarTexturaPlani(_plani);
        DibujarCurvasPlani(_plani);
    }

    private void SubirPlanimetria(PlanimetriaMapSnapshot p)
    {
        if (_gl == null) return;
        MarcarSubida("planimetria");
        try
        {
            if (_planiTex == 0) _planiTex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _planiTex);
            unsafe
            {
                fixed (byte* px = p.Rgba)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba,
                        (uint)p.W, (uint)p.H, 0, PixelFormat.Rgba, PixelType.UnsignedByte, px);
                }
            }
            // Alturas: lineal (degradé continuo). Ambientes: nearest (bordes de zona nítidos).
            int filtro = (int)(p.Modo == "ambientes" ? GLEnum.Nearest : GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filtro);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filtro);

            // Curvas: comunes + maestras + guía en un solo VBO estático.
            int nC = p.Curvas.Length, nM = p.Maestras.Length, nG = p.Guia.Length;
            int total = nC + nM + nG;
            _planiOffMaestras = nC / 2;
            _planiOffGuia = (nC + nM) / 2;
            if (_planiVbo == 0) { _planiVbo = _gl.GenBuffer(); _planiVboCapFloats = 0; }
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _planiVbo);
            if (total > 0)
            {
                if (_planiVboCapFloats < total)
                {
                    int cap = Math.Max(_planiVboCapFloats, 256);
                    while (cap < total) cap *= 2;
                    _planiVboCapFloats = cap;
                    unsafe { _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cap * sizeof(float)), (void*)0, BufferUsageARB.StaticDraw); }
                }
                var todo = new float[total];
                Array.Copy(p.Curvas, 0, todo, 0, nC);
                Array.Copy(p.Maestras, 0, todo, nC, nM);
                Array.Copy(p.Guia, 0, todo, nC + nM, nG);
                unsafe
                {
                    fixed (float* f = todo)
                        _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(total * sizeof(float)), f);
                }
            }
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _plani = p;
            _ultimoPlani = p;
            _planiListo = true;
        }
        catch (Exception ex)
        {
            _planiListo = false;
            Console.Error.WriteLine("[MapGlSurface] planimetria (subida): " + ex.Message);
            try { _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo); } catch { }
        }
    }

    private void DibujarTexturaPlani(PlanimetriaMapSnapshot p)
    {
        if (_gl == null) return;
        float x0 = (float)p.EOeste, x1 = (float)p.EEste, y0 = (float)p.NSur, y1 = (float)p.NNorte;
        // Fila 0 de la textura = NORTE → v = 0 arriba (n_norte), v = 1 abajo.
        float[] v =
        {
            x0, y0, 0f, 1f,
            x1, y0, 1f, 1f,
            x1, y1, 1f, 0f,
            x0, y0, 0f, 1f,
            x1, y1, 1f, 0f,
            x0, y1, 0f, 0f,
        };
        try
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.UseProgram(_texProgram);
            unsafe
            {
                fixed (float* m = _mvpCache) _gl.UniformMatrix4(_uTexMvp, 1, false, m);
                fixed (float* pv = v)
                {
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _texVbo);
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(v.Length * sizeof(float)), pv, BufferUsageARB.StreamDraw);
                }
                _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 4, (void*)0);
                _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, sizeof(float) * 4, (void*)(sizeof(float) * 2));
            }
            _gl.EnableVertexAttribArray(0);
            _gl.EnableVertexAttribArray(1);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _planiTex);
            if (_isDay) _gl.Uniform4(_uTexTint, 1f, 1f, 1f, 1f);
            else _gl.Uniform4(_uTexTint, 0.55f, 0.55f, 0.55f, 0.85f);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            _gl.Uniform4(_uTexTint, 1f, 1f, 1f, 1f);
        }
        catch (Exception ex)
        {
            _planiListo = false;   // una capa que falla no voltea el frame
            Console.Error.WriteLine("[MapGlSurface] planimetria (textura): " + ex.Message);
        }
        finally
        {
            // Mismo cierre que DrawFloor: el resto del frame espera el programa
            // de color plano, el VBO dinámico y el atributo 1 apagado.
            _gl.DisableVertexAttribArray(1);
            _gl.UseProgram(_program);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0); }
            _gl.Disable(EnableCap.Blend);
        }
    }

    private void DibujarCurvasPlani(PlanimetriaMapSnapshot p)
    {
        if (_gl == null || _planiVbo == 0) return;
        if (p.RangosCurvas.Count == 0 && p.RangosMaestras.Count == 0 && p.RangosGuia.Count == 0) return;
        try
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _planiVbo);
            unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0); }

            var c = _isDay ? ColCurvaDia : ColCurvaNoche;
            _gl.Uniform4(_uColor, c[0], c[1], c[2], c[3]);
            _gl.LineWidth(1.0f);
            foreach (var (ini, n) in p.RangosCurvas) _gl.DrawArrays(PrimitiveType.LineStrip, ini, (uint)n);

            var m = _isDay ? ColMaestraDia : ColMaestraNoche;
            _gl.Uniform4(_uColor, m[0], m[1], m[2], m[3]);
            _gl.LineWidth(2.0f);
            foreach (var (ini, n) in p.RangosMaestras) _gl.DrawArrays(PrimitiveType.LineStrip, _planiOffMaestras + ini, (uint)n);

            if (p.RangosGuia.Count > 0)
            {
                _gl.Uniform4(_uColor, ColCurvaGuia[0], ColCurvaGuia[1], ColCurvaGuia[2], ColCurvaGuia[3]);
                _gl.LineWidth(3.0f);
                foreach (var (ini, n) in p.RangosGuia) _gl.DrawArrays(PrimitiveType.LineStrip, _planiOffGuia + ini, (uint)n);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MapGlSurface] planimetria (curvas): " + ex.Message);
        }
        finally
        {
            _gl.LineWidth(1.0f);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, sizeof(float) * 2, (void*)0); }
            _gl.Disable(EnableCap.Blend);
        }
    }

    /// <summary>Deinit con contexto vivo: se borran textura y VBO.</summary>
    private void LiberarPlanimetriaGl()
    {
        if (_gl == null) return;
        if (_planiTex != 0) _gl.DeleteTexture(_planiTex);
        if (_planiVbo != 0) _gl.DeleteBuffer(_planiVbo);
        _planiTex = 0;
        _planiVbo = 0;
        _planiVboCapFloats = 0;
        _planiListo = false;
        // Si vuelve un init, la capa se re-sube desde la última guardada.
        if (_ultimoPlani != null) _pendingPlani = _ultimoPlani;
    }

    /// <summary>Contexto perdido (TDR): handles a cero, sin llamar a GL, y re-encolar.</summary>
    private void PerderPlanimetriaGl()
    {
        _planiTex = 0;
        _planiVbo = 0;
        _planiVboCapFloats = 0;
        _planiListo = false;
        if (_ultimoPlani != null) _pendingPlani = _ultimoPlani;
    }

    /// <summary>Surface jubilada: soltar las referencias grandes.</summary>
    private void SoltarPlanimetria()
    {
        _plani = null;
        _ultimoPlani = null;
        _pendingPlani = null;
    }
}
