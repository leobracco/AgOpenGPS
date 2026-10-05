// ============================================================================
// TareasStore.cs — Tareas.json en la carpeta del lote.
//
// Escritura atómica (AtomicJson: tmp + flush + File.Replace, deja .bak): se
// escribe con el tractor andando y un corte de luz en la cabina no puede dejar
// el archivo a medias. Leer NUNCA crea archivos en el lote, y un archivo roto
// no tira: se arranca de cero (lo peor es perder el registro de tareas, jamás
// abrir el lote).
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using AgroParallel.Common;
using AgroParallel.Models;

namespace AgroParallel.Services.Tareas
{
    public static class TareasStore
    {
        public const string NombreArchivo = "Tareas.json";

        private static readonly JsonSerializerOptions LeerOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private static readonly JsonSerializerOptions EscribirOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            // Acentos y eñes legibles en el archivo (es texto del operario).
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Ruta(string loteDir) => Path.Combine(loteDir ?? "", NombreArchivo);

        public static TareasArchivo Cargar(string loteDir)
        {
            if (string.IsNullOrEmpty(loteDir)) return new TareasArchivo();
            try
            {
                var a = AtomicJson.Read<TareasArchivo>(Ruta(loteDir), LeerOpts);
                if (a == null) return new TareasArchivo();
                if (a.Tareas == null) a.Tareas = new System.Collections.Generic.List<Tarea>();
                a.Tareas.RemoveAll(t => t == null);
                foreach (var t in a.Tareas)
                    if (t.Tramos == null) t.Tramos = new System.Collections.Generic.List<TareaTramo>();
                return a;
            }
            catch (Exception ex)
            {
                AgpLog.Warn("Tareas", "Tareas.json ilegible en " + loteDir + ": " + ex.Message);
                return new TareasArchivo();
            }
        }

        public static void Guardar(string loteDir, TareasArchivo archivo)
        {
            if (string.IsNullOrEmpty(loteDir)) throw new ArgumentException("Sin carpeta de lote.", nameof(loteDir));
            if (!Directory.Exists(loteDir)) throw new DirectoryNotFoundException(loteDir);

            // Un Tareas.json que existe pero no se puede leer NO se pisa a
            // ciegas: se aparta como .roto para poder recuperarlo a mano. Si no,
            // la primera tarea nueva borraría el historial entero del lote.
            string ruta = Ruta(loteDir);
            if (File.Exists(ruta) && AtomicJson.Read<TareasArchivo>(ruta, LeerOpts) == null)
            {
                try { File.Copy(ruta, ruta + ".roto", true); } catch { }
            }

            AtomicJson.Write(Ruta(loteDir), JsonSerializer.Serialize(archivo ?? new TareasArchivo(), EscribirOpts));
        }
    }
}
