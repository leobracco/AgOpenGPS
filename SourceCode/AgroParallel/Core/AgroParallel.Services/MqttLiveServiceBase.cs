// ============================================================================
// MqttLiveServiceBase<TReading>
//
// Base de los live services MQTT (FlowX, LineX, StormX, VistaX).
// Cubre el ritual Start/Stop, el filtrado por topic-prefix, el parse JSON
// del payload con try/catch y el registry de lecturas por UID con timeout de
// online. Los helpers ReadDouble/ReadBool/ReadString, antes copiados en cada
// service, viven acá como protected static.
//
// Diseño para VistaX:
//   VistaX usa topics legacy "vistax/nodos/telemetria" (3 partes) y
//   "vistax/<uid>/telemetria" (3 partes), con prefijo "vistax/" —  NO "agp/".
//   Por eso la base NO fuerza un parsing fijo de UID desde parts[2]: en su
//   lugar expone el virtual ExtractUid() que la subclase sobreescribe si
//   necesita otra posición o inferencia desde el payload.
//   Además, el mínimo de partes requerido también es configurable (MinParts).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    /// <summary>
    /// Base de los live services MQTT (FlowX/LineX/StormX/VistaX).
    /// </summary>
    /// <typeparam name="TReading">Tipo de registro interno por nodo (clase interna del service concreto).</typeparam>
    public abstract class MqttLiveServiceBase<TReading> where TReading : class
    {
        protected readonly object _lock = new object();
        protected readonly Dictionary<string, TReading> _readings =
            new Dictionary<string, TReading>(StringComparer.OrdinalIgnoreCase);

        private readonly INodoRegistryService _nodos;

        // ── Puntos de extensión ──────────────────────────────────────────────

        /// <summary>
        /// Prefijo de topic a filtrar (ej. "agp/flow/" o "vistax/").
        /// El chequeo usa StartsWith OrdinalIgnoreCase.
        /// </summary>
        protected abstract string TopicPrefix { get; }

        /// <summary>
        /// Filtros de suscripción MQTT a registrar en Start() (ej. "agp/flow/+/status_live").
        /// Si el service concreto prefiere suscribirse manualmente en OnStart(), puede
        /// devolver un array vacío y llamar a _nodos.SubscribeAsync() en OnStart().
        /// </summary>
        protected abstract string[] Subscriptions { get; }

        /// <summary>
        /// Mínimo de partes esperadas al hacer Topic.Split('/').
        /// Default 3 (cubre "vistax/nodos/telemetria"). Para topics agp/prod/uid/sub,
        /// sobreescribir con 4.
        /// </summary>
        protected virtual int MinParts => 3;

        /// <summary>
        /// Timeout para considerar un nodo online (ms). Default 3000.
        /// StormX lo sobreescribe a LogIntervalSec*2 (mín 30s).
        /// </summary>
        protected virtual int TimeoutMs => 3000;

        /// <summary>
        /// Extrae el UID del topic ya spliteado. Default: parts[2] (posición estándar
        /// "agp/prod/uid/subtopic"). VistaX sobreescribe: usa parts[1] para
        /// "vistax/{uid}/telemetria", con fallback al payload.
        /// Puede devolver null/empty si no hay UID inferible (la base descarta el mensaje).
        /// </summary>
        protected virtual string ExtractUid(string[] parts, JsonElement root)
        {
            // Posición canónica: agp/<prod>/<uid>/<subtopic>
            if (parts.Length > 2) return parts[2];
            return null;
        }

        /// <summary>
        /// Extrae el subtopic del topic ya spliteado. Default: parts[3] si existe.
        /// </summary>
        protected virtual string ExtractSubtopic(string[] parts)
        {
            return parts.Length > 3 ? parts[3] : (parts.Length > 0 ? parts[parts.Length - 1] : "");
        }

        /// <summary>
        /// Procesa un payload ya parseado en JSON.
        /// Se llama con <c>_lock</c> NO tomado — tomarlo al escribir en _readings.
        /// El <paramref name="uid"/> ya viene extraído (no vacío).
        /// </summary>
        protected abstract void OnPayload(string uid, string subtopic, string[] topicParts, JsonElement root);

        /// <summary>
        /// Llamado al final de Start(), después de registrar el handler y suscribir
        /// los filtros de Subscriptions. Útil para suscripciones dinámicas adicionales.
        /// </summary>
        protected virtual void OnStart() { }

        /// <summary>
        /// Llamado al inicio de Stop(), antes de remover el handler y limpiar _readings.
        /// </summary>
        protected virtual void OnStop() { }

        // ── Constructor ──────────────────────────────────────────────────────

        protected MqttLiveServiceBase(INodoRegistryService nodos)
        {
            _nodos = nodos ?? throw new ArgumentNullException("nodos");
        }

        // ── Ciclo de vida ────────────────────────────────────────────────────

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            if (_nodos == null) return;
            _nodos.MessageReceived += OnMqttMessage;
            var subs = Subscriptions;
            if (subs != null)
            {
                foreach (var s in subs)
                    _ = _nodos.SubscribeAsync(s);
            }
            OnStart();
            IsRunning = true;
        }

        public void Stop()
        {
            OnStop();
            try { _nodos.MessageReceived -= OnMqttMessage; }
            catch (Exception ex) { AgpLog.Warn(GetType().Name, "desuscribiendo MQTT", ex); }
            IsRunning = false;
            lock (_lock) _readings.Clear();
        }

        // ── Handler interno ──────────────────────────────────────────────────

        private void OnMqttMessage(object sender, MqttMessageReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Topic)) return;
            if (!e.Topic.StartsWith(TopicPrefix, StringComparison.OrdinalIgnoreCase)) return;

            var parts = e.Topic.Split('/');
            if (parts.Length < MinParts) return;

            try
            {
                using (var doc = JsonDocument.Parse(e.Payload))
                {
                    var root = doc.RootElement;
                    string uid = ExtractUid(parts, root);
                    if (string.IsNullOrEmpty(uid)) return;
                    string subtopic = ExtractSubtopic(parts);
                    OnPayload(uid, subtopic, parts, root);
                }
            }
            catch (Exception ex)
            {
                AgpLog.Warn(GetType().Name, "payload MQTT inválido en " + e.Topic, ex);
            }
        }

        // ── Helpers de tiempo ────────────────────────────────────────────────

        /// <summary>Retorna true si lastTs está dentro del TimeoutMs.</summary>
        protected bool IsOnline(DateTime lastTs, DateTime now)
            => lastTs != default(DateTime) && (now - lastTs).TotalMilliseconds <= TimeoutMs;

        // ── Helpers de parse multi-key (antes copiados en cada service) ──────

        /// <summary>
        /// Lee el primer campo numérico que encuentre entre los keys dados.
        /// Retorna 0 si ninguno existe o tiene tipo distinto a Number.
        /// </summary>
        protected static double ReadDouble(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetDouble();
            }
            return 0;
        }

        /// <summary>
        /// Lee el primer campo booleano/numérico/string que encuentre.
        /// Acepta true/false, 1/0, "true"/"1"/"ok" (case-insensitive).
        /// Retorna false si ningún key existe.
        /// </summary>
        protected static bool ReadBool(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!root.TryGetProperty(k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble() != 0;
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase)
                        || s == "1";
                }
            }
            return false;
        }

        /// <summary>
        /// Lee el primer campo de tipo String que encuentre entre los keys dados.
        /// Retorna null si ninguno existe o su ValueKind no es String.
        /// </summary>
        protected static string ReadString(JsonElement root, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            return null;
        }

        // ── Helper de suscripción adicional (para subclases en OnStart) ──────

        /// <summary>Atajo para suscribir un filtro MQTT adicional desde OnStart().</summary>
        protected void Subscribe(string topicFilter)
        {
            _ = _nodos.SubscribeAsync(topicFilter);
        }
    }
}
