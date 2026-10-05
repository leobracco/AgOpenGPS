// ============================================================================
// PuertaControl.cs — la puerta por la que pasa todo comando que acciona.
//
// Junta la AutoridadControl (quién es dueño) con el MODO:
//   · SoloRegistro (default): nunca rechaza; anota en el log quién accionó
//     desde la red. Es el modo de hoy porque la PWA del celular (/m/) ya
//     acciona — prueba de motores QuantiX, calibración VistaX, config FlowX —
//     y no se la puede romper sin avisar.
//   · Exigir (`--autoridad-control`): el accionamiento de un remoto sin
//     control se rechaza con 423 y un motivo en castellano.
// En los dos modos:
//   · la cabina que acciona RETOMA el control (el operario sentado manda);
//   · Lectura y Parada pasan siempre;
//   · un remoto dueño que engancha la dirección queda marcado, para soltarla
//     si después se cae.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Services.Control
{
    public sealed class DecisionControl
    {
        public bool Permitido { get; }
        /// <summary>HTTP a devolver si no se permite (423 Locked).</summary>
        public int StatusHttp { get; }
        public string Motivo { get; }
        /// <summary>Se dejó pasar sólo porque el modo es SoloRegistro.</summary>
        public bool SoloRegistrado { get; }

        public DecisionControl(bool permitido, int status, string motivo, bool soloRegistrado)
        {
            Permitido = permitido; StatusHttp = status; Motivo = motivo; SoloRegistrado = soloRegistrado;
        }

        public static readonly DecisionControl Ok = new DecisionControl(true, 200, null, false);
    }

    /// <summary>Último accionamiento que llegó desde la red (para el indicador).</summary>
    public sealed class AccionRemota
    {
        public string Nombre { get; set; }
        public string Ip { get; set; }
        public string Accion { get; set; }
        public bool TeniaControl { get; set; }
        public long CuandoMs { get; set; }
    }

    public sealed class PuertaControl
    {
        /// <summary>El mismo (cliente, acción) no se vuelve a loguear antes de esto
        /// (el asistente de dirección late cada 300 ms).</summary>
        public const long DedupLogMs = 5000;

        private readonly Action<string> _log;
        private readonly Func<long> _reloj;
        private readonly Func<bool> _pilotoEnganchado;
        private readonly object _lock = new object();
        private readonly Dictionary<string, long> _ultimoLog = new Dictionary<string, long>();
        private AccionRemota _ultima;

        public AutoridadControl Autoridad { get; }
        public ModoAutoridad Modo { get; set; }

        public PuertaControl(AutoridadControl autoridad, ModoAutoridad modo, Action<string> log,
                             Func<long> relojMs = null, Func<bool> pilotoEnganchado = null)
        {
            Autoridad = autoridad ?? throw new ArgumentNullException(nameof(autoridad));
            Modo = modo;
            _log = log ?? (_ => { });
            if (relojMs != null) _reloj = relojMs;
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _reloj = () => sw.ElapsedMilliseconds;
            }
            _pilotoEnganchado = pilotoEnganchado ?? (() => false);
        }

        /// <summary>Último accionamiento remoto visto (null = ninguno).</summary>
        public AccionRemota UltimaAccionRemota
        {
            get
            {
                lock (_lock)
                {
                    if (_ultima == null) return null;
                    return new AccionRemota
                    {
                        Nombre = _ultima.Nombre, Ip = _ultima.Ip, Accion = _ultima.Accion,
                        TeniaControl = _ultima.TeniaControl, CuandoMs = _ultima.CuandoMs,
                    };
                }
            }
        }

        public long AhoraMs => _reloj();

        /// <summary>Decide si <paramref name="cliente"/> puede ejecutar <paramref name="accion"/>.
        /// <paramref name="esDireccion"/>: la acción engancha/mueve el volante.</summary>
        public DecisionControl Autorizar(ClienteControl cliente, NivelComando nivel, string accion, bool esDireccion = false)
        {
            if (cliente == null) cliente = ClienteControl.Remoto("desconocido", "Desconocido", "?");
            if (nivel == NivelComando.Lectura) return DecisionControl.Ok;

            if (cliente.EsCabina)
            {
                if (nivel == NivelComando.Accionamiento && !Autoridad.TieneControl(cliente))
                {
                    var antes = Autoridad.Estado();
                    Autoridad.Recuperar("la cabina retomó el control");
                    _log("[Control] La cabina retomó el control (lo tenía " + antes.DuenoNombre +
                         " " + antes.DuenoIp + ") al accionar: " + accion);
                }
                return DecisionControl.Ok;
            }

            if (nivel == NivelComando.Parada)
            {
                Registrar(cliente, accion, true, "PARADA");
                return DecisionControl.Ok;
            }

            bool tiene = Autoridad.TieneControl(cliente);
            if (tiene)
            {
                if (esDireccion) Autoridad.MarcarDireccion(cliente);
                Registrar(cliente, accion, true, "con control");
                return DecisionControl.Ok;
            }

            var e = Autoridad.Estado();
            string motivo = "Esta pantalla no tiene el control de la máquina (lo tiene " +
                            (e.DuenoEsCabina ? "la cabina" : e.DuenoNombre) +
                            "). Pedí el control y esperá que el operario lo ceda en la cabina.";

            if (Modo == ModoAutoridad.SoloRegistro)
            {
                Registrar(cliente, accion, false, "SIN control (solo registro, se dejó pasar)");
                return new DecisionControl(true, 200, motivo, true);
            }

            Registrar(cliente, accion, false, "RECHAZADO: sin control");
            return new DecisionControl(false, 423, motivo, false);
        }

        /// <summary>Pedido de control con la regla extra: recibir el control con el
        /// piloto puesto hace al remoto responsable de la dirección.</summary>
        public RespuestaPedido Pedir(ClienteControl c)
        {
            var r = Autoridad.Pedir(c);
            if (r.Resultado == ResultadoPedido.Concedido && c != null && !c.EsCabina)
            {
                if (SafePiloto()) Autoridad.MarcarDireccion(c);
                _log("[Control] " + Quien(c) + " tomó el control (" + r.Mensaje + ")");
            }
            else if (r.Resultado == ResultadoPedido.Pendiente && c != null)
            {
                _log("[Control] " + Quien(c) + " pidió el control: esperando a la cabina");
            }
            return r;
        }

        /// <summary>La cabina cede al pedido pendiente.</summary>
        public bool Ceder()
        {
            var antes = Autoridad.Estado();
            bool ok = Autoridad.Ceder();
            if (ok)
            {
                var c = ClienteControl.Remoto(antes.PedidoId, antes.PedidoNombre, antes.PedidoIp);
                if (SafePiloto()) Autoridad.MarcarDireccion(c);
                _log("[Control] La cabina cedió el control a " + Quien(c));
            }
            return ok;
        }

        private bool SafePiloto()
        {
            try { return _pilotoEnganchado(); } catch { return false; }
        }

        private void Registrar(ClienteControl c, string accion, bool teniaControl, string como)
        {
            long ahora = _reloj();
            bool loguear;
            lock (_lock)
            {
                _ultima = new AccionRemota
                {
                    Nombre = c.Nombre, Ip = c.Ip, Accion = accion, TeniaControl = teniaControl, CuandoMs = ahora,
                };
                string clave = c.Id + "|" + c.Ip + "|" + accion + "|" + como;
                loguear = !_ultimoLog.TryGetValue(clave, out long ult) || (ahora - ult) >= DedupLogMs;
                if (loguear)
                {
                    _ultimoLog[clave] = ahora;
                    if (_ultimoLog.Count > 256) _ultimoLog.Clear(); // cota de memoria
                }
            }
            if (loguear) _log("[Control] Acciona " + Quien(c) + ": " + accion + " — " + como);
        }

        private static string Quien(ClienteControl c) =>
            c.EsCabina ? c.Nombre : c.Nombre + " (" + c.Ip + ")";
    }
}
