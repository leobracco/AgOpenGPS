// ============================================================================
// ColaLotesBorrados.cs — los lotes que el operario borró desde la cabina.
//
// Son DOS conceptos separados, persistidos en el mismo archivo:
//  · AVISOS PENDIENTES: lo que OrbitXSync tiene que mandarle al cloud cuando
//    haya señal. En el lote no hay WiFi, así que el borrado no puede depender
//    de que la haya. Si el aviso se abandona (demasiados reintentos), esta
//    lista se vacía: no tiene sentido seguir insistiendo.
//  · TOMBSTONES: mientras el lote esté acá, el sync NO lo repone.
//    ResolutorLoteCloud devuelve Crear cuando la carpeta no existe, así que sin
//    este chequeo el operario borra un lote y le reaparece en el ciclo
//    siguiente. El tombstone SÓLO se levanta cuando el cloud confirma
//    explícitamente que borró su copia (Confirmar) — nunca por abandonar el
//    aviso.
//
// Antes vivían en una sola lista y "dejar de avisar" (Descartar) también
// desprotegía el lote: un 404 del endpoint (que hoy ni existe en el server)
// se leía como "ya está borrado" y levantaba el tombstone en el primer
// intento, haciendo reaparecer lotes borrados en el mismo ciclo. Separar las
// dos listas es justamente el arreglo de ese bug.
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
        // Formato de archivo nuevo: dos listas. El formato viejo (una lista
        // pelada de strings) se sigue pudiendo leer, ver Cargar().
        private sealed class DatosCola
        {
            public List<string> Pendientes { get; set; }
            public List<string> Tombstones { get; set; }
        }

        private readonly string _rutaArchivo;
        private readonly object _candado = new object();
        private readonly HashSet<string> _pendientes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _tombstones =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ColaLotesBorrados(string rutaArchivo)
        {
            _rutaArchivo = rutaArchivo;
            Cargar();
        }

        /// <summary>Se borró un lote: entra al aviso pendiente Y al tombstone.</summary>
        public void Encolar(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return;
            var nombre = loteNombre.Trim();
            lock (_candado)
            {
                bool cambio = _pendientes.Add(nombre);
                cambio |= _tombstones.Add(nombre);
                if (cambio) Guardar();
            }
        }

        /// <summary>Lo que falta avisarle al cloud.</summary>
        public IReadOnlyList<string> Pendientes()
        {
            lock (_candado) return new List<string>(_pendientes);
        }

        /// <summary>El tombstone: true mientras el lote no se haya podido confirmar.</summary>
        public bool EstaBorrado(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return false;
            lock (_candado) return _tombstones.Contains(loteNombre.Trim());
        }

        /// <summary>El cloud confirmó el borrado: saca el lote de las DOS listas.</summary>
        public void Confirmar(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return;
            var nombre = loteNombre.Trim();
            lock (_candado)
            {
                bool cambio = _pendientes.Remove(nombre);
                cambio |= _tombstones.Remove(nombre);
                if (cambio) Guardar();
            }
        }

        /// <summary>
        /// Se abandona el AVISO (demasiados intentos fallidos, o el cloud
        /// respondió algo que no se puede leer como confirmación — p.ej. un
        /// 404 indistinguible de "la ruta no existe"). El tombstone NO se
        /// toca: el lote sigue protegido contra la reposición.
        ///
        /// LIMITACIÓN CONOCIDA: si el aviso nunca le llega al cloud, este
        /// lote queda protegido para siempre — el operario no va a poder
        /// volver a bajarlo desde OrbitX, porque el sync sigue viendo el
        /// tombstone y nunca lo repone. Es el lado seguro a propósito: es
        /// preferible que un lote borrado se quede borrado a que reaparezca
        /// solo en la cabina sin que el operario lo pidiera. No hay acá un
        /// mecanismo para "olvidar" el tombstone — si hace falta liberarlo,
        /// es una decisión manual, no automática.
        /// </summary>
        public void AbandonarAviso(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return;
            var nombre = loteNombre.Trim();
            lock (_candado)
            {
                if (_pendientes.Remove(nombre)) Guardar();
            }
        }

        private void Cargar()
        {
            try
            {
                // AtomicJson.Read cae solo al .bak si el principal está roto: acá
                // eso importa doble, porque este archivo tiene los tombstones y
                // perderlo repone TODOS los lotes que el operario había borrado.
                var datos = AtomicJson.Read<DatosCola>(_rutaArchivo, null);
                if (datos != null)
                {
                    if (datos.Pendientes != null)
                        foreach (var n in datos.Pendientes)
                            if (!string.IsNullOrWhiteSpace(n)) _pendientes.Add(n.Trim());
                    if (datos.Tombstones != null)
                        foreach (var n in datos.Tombstones)
                            if (!string.IsNullOrWhiteSpace(n)) _tombstones.Add(n.Trim());
                    return;
                }

                // No hay datos en el formato nuevo (objeto). Puede ser que el
                // archivo no exista todavía, o que sea el formato VIEJO (una
                // lista pelada de nombres, de antes de separar aviso/tombstone).
                // Con el formato viejo lo conservador es tratar cada nombre como
                // las DOS cosas: era lo que ya hacía la lista única.
                var viejo = AtomicJson.Read<List<string>>(_rutaArchivo, null);
                if (viejo == null) return;
                foreach (var n in viejo)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    var nombre = n.Trim();
                    _pendientes.Add(nombre);
                    _tombstones.Add(nombre);
                }
            }
            catch (Exception ex)
            {
                // Archivo roto (y sin .bak recuperable): se arranca con la cola
                // vacía. Es preferible perder avisos pendientes/tombstones a
                // dejar a PilotX sin poder borrar. Se loguea para poder
                // diagnosticar en campo por qué se perdió el tombstone.
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
                var datos = new DatosCola
                {
                    Pendientes = new List<string>(_pendientes),
                    Tombstones = new List<string>(_tombstones)
                };
                AtomicJson.Write(_rutaArchivo, JsonSerializer.Serialize(datos));
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
