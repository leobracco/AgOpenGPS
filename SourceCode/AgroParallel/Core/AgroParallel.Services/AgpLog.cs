using System;
using System.Diagnostics;

namespace AgroParallel.Services
{
    /// <summary>
    /// Fachada estática de logging. Rutea a Trace.WriteLine con el formato
    /// "[Modulo] mensaje" que el TraceListener de DebugLogService ya captura
    /// (nivel inferido por heurística). No abre archivos ni lanza nunca.
    /// Trace y no Debug: Debug.WriteLine tiene [Conditional("DEBUG")] y se
    /// elimina en Release, que es como se distribuye PilotX; TRACE está
    /// definido en ambas configuraciones y el listener vive en Trace.Listeners.
    /// </summary>
    public static class AgpLog
    {
        public static void Info(string modulo, string msg)
            => Write(modulo, msg);

        public static void Warn(string modulo, string msg)
            => Write(modulo, "WARN: " + msg);

        public static void Error(string modulo, string msg)
            => Write(modulo, "ERROR: " + msg);

        /// <summary>Para catch: loguea tipo + mensaje (sin stacktrace completo, va al ring buffer).</summary>
        public static void Error(string modulo, string contexto, Exception ex)
            => Write(modulo, $"ERROR: {contexto}: {ex.GetType().Name}: {ex.Message}");

        public static void Warn(string modulo, string contexto, Exception ex)
            => Write(modulo, $"WARN: {contexto}: {ex.GetType().Name}: {ex.Message}");

        private static void Write(string modulo, string msg)
        {
            try { Trace.WriteLine($"[{modulo}] {msg}"); } catch { /* jamás romper por loguear */ }
        }
    }
}
