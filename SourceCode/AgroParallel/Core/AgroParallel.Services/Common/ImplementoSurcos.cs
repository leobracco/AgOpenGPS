// Regenera surcos[] cuando cambia la cantidad de secciones de PilotX.
// Una sección = un surco (decisión de la spec 2026-08-01): la cantidad de
// surcos no se edita por separado, ES numSections. Conserva la asignación
// surco→tren por índice para no perder el trabajo del operario al ajustar.

using AgroParallel.Models;

namespace AgroParallel.Services.Common
{
    public static class ImplementoSurcos
    {
        public static void Regenerar(ImplementoDto impl, int numSecciones)
        {
            if (impl == null || numSecciones < 1) return;
            var viejos = impl.Surcos;
            int ultimoTren = 1;
            if (viejos != null && viejos.Count > 0 && viejos[viejos.Count - 1] != null)
                ultimoTren = viejos[viejos.Count - 1].TrenId;

            var nuevos = new System.Collections.Generic.List<SurcoDto>(numSecciones);
            for (int i = 1; i <= numSecciones; i++)
            {
                int tren = (viejos != null && i <= viejos.Count && viejos[i - 1] != null)
                    ? viejos[i - 1].TrenId : ultimoTren;
                if (tren < 1) tren = 1;
                nuevos.Add(new SurcoDto { Numero = i, TrenId = tren, SeccionPilotX = i });
            }
            impl.Surcos = nuevos;
            impl.NumeroSurcos = numSecciones;
        }

        /// <summary>
        /// Deja <c>impl.Secciones</c> con exactamente <c>impl.NumeroSurcos</c>
        /// entradas, conservando por índice nombre y lookaheads.
        ///
        /// Por qué existe: el write-back central→Tool deriva NumSections de
        /// <c>Secciones.Count</c>. Un PUT del implemento con la lista vieja
        /// (cualquier cliente que leyó-GET, tocó otra cosa y mandó-PUT) pisaba
        /// la cantidad de secciones del guiado sin que nadie lo pidiera — "puse
        /// 14 secciones, reinicié y volvió a 3" en el equipo real.
        /// </summary>
        public static void SincronizarSecciones(ImplementoDto impl)
        {
            if (impl == null || impl.NumeroSurcos < 1) return;
            var viejas = impl.Secciones;
            var nuevas = new System.Collections.Generic.List<SeccionDto>(impl.NumeroSurcos);
            for (int i = 1; i <= impl.NumeroSurcos; i++)
            {
                SeccionDto prev = (viejas != null && i <= viejas.Count) ? viejas[i - 1] : null;
                nuevas.Add(new SeccionDto
                {
                    Id = i,
                    Nombre = (prev != null && !string.IsNullOrEmpty(prev.Nombre)) ? prev.Nombre : ("Sección " + i),
                    LookaheadOn = prev != null ? prev.LookaheadOn : 0,
                    LookaheadOff = prev != null ? prev.LookaheadOff : 0
                });
            }
            impl.Secciones = nuevas;
        }
    }
}
