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

        /// <summary>
        /// Carpeta wwwroot REAL que está sirviendo el web host, resuelta al
        /// arrancar (la setea AgpWebHost). Los controllers que necesitan leer
        /// archivos servidos —el catálogo de sprites de vehículo, por ejemplo—
        /// tienen que usar esto y NO armar la ruta desde BaseDirectory: cuando
        /// el motor corre desde una subcarpeta (&lt;install&gt;\Engine\) esa ruta no
        /// existe y el catálogo sale VACÍO sin ningún error visible.
        /// </summary>
        public static string WwwRoot { get; set; }
    }
}
