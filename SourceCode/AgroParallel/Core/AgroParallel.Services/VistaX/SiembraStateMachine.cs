// ============================================================================
// SiembraStateMachine.cs — Máquina de estado "¿estamos sembrando?" COMPARTIDA.
//
// Antes esta lógica vivía duplicada (y divergente) en:
//   · SeedMonitor.EvaluarInicio()        — panel nativo WinForms
//   · VistaXLiveService.EvaluarSembrando() — overlay HTML del Hub
//
// Reglas unificadas (deuda D#1):
//   · Método "pintando":    arranca con ≥1 sección pintando + vel > 0.5 km/h.
//                           Se apaga si deja de pintar por > 2 s.
//   · Método "sensores":    arranca con ≥ umbral sensores de semilla con flujo
//                           + vel > 1.0 km/h, SOSTENIDO TiempoConfirmacionMs.
//   · Método "herramienta": arranca cuando hay info de secciones y ≥1 activa
//                           (herramienta bajada), sin condición de velocidad.
//   · Método "manual":      solo vía ForzarManual(true/false).
//   · Histéresis común:     una vez activo, sigue activo hasta vel < 0.3 km/h
//                           sostenida > 10 s (parada del tractor).
//
// La máquina es estado puro: el tiempo entra por parámetro (testeable) y las
// LECTURAS (velocidad, secciones, sensores) las provee el caller en cada
// Evaluar(). NO es thread-safe — cada consumidor la usa adentro de su lock.
// ============================================================================

using System;

namespace AgroParallel.VistaX
{
    /// <summary>Lecturas del ciclo actual que alimenta el caller.</summary>
    public sealed class SiembraEntrada
    {
        /// <summary>Velocidad del tractor en km/h (0 si no hay fuente).</summary>
        public double VelocidadKmh;

        /// <summary>Cantidad de secciones pintando/activas ahora.</summary>
        public int SeccionesActivas;

        /// <summary>true si hay fuente de secciones (aunque estén todas en 0).</summary>
        public bool SeccionesDisponibles;

        /// <summary>Sensores de semilla con flujo detectado (no muted, con dato).</summary>
        public int SensoresActivos;
    }

    public sealed class SiembraStateMachine
    {
        // Umbrales unificados — antes divergían entre SeedMonitor y Live.
        public const double VelMinPintando = 0.5;
        public const double VelMinSensores = 1.0;
        public const double VelParada = 0.3;
        public const double ParadaSegundos = 10;
        public const double SinPintarSegundos = 2;

        private string _metodo = "sensores";
        private int _umbralSensores = 3;
        private int _confirmacionMs = 500;

        private bool _activo;
        private DateTime _paradaDesde = DateTime.MinValue;
        private DateTime _sinPintarDesde = DateTime.MinValue;
        private DateTime _confirmacionDesde = DateTime.MinValue;

        public bool Activo { get { return _activo; } }

        /// <summary>Método normalizado ("sensores"|"herramienta"|"pintando"|"manual").</summary>
        public string Metodo { get { return _metodo; } }

        /// <summary>Por qué NO está activo (vacío si está activo). Para UI/diagnóstico.</summary>
        public string MotivoDetenido { get; private set; }

        /// <summary>Velocidad mínima que aplica al método actual (para el snapshot).</summary>
        public double VelMinima
        {
            get
            {
                if (_metodo == "pintando") return VelMinPintando;
                if (_metodo == "sensores") return VelMinSensores;
                return 0;
            }
        }

        public int UmbralSensores { get { return _umbralSensores; } }

        public SiembraStateMachine()
        {
            MotivoDetenido = "";
        }

        /// <summary>Aplica la config del usuario. Llamar al cargar/recargar config.</summary>
        public void Configurar(string metodoInicio, int umbralSensores, int tiempoConfirmacionMs)
        {
            string m = (metodoInicio ?? "sensores").Trim().ToLowerInvariant();
            if (m != "herramienta" && m != "pintando" && m != "manual") m = "sensores";
            _metodo = m;
            _umbralSensores = Math.Max(1, umbralSensores);
            _confirmacionMs = Math.Max(0, tiempoConfirmacionMs);
        }

        /// <summary>Inicio/parada manual (método "manual", o stop del operario).</summary>
        public void ForzarManual(bool activo)
        {
            _activo = activo;
            ResetTimers();
            MotivoDetenido = activo ? "" : "Monitoreo detenido manualmente";
        }

        /// <summary>Vuelve a inactivo y limpia timers (stop del servicio, cambio de config).</summary>
        public void Reset()
        {
            _activo = false;
            ResetTimers();
            MotivoDetenido = "";
        }

        private void ResetTimers()
        {
            _paradaDesde = DateTime.MinValue;
            _sinPintarDesde = DateTime.MinValue;
            _confirmacionDesde = DateTime.MinValue;
        }

        /// <summary>
        /// Evalúa un ciclo con las lecturas actuales. Devuelve el estado activo
        /// resultante y deja el motivo en MotivoDetenido.
        /// </summary>
        public bool Evaluar(SiembraEntrada e, DateTime now)
        {
            if (e == null) return _activo;

            if (_activo)
            {
                EvaluarApagado(e, now);
            }
            else
            {
                EvaluarArranque(e, now);
            }
            if (_activo) MotivoDetenido = "";
            return _activo;
        }

        // ── Apagado (histéresis) ─────────────────────────────────────────────
        private void EvaluarApagado(SiembraEntrada e, DateTime now)
        {
            // Parada del tractor: vel < 0.3 sostenida > 10 s → off.
            if (e.VelocidadKmh < VelParada)
            {
                if (_paradaDesde == DateTime.MinValue) _paradaDesde = now;
                else if ((now - _paradaDesde).TotalSeconds > ParadaSegundos)
                {
                    _activo = false;
                    ResetTimers();
                    MotivoDetenido = "Tractor detenido (vel < " + VelParada.ToString("0.0") + " km/h)";
                    return;
                }
            }
            else
            {
                _paradaDesde = DateTime.MinValue;
            }

            // Modo pintando: si PilotX deja de pintar todas las secciones por
            // > 2 s, apagamos sin esperar a la parada — "no pinta → no siembro".
            if (_metodo == "pintando")
            {
                if (e.SeccionesActivas == 0)
                {
                    if (_sinPintarDesde == DateTime.MinValue) _sinPintarDesde = now;
                    else if ((now - _sinPintarDesde).TotalSeconds > SinPintarSegundos)
                    {
                        _activo = false;
                        ResetTimers();
                        MotivoDetenido = "PilotX dejó de pintar secciones";
                        return;
                    }
                }
                else
                {
                    _sinPintarDesde = DateTime.MinValue;
                }
            }
        }

        // ── Arranque (por método) ────────────────────────────────────────────
        private void EvaluarArranque(SiembraEntrada e, DateTime now)
        {
            switch (_metodo)
            {
                case "pintando":
                    if (!e.SeccionesDisponibles)
                    {
                        MotivoDetenido = "Sin fuente de secciones PilotX";
                    }
                    else if (e.SeccionesActivas == 0)
                    {
                        MotivoDetenido = "PilotX no está pintando ninguna sección";
                    }
                    else if (e.VelocidadKmh <= VelMinPintando)
                    {
                        MotivoDetenido = "Velocidad " + e.VelocidadKmh.ToString("0.0")
                            + " km/h ≤ " + VelMinPintando.ToString("0.0");
                    }
                    else
                    {
                        Activar();
                    }
                    break;

                case "herramienta":
                    if (!e.SeccionesDisponibles)
                    {
                        MotivoDetenido = "Sin fuente de secciones PilotX";
                    }
                    else if (e.SeccionesActivas == 0)
                    {
                        MotivoDetenido = "Herramienta levantada (sin secciones activas)";
                    }
                    else
                    {
                        Activar();
                    }
                    break;

                case "manual":
                    MotivoDetenido = "Esperando inicio manual del operario";
                    break;

                default: // "sensores"
                    bool condicion = e.SensoresActivos >= _umbralSensores
                                  && e.VelocidadKmh > VelMinSensores;
                    if (condicion)
                    {
                        // Confirmación sostenida: la condición debe mantenerse
                        // TiempoConfirmacionMs para filtrar falsos arranques.
                        if (_confirmacionDesde == DateTime.MinValue)
                        {
                            _confirmacionDesde = now;
                            if (_confirmacionMs > 0)
                                MotivoDetenido = "Confirmando inicio…";
                            else
                                Activar();
                        }
                        else if ((now - _confirmacionDesde).TotalMilliseconds >= _confirmacionMs)
                        {
                            Activar();
                        }
                        else
                        {
                            MotivoDetenido = "Confirmando inicio…";
                        }
                    }
                    else
                    {
                        _confirmacionDesde = DateTime.MinValue;
                        if (e.SensoresActivos < _umbralSensores)
                            MotivoDetenido = "Solo " + e.SensoresActivos
                                + " sensor(es) con caída de semilla (umbral " + _umbralSensores + ")";
                        else
                            MotivoDetenido = "Velocidad " + e.VelocidadKmh.ToString("0.0")
                                + " km/h ≤ " + VelMinSensores.ToString("0.0");
                    }
                    break;
            }
        }

        private void Activar()
        {
            _activo = true;
            ResetTimers();
            MotivoDetenido = "";
        }
    }
}
