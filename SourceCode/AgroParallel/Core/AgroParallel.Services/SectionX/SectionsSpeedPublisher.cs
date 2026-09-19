// ============================================================================
// SectionsSpeedPublisher.cs
//
// Publica por MQTT la velocidad real de cada sección del implemento (km/h).
// Origen: AogStateSnapshot.SectionSpeedsKmh, calculado por PilotX en
// CalculateSectionLookAhead() — captura el efecto de rotación del implemento
// en curvas (sección externa más rápida, interna más lenta o negativa).
//
// Topic: agp/aog/sections_speed
// Cadencia: 5 Hz (200 ms). El raw de PilotX es 10 Hz, pero a 5 Hz alcanza para
// dosis variable + diagnóstico y baja a la mitad la carga del broker.
//
// Consumidores típicos:
//   - QuantiX (dosis por motor, usa la velocidad del set de surcos que cubre)
//   - FlowX (dosis líquida proporcional)
//   - VistaX (SPM esperado por surco → detección de tapado/exceso real en giros)
//   - Logger/observabilidad
//
// El publisher NO depende de SectionXConfig.Nodos — la velocidad es info de
// PilotX independiente de si hay nodos SectionX conectados. Sí toma el broker
// de VistaXConfig por consistencia con los otros bridges.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.SectionX
{
    public sealed class SectionsSpeedPublisher : IDisposable
    {
        private readonly IAogStateProvider _state;
        // Enlace compartido con reconexion por backoff (MqttPublisherLink).
        // Antes: un IMqttClient propio, un solo ConnectAsync y, si fallaba,
        // return -> el publisher quedaba mudo hasta que el vigilante del host
        // lo rearrancara; y si el broker se caia despues de conectar, _connected
        // nunca bajaba y cada publicacion moria en un catch vacio.
        private MqttPublisherLink _link;
        private System.Timers.Timer _timer;
        private bool _disposed;

        public bool IsRunning { get; private set; }
        public long MessagesSent { get; private set; }

        /// <summary>Hay enlace vivo con el broker. False mientras reconecta.</summary>
        public bool MqttConectado { get { return _link != null && _link.Conectado; } }

        /// <summary>Publicaciones de velocidad por seccion que no salieron.</summary>
        public long PublicacionesPerdidas { get { return _link != null ? _link.Perdidas : 0; } }

        // Cadencia: 200 ms = 5 Hz. Lo suficiente para dosis variable y monitoreo.
        private const int IntervalMs = 200;

        // Mismo topic para todos los consumidores (broadcast PilotX).
        private const string Topic = "agp/aog/sections_speed";

        private static readonly string LogPath = Path.Combine(
            AgroParallel.Common.AgpPaths.ConfigRoot, "sections_speed.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + msg + "\n"); }
            catch { }
        }

        public SectionsSpeedPublisher(IAogStateProvider state)
        {
            if (state == null) throw new ArgumentNullException("state");
            _state = state;
        }

        public async Task StartAsync()
        {
            if (IsRunning) return;

            // Arranca igual sin broker: el enlace reintenta solo. El broker es
            // embebido y levanta en el mismo arranque que este publisher, asi
            // que el primer intento falla por carrera, no por falla real.
            _link = new MqttPublisherLink("VelSecciones", "SXSPD", Log);
            bool conectado = await _link.StartAsync();
            if (!conectado)
                Log("Broker todavia no disponible: el enlace reintenta en segundo plano");

            _timer = new System.Timers.Timer { Interval = IntervalMs, AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
            IsRunning = true;
            Log("Iniciado · topic=" + Topic + " · intervalo=" + IntervalMs + "ms");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            if (_link != null)
            {
                var l = _link; _link = null;
                try { l.Dispose(); }
                catch { }
            }
        }

        private async void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _link == null) return;
            try
            {
                AogStateSnapshot snap = null;
                try { snap = _state.GetSnapshot(); } catch { }
                if (snap == null) return;
                if (!snap.IsJobStarted) return;  // sin lote abierto no hay nada que reportar
                if (snap.SectionSpeedsKmh == null || snap.SectionSpeedsKmh.Length == 0) return;

                string payload = BuildPayload(snap);
                // Devuelve false (sin tirar) si no hay enlace; el enlace lleva
                // la cuenta de lo perdido y avisa resumido.
                if (await _link.PublicarAsync(Topic, payload))
                    MessagesSent++;
            }
            catch { /* nunca romper el timer */ }
        }

        // Payload JSON manual para evitar dependencia de System.Text.Json en hot path
        // y mantener compatibilidad con consumidores ESP32 que parsean con ArduinoJson.
        // Formato:
        // { "ts":<ms>, "avg_kmh":8.4, "tool_left_kmh":9.2, "tool_right_kmh":7.6,
        //   "sections":[{"i":0,"kmh":9.2,"rev":false}, ...] }
        private static string BuildPayload(AogStateSnapshot snap)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append("{\"ts\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            sb.Append(",\"avg_kmh\":").Append(snap.AvgSpeed.ToString("F2", inv));
            sb.Append(",\"tool_left_kmh\":").Append(snap.ToolFarLeftSpeedKmh.ToString("F2", inv));
            sb.Append(",\"tool_right_kmh\":").Append(snap.ToolFarRightSpeedKmh.ToString("F2", inv));
            sb.Append(",\"sections\":[");
            var sp = snap.SectionSpeedsKmh;
            for (int i = 0; i < sp.Length; i++)
            {
                if (i > 0) sb.Append(',');
                double v = sp[i];
                sb.Append("{\"i\":").Append(i)
                  .Append(",\"kmh\":").Append(v.ToString("F2", inv))
                  .Append(",\"rev\":").Append(v < 0 ? "true" : "false")
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
    }
}
