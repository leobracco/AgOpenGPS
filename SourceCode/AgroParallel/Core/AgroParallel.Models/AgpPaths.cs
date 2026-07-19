using System;

namespace AgroParallel.Common
{
    /// <summary>
    /// Raíz de configs/logs/data de los servicios AgroParallel.
    /// En Windows queda el BaseDirectory (junto al exe — comportamiento
    /// histórico: vistaX.json, quantix.json, data/, logs de bridges, etc.).
    /// En Android el shell la setea a Context.FilesDir ANTES de instanciar
    /// cualquier servicio, porque el BaseDirectory del APK no es escribible
    /// (bloque 7 matriz Android, 2026-07-19).
    /// Lo que apunta a binarios junto al exe (ffmpeg, Updater, aog_path
    /// informativo del sync) sigue usando BaseDirectory a propósito.
    /// </summary>
    public static class AgpPaths
    {
        public static string ConfigRoot { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
    }
}
