// ============================================================================
// AvisosCabina.cs — por que PilotX no deja hacer algo, dicho en criollo.
//
// El problema que resuelve (pedido del usuario, 2026-09-19, sembrando en LAS
// GRINGAS): la pantalla fallaba EN SILENCIO. El operario tocaba el piloto y no
// pasaba nada; tiraba una A/B y no se guardaba; la sembradora no aparecia en el
// mapa y no habia forma de saber por que. Arriba del tractor, con la maquina
// andando, "no pasa nada" es el peor mensaje posible: no se puede distinguir un
// equipo roto de un requisito que falta.
//
// Regla de la casa: si PilotX no puede hacer algo, LO DICE Y DICE POR QUE.
// Nunca un boton muerto sin explicacion.
//
// Todo lo de aca es funcion PURA sobre el estado: sin UI, sin HTTP, sin reloj.
// Asi el texto que ve el operario se fija con tests en vez de comprobarse a
// mano arriba de una sembradora.
// ============================================================================

using System;

namespace AgroParallel.Cabina
{
    /// <summary>Lo que la cabina necesita saber del estado para poder explicar.</summary>
    public struct EstadoCabina
    {
        /// <summary>Hay comunicacion con el motor de guiado.</summary>
        public bool Conectado;

        /// <summary>Hay fix de GPS (lat/lon validas).</summary>
        public bool HayGps;

        /// <summary>Hay un lote abierto.</summary>
        public bool LoteAbierto;

        /// <summary>El lote tiene lindero cargado.</summary>
        public bool HayLindero;

        /// <summary>El tractor esta FUERA del lindero (lo calcula el motor).</summary>
        public bool FueraDelLindero;

        /// <summary>Cuantas guias hay cargadas.</summary>
        public int Guias;

        /// <summary>Distancia al lote que se abrio, en km. Negativa = no se sabe.</summary>
        public double DistanciaAlLoteKm;
    }

    public static class AvisosCabina
    {
        /// <summary>A partir de esta distancia se avisa que el lote abierto no es
        /// donde esta el tractor. 20 km es lo que pidio el usuario: mas que un
        /// lote grande, menos que un campo vecino.</summary>
        public const double LejosDelLoteKm = 20.0;

        // ── Requisitos, uno por accion ──────────────────────────────────────
        //
        // El ORDEN importa: se informa el primer obstaculo que el operario tiene
        // que resolver, no todos juntos. Decirle cuatro cosas a la vez arriba del
        // tractor no sirve; decirle la que le toca ahora, si.

        /// <summary>Por que no se puede tirar una guia A/B. null = se puede.</summary>
        public static string PorQueNoSePuedeTirarGuia(EstadoCabina e)
        {
            if (!e.Conectado) return "Sin conexión con el motor de guiado.";
            if (!e.LoteAbierto)
                return "Abrí un lote primero. La guía se guarda adentro del lote, " +
                       "así que sin lote no hay dónde ponerla.";
            if (!e.HayGps)
                return "Sin señal de GPS. La A y la B se marcan con la posición del tractor.";
            return null;
        }

        /// <summary>Por que no se puede activar el piloto. null = se puede.</summary>
        public static string PorQueNoSePuedeActivarPiloto(EstadoCabina e)
        {
            if (!e.Conectado) return "Sin conexión con el motor de guiado.";
            if (!e.HayGps)    return "Sin señal de GPS. El piloto necesita saber dónde está el tractor.";
            if (!e.LoteAbierto) return "Abrí un lote antes de activar el piloto.";
            if (e.Guias <= 0)
                return "No hay ninguna guía. Tirá una A/B o elegí una guardada.";
            return null;
        }

        // ── Advertencias: NO bloquean, avisan ───────────────────────────────
        //
        // Son distintas de los requisitos: acá el operario PUEDE seguir, pero si
        // no se le avisa va a trabajar creyendo que esta sembrando y no.

        /// <summary>Aviso al activar el piloto estando fuera del lindero. null =
        /// no hay nada que avisar. NO impide activar: puede estar entrando al
        /// lote o haciendo una pasada de reconocimiento a proposito.</summary>
        public static string AvisoFueraDelLindero(EstadoCabina e)
        {
            if (!e.HayLindero || !e.FueraDelLindero) return null;
            return "Estás FUERA del lindero: no va a pintar ni a sembrar hasta que entres.";
        }

        /// <summary>Aviso de que el lote abierto queda lejos de donde esta el
        /// tractor — tipicamente, quedo abierto el lote de ayer. null = nada que
        /// avisar.</summary>
        public static string AvisoLejosDelLote(EstadoCabina e)
        {
            if (!e.LoteAbierto || !e.HayGps) return null;
            if (e.DistanciaAlLoteKm < 0) return null;            // sin dato
            if (e.DistanciaAlLoteKm < LejosDelLoteKm) return null;
            return "Estás a " + Redondear(e.DistanciaAlLoteKm) + " km del lote que abriste. " +
                   "¿Es el lote correcto?";
        }

        /// <summary>Por que no se ve la sembradora en el mapa. null = se tiene
        /// que estar viendo, y si no se ve es un problema de dibujo, no de
        /// estado — que es justamente lo que hay que poder distinguir.</summary>
        public static string PorQueNoSeVeLaSembradora(EstadoCabina e)
        {
            if (!e.Conectado)   return "Sin conexión con el motor de guiado.";
            if (!e.HayGps)      return "Sin señal de GPS: el mapa no sabe dónde poner la máquina.";
            if (!e.LoteAbierto) return "Sin lote abierto.";
            return null;
        }

        // ── Distancia ───────────────────────────────────────────────────────

        /// <summary>Distancia en km entre dos coordenadas (haversine). Devuelve
        /// -1 si alguna no es valida: 0/0 es "sin dato", no el golfo de Guinea.</summary>
        public static double DistanciaKm(double lat1, double lon1, double lat2, double lon2)
        {
            if (!CoordValida(lat1, lon1) || !CoordValida(lat2, lon2)) return -1;

            const double R = 6371.0088;   // radio medio terrestre, km
            double dLat = Rad(lat2 - lat1);
            double dLon = Rad(lon2 - lon1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        private static bool CoordValida(double lat, double lon)
        {
            if (double.IsNaN(lat) || double.IsNaN(lon)) return false;
            if (lat == 0 && lon == 0) return false;   // "sin dato" en todo el repo
            return lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
        }

        private static double Rad(double grados) { return grados * Math.PI / 180.0; }

        private static string Redondear(double km)
        {
            return km < 100
                ? km.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                : Math.Round(km / 10) * 10 + "";
        }
    }
}
