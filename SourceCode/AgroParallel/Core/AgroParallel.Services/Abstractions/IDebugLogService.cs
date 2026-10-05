// ============================================================================
// IDebugLogService.cs — Punto único de log para todo el sistema AgroParallel.
// Mantiene un ring buffer en RAM + grabación opcional a NDJSON.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface IDebugLogService
    {
        /// <summary>Devuelve la configuración actual.</summary>
        DebugConfigDto GetConfig();

        /// <summary>Persiste la configuración (a debug.json) y la aplica.</summary>
        void SaveConfig(DebugConfigDto cfg);

        /// <summary>Habilita / deshabilita un módulo individual sin guardar la
        /// config entera.</summary>
        void SetModuleEnabled(string module, bool enabled);

        /// <summary>Inserta una entrada al log. Si el módulo no está habilitado
        /// o el nivel es menor al mínimo, se ignora (no rompe el caller).</summary>
        void Append(string module, string level, string message);

        /// <summary>Devuelve entradas con seq &gt; <paramref name="sinceSeq"/>.
        /// Si <paramref name="modules"/> es no-vacío, filtra por esos módulos.</summary>
        List<DebugEntryDto> GetEntriesSince(long sinceSeq, IReadOnlyCollection<string> modules);

        /// <summary>Devuelve un snapshot con buffer + config + estado de grabación.</summary>
        DebugSnapshotDto GetSnapshot(int maxEntries);

        /// <summary>Limpia el ring buffer (no toca archivos grabados).</summary>
        void Clear();

        /// <summary>Inicia la grabación a un archivo NDJSON. Devuelve la ruta.</summary>
        string StartRecording();

        /// <summary>Cierra el archivo de grabación si está abierto.</summary>
        void StopRecording();

        /// <summary>Suscripción para WS hub: callback se llama con cada entrada
        /// nueva (después de filtrado de módulo/nivel). Devuelve un IDisposable
        /// para cancelar.</summary>
        IDisposable Subscribe(Action<DebugEntryDto> handler);

        /// <summary>Lista de módulos vistos en lo que va de la sesión (para que
        /// el UI sepa qué toggles ofrecer).</summary>
        IReadOnlyList<string> KnownModules { get; }

        /// <summary>True si actualmente está escribiendo a archivo.</summary>
        bool IsRecording { get; }

        /// <summary>Path del archivo de grabación activa o null.</summary>
        string RecordingFile { get; }
    }
}
