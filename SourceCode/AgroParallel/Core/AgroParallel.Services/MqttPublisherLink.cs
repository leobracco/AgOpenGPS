// ============================================================================
// MqttPublisherLink.cs — Conexión MQTT compartida de los bridges que PUBLICAN
// (FlowX, corte por secciones, velocidad por sección).
//
// Complementa a MqttLiveServiceBase, que cubre el lado que ESCUCHA. Del lado
// que publica, cada bridge tenía su propio ritual copiado: crear el cliente,
// conectar UNA vez, y si fallaba loguear "MQTT error: ..." y quedarse mudo
// para siempre. Dos agujeros reales, los dos vistos en los logs de cabina:
//
//   1) Carrera de arranque. El broker es embebido (MqttBrokerService, lo
//      levanta CoreXEngineHost.StartServices) pero Program.cs arranca el
//      webHost — y con él los bridges — ANTES. El primer ConnectAsync falla
//      siempre y el bridge quedaba esperando que un vigilante externo lo
//      rearranque 15 s después.
//   2) Publicar sin conexión. Si el enlace se caía DESPUÉS de conectar y nadie
//      escuchaba DisconnectedAsync, el bridge seguía creyéndose conectado y
//      cada publicación tiraba "The MQTT client is not connected", que iba a
//      parar a un catch + una línea de log. En FlowX eso es un comando de
//      válvula perdido en silencio: la máquina sigue dosificando con el último
//      target hasta que el firmware corta por comms-loss.
//
// Este enlace resuelve las dos: reintenta con backoff hasta que el broker
// levante, y una publicación sin conexión NO se pierde callada — devuelve
// false, se cuenta y se reporta.
//
// Por qué NO encola: el payload de estos bridges es una CONSIGNA (L/min, pps,
// bits de sección), no un evento. Una consigna de hace cinco segundos aplicada
// tarde es peor que ninguna — el firmware ya tiene su propio corte por
// comms-loss (3-4 s) que deja la máquina en seguro. Entonces se descarta, se
// cuenta y se avisa; lo que se recupera al reconectar es el estado actual, que
// el tick vuelve a publicar solo.
//
// Nada de esto bloquea el tick de 200 ms que comanda motores: conectar y
// reconectar pasan en background (System.Threading.Timer de un solo tiro,
// reprogramado con la escalera de MqttBackoff) y PublicarAsync sale por false
// sin esperar nada cuando no hay enlace.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.VistaX;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.Services
{
    /// <summary>
    /// Transporte MQTT mínimo del lado que publica. Existe como interfaz para
    /// poder testear el enlace (backoff, guard de publicación, reconexión) sin
    /// broker ni nodos reales.
    /// </summary>
    public interface IMqttTransporte : IDisposable
    {
        bool Conectado { get; }
        Task ConectarAsync(string host, int puerto, string clientId);
        Task PublicarAsync(string topic, string payload);
        Task DesconectarAsync();

        /// <summary>El broker cortó (o se cayó la red). El argumento puede ser null.</summary>
        event EventHandler<Exception> Desconectado;
    }

    /// <summary>
    /// Enlace MQTT de publicación con reconexión automática y contabilidad de
    /// publicaciones perdidas. Uno por bridge.
    /// </summary>
    public sealed class MqttPublisherLink : IDisposable
    {
        private readonly string _nombre;          // "FlowX", "Corte", "VelSecciones"
        private readonly string _clientIdPrefijo; // "FX", "CUT", "SXSPD"
        private readonly Action<string> _log;     // log propio del bridge (su .log)
        private readonly IMqttTransporte _transporte;
        private readonly MqttBackoff _backoff = new MqttBackoff();
        private readonly MqttPerdidasContador _perdidas = new MqttPerdidasContador();
        private readonly object _lock = new object();

        private Timer _reintento;
        private volatile bool _activo;
        private volatile bool _conectando;
        private string _ultimoMotivo;
        private long _publicadas;

        public MqttPublisherLink(string nombre, string clientIdPrefijo, Action<string> log)
            : this(nombre, clientIdPrefijo, log, null)
        {
        }

        public MqttPublisherLink(string nombre, string clientIdPrefijo, Action<string> log, IMqttTransporte transporte)
        {
            _nombre = string.IsNullOrEmpty(nombre) ? "MQTT" : nombre;
            _clientIdPrefijo = string.IsNullOrEmpty(clientIdPrefijo) ? "PX" : clientIdPrefijo;
            _log = log;
            _transporte = transporte ?? new MqttnetTransporte();
            _transporte.Desconectado += OnTransporteDesconectado;
        }

        // ── Estado observable (lo leen los bridges y, por ellos, la UI) ──────

        /// <summary>Hay enlace vivo con el broker AHORA.</summary>
        public bool Conectado { get { return _activo && _transporte.Conectado; } }

        /// <summary>Publicaciones entregadas al broker desde el arranque.</summary>
        public long Publicadas { get { return Interlocked.Read(ref _publicadas); } }

        /// <summary>
        /// Publicaciones DESCARTADAS por no haber enlace. Distinto de cero
        /// significa que hubo consignas que el nodo nunca recibió.
        /// </summary>
        public long Perdidas { get { return _perdidas.Total; } }

        /// <summary>Último motivo de fallo, en castellano (vía AgpErrorMapper).</summary>
        public string UltimoMotivo { get { lock (_lock) return _ultimoMotivo; } }

        /// <summary>Reintentos de conexión encadenados sin éxito.</summary>
        public int FallosDeConexion { get { return _backoff.Fallos; } }

        // ── Ciclo de vida ────────────────────────────────────────────────────

        /// <summary>
        /// Arranca el enlace. Hace UN intento de conexión y devuelve si salió;
        /// haya salido o no, a partir de acá el enlace se mantiene solo. El
        /// bridge NO debe abortar su arranque porque esto devuelva false: el
        /// caso normal es que el broker embebido todavía no terminó de levantar.
        /// </summary>
        public async Task<bool> StartAsync()
        {
            if (_activo) return Conectado;
            _activo = true;
            bool ok = await IntentarConectarAsync().ConfigureAwait(false);
            if (!ok) ProgramarReintento();
            return ok;
        }

        public void Stop()
        {
            _activo = false;
            CancelarReintento();
            try
            {
                var t = _transporte.DesconectarAsync();
                if (t != null) t.Wait(2000);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            // La desuscripción va acá y no en Stop(): un enlace parado se puede
            // volver a arrancar, y sin el handler perdería los avisos de caída.
            try { _transporte.Desconectado -= OnTransporteDesconectado; }
            catch { }
            try { _transporte.Dispose(); }
            catch { }
        }

        // ── Publicación ──────────────────────────────────────────────────────

        /// <summary>
        /// Publica una consigna. Devuelve false — NUNCA tira — si no hay enlace
        /// o si el broker rechaza: en ese caso la publicación se cuenta como
        /// perdida y se avisa (resumido) en el log. El llamador decide si eso
        /// además apaga algo en la UI.
        /// </summary>
        public async Task<bool> PublicarAsync(string topic, string payload)
        {
            if (!_activo || !_transporte.Conectado)
            {
                ContarPerdida("sin enlace con el broker");
                return false;
            }

            try
            {
                await _transporte.PublicarAsync(topic, payload).ConfigureAwait(false);
                Interlocked.Increment(ref _publicadas);
                return true;
            }
            catch (Exception ex)
            {
                // Caso clásico "The MQTT client is not connected": el enlace se
                // cayó y el evento de desconexión no llegó (o llegó tarde). Se
                // baja a mano para que el lazo de reconexión arranque ya, en
                // vez de seguir tirando consignas contra un cliente muerto.
                var err = AgpErrorMapper.FromException(ex);
                ContarPerdida(err.Friendly);
                MarcarCaido(ex);
                return false;
            }
        }

        private void ContarPerdida(string motivo)
        {
            lock (_lock) _ultimoMotivo = motivo;
            string aviso = _perdidas.Anotar(motivo);
            if (aviso == null) return;   // se está resumiendo, todavía no toca
            Loguear(aviso);
            AgpLog.Error(_nombre, aviso);
        }

        // ── Conexión / reconexión ────────────────────────────────────────────

        /// <summary>
        /// Un intento de conexión. Público para que los tests manejen el lazo
        /// sin depender de timers; en producción lo llaman StartAsync y el
        /// temporizador de reintento.
        /// </summary>
        public async Task<bool> IntentarConectarAsync()
        {
            if (!_activo) return false;
            if (_conectando) return _transporte.Conectado;
            _conectando = true;
            try
            {
                if (_transporte.Conectado) { _backoff.Reset(); return true; }

                string host;
                int puerto;
                ResolverBroker(out host, out puerto);
                string clientId = _clientIdPrefijo + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);

                await _transporte.ConectarAsync(host, puerto, clientId).ConfigureAwait(false);

                bool reconexion = _backoff.Fallos > 0;
                _backoff.Reset();
                lock (_lock) _ultimoMotivo = null;
                Loguear(reconexion
                    ? "MQTT reconectado a " + host + ":" + puerto
                    : "MQTT conectado a " + host + ":" + puerto);
                return true;
            }
            catch (Exception ex)
            {
                var err = AgpErrorMapper.FromException(ex);
                int fallos = _backoff.Fallos + 1;
                string anterior;
                lock (_lock) { anterior = _ultimoMotivo; _ultimoMotivo = err.Friendly; }

                if (MqttBackoff.DebeLoguearFallo(fallos, anterior, err.Friendly))
                {
                    Loguear("MQTT sin conexion (" + err.Code + "): " + err.Friendly
                          + " · intento " + fallos + " · reintenta en "
                          + _backoff.EsperaTrasOtroFalloMs + "ms · detalle: " + err.Technical);
                }
                return false;
            }
            finally
            {
                _conectando = false;
            }
        }

        private static void ResolverBroker(out string host, out int puerto)
        {
            host = "127.0.0.1";
            puerto = 1883;
            try
            {
                // Se relee en cada intento a propósito: si el operario corrige
                // la dirección del broker, la reconexión la toma sin reiniciar.
                var cfg = VistaXConfig.Load();
                if (cfg != null)
                {
                    if (!string.IsNullOrEmpty(cfg.BrokerAddress)) host = cfg.BrokerAddress;
                    if (cfg.BrokerPort > 0) puerto = cfg.BrokerPort;
                }
            }
            catch { }
        }

        private void OnTransporteDesconectado(object sender, Exception ex)
        {
            if (!_activo) return;
            string motivo = ex != null ? AgpErrorMapper.FromException(ex).Friendly : "el broker cerro la conexion";
            lock (_lock) _ultimoMotivo = motivo;
            Loguear("MQTT desconectado: " + motivo + " — reintentando");
            AgpLog.Warn(_nombre, "MQTT desconectado: " + motivo);
            ProgramarReintento();
        }

        private void MarcarCaido(Exception ex)
        {
            // No se llama a Desconectar() acá: el transporte real ya está caído
            // y desconectar de nuevo sólo agrega excepciones. Alcanza con
            // programar el reintento, que crea un cliente nuevo.
            ProgramarReintento();
        }

        private void ProgramarReintento()
        {
            if (!_activo) return;
            int espera = _backoff.RegistrarFallo();
            lock (_lock)
            {
                CancelarReintentoSinLock();
                if (!_activo) return;
                _reintento = new Timer(OnReintento, null, espera, Timeout.Infinite);
            }
        }

        private void OnReintento(object state)
        {
            if (!_activo) return;
            // Fire-and-forget: el timer no puede esperar I/O, y el tick del
            // bridge tampoco tiene que enterarse de esto.
            var _ = Task.Run(async () =>
            {
                bool ok = false;
                try { ok = await IntentarConectarAsync().ConfigureAwait(false); }
                catch { }
                if (!ok && _activo) ProgramarReintento();
            });
        }

        private void CancelarReintento()
        {
            lock (_lock) CancelarReintentoSinLock();
        }

        private void CancelarReintentoSinLock()
        {
            if (_reintento == null) return;
            try { _reintento.Dispose(); }
            catch { }
            _reintento = null;
        }

        private void Loguear(string msg)
        {
            var log = _log;
            if (log == null) return;
            try { log(msg); }
            catch { }
        }
    }

    /// <summary>
    /// Transporte real sobre MQTTnet. Crea un cliente nuevo por conexión: un
    /// IMqttClient que ya se cayó se reconecta mal, y un cliente abandonado sin
    /// Dispose era la fuga que dejaba cada reintento del vigilante.
    /// </summary>
    internal sealed class MqttnetTransporte : IMqttTransporte
    {
        private readonly object _lock = new object();
        private IMqttClient _cliente;

        public event EventHandler<Exception> Desconectado;

        public bool Conectado
        {
            get
            {
                var c = _cliente;
                return c != null && c.IsConnected;
            }
        }

        public async Task ConectarAsync(string host, int puerto, string clientId)
        {
            IMqttClient viejo;
            lock (_lock) { viejo = _cliente; _cliente = null; }
            if (viejo != null)
            {
                try { viejo.Dispose(); }
                catch { }
            }

            var factory = new MqttFactory();
            var cliente = factory.CreateMqttClient();
            cliente.DisconnectedAsync += e =>
            {
                // Sólo avisa el cliente vigente: uno viejo cerrándose no tiene
                // que disparar el lazo de reconexión del nuevo.
                bool vigente;
                lock (_lock) vigente = ReferenceEquals(_cliente, cliente);
                if (vigente)
                {
                    var h = Desconectado;
                    if (h != null)
                    {
                        try { h(this, e != null ? e.Exception : null); }
                        catch { }
                    }
                }
                return Task.CompletedTask;
            };

            var opts = new MqttClientOptionsBuilder()
                .WithTcpServer(host, puerto)
                .WithClientId(clientId)
                .WithCleanSession(true)
                // Acotado a propósito: el broker es LOCAL (mismo proceso). Si
                // no contesta en 5 s no va a contestar, y el default de MQTTnet
                // dejaría colgado al vigilante del host, que llama a StartAsync
                // de forma bloqueante desde su timer.
                .WithTimeout(TimeSpan.FromSeconds(5))
                .Build();

            try
            {
                await cliente.ConnectAsync(opts).ConfigureAwait(false);
            }
            catch
            {
                try { cliente.Dispose(); }
                catch { }
                throw;
            }

            lock (_lock) _cliente = cliente;
        }

        public async Task PublicarAsync(string topic, string payload)
        {
            var c = _cliente;
            if (c == null) throw new InvalidOperationException("MQTT sin cliente");
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                .Build();
            await c.PublishAsync(msg).ConfigureAwait(false);
        }

        public async Task DesconectarAsync()
        {
            IMqttClient c;
            lock (_lock) { c = _cliente; _cliente = null; }
            if (c == null) return;
            try
            {
                if (c.IsConnected)
                    await c.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build()).ConfigureAwait(false);
            }
            catch { }
            try { c.Dispose(); }
            catch { }
        }

        public void Dispose()
        {
            IMqttClient c;
            lock (_lock) { c = _cliente; _cliente = null; }
            if (c == null) return;
            try { c.Dispose(); }
            catch { }
        }
    }
}
