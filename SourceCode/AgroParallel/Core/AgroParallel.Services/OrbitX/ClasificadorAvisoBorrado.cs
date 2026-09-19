// ============================================================================
// ClasificadorAvisoBorrado.cs — qué hacer con la respuesta del cloud cuando se
// avisa que el operario borró un lote (POST /api/aog/lote/borrado).
//
// El aviso tiene TRES desenlaces posibles y confundirlos cuesta caro:
//
//  · Confirmado    → el cloud ya no tiene el lote. Se levanta el tombstone.
//  · Reintentable  → fallo transitorio (sin red, 5xx, proxy caído, timeout).
//                    NO se cuenta como rechazo: la PC del tractor pasa horas
//                    sin señal y el aviso tiene que sobrevivir a eso.
//  · RutaInexistente / Rechazado → el server nunca va a aceptar este aviso tal
//                    cual está. Se deja de insistir (una sola línea de log) y
//                    el tombstone QUEDA: el lote sigue protegido contra la
//                    reposición aunque el cloud no se haya enterado.
//
// Por qué un 404 NO es automáticamente "el lote ya no está":
// el server de OrbitX monta un catch-all que responde 404
// {"error":"Ruta no encontrada"} para CUALQUIER ruta que no exista, y hoy
// /api/aog/lote/borrado no está implementado (routes/aog.js no lo tiene). O sea
// que el 404 que se ve en orbitx_sync.log significa "no existe el endpoint", no
// "no existe el lote". Leerlo como éxito levantaría el tombstone y el sync
// repondría en el ciclo siguiente el lote que el operario acaba de borrar —
// exactamente la regresión que ya pasó una vez (ver cabecera de
// ColaLotesBorrados.cs).
//
// Por eso el 404 se decide MIRANDO EL CUERPO: si el cuerpo dice que el lote no
// está, es éxito (era el objetivo); si dice que la ruta no existe, o no dice
// nada legible, se toma como endpoint ausente. Ante la duda, lo seguro es NO
// confirmar. Esto no cambia el contrato de wire: sólo se lee lo que el server
// ya manda.
// ============================================================================

using System;

namespace AgroParallel.Services.OrbitX
{
    /// <summary>Desenlace del aviso de borrado de un lote al cloud.</summary>
    public enum ResultadoAvisoBorrado
    {
        /// <summary>El cloud ya no tiene el lote: objetivo cumplido.</summary>
        Confirmado,

        /// <summary>Fallo transitorio (sin red, 5xx, timeout): se reintenta en el próximo tick.</summary>
        Reintentable,

        /// <summary>El server no tiene el endpoint: no hay nada que reintentar en NINGÚN lote.</summary>
        RutaInexistente,

        /// <summary>Rechazo permanente del server para este aviso puntual.</summary>
        Rechazado
    }

    public static class ClasificadorAvisoBorrado
    {
        /// <summary>Código ficticio para "no hubo respuesta" (excepción de red).</summary>
        public const int SinRespuesta = 0;

        /// <summary>
        /// Clasifica la respuesta del cloud. <paramref name="cuerpo"/> puede venir
        /// null o vacío (no se pudo leer): en ese caso un 404 se trata como
        /// endpoint ausente, que es el lado seguro.
        /// </summary>
        public static ResultadoAvisoBorrado Clasificar(int codigoHttp, string cuerpo)
        {
            // Sin respuesta: se cortó la conexión, no hay señal en el lote.
            if (codigoHttp <= 0) return ResultadoAvisoBorrado.Reintentable;

            if (codigoHttp >= 200 && codigoHttp < 300) return ResultadoAvisoBorrado.Confirmado;

            // 410 Gone es explícito: el recurso estaba y ya no está. Es el
            // objetivo del aviso.
            if (codigoHttp == 410) return ResultadoAvisoBorrado.Confirmado;

            // El server tuvo un problema suyo (502 del proxy, 503 en deploy,
            // 504 de timeout). Transitorio por definición.
            if (codigoHttp >= 500) return ResultadoAvisoBorrado.Reintentable;

            // 408 request timeout, 429 demasiadas consultas: esperar y reintentar.
            if (codigoHttp == 408 || codigoHttp == 429) return ResultadoAvisoBorrado.Reintentable;

            // 401/403: el token puede estar rotado o el device recién bloqueado.
            // Se arregla desde el panel sin tocar la cabina, así que no se tira
            // el aviso: se reintenta cuando el token vuelva a servir.
            if (codigoHttp == 401 || codigoHttp == 403) return ResultadoAvisoBorrado.Reintentable;

            if (codigoHttp == 404) return Clasificar404(cuerpo);

            // Resto de 4xx (400, 405, 409, 413, 415, 422…): el server rechaza
            // este aviso siempre. Insistir sólo gasta batería y llena el log.
            if (codigoHttp >= 400) return ResultadoAvisoBorrado.Rechazado;

            // 1xx/3xx: raro contra este endpoint; se reintenta por las dudas.
            return ResultadoAvisoBorrado.Reintentable;
        }

        private static ResultadoAvisoBorrado Clasificar404(string cuerpo)
        {
            if (string.IsNullOrWhiteSpace(cuerpo)) return ResultadoAvisoBorrado.RutaInexistente;

            // Primero lo que delata al catch-all del server / al 404 de Express.
            // Va antes que la búsqueda del lote porque "Ruta no encontrada"
            // también contiene "no encontrada".
            if (Contiene(cuerpo, "ruta no encontrada")
                || Contiene(cuerpo, "cannot post")
                || Contiene(cuerpo, "cannot delete")
                || Contiene(cuerpo, "<html"))
                return ResultadoAvisoBorrado.RutaInexistente;

            // El endpoint existe y dice que ese lote no está: es justo lo que se
            // quería lograr.
            if (Contiene(cuerpo, "lote")
                && (Contiene(cuerpo, "no encontrado") || Contiene(cuerpo, "no encontrada")
                    || Contiene(cuerpo, "no existe") || Contiene(cuerpo, "inexistente")
                    || Contiene(cuerpo, "not_found") || Contiene(cuerpo, "not found")))
                return ResultadoAvisoBorrado.Confirmado;

            // CouchDB/OrbitX a veces contesta el not_found pelado sin nombrar el
            // lote. Igual es "el documento no está".
            if (Contiene(cuerpo, "not_found") || Contiene(cuerpo, "deleted"))
                return ResultadoAvisoBorrado.Confirmado;

            // Cuerpo que no se entiende: no se confirma nada.
            return ResultadoAvisoBorrado.RutaInexistente;
        }

        private static bool Contiene(string texto, string aguja)
        {
            return texto.IndexOf(aguja, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
