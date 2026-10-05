// ============================================================================
// VeredictoUpdater.cs — qué dijo el AgroParallel.Updater, y qué hace PilotX.
//
// Caso real (pantalla de Francisco Barbero, 8 intentos): ApplyAsync lanzaba
// el Updater y pedía el cierre 1,5 s después SIN esperar su veredicto. Si el
// Updater abortaba (parche rechazado, `return 3`), PilotX ya se había cerrado,
// el vigilante lo relanzaba en la misma versión y la pantalla quedaba en
// "Aplicando" sin decir por qué.
//
// Ahora, entre lanzar el Updater y pedir el cierre, PilotX espera:
//   · el Updater escribe updater-resultado.json (ver Updater/ResultadoArchivo)
//     — "aplicando" = cerrar; "rechazado" = NO cerrar y mostrar el motivo;
//   · si el Updater TERMINA antes de que PilotX cierre, abortó: un Updater que
//     va a instalar se queda esperando que PilotX salga (hasta 60 s). Esto
//     cubre a los Updater viejos que no escriben el archivo: el motivo sale
//     de updater.log ("PARCHE RECHAZADO: ...") o del código de salida;
//   · sin archivo y con el Updater vivo pasados unos segundos, es un Updater
//     viejo que ya validó y espera: se cierra como siempre.
//
// Además deja una marca (apply-pendiente.json) al pedir el cierre: al volver
// a arrancar, PilotX compara su versión con la que se quiso instalar y
// reporta ok/falla a OrbitX (POST /api/ota/resultado).
//
// Todo lo de acá es puro (sin procesos ni disco) para poder testearlo.
// ============================================================================

using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgroParallel.OrbitX
{
    /// <summary>Contenido de updater-resultado.json.</summary>
    public sealed class VeredictoUpdater
    {
        public const string Validando = "validando";
        public const string Aplicando = "aplicando";
        public const string Rechazado = "rechazado";
        public const string Ok = "ok";
        public const string Fallo = "fallo";

        public string Estado { get; set; }
        public string Motivo { get; set; }
        public int Codigo { get; set; }

        /// <summary>null si el texto no es un veredicto legible (vacío, a medio
        /// escribir, otro formato).</summary>
        public static VeredictoUpdater Parsear(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return null;
                    string estado = Texto(r, "estado");
                    if (string.IsNullOrEmpty(estado)) return null;
                    int codigo = 0;
                    if (r.TryGetProperty("codigo", out var c) && c.ValueKind == JsonValueKind.Number)
                        c.TryGetInt32(out codigo);
                    return new VeredictoUpdater
                    {
                        Estado = estado.Trim().ToLowerInvariant(),
                        Motivo = Texto(r, "motivo"),
                        Codigo = codigo,
                    };
                }
            }
            catch (JsonException) { return null; }
        }

        private static string Texto(JsonElement r, string campo)
            => r.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public enum DecisionApply
    {
        /// <summary>Todavía no hay veredicto: seguir esperando.</summary>
        Esperar,
        /// <summary>El Updater va a instalar: PilotX se cierra.</summary>
        Cerrar,
        /// <summary>El Updater no va a instalar: PilotX sigue abierto.</summary>
        Abortar,
    }

    public sealed class EsperaVeredicto
    {
        public DecisionApply Decision { get; set; }
        /// <summary>Solo con Abortar: por qué, en castellano para el operario.</summary>
        public string Motivo { get; set; }
        /// <summary>true si se cerró por tiempo sin veredicto (Updater viejo).</summary>
        public bool PorTiempo { get; set; }
    }

    /// <summary>Marca que deja PilotX al pedir el cierre (apply-pendiente.json).</summary>
    public sealed class MarcaAplicacion
    {
        public string VersionObjetivo { get; set; }
        public string VersionAnterior { get; set; }
        public long Ts { get; set; }

        public string Armar() => JsonSerializer.Serialize(new
        {
            version_objetivo = VersionObjetivo,
            version_anterior = VersionAnterior,
            ts = Ts,
        });

        public static MarcaAplicacion Parsear(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return null;
                    string obj = r.TryGetProperty("version_objetivo", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                    if (string.IsNullOrWhiteSpace(obj)) return null;
                    long ts = 0;
                    if (r.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number) t.TryGetInt64(out ts);
                    return new MarcaAplicacion
                    {
                        VersionObjetivo = obj,
                        VersionAnterior = r.TryGetProperty("version_anterior", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
                        Ts = ts,
                    };
                }
            }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Cómo terminó la actualización anterior, visto al volver a arrancar.</summary>
    public sealed class ResultadoActualizacion
    {
        public bool Ok { get; set; }
        public string VersionObjetivo { get; set; }
        public string VersionAnterior { get; set; }
        public string Motivo { get; set; }
    }

    public static class ReglasVeredicto
    {
        /// <summary>
        /// Qué hacer con lo que se sabe del Updater hasta ahora.
        /// </summary>
        /// <param name="v">Veredicto leído (null = no hay archivo todavía).</param>
        /// <param name="termino">El proceso del Updater ya salió.</param>
        /// <param name="transcurrido">Desde que se lanzó.</param>
        /// <param name="graciaSinArchivo">Sin archivo y con el Updater vivo pasado
        /// este tiempo, es un Updater viejo que ya validó: se cierra.</param>
        /// <param name="esperaMaxima">Tope absoluto: pasado esto se cierra igual
        /// (comportamiento de siempre; el Updater mataría a PilotX a los 60 s).</param>
        public static DecisionApply Decidir(VeredictoUpdater v, bool termino, TimeSpan transcurrido,
                                            TimeSpan graciaSinArchivo, TimeSpan esperaMaxima)
        {
            // Un Updater que va a instalar se queda esperando que PilotX salga.
            // Si ya terminó, no instala nada — aunque haya llegado a escribir
            // "aplicando" (murió después): cerrar solo dejaría la pantalla
            // reiniciándose en la misma versión.
            if (termino)
                return v != null && v.Estado == VeredictoUpdater.Ok ? DecisionApply.Cerrar : DecisionApply.Abortar;

            if (v != null)
            {
                switch (v.Estado)
                {
                    case VeredictoUpdater.Aplicando:
                    case VeredictoUpdater.Ok:
                        return DecisionApply.Cerrar;
                    case VeredictoUpdater.Rechazado:
                    case VeredictoUpdater.Fallo:
                        return DecisionApply.Abortar;
                }
                // "validando" (o algo desconocido): esperar hasta el tope.
                return transcurrido >= esperaMaxima ? DecisionApply.Cerrar : DecisionApply.Esperar;
            }

            return transcurrido >= graciaSinArchivo ? DecisionApply.Cerrar : DecisionApply.Esperar;
        }

        /// <summary>Motivo del aborto para la pantalla: el del veredicto, si no
        /// el del log del Updater, si no el código de salida.</summary>
        public static string MotivoAborto(VeredictoUpdater v, int? codigoSalida, string logUpdater)
        {
            if (v != null && !string.IsNullOrWhiteSpace(v.Motivo)) return v.Motivo.Trim();
            string delLog = MotivoDesdeLog(logUpdater);
            if (!string.IsNullOrEmpty(delLog)) return delLog;
            int? codigo = codigoSalida ?? (v != null && v.Codigo != 0 ? v.Codigo : (int?)null);
            switch (codigo)
            {
                case 3: return "El actualizador rechazó el paquete (código 3). Probá con el paquete completo.";
                case 2: return "Al actualizador le faltaron datos para arrancar (código 2).";
                case null: return "El actualizador se cerró sin aplicar la actualización.";
                default: return "El actualizador terminó sin aplicar la actualización (código " + codigo.Value + ").";
            }
        }

        private const string MarcaInicioLog = "==== AgroParallel.Updater iniciando ====";

        /// <summary>
        /// Busca el motivo en la ÚLTIMA corrida de updater.log (el archivo se
        /// acumula entre actualizaciones). null si no hay nada reconocible.
        /// </summary>
        public static string MotivoDesdeLog(string log)
        {
            if (string.IsNullOrEmpty(log)) return null;
            int ini = log.LastIndexOf(MarcaInicioLog, StringComparison.Ordinal);
            string ultima = ini >= 0 ? log.Substring(ini) : log;

            var m = Regex.Match(ultima, @"PARCHE RECHAZADO:\s*(.+)");
            if (m.Success) return m.Groups[1].Value.Trim();
            m = Regex.Match(ultima, @"Extraccion FALLO:\s*(.+)");
            if (m.Success) return "Falló la instalación (" + m.Groups[1].Value.Trim() + "); se volvió a la versión anterior.";
            m = Regex.Match(ultima, @"ERROR: exe no existe tras update:\s*(.+)");
            if (m.Success) return "Tras actualizar no se encontró el programa a relanzar: " + m.Groups[1].Value.Trim();
            return null;
        }

        /// <summary>
        /// Al volver a arrancar con una marca de aplicación pendiente: ¿quedó
        /// instalada la versión que se quiso aplicar?
        /// </summary>
        public static ResultadoActualizacion EvaluarAlArrancar(MarcaAplicacion marca, VeredictoUpdater v,
                                                               string logUpdater, string versionActual)
        {
            if (marca == null) return null;
            var r = new ResultadoActualizacion
            {
                VersionObjetivo = marca.VersionObjetivo,
                VersionAnterior = marca.VersionAnterior,
                Ok = PilotXSelfUpdate.CompareSemver(versionActual, marca.VersionObjetivo) >= 0,
            };
            if (r.Ok) return r;

            if (v != null && (v.Estado == VeredictoUpdater.Fallo || v.Estado == VeredictoUpdater.Rechazado)
                && !string.IsNullOrWhiteSpace(v.Motivo))
                r.Motivo = v.Motivo.Trim();
            else
                r.Motivo = MotivoDesdeLog(logUpdater)
                    ?? ("PilotX volvió a arrancar con la " + (versionActual ?? "?")
                        + " en vez de la " + marca.VersionObjetivo + ".");
            return r;
        }

        /// <summary>
        /// Espera el veredicto del Updater. Las fuentes entran por delegados
        /// (archivo, proceso, log) para poder probarlo sin procesos reales.
        /// </summary>
        public static async Task<EsperaVeredicto> EsperarAsync(
            Func<string> leerResultado,
            Func<bool> termino,
            Func<int?> codigoSalida,
            Func<string> leerLog,
            TimeSpan graciaSinArchivo,
            TimeSpan esperaMaxima,
            TimeSpan paso,
            CancellationToken ct = default)
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                VeredictoUpdater v = Leer(leerResultado);
                bool salio = Seguro(termino);
                // Si salió, releer: pudo escribir el veredicto justo antes de salir.
                if (salio) v = Leer(leerResultado) ?? v;

                var d = Decidir(v, salio, reloj.Elapsed, graciaSinArchivo, esperaMaxima);
                if (d == DecisionApply.Abortar)
                {
                    string log = null;
                    try { log = leerLog?.Invoke(); } catch { }
                    int? codigo = null;
                    try { codigo = codigoSalida?.Invoke(); } catch { }
                    return new EsperaVeredicto { Decision = d, Motivo = MotivoAborto(v, codigo, log) };
                }
                if (d == DecisionApply.Cerrar)
                {
                    bool porTiempo = v == null || v.Estado == VeredictoUpdater.Validando;
                    return new EsperaVeredicto { Decision = d, PorTiempo = porTiempo };
                }
                await Task.Delay(paso, ct).ConfigureAwait(false);
            }
        }

        private static VeredictoUpdater Leer(Func<string> leer)
        {
            try { return VeredictoUpdater.Parsear(leer?.Invoke()); }
            catch { return null; }
        }

        private static bool Seguro(Func<bool> f)
        {
            try { return f != null && f(); }
            catch { return false; }
        }
    }
}
