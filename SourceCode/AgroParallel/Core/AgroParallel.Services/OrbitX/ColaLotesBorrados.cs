// ============================================================================
// ColaLotesBorrados.cs — los lotes que el operario borró y todavía no se le
// pudieron avisar al cloud.
//
// Hace dos cosas con la misma lista:
//  · COLA: lo que OrbitXSync tiene que mandar cuando haya señal. En el lote no
//    hay WiFi, así que el borrado no puede depender de que la haya.
//  · TOMBSTONE: mientras el lote esté acá, el sync NO lo repone.
//    ResolutorLoteCloud devuelve Crear cuando la carpeta no existe, así que sin
//    este chequeo el operario borra un lote y le reaparece en el ciclo
//    siguiente.
//
// Persiste en disco a propósito: si el tombstone viviera en memoria, un
// reinicio de PilotX bastaría para que el lote volviera.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AgroParallel.Common;

namespace AgroParallel.Services.OrbitX
{
    public sealed class ColaLotesBorrados
    {
        private readonly string _rutaArchivo;
        private readonly object _candado = new object();
        private readonly HashSet<string> _pendientes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ColaLotesBorrados(string rutaArchivo)
        {
            _rutaArchivo = rutaArchivo;
            Cargar();
        }

        public void Encolar(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return;
            lock (_candado)
            {
                if (_pendientes.Add(loteNombre.Trim())) Guardar();
            }
        }

        /// <summary>Lo que falta avisarle al cloud.</summary>
        public IReadOnlyList<string> Pendientes()
        {
            lock (_candado) return new List<string>(_pendientes);
        }

        /// <summary>El tombstone: true mientras el lote no se haya podido avisar.</summary>
        public bool EstaBorrado(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return false;
            lock (_candado) return _pendientes.Contains(loteNombre.Trim());
        }

        /// <summary>El cloud confirmó el borrado: ya no hay nada que reponer.</summary>
        public void Confirmar(string loteNombre) => Sacar(loteNombre);

        /// <summary>Se abandona el aviso (demasiados intentos fallidos).</summary>
        public void Descartar(string loteNombre) => Sacar(loteNombre);

        private void Sacar(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return;
            lock (_candado)
            {
                if (_pendientes.Remove(loteNombre.Trim())) Guardar();
            }
        }

        private void Cargar()
        {
            try
            {
                // AtomicJson.Read cae solo al .bak si el principal está roto: acá
                // eso importa doble, porque este archivo es el tombstone y
                // perderlo repone TODOS los lotes que el operario había borrado.
                var leidos = AtomicJson.Read<List<string>>(_rutaArchivo, null);
                if (leidos == null) return;
                foreach (var n in leidos)
                    if (!string.IsNullOrWhiteSpace(n)) _pendientes.Add(n.Trim());
            }
            catch (Exception ex)
            {
                // Archivo roto (y sin .bak recuperable): se arranca con la cola
                // vacía. Es preferible perder avisos pendientes a dejar a
                // PilotX sin poder borrar. Se loguea para poder diagnosticar en
                // campo por qué se perdió el tombstone.
                AgpLog.Warn("ColaLotesBorrados", "cargando cola de lotes borrados", ex);
            }
        }

        private void Guardar()
        {
            try
            {
                // Escritura atómica a propósito (ver cabecera del archivo): con
                // File.WriteAllText, un apagado de golpe en cabina puede truncar
                // el archivo y hacer que Cargar() lo trate como vacío, perdiendo
                // el tombstone entero de una sola vez.
                AtomicJson.Write(_rutaArchivo, JsonSerializer.Serialize(new List<string>(_pendientes)));
            }
            catch (Exception ex)
            {
                // Si no se puede escribir, la cola sigue viva en memoria hasta
                // el próximo reinicio. No vale frenar el borrado por esto, pero
                // se loguea para poder diagnosticar en campo.
                AgpLog.Warn("ColaLotesBorrados", "guardando cola de lotes borrados", ex);
            }
        }
    }
}
