// ============================================================================
// CapturaPantalla.cs — foto de la pantalla para el "Reportar falla".
//
// Se toma ANTES de abrir el panel del reporte: lo que soporte necesita ver es
// lo que el operario tenía delante cuando algo falló, no el formulario.
//
// Windows: copia por GDI (BitBlt desde la pantalla) del rectángulo de la
// ventana. Es la única forma de que salga el MAPA: está dibujado por OpenGL en
// una superficie nativa, y un RenderTargetBitmap de Avalonia lo deja en negro.
// Fuera de Windows (Linux/Android, PilotX.UI es compartido): RenderTargetBitmap
// del árbol visual — sale la UI aunque el mapa quede vacío, que igual sirve.
//
// Nunca tira: sin captura el reporte sale igual (el armador lo anota).
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PilotX.Desktop.Services;

public static class CapturaPantalla
{
    // Más ancho que esto se achica: una 1920 en PNG pesa varios MB y para leer
    // la pantalla alcanza con 1600.
    private const int AnchoMax = 1600;

    /// <summary>PNG de lo que se ve en la ventana; null si no se pudo.</summary>
    public static byte[]? Tomar(TopLevel? ventana)
    {
        if (ventana == null) return null;
        byte[]? png = null;
        if (OperatingSystem.IsWindows())
        {
            try { png = TomarGdi(ventana); }
            catch { png = null; } // cae al render de Avalonia
        }
        if (png == null)
        {
            try { png = TomarAvalonia(ventana); }
            catch { png = null; }
        }
        return png;
    }

    // ── Windows / GDI ───────────────────────────────────────────────────────

    private static byte[]? TomarGdi(TopLevel ventana)
    {
        int x = 0, y = 0, w, h;
        IntPtr hwnd = ventana.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
        {
            x = r.Left; y = r.Top; w = r.Right - r.Left; h = r.Bottom - r.Top;
        }
        else
        {
            w = GetSystemMetrics(0); // SM_CXSCREEN
            h = GetSystemMetrics(1); // SM_CYSCREEN
        }
        if (w <= 0 || h <= 0 || w > 10000 || h > 10000) return null;

        IntPtr hdcPantalla = GetDC(IntPtr.Zero);
        if (hdcPantalla == IntPtr.Zero) return null;
        IntPtr hdcMem = IntPtr.Zero, hbm = IntPtr.Zero, viejo = IntPtr.Zero;
        try
        {
            hdcMem = CreateCompatibleDC(hdcPantalla);
            hbm = CreateCompatibleBitmap(hdcPantalla, w, h);
            if (hdcMem == IntPtr.Zero || hbm == IntPtr.Zero) return null;
            viejo = SelectObject(hdcMem, hbm);
            const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
            if (!BitBlt(hdcMem, 0, 0, w, h, hdcPantalla, x, y, SRCCOPY | CAPTUREBLT)) return null;
            SelectObject(hdcMem, viejo);
            viejo = IntPtr.Zero;

            var bmi = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(),
                biWidth = w,
                biHeight = -h,          // de arriba hacia abajo
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,      // BI_RGB
            };
            var bgra = new byte[w * h * 4];
            if (GetDIBits(hdcPantalla, hbm, 0, (uint)h, bgra, ref bmi, 0) == 0) return null;
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;   // GDI deja el alfa en 0

            return APng(bgra, w, h);
        }
        finally
        {
            if (viejo != IntPtr.Zero) SelectObject(hdcMem, viejo);
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcPantalla);
        }
    }

    private static byte[] APng(byte[] bgra, int w, int h)
    {
        using var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
        {
            int fila = w * 4;
            for (int yy = 0; yy < h; yy++)
                Marshal.Copy(bgra, yy * fila, fb.Address + yy * fb.RowBytes, fila);
        }
        return Guardar(wb, w, h);
    }

    // ── Avalonia (fallback) ─────────────────────────────────────────────────

    private static byte[]? TomarAvalonia(TopLevel ventana)
    {
        var tam = ventana.ClientSize;
        double escala = ventana.RenderScaling;
        int w = (int)Math.Ceiling(tam.Width * escala), h = (int)Math.Ceiling(tam.Height * escala);
        if (w <= 0 || h <= 0) return null;
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96 * escala, 96 * escala));
        rtb.Render(ventana);
        return Guardar(rtb, w, h);
    }

    private static byte[] Guardar(Bitmap bmp, int w, int h)
    {
        using var ms = new MemoryStream();
        if (w > AnchoMax)
        {
            int nh = Math.Max(1, (int)Math.Round(h * (AnchoMax / (double)w)));
            using var chica = bmp.CreateScaledBitmap(new PixelSize(AnchoMax, nh), BitmapInterpolationMode.MediumQuality);
            chica.Save(ms);
        }
        else
        {
            bmp.Save(ms);
        }
        return ms.ToArray();
    }

    // ── P/Invoke (solo Windows) ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                                        [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);
}
