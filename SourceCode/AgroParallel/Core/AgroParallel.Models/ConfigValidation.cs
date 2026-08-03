// ============================================================================
// ConfigValidation.cs — Validación de configs ANTES de persistir.
//
// Cada controller de save-config corre el validador de su módulo y, si falla,
// responde 400 con código AGP-CFG-001 (mensaje amigable + detalle técnico).
// Regla de oro: validar solo lo que el usuario pudo romper desde la UI; los
// defaults legítimos de "nodo recién dado de alta" (ej. ancho 0) NO se
// rechazan — están anotados campo por campo.
// ============================================================================

using System.Collections.Generic;

namespace AgroParallel.Models
{
    /// <summary>Resultado de validar un config antes de persistir.</summary>
    public class ValidationResult
    {
        public List<string> Errores { get; } = new List<string>();
        public bool Ok => Errores.Count == 0;
        public void Requerir(bool cond, string msg) { if (!cond) Errores.Add(msg); }
    }

    public static class ConfigValidation
    {
        public static ValidationResult ValidarRango(double v, double min, double max, string campo)
        {
            var r = new ValidationResult();
            r.Requerir(v >= min && v <= max, $"{campo} fuera de rango [{min}..{max}]: {v}");
            return r;
        }

        // ---------------- Helpers comunes ----------------

        /// <summary>UID de nodo: sin espacios; si el nodo está habilitado, requerido.</summary>
        private static void ValidarUidNodo(ValidationResult r, string uid, bool habilitado, string contexto)
        {
            if (uid != null && uid.Trim().Length > 0)
                r.Requerir(!uid.Contains(" "), $"{contexto}: el UID '{uid}' no puede contener espacios");
            else
                r.Requerir(!habilitado, $"{contexto}: nodo habilitado sin UID");
        }

        private static void Rango(ValidationResult r, double v, double min, double max, string campo)
        {
            r.Requerir(v >= min && v <= max, $"{campo} fuera de rango [{min}..{max}]: {v}");
        }

        // ---------------- FlowX ----------------

        public static ValidationResult ValidarFlowX(FlowXConfigDto cfg)
        {
            var r = new ValidationResult();
            if (cfg == null) { r.Requerir(false, "config FlowX vacía"); return r; }
            if (cfg.Nodos == null) return r;

            foreach (var n in cfg.Nodos)
            {
                if (n == null) continue;
                string ctx = "FlowX nodo '" + (n.Uid ?? "?") + "'";
                ValidarUidNodo(r, n.Uid, n.Habilitado, ctx);
                // AnchoBarraM = 0 es el default de alta automática (nodo recién
                // descubierto); solo rechazamos negativos.
                r.Requerir(n.AnchoBarraM >= 0, $"{ctx}: ancho de barra negativo: {n.AnchoBarraM}");

                if (n.Productos == null) continue;
                foreach (var p in n.Productos)
                {
                    if (p == null) continue;
                    string pctx = ctx + " producto '" + (p.Nombre ?? p.Id.ToString()) + "'";
                    Rango(r, p.PwmMin, 0, 4095, pctx + ": pwm_min");
                    // PwmMax 0 (o <= PwmMin) significa "sin techo extra" según el
                    // firmware — solo exigimos coherencia cuando hay techo real.
                    if (p.PwmMax > 0)
                    {
                        Rango(r, p.PwmMax, 1, 4095, pctx + ": pwm_max");
                        r.Requerir(p.PwmMin < p.PwmMax, $"{pctx}: pwm_min ({p.PwmMin}) debe ser menor que pwm_max ({p.PwmMax})");
                    }
                    r.Requerir(p.MeterCal > 0, $"{pctx}: meter_cal debe ser mayor que 0: {p.MeterCal}");
                    r.Requerir(p.Kp >= 0, $"{pctx}: kp no puede ser negativo: {p.Kp}");
                    r.Requerir(p.Ki >= 0, $"{pctx}: ki no puede ser negativo: {p.Ki}");
                    r.Requerir(p.Kd >= 0, $"{pctx}: kd no puede ser negativo: {p.Kd}");
                    r.Requerir(p.DosisLha >= 0, $"{pctx}: dosis (L/ha) no puede ser negativa: {p.DosisLha}");
                    r.Requerir(p.ManualLmin >= 0, $"{pctx}: caudal manual (L/min) no puede ser negativo: {p.ManualLmin}");
                }
            }
            return r;
        }

        // ---------------- QuantiX ----------------

        public static ValidationResult ValidarQuantiX(QxMotoresConfigDto cfg)
        {
            var r = new ValidationResult();
            if (cfg == null) { r.Requerir(false, "config QuantiX vacía"); return r; }
            if (cfg.Nodos == null) return r;

            foreach (var n in cfg.Nodos)
            {
                if (n == null) continue;
                string ctx = "QuantiX nodo '" + (n.Uid ?? "?") + "'";
                ValidarUidNodo(r, n.Uid, n.Habilitado, ctx);

                if (n.Motores == null) continue;
                foreach (var m in n.Motores)
                {
                    if (m == null) continue;
                    string mctx = ctx + " motor '" + (m.Nombre ?? "?") + "'";
                    // dientes_engranaje = pulsos por vuelta del dosificador.
                    r.Requerir(m.DientesEngranaje > 0, $"{mctx}: pulsos por vuelta (dientes_engranaje) debe ser mayor que 0: {m.DientesEngranaje}");
                    r.Requerir(m.DosisFija >= 0, $"{mctx}: dosis objetivo no puede ser negativa: {m.DosisFija}");
                    Rango(r, m.PwmMin, 0, 4095, mctx + ": pwm_min");
                    Rango(r, m.PwmMax, 1, 4095, mctx + ": pwm_max");
                    r.Requerir(m.PwmMin < m.PwmMax, $"{mctx}: pwm_min ({m.PwmMin}) debe ser menor que pwm_max ({m.PwmMax})");
                    r.Requerir(m.Kp >= 0, $"{mctx}: kp no puede ser negativo: {m.Kp}");
                    r.Requerir(m.Ki >= 0, $"{mctx}: ki no puede ser negativo: {m.Ki}");
                    r.Requerir(m.Kd >= 0, $"{mctx}: kd no puede ser negativo: {m.Kd}");
                    r.Requerir(m.MeterCal > 0, $"{mctx}: meter_cal debe ser mayor que 0: {m.MeterCal}");
                }
            }
            return r;
        }

        // ---------------- VistaX ----------------

        /// <summary>Valida vistaX.json (timeouts / cadencias de la app).</summary>
        public static ValidationResult ValidarVistaXConfig(VistaXConfigDto cfg)
        {
            var r = new ValidationResult();
            if (cfg == null) { r.Requerir(false, "config VistaX vacía"); return r; }
            r.Requerir(cfg.SensorTimeoutMs >= 500, $"timeout de sensor debe ser al menos 500 ms: {cfg.SensorTimeoutMs}");
            r.Requerir(cfg.UiUpdateIntervalMs >= 100, $"intervalo de refresco de UI debe ser al menos 100 ms: {cfg.UiUpdateIntervalMs}");
            r.Requerir(cfg.TiempoConfirmacionMs >= 0, $"tiempo de confirmación no puede ser negativo: {cfg.TiempoConfirmacionMs}");
            return r;
        }

        /// <summary>
        /// Valida el implemento VistaX RESULTANTE del merge (el controller pisa
        /// solo los campos VistaX-owned sobre lo persistido; la geometría física
        /// la manda el implemento central y acá no se valida).
        /// </summary>
        public static ValidationResult ValidarVistaXImplemento(VistaXImplementoDto imp)
        {
            var r = new ValidationResult();
            if (imp == null) { r.Requerir(false, "implemento VistaX vacío"); return r; }

            var s = imp.Setup;
            if (s != null)
            {
                r.Requerir(s.DensidadObjetivo >= 0, $"densidad objetivo no puede ser negativa: {s.DensidadObjetivo}");
                Rango(r, s.ToleranciaDesvio, 0, 100, "tolerancia de desvío (%)");
                r.Requerir(s.MaxDensidadSensor > 0, $"máx. densidad del sensor debe ser mayor que 0: {s.MaxDensidadSensor}");
                r.Requerir(s.FactorK >= 0, $"factor K no puede ser negativo: {s.FactorK}");
                // 0 = derivar TotalSurcos/Torres (default legítimo).
                if (s.SurcosPorTorre != 0)
                    Rango(r, s.SurcosPorTorre, 1, 32, "surcos por torre");
            }

            if (imp.MapeoSensores != null)
            {
                foreach (var sen in imp.MapeoSensores)
                {
                    if (sen == null) continue;
                    string ctx = "sensor '" + (sen.Uid ?? "?") + "' cable " + sen.Cable;
                    ValidarUidNodo(r, sen.Uid, sen.IsActive, ctx);
                    r.Requerir(sen.Cable >= 0, $"{ctx}: cable negativo");
                    if (sen.SurcoDesde > 0 && sen.SurcoHasta > 0)
                        r.Requerir(sen.SurcoDesde <= sen.SurcoHasta, $"{ctx}: surco_desde ({sen.SurcoDesde}) no puede superar surco_hasta ({sen.SurcoHasta})");
                }
            }
            return r;
        }

        // ---------------- SectionX ----------------

        public static ValidationResult ValidarSectionX(SectionXConfigDto cfg)
        {
            var r = new ValidationResult();
            if (cfg == null) { r.Requerir(false, "config SectionX vacía"); return r; }
            if (cfg.Nodos == null) return r;

            foreach (var n in cfg.Nodos)
            {
                if (n == null) continue;
                string ctx = "SectionX nodo '" + (n.Uid ?? "?") + "'";
                ValidarUidNodo(r, n.Uid, n.Habilitado, ctx);
                r.Requerir(n.DistanciaEntreTrenes >= 0, $"{ctx}: distancia entre trenes negativa: {n.DistanciaEntreTrenes}");
                if (n.Cables == null) continue;
                r.Requerir(n.Cables.Count <= 16, $"{ctx}: máximo 16 secciones por nodo, hay {n.Cables.Count}");
                foreach (var c in n.Cables)
                {
                    if (c == null) continue;
                    Rango(r, c.Cable, 1, 16, ctx + ": número de cable");
                    r.Requerir(c.SeccionAOG >= 0, $"{ctx}: sección PilotX negativa en cable {c.Cable}");
                }
            }
            return r;
        }

        // ---------------- LineX ----------------

        public static ValidationResult ValidarLineX(LineXConfigDto cfg)
        {
            var r = new ValidationResult();
            if (cfg == null) { r.Requerir(false, "config LineX vacía"); return r; }
            if (cfg.Nodos == null) return r;

            foreach (var n in cfg.Nodos)
            {
                if (n == null) continue;
                string ctx = "LineX nodo '" + (n.Uid ?? "?") + "'";
                ValidarUidNodo(r, n.Uid, n.Habilitado, ctx);
                Rango(r, n.SectionCount, 1, 32, ctx + ": cantidad de surcos (section_count)");
                r.Requerir(n.CommTimeoutMs >= 500, $"{ctx}: timeout de comunicación debe ser al menos 500 ms: {n.CommTimeoutMs}");
                if (n.Surcos == null) continue;
                foreach (var sx in n.Surcos)
                {
                    if (sx == null) continue;
                    string sctx = ctx + " surco " + sx.Idx;
                    r.Requerir(sx.Channel >= 0, $"{sctx}: canal negativo: {sx.Channel}");
                    r.Requerir(sx.SeccionAOG >= 0, $"{sctx}: sección PilotX negativa: {sx.SeccionAOG}");
                    // Solo aplica al backend servo: pulsos de calibración coherentes.
                    if (sx.MinUs > 0 && sx.MaxUs > 0)
                        r.Requerir(sx.MinUs < sx.MaxUs, $"{sctx}: min_us ({sx.MinUs}) debe ser menor que max_us ({sx.MaxUs})");
                }
            }
            return r;
        }

        // ---------------- StormX ----------------

        public static ValidationResult ValidarStormX(StormXConfigDto cfg)
        {
            var r = new ValidationResult();
            if (cfg == null) { r.Requerir(false, "config StormX vacía"); return r; }

            // 0 = no loggear (documentado en el DTO); solo rechazamos negativos.
            r.Requerir(cfg.LogIntervalSec >= 0, $"intervalo de log no puede ser negativo: {cfg.LogIntervalSec}");

            var l = cfg.Limits;
            if (l != null)
            {
                r.Requerir(l.WindMinMs >= 0, $"viento mínimo no puede ser negativo: {l.WindMinMs}");
                r.Requerir(l.WindMinMs < l.WindMaxMs, $"viento mínimo ({l.WindMinMs}) debe ser menor que viento máximo ({l.WindMaxMs})");
                Rango(r, l.HumMinPct, 0, 100, "humedad mínima (%)");
                r.Requerir(l.DeltaTMaxC > 0, $"delta-T máximo debe ser mayor que 0: {l.DeltaTMaxC}");
            }

            if (cfg.Nodos != null)
            {
                foreach (var n in cfg.Nodos)
                {
                    if (n == null) continue;
                    string ctx = "StormX nodo '" + (n.Uid ?? "?") + "'";
                    ValidarUidNodo(r, n.Uid, n.Habilitado, ctx);
                    r.Requerir(n.AlturaM >= 0, $"{ctx}: altura del sensor negativa: {n.AlturaM}");
                }
            }
            return r;
        }

        // ---------------- Implemento central ----------------

        public static ValidationResult ValidarImplemento(ImplementoDto imp)
        {
            var r = new ValidationResult();
            if (imp == null) { r.Requerir(false, "implemento vacío"); return r; }

            r.Requerir(!string.IsNullOrWhiteSpace(imp.Nombre), "el implemento necesita un nombre");
            // Ancho 0 es el default de un implemento recién creado (todavía sin
            // configurar) — solo rechazamos negativos para no romper flujos
            // parciales como el pin de nodos en /pages/nodos.html.
            r.Requerir(imp.AnchoTotalM >= 0, $"ancho total negativo: {imp.AnchoTotalM}");
            r.Requerir(imp.NumeroSurcos >= 0, $"número de surcos negativo: {imp.NumeroSurcos}");
            r.Requerir(imp.DistanciaEntreSurcosM >= 0, $"distancia entre surcos negativa: {imp.DistanciaEntreSurcosM}");

            if (imp.NodosUids != null)
            {
                foreach (var uid in imp.NodosUids)
                {
                    r.Requerir(!string.IsNullOrWhiteSpace(uid), "nodos_uids: hay un UID vacío");
                    if (uid != null && uid.Trim().Length > 0)
                        r.Requerir(!uid.Contains(" "), $"nodos_uids: el UID '{uid}' no puede contener espacios");
                }
            }
            return r;
        }

        /// <summary>
        /// Valida los trenes del implemento (rango de distancia, tren delantero
        /// en 0, surcos sin tren huérfano). Son errores de REPORTE nomás: el
        /// guardado del implemento no se bloquea por esto (criterio de la spec
        /// de "implemento unificado" fase 1).
        /// </summary>
        public static ValidationResult ValidarTrenes(ImplementoDto imp)
        {
            var r = new ValidationResult();
            if (imp == null) return r;

            var trenes = imp.Trenes ?? new List<TrenDto>();
            r.Requerir(trenes.Count <= 4, $"implemento: máximo 4 trenes, hay {trenes.Count}");

            var ids = new HashSet<int>();
            foreach (var t in trenes)
            {
                if (t == null) continue;
                r.Requerir(!ids.Contains(t.Id), $"tren duplicado: id {t.Id}");
                ids.Add(t.Id);
                Rango(r, t.DistanciaM, 0, 20, $"tren {t.Id}: distancia");
                if (t.Id == 1)
                    r.Requerir(t.DistanciaM == 0, $"tren 1 (delantero) debe tener distancia 0: {t.DistanciaM}");
            }

            if (imp.Surcos != null && trenes.Count > 0)
            {
                foreach (var s in imp.Surcos)
                {
                    if (s == null) continue;
                    r.Requerir(ids.Contains(s.TrenId), $"surco {s.Numero}: tren inexistente {s.TrenId}");
                }
            }
            return r;
        }
    }
}
