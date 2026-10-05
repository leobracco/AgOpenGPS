// ============================================================================
// CompensacionImplemento.cs — guiado del implemento, "nivel A" (pasivo).
//
// En una curva el implemento no pisa la huella del tractor:
//   · de ARRASTRE (lanza de largo L): va por ADENTRO ≈ L²/(2R)
//   · de 3 PUNTOS (rígido a d detrás del eje trasero): va por AFUERA ≈ d²/(2R)
// Esta clase calcula cuánto tiene que correrse el TRACTOR para que el
// IMPLEMENTO vaya sobre la línea, usando solo el modelo (sin AngleX, sin
// segundo GPS). Es feedforward: no cierra lazo. En recta da 0, así que NO
// corrige deriva ni ladera — para eso está la compensación de ladera.
//
// Modelo (el mismo encadenado de CPositionUpdater, en régimen estacionario):
// el pivote (eje trasero) gira sobre radio R y el tractor es tangente ahí.
//   enganche rígido a h del pivote:   Rh² = R² + h²
//   cada tramo arrastrado de largo Li: R_{i+1}² = R_i² − Li²
//   punto de trabajo a p del eje:     Rw² = R_eje² + p²
//   corrección hacia afuera = R − Rw
// En arrastre el enganche (barra) suma por afuera y la lanza resta por
// adentro: da (ΣLi² − h²)/(2R), no (h+L)²/(2R).
//
// Integración ("pivote virtual", CAutoSteerUpdater): a GetCurrentABLine /
// GetCurrentCurveLine se les pasa el pivote y el eje delantero corridos
// −corrección perpendicular al rumbo (o sea, del lado donde va el
// implemento). La guía lleva ese punto a la línea y el tractor queda abierto
// (o cerrado) lo justo. La elección de pasada (Build*) y el pintado de
// secciones (CPositionUpdater) siguen con el pivote real.
//
// FLAG APAGADO POR DEFECTO (setAS_guiadoImplemento = 0) hasta validarlo en
// lote. Salida de emergencia en el Engine: --sin-guiado-implemento.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AgOpenGPS
{
    /// <summary>Distancias del implemento que importan para el modelo (m; el signo no importa).</summary>
    public readonly struct GeometriaImplemento
    {
        /// <summary>true = de arrastre (sigue al enganche); false = rígido (3 puntos / frontal).</summary>
        public readonly bool Arrastre;
        /// <summary>Pivote (eje trasero) → enganche, rígido con el tractor.</summary>
        public readonly double EngancheM;
        /// <summary>Enganche → eje del tanque (TBT). 0 si no hay tanque.</summary>
        public readonly double TanqueM;
        /// <summary>Enganche (o tanque) → eje del implemento de arrastre.</summary>
        public readonly double LanzaM;
        /// <summary>Eje del implemento → barra de trabajo (sobre la misma línea).</summary>
        public readonly double HerramientaAlEjeM;

        public GeometriaImplemento(bool arrastre, double engancheM, double tanqueM, double lanzaM, double herramientaAlEjeM)
        {
            Arrastre = arrastre;
            EngancheM = engancheM;
            TanqueM = tanqueM;
            LanzaM = lanzaM;
            HerramientaAlEjeM = herramientaAlEjeM;
        }

        /// <summary>
        /// Arma la geometría desde CTool. <paramref name="engancheDesdePivote"/>
        /// es CTool.GetHitchLengthFromVehiclePivot() (en articulados le suma
        /// medio entre-ejes); se pasa aparte porque necesita el host.
        /// </summary>
        public static GeometriaImplemento DesdeTool(CTool tool, double engancheDesdePivote)
        {
            if (tool == null) return default;
            if (!tool.isToolTrailing)
                return new GeometriaImplemento(false, engancheDesdePivote, 0, 0, 0);

            return new GeometriaImplemento(true, engancheDesdePivote,
                tool.isToolTBT ? tool.tankTrailingHitchLength : 0,
                tool.trailingHitchLength,
                tool.trailingToolToPivotLength);
        }
    }

    public sealed class CompensacionImplemento
    {
        /// <summary>Corrección máxima (m) — el modelo es pasivo, no se le confía más.</summary>
        public const double TopeDefaultM = 0.5;
        /// <summary>Variación máxima de la corrección (m/s): entra y sale suave.</summary>
        public const double VariacionMaxDefaultMps = 0.05;
        /// <summary>Radios mayores que esto se toman como recta.</summary>
        public const double RadioRectaM = 1000;
        /// <summary>Un hueco de GPS no habilita un salto mayor que este dt.</summary>
        public const double DtMaximoS = 0.5;
        /// <summary>Distancia adelante del pivote donde se mide la curvatura (m).</summary>
        public const double MiraM = 3;
        /// <summary>Largo del tramo de curva sobre el que se promedia la curvatura (m).</summary>
        public const double TramoM = 8;

        public double TopeM { get; set; } = TopeDefaultM;
        public double VariacionMaxMps { get; set; } = VariacionMaxDefaultMps;

        /// <summary>
        /// Corrección vigente (m) en el marco del tractor: + = el tractor se corre
        /// a la DERECHA de su rumbo. 0 = guiado como siempre.
        /// </summary>
        public double CorreccionDerecha { get; private set; }

        /// <summary>
        /// Avanza un paso: calcula el objetivo y acerca la corrección con el
        /// límite de variación. Apagado o anulado (U-turn, marcha atrás, sin
        /// curva) vuelve a 0 EN EL ACTO — lo apagado no deja nada colgado.
        /// </summary>
        /// <param name="curvatura">1/R de la guía en el punto de mira (+ = dobla a la derecha).</param>
        public void Paso(bool habilitado, bool anular, double curvatura, GeometriaImplemento geo, double dt)
        {
            if (!habilitado || anular)
            {
                CorreccionDerecha = 0;
                return;
            }

            double objetivo = Objetivo(curvatura, geo, TopeM);
            if (double.IsNaN(dt) || dt < 0) dt = 0;
            if (dt > DtMaximoS) dt = DtMaximoS;
            double maxPaso = VariacionMaxMps * dt;
            double delta = objetivo - CorreccionDerecha;
            if (delta > maxPaso) delta = maxPaso;
            else if (delta < -maxPaso) delta = -maxPaso;
            CorreccionDerecha += delta;
        }

        /// <summary>El punto que va a la guía: el real corrido −corrección. Sin corrección, el mismo.</summary>
        public vec3 Aplicar(vec3 punto)
        {
            return CorreccionDerecha == 0 ? punto : Desplazar(punto, CorreccionDerecha);
        }

        // ---------------------------------------------------------------------
        // Funciones puras
        // ---------------------------------------------------------------------

        /// <summary>
        /// Cuánto tiene que abrirse el tractor hacia AFUERA de la curva (m) para
        /// que el implemento vaya sobre la línea, en régimen estacionario.
        /// Negativo = cerrarse hacia adentro (3 puntos).
        /// </summary>
        public static double CorreccionHaciaAfuera(double radio, GeometriaImplemento geo)
        {
            radio = Math.Abs(radio);
            if (radio <= 0 || double.IsNaN(radio) || double.IsInfinity(radio)) return 0;

            double h = geo.EngancheM;
            double r2 = radio * radio + h * h;
            if (geo.Arrastre)
            {
                r2 -= geo.TanqueM * geo.TanqueM;
                r2 -= geo.LanzaM * geo.LanzaM;
                // Lanza más larga que el radio: el modelo no tiene solución (iría
                // cruzado). Se devuelve "todo hacia afuera" y el tope lo recorta.
                if (r2 <= 0) return radio;
                r2 += geo.HerramientaAlEjeM * geo.HerramientaAlEjeM;
            }
            return radio - Math.Sqrt(r2);
        }

        /// <summary>
        /// Objetivo de corrección en el marco del tractor (+ = a la derecha),
        /// con tope. 0 en recta o radio enorme.
        /// </summary>
        public static double Objetivo(double curvatura, GeometriaImplemento geo, double tope)
        {
            if (double.IsNaN(curvatura) || Math.Abs(curvatura) < 1.0 / RadioRectaM) return 0;

            double afuera = CorreccionHaciaAfuera(1.0 / Math.Abs(curvatura), geo);
            // curvatura > 0 = dobla a la derecha → afuera es la izquierda.
            double derecha = curvatura > 0 ? -afuera : afuera;
            tope = Math.Abs(tope);
            if (derecha > tope) derecha = tope;
            if (derecha < -tope) derecha = -tope;
            return derecha;
        }

        /// <summary>
        /// Corre el punto −correccionDerecha perpendicular a su rumbo (rumbo AOG:
        /// horario desde el norte; derecha = (cos ψ, −sin ψ)). Rumbo intacto.
        /// </summary>
        public static vec3 Desplazar(vec3 p, double correccionDerecha)
        {
            return new vec3(
                p.easting - correccionDerecha * Math.Cos(p.heading),
                p.northing + correccionDerecha * Math.Sin(p.heading),
                p.heading);
        }

        /// <summary>
        /// Curvatura (1/m, + = dobla a la derecha en el sentido de marcha) de la
        /// curva activa alrededor del punto de mira: el punto más cercano al
        /// pivote, <see cref="MiraM"/> más adelante, promediando el cambio de
        /// rumbo sobre <see cref="TramoM"/>. Lista nula/corta → 0.
        /// </summary>
        /// <param name="sentidoLista">true = el tractor recorre la lista en orden creciente.</param>
        public static double CurvaturaEnMira(IList<vec3> pts, vec3 pivote, bool sentidoLista)
        {
            if (pts == null || pts.Count < 3) return 0;
            int n = pts.Count;

            int cerca = 0;
            double mejor = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double de = pts[i].easting - pivote.easting;
                double dn = pts[i].northing - pivote.northing;
                double d2 = de * de + dn * dn;
                if (d2 < mejor) { mejor = d2; cerca = i; }
            }

            int dir = sentidoLista ? 1 : -1;
            int mira = Caminar(pts, cerca, dir, MiraM);
            int a = Caminar(pts, mira, -dir, TramoM * 0.5);
            int b = Caminar(pts, mira, dir, TramoM * 0.5);

            // a → b en sentido de marcha; hacen falta al menos dos segmentos.
            if ((b - a) * dir < 2) return 0;

            double rumboA = Rumbo(pts[a], pts[a + dir]);
            double rumboB = Rumbo(pts[b - dir], pts[b]);
            double largo = 0;
            for (int i = a; i != b; i += dir)
                largo += Distancia(pts[i], pts[i + dir]);
            if (largo < 1) return 0;

            double dRumbo = rumboB - rumboA;
            while (dRumbo > Math.PI) dRumbo -= 2 * Math.PI;
            while (dRumbo < -Math.PI) dRumbo += 2 * Math.PI;

            // Los dos rumbos son de los segmentos extremos: su centro está medio
            // segmento adentro de cada punta.
            double primero = Distancia(pts[a], pts[a + dir]);
            double ultimo = Distancia(pts[b - dir], pts[b]);
            double base_ = largo - 0.5 * (primero + ultimo);
            if (base_ < 0.5) return 0;
            return dRumbo / base_;
        }

        private static int Caminar(IList<vec3> pts, int desde, int dir, double distancia)
        {
            int i = desde;
            double recorrido = 0;
            while (recorrido < distancia)
            {
                int sig = i + dir;
                if (sig < 0 || sig >= pts.Count) break;
                recorrido += Distancia(pts[i], pts[sig]);
                i = sig;
            }
            return i;
        }

        private static double Rumbo(vec3 desde, vec3 hasta)
            => Math.Atan2(hasta.easting - desde.easting, hasta.northing - desde.northing);

        private static double Distancia(vec3 a, vec3 b)
        {
            double de = b.easting - a.easting, dn = b.northing - a.northing;
            return Math.Sqrt(de * de + dn * dn);
        }

        /// <summary>
        /// Avisa si el perfil tiene distancias raras para el tipo declarado
        /// (null = nada raro). NO toca el perfil: solo lo dice. Caso conocido:
        /// implemento declarado rígido con enganche a −5 m (el zigzag de 2,2 m):
        /// con el guiado del implemento prendido se compensaría al revés.
        /// </summary>
        public static string ValidarPerfil(bool arrastre, double hitchLength, double trailingHitchLength,
                                           double tankTrailingHitchLength, bool tbt)
        {
            var es = CultureInfo.InvariantCulture;
            if (!arrastre && Math.Abs(hitchLength) > 3.0)
            {
                return "Implemento declarado RÍGIDO (3 puntos) con el enganche a "
                    + Math.Abs(hitchLength).ToString("0.0", es)
                    + " m del eje trasero: es raro para un 3 puntos. Si en realidad es de arrastre, "
                    + "declaralo así; con el guiado del implemento prendido se compensaría para el lado equivocado.";
            }
            if (arrastre && Math.Abs(trailingHitchLength) < 0.3 && (!tbt || Math.Abs(tankTrailingHitchLength) < 0.3))
            {
                return "Implemento declarado DE ARRASTRE con la lanza en "
                    + Math.Abs(trailingHitchLength).ToString("0.0", es)
                    + " m: falta medir el largo de la lanza (enganche a eje del implemento); "
                    + "sin eso el guiado del implemento no corrige nada.";
            }
            return null;
        }
    }
}
