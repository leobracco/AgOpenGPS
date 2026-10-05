// ============================================================================
// CoverageGeometry.cs — ¿cuánto de una sección cae sobre área YA trabajada?
//
// Es la base del corte por solape. Hoy PilotX no corta sobre lo ya sembrado
// (GuidanceEngineHost.SectionsRuntime dice "MVP: se saltea el anti-overlap por
// PÍXELES"), y el AOG legacy lo resolvía leyendo el framebuffer del mapa
// (GL.ReadPixels + grnPixels[a] == 250). Eso ata el control de la máquina a lo
// que se está dibujando: la resolución del corte pasa a depender del zoom y de
// la cámara, y cualquier cambio de render mueve el comportamiento en el lote.
//
// Acá el cálculo es GEOMÉTRICO y puro: entran coordenadas, sale un porcentaje.
// No conoce OpenGL, ni cámara, ni zoom, ni el hilo de UI.
//
// Método (por segmentos, no por punto). Chequear un solo punto central se pierde
// los huecos dentro del ancho de la sección; acá se evalúa el ANCHO ENTERO y se
// devuelve qué fracción está cubierta. Se transforman los triángulos de
// cobertura a un sistema local donde la sección es el eje X: entonces
// "¿solapa?" se vuelve "¿el triángulo cruza Y=0?", y encontrar DÓNDE solapa es
// una interpolación lineal. El look-ahead sale del MISMO transform, cambiando
// el umbral de Y — no hay que transformar de nuevo.
//
// Crédito del enfoque: plan SEGMENT_BASED_COVERAGE de AgOpenWeb
// (AgOpenGPS-Official/AgOpenWeb, GPL-3.0), atribuido a Brian (AgOpenGPS) y a
// Michael Torrie (QtAOG). La aritmética del transform está reescrita acá para
// respetar la convención de ejes de AOG (ver ATransformar) en vez de copiarse
// literal.
//
// Fase 1 del port: geometría pura + tests. NO está enganchado a la decisión de
// sección todavía — eso va detrás de un flag, después de validar en el lote.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Coverage
{
    /// <summary>Resultado del análisis de solape de UNA sección.</summary>
    public struct CoverageResult
    {
        /// <summary>Fracción cubierta, 0.0 (nada) a 1.0 (todo).</summary>
        public double CoveragePercent;

        /// <summary>Hay algo de solape (aunque sea poco).</summary>
        public bool HasAnyOverlap;

        /// <summary>Cubierta por completo (dentro de tolerancia).</summary>
        public bool IsFullyCovered;

        /// <summary>Metros del ancho de la sección que quedan SIN cubrir.</summary>
        public double UncoveredLength;
    }

    /// <summary>Tramo cubierto sobre el eje de la sección, en metros
    /// relativos al centro (negativo = izquierda).</summary>
    public struct XInterval
    {
        public double Start;
        public double End;

        public XInterval(double start, double end) { Start = start; End = end; }

        public double Length => End - Start;
    }

    /// <summary>
    /// Geometría del solape. Todo estático y puro: mismas entradas, mismas
    /// salidas, sin estado ni dependencias.
    /// </summary>
    public static class CoverageGeometry
    {
        /// <summary>Por debajo de esto, un solape se considera ruido numérico.</summary>
        public const double ToleranciaM = 0.001;

        /// <summary>Fracción a partir de la cual se considera cubierta entera.</summary>
        public const double UmbralCubiertaTotal = 0.99;

        // -------------------------------------------------------------------
        // Transformación de ejes
        // -------------------------------------------------------------------
        //
        // Convención de AOG, la MISMA que usa CPositionUpdater/OpenGL.Designer:
        //   · rumbo 0 = Norte, creciendo en sentido horario (hacia el Este)
        //   · vector de avance  = ( sin h,  cos h )   sobre (easting, northing)
        //   · vector lateral    = ( cos h, -sin h )   positivo hacia la DERECHA
        //
        // De ahí sale el transform: proyectar el desplazamiento sobre cada uno.
        //   localX = d · lateral = dx·cos h - dy·sin h      (ancho de la sección)
        //   localY = d · avance  = dx·sin h + dy·cos h      (adelante del tractor)
        //
        // OJO: no copiar la versión de AgOpenWeb, que precalcula Cos(-heading) y
        // Sin(-heading) y los combina con otros signos. Con esa fórmula, a rumbo
        // Este un punto al este da localY negativo (o sea, "atrás"), que es al
        // revés. Los tests de este archivo fijan la convención de acá.

        /// <summary>
        /// Lleva un punto del mundo al sistema local de la sección: X a lo ancho
        /// (+ derecha), Y hacia adelante. <paramref name="cos"/> y
        /// <paramref name="sin"/> son los del rumbo, precalculados una vez por
        /// sección y reusados para todos los triángulos.
        /// </summary>
        public static void ATransformar(
            double e, double n,
            double centroE, double centroN,
            double cos, double sin,
            out double localX, out double localY)
        {
            double dx = e - centroE;
            double dy = n - centroN;
            localX = dx * cos - dy * sin;
            localY = dx * sin + dy * cos;
        }

        // -------------------------------------------------------------------
        // Cruce de una arista con la línea Y = umbral
        // -------------------------------------------------------------------

        /// <summary>
        /// X donde la arista (x1,y1)→(x2,y2) cruza la horizontal Y=umbral.
        /// Devuelve false si no la cruza (ambos extremos del mismo lado).
        /// </summary>
        public static bool CruceEnY(
            double x1, double y1, double x2, double y2,
            double umbralY, out double x)
        {
            x = 0;
            double a = y1 - umbralY;
            double b = y2 - umbralY;

            // Ambos del mismo lado -> no cruza. El caso "los dos exactamente en
            // la línea" (arista horizontal justo sobre el umbral) también cae
            // acá y se descarta: el aporte de esa arista ya lo dan las otras dos
            // del triángulo, contarla sería duplicar.
            if (a > 0 == b > 0) return false;
            if (a == 0 && b == 0) return false;

            double den = b - a;
            if (den == 0) return false;

            double t = -a / den;
            x = x1 + t * (x2 - x1);
            return true;
        }

        /// <summary>
        /// Tramo del ancho de la sección que tapa UN triángulo, ya en coords
        /// locales. Devuelve false si el triángulo no cruza la línea Y=umbral o
        /// si queda entero fuera del ancho.
        /// </summary>
        public static bool TrianguloEnIntervalo(
            double ax, double ay, double bx, double by, double cx, double cy,
            double umbralY, double medioAncho, out XInterval intervalo)
        {
            intervalo = default(XInterval);

            // Descarte rápido: si los tres vértices están del mismo lado de la
            // línea, el triángulo no la toca. Es tres comparaciones de signo y
            // se lleva la enorme mayoría de los triángulos del lote.
            bool a = ay > umbralY, b = by > umbralY, c = cy > umbralY;
            if (a == b && b == c) return false;

            // Un triángulo que cruza una horizontal la corta en exactamente dos
            // aristas (salvo casos degenerados, que quedan descartados abajo).
            double x1 = 0, x2 = 0;
            int n = 0;
            double x;
            if (CruceEnY(ax, ay, bx, by, umbralY, out x)) { if (n == 0) x1 = x; else x2 = x; n++; }
            if (CruceEnY(bx, by, cx, cy, umbralY, out x)) { if (n == 0) x1 = x; else x2 = x; n++; }
            if (n < 2 && CruceEnY(cx, cy, ax, ay, umbralY, out x)) { if (n == 0) x1 = x; else x2 = x; n++; }
            if (n < 2) return false;

            double ini = Math.Min(x1, x2);
            double fin = Math.Max(x1, x2);

            // Recortar al ancho de la sección.
            if (ini < -medioAncho) ini = -medioAncho;
            if (fin > medioAncho) fin = medioAncho;
            if (fin - ini <= 0) return false;   // quedó entero fuera del ancho

            intervalo = new XInterval(ini, fin);
            return true;
        }

        // -------------------------------------------------------------------
        // Unión de tramos
        // -------------------------------------------------------------------

        /// <summary>
        /// Une tramos superpuestos. Sin esto, dos pasadas que se pisan contarían
        /// el mismo metro dos veces y el porcentaje daría más de 100%.
        /// </summary>
        public static List<XInterval> Unir(List<XInterval> intervalos)
        {
            var salida = new List<XInterval>();
            if (intervalos == null || intervalos.Count == 0) return salida;

            intervalos.Sort((p, q) => p.Start.CompareTo(q.Start));

            var actual = intervalos[0];
            for (int i = 1; i < intervalos.Count; i++)
            {
                var sig = intervalos[i];
                if (sig.Start <= actual.End)
                {
                    // Se tocan o se pisan: estirar el actual.
                    if (sig.End > actual.End) actual.End = sig.End;
                }
                else
                {
                    salida.Add(actual);
                    actual = sig;
                }
            }
            salida.Add(actual);
            return salida;
        }

        /// <summary>
        /// Arma el resultado a partir de los tramos cubiertos y el ancho total.
        /// Los tramos NO necesitan venir unidos: se unen acá.
        /// </summary>
        public static CoverageResult Resultado(List<XInterval> intervalos, double medioAncho)
        {
            double anchoTotal = medioAncho * 2.0;
            var unidos = Unir(intervalos);

            double cubierto = 0;
            for (int i = 0; i < unidos.Count; i++) cubierto += unidos[i].Length;

            if (cubierto < 0) cubierto = 0;
            if (cubierto > anchoTotal) cubierto = anchoTotal;

            double frac = anchoTotal > 0 ? cubierto / anchoTotal : 0.0;

            return new CoverageResult
            {
                CoveragePercent = frac,
                HasAnyOverlap   = cubierto > ToleranciaM,
                IsFullyCovered  = frac >= UmbralCubiertaTotal,
                UncoveredLength = anchoTotal - cubierto,
            };
        }
    }
}
