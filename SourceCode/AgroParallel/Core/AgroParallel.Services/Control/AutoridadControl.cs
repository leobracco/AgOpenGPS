// ============================================================================
// AutoridadControl.cs — "quién tiene el control" de la máquina.
//
// Una sola pantalla acciona a la vez (piloto, secciones, giro, manejo libre,
// asistente de dirección, corregir posición...). La pantalla de la cabina
// (PilotX.Desktop local, loopback) lo tiene por defecto. Un celular/tablet de
// la LAN lo puede PEDIR; se le da sólo si:
//   a) la cabina lo CEDE (el operario toca "Ceder" en el cartelito), o
//   b) la cabina NO está activa: ninguna pantalla local latió en los últimos
//      CabinaAusenteMs (Desktop cerrado o colgado). Sin esto, con la pantalla
//      de la cabina rota no habría forma de parar nada desde el celular.
// El dueño remoto tiene que latir cada ~LatidoEsperadoMs; si pasan
// VencimientoMs sin latido se le REVOCA y el control vuelve a la cabina
// (evento Revocado → el host desengancha la dirección si ese remoto la había
// accionado). La cabina puede RECUPERAR en cualquier momento sin pedir
// permiso: el que está sentado en el tractor manda (no dispara desenganche,
// el operario está ahí).
//
// Identidad: la cabina se reconoce SOLO por loopback (lo decide el WebHost,
// nunca un header). Un remoto es (id + ip): el mismo id desde otra IP es otro
// cliente, así un equipo no se "cuelga" del control de otro copiando el id.
//
// Puro (sin timers ni red): el reloj se inyecta y el host llama Barrer()
// periódicamente. Thread-safe: todo bajo lock, eventos FUERA del lock.
//
// Idea tomada de AgOpenWeb (Shared/AgOpenWeb.RemoteServer/ControlAuthority.cs:
// un solo dueño, presencia por heartbeat, revocación por hombre muerto que
// dispara el failsafe). Reescrito para PilotX: cabina con prioridad, pedido
// pendiente + cesión explícita, identidad por (id, ip).
// ============================================================================

using System;
using System.Diagnostics;

namespace AgroParallel.Services.Control
{
    /// <summary>Nivel de un comando/endpoint frente a la autoridad de control.</summary>
    public enum NivelComando
    {
        /// <summary>Lee o cambia algo que no mueve la máquina (pantalla, idioma,
        /// chat, preferencias). Pasa siempre.</summary>
        Lectura = 0,
        /// <summary>Para algo (desenganchar piloto, frenar un motor). Pasa siempre:
        /// parar nunca puede depender de quién tiene el control.</summary>
        Parada = 1,
        /// <summary>Mueve la máquina o cambia lo que aplica. Sólo el dueño del control.</summary>
        Accionamiento = 2,
        /// <summary>El endpoint depende del cuerpo (comando de guiado): lo decide el controller.</summary>
        PorComando = 3,
    }

    /// <summary>Qué hace la puerta con un accionamiento de quien no tiene el control.</summary>
    public enum ModoAutoridad
    {
        /// <summary>Default de hoy: deja pasar y anota quién accionó (la PWA del
        /// celular ya acciona QuantiX/VistaX y no se la rompe).</summary>
        SoloRegistro = 0,
        /// <summary>Rechaza con 423 y motivo en castellano (`--autoridad-control`).</summary>
        Exigir = 1,
    }

    /// <summary>Un cliente de la API: la cabina (loopback) o un equipo de la LAN.</summary>
    public sealed class ClienteControl
    {
        public const string IdCabina = "cabina";

        public string Id { get; }
        public string Nombre { get; }
        public string Ip { get; }
        public bool EsCabina { get; }

        private ClienteControl(string id, string nombre, string ip, bool esCabina)
        {
            Id = id ?? "";
            Nombre = string.IsNullOrWhiteSpace(nombre) ? (esCabina ? "Cabina" : "Equipo " + ip) : nombre.Trim();
            Ip = ip ?? "";
            EsCabina = esCabina;
        }

        /// <summary>La pantalla local. <paramref name="nombre"/> es sólo para el
        /// registro (ej. "Soporte OrbitX", que también entra por loopback).</summary>
        public static ClienteControl Cabina(string nombre = null) =>
            new ClienteControl(IdCabina, nombre ?? "Cabina", "127.0.0.1", true);

        public static ClienteControl Remoto(string id, string nombre, string ip) =>
            new ClienteControl(id, nombre, ip, false);

        public bool EsElMismo(ClienteControl otro) =>
            otro != null && otro.EsCabina == EsCabina &&
            (EsCabina || (otro.Id == Id && otro.Ip == Ip));
    }

    public enum ResultadoPedido { Concedido, Pendiente, Rechazado }

    public sealed class RespuestaPedido
    {
        public ResultadoPedido Resultado { get; }
        public string Mensaje { get; }
        public RespuestaPedido(ResultadoPedido r, string mensaje) { Resultado = r; Mensaje = mensaje; }
    }

    /// <summary>Foto inmutable del estado (para la API y el indicador de la cabina).</summary>
    public sealed class EstadoControl
    {
        public bool DuenoEsCabina { get; set; }
        public string DuenoId { get; set; }
        public string DuenoNombre { get; set; }
        public string DuenoIp { get; set; }
        /// <summary>ms hasta que vence el latido del dueño remoto; -1 si es la cabina.</summary>
        public long VenceEnMs { get; set; }
        public bool HayPedido { get; set; }
        public string PedidoId { get; set; }
        public string PedidoNombre { get; set; }
        public string PedidoIp { get; set; }
        public bool CabinaPresente { get; set; }
    }

    /// <summary>Un remoto perdió el control sin que la cabina lo retomara.</summary>
    public sealed class RevocacionControl
    {
        public ClienteControl Remoto { get; }
        public string Motivo { get; }
        /// <summary>true = dejó de latir; false = lo soltó él.</summary>
        public bool Involuntaria { get; }
        /// <summary>Ese remoto enganchó el piloto / manejo libre / asistente, o
        /// recibió el control con el piloto puesto: hay que soltar la dirección.</summary>
        public bool DireccionAccionada { get; }

        public RevocacionControl(ClienteControl remoto, string motivo, bool involuntaria, bool direccion)
        {
            Remoto = remoto; Motivo = motivo; Involuntaria = involuntaria; DireccionAccionada = direccion;
        }
    }

    public sealed class AutoridadControl
    {
        /// <summary>Cada cuánto late el dueño (cliente).</summary>
        public const long LatidoEsperadoMs = 1500;
        /// <summary>Sin latido por más de esto se revoca: tolera un latido perdido
        /// en el WiFi del tractor (2×1,5 s) más medio segundo de jitter.</summary>
        public const long VencimientoMs = 3500;
        /// <summary>Sin señal de la pantalla local por más de esto la cabina
        /// cuenta como ausente y un remoto puede tomar el control sin cesión.</summary>
        public const long CabinaAusenteMs = 5000;

        private readonly object _lock = new object();
        private readonly Func<long> _reloj;

        private ClienteControl _dueno;            // null = cabina
        private long _duenoLatido;
        private bool _duenoDireccion;

        private ClienteControl _pedido;           // null = sin pedido
        private long _pedidoLatido;

        private long _cabinaLatido = long.MinValue; // nunca latió

        public event Action<EstadoControl> Cambio;
        public event Action<RevocacionControl> Revocado;

        public AutoridadControl(Func<long> relojMs = null)
        {
            if (relojMs != null) _reloj = relojMs;
            else
            {
                var sw = Stopwatch.StartNew();
                _reloj = () => sw.ElapsedMilliseconds;
            }
        }

        public EstadoControl Estado()
        {
            lock (_lock) return EstadoSinLock(_reloj());
        }

        /// <summary>true si <paramref name="c"/> puede accionar ahora: la cabina
        /// cuando es dueña; un remoto cuando es dueño Y su latido está fresco.</summary>
        public bool TieneControl(ClienteControl c)
        {
            if (c == null) return false;
            lock (_lock)
            {
                if (c.EsCabina) return _dueno == null;
                return _dueno != null && _dueno.EsElMismo(c) && (_reloj() - _duenoLatido) <= VencimientoMs;
            }
        }

        /// <summary>Hay una pantalla local viva (latió hace menos de CabinaAusenteMs).</summary>
        public bool CabinaPresente
        {
            get { lock (_lock) return CabinaPresenteSinLock(_reloj()); }
        }

        /// <summary>Señal de vida de la pantalla local. El WebHost la marca con
        /// cualquier request loopback a /api, además del latido explícito.</summary>
        public void MarcarPresenciaCabina()
        {
            lock (_lock) _cabinaLatido = _reloj();
        }

        public RespuestaPedido Pedir(ClienteControl c)
        {
            if (c == null) return new RespuestaPedido(ResultadoPedido.Rechazado, "Cliente desconocido.");
            if (c.EsCabina)
            {
                Recuperar("la cabina retomó el control");
                return new RespuestaPedido(ResultadoPedido.Concedido, "La cabina tiene el control.");
            }

            // Un dueño remoto vencido se barre antes de decidir.
            Barrer();

            EstadoControl cambio = null;
            RespuestaPedido resp;
            lock (_lock)
            {
                long ahora = _reloj();
                if (_dueno != null)
                {
                    if (_dueno.EsElMismo(c))
                    {
                        _duenoLatido = ahora;
                        return new RespuestaPedido(ResultadoPedido.Concedido, "Ya tenés el control.");
                    }
                    return new RespuestaPedido(ResultadoPedido.Rechazado,
                        "El control lo tiene " + _dueno.Nombre + ". Tiene que soltarlo o la cabina recuperarlo.");
                }

                if (!CabinaPresenteSinLock(ahora))
                {
                    ConcederSinLock(c, ahora);
                    cambio = EstadoSinLock(ahora);
                    resp = new RespuestaPedido(ResultadoPedido.Concedido,
                        "La pantalla de la cabina no está activa: tenés el control.");
                }
                else if (_pedido != null && !_pedido.EsElMismo(c) && (ahora - _pedidoLatido) <= VencimientoMs)
                {
                    return new RespuestaPedido(ResultadoPedido.Rechazado,
                        "Ya hay un pedido de control de " + _pedido.Nombre + " esperando a la cabina.");
                }
                else
                {
                    bool nuevo = _pedido == null || !_pedido.EsElMismo(c);
                    _pedido = c;
                    _pedidoLatido = ahora;
                    if (nuevo) cambio = EstadoSinLock(ahora);
                    resp = new RespuestaPedido(ResultadoPedido.Pendiente,
                        "Pedido enviado: el operario tiene que tocar «Ceder» en la pantalla de la cabina.");
                }
            }
            if (cambio != null) Cambio?.Invoke(cambio);
            return resp;
        }

        /// <summary>La cabina cede el control al pedido pendiente (si sigue vivo).</summary>
        public bool Ceder()
        {
            EstadoControl cambio;
            lock (_lock)
            {
                long ahora = _reloj();
                if (_dueno != null || _pedido == null) return false;
                if ((ahora - _pedidoLatido) > VencimientoMs) { _pedido = null; return false; }
                ConcederSinLock(_pedido, ahora);
                cambio = EstadoSinLock(ahora);
            }
            Cambio?.Invoke(cambio);
            return true;
        }

        /// <summary>La cabina rechaza el pedido pendiente.</summary>
        public bool RechazarPedido()
        {
            EstadoControl cambio;
            lock (_lock)
            {
                if (_pedido == null) return false;
                _pedido = null;
                cambio = EstadoSinLock(_reloj());
            }
            Cambio?.Invoke(cambio);
            return true;
        }

        /// <summary>La cabina retoma el control. Sin desenganche: el operario está ahí.</summary>
        public void Recuperar(string motivo)
        {
            EstadoControl cambio = null;
            lock (_lock)
            {
                if (_dueno != null || _pedido != null)
                {
                    _dueno = null;
                    _duenoDireccion = false;
                    _pedido = null;
                    cambio = EstadoSinLock(_reloj());
                }
            }
            if (cambio != null) Cambio?.Invoke(cambio);
        }

        /// <summary>El remoto suelta el control voluntariamente.</summary>
        public bool Soltar(ClienteControl c)
        {
            RevocacionControl rev = null;
            EstadoControl cambio = null;
            lock (_lock)
            {
                if (c != null && !c.EsCabina && _dueno != null && _dueno.EsElMismo(c))
                {
                    rev = new RevocacionControl(_dueno, "soltó el control", false, _duenoDireccion);
                    _dueno = null;
                    _duenoDireccion = false;
                    cambio = EstadoSinLock(_reloj());
                }
                else if (c != null && _pedido != null && _pedido.EsElMismo(c))
                {
                    // Un pedido pendiente también se puede retirar.
                    _pedido = null;
                    cambio = EstadoSinLock(_reloj());
                }
            }
            if (rev != null) Revocado?.Invoke(rev);
            if (cambio != null) Cambio?.Invoke(cambio);
            return rev != null;
        }

        /// <summary>Latido: cabina = presencia; dueño remoto = mantiene el control;
        /// remoto con pedido pendiente = mantiene el pedido. Otros: nada.</summary>
        public EstadoControl Latido(ClienteControl c)
        {
            lock (_lock)
            {
                long ahora = _reloj();
                if (c != null)
                {
                    if (c.EsCabina) _cabinaLatido = ahora;
                    else if (_dueno != null && _dueno.EsElMismo(c)) _duenoLatido = ahora;
                    else if (_pedido != null && _pedido.EsElMismo(c)) _pedidoLatido = ahora;
                }
                return EstadoSinLock(ahora);
            }
        }

        /// <summary>El dueño remoto accionó la dirección (enganchó piloto, manejo
        /// libre, asistente): si después se cae, el host la suelta.</summary>
        public void MarcarDireccion(ClienteControl c)
        {
            lock (_lock)
            {
                if (c != null && !c.EsCabina && _dueno != null && _dueno.EsElMismo(c))
                    _duenoDireccion = true;
            }
        }

        /// <summary>Revoca al dueño remoto vencido y limpia pedidos muertos. El host
        /// lo llama cada ~500 ms. true si revocó a alguien.</summary>
        public bool Barrer()
        {
            RevocacionControl rev = null;
            EstadoControl cambio = null;
            lock (_lock)
            {
                long ahora = _reloj();
                if (_dueno != null && (ahora - _duenoLatido) > VencimientoMs)
                {
                    rev = new RevocacionControl(_dueno, "dejó de responder", true, _duenoDireccion);
                    _dueno = null;
                    _duenoDireccion = false;
                    cambio = EstadoSinLock(ahora);
                }
                if (_pedido != null && (ahora - _pedidoLatido) > VencimientoMs)
                {
                    _pedido = null;
                    cambio = EstadoSinLock(ahora);
                }
            }
            if (rev != null) Revocado?.Invoke(rev);
            if (cambio != null) Cambio?.Invoke(cambio);
            return rev != null;
        }

        private void ConcederSinLock(ClienteControl c, long ahora)
        {
            _dueno = c;
            _duenoLatido = ahora;
            _duenoDireccion = false;
            if (_pedido != null && _pedido.EsElMismo(c)) _pedido = null;
        }

        private bool CabinaPresenteSinLock(long ahora) =>
            _cabinaLatido != long.MinValue && (ahora - _cabinaLatido) <= CabinaAusenteMs;

        private EstadoControl EstadoSinLock(long ahora)
        {
            var e = new EstadoControl
            {
                DuenoEsCabina = _dueno == null,
                DuenoId = _dueno?.Id ?? ClienteControl.IdCabina,
                DuenoNombre = _dueno?.Nombre ?? "Cabina",
                DuenoIp = _dueno?.Ip ?? "",
                VenceEnMs = _dueno == null ? -1 : Math.Max(0, VencimientoMs - (ahora - _duenoLatido)),
                CabinaPresente = CabinaPresenteSinLock(ahora),
            };
            if (_pedido != null)
            {
                e.HayPedido = true;
                e.PedidoId = _pedido.Id;
                e.PedidoNombre = _pedido.Nombre;
                e.PedidoIp = _pedido.Ip;
            }
            return e;
        }
    }
}
