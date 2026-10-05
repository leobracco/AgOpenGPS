// ============================================================================
// VxSurcoEvaluator.cs — decide el ESTADO de un surco de VistaX y si alarma.
//
// Es la decisión que el operario ve como un cuadrito de color en el overlay, y
// la que lo hace frenar el tractor. Vivía embebida en el tick de
// VistaXLiveService, mezclada con MQTT, catálogo de insumos, máquina de
// siembra y estado de secciones: correcta, pero imposible de testear.
//
// Acá queda como función PURA para poder fijar con tests los casos que
// importan en campo — bajada tapada, sensor sin señal, sección levantada,
// tolva vacía — y que la UI y el overlay lean la misma verdad.
//
// El ORDEN de las reglas es parte del contrato, no un detalle: sección cortada
// gana sobre silenciado, y silenciado gana sobre sin-datos. Si se invierte,
// un cuerpo levantado empieza a alarmar en cada cabecera.
// ============================================================================

using System;

namespace AgroParallel.VistaX
{
    /// <summary>Lo que se sabe de un surco en este tick.</summary>
    public struct VxSurcoInput
    {
        /// <summary>La sección que cubre este surco está cortada (cabecera,
        /// corte manual o automático).</summary>
        public bool SeccionCortada;
        /// <summary>El operario silenció este sensor por configuración.</summary>
        public bool Silenciado;
        /// <summary>No llega telemetría hace más que el timeout.</summary>
        public bool SinDatos;
        /// <summary>El sensor mide siembra o fertilizante (no turbina ni estado).</summary>
        public bool EsSiembra;
        /// <summary>Sensor on/off (bajada, tolva, presión, final de carrera).</summary>
        public bool EsEstado;
        /// <summary>Para sensores de estado: es específicamente tolva vacía.</summary>
        public bool EsTolvaVacia;
        /// <summary>Valor crudo del sensor de estado (≥0,5 = activo).</summary>
        public double Valor;

        /// <summary>La máquina de siembra dice que estamos sembrando.</summary>
        public bool SembrandoActivo;

        /// <summary>Semillas por minuto medidas.</summary>
        public double Spm;
        /// <summary>Objetivo en sem/min. ≤0 = sin objetivo, no se evalúa.</summary>
        public double ObjetivoSpm;
        /// <summary>Límites absolutos en sem/min (del insumo o de la tolerancia).</summary>
        public double LimiteBajo;
        public double LimiteAlto;
    }

    /// <summary>Estado + si dispara alarma productiva.</summary>
    public struct VxSurcoEstado
    {
        public string Estado;
        public bool Alerta;
        public VxSurcoEstado(string estado, bool alerta) { Estado = estado; Alerta = alerta; }
    }

    public static class VxSurcoEvaluator
    {
        // Nombres de estado que consume la UI. No cambiar sin tocar el overlay.
        public const string SeccionOff = "seccion-off";
        public const string Silenciado = "muted";
        public const string SinDatos = "no-data";
        public const string Tapado = "tapado";
        public const string Bajo = "bajo";
        public const string Exceso = "exceso";
        public const string Alerta = "alerta";
        public const string Ok = "ok";

        /// <summary>Debajo de esto se considera que no cae nada por la bajada.</summary>
        public const double SpmMinimo = 0.5;

        public static VxSurcoEstado Evaluar(VxSurcoInput e)
        {
            // 1) Sección cortada gana sobre todo: el operario (o el corte
            //    automático) decidió que esta franja no siembra ahora. Sin esta
            //    prioridad, cada cabecera dispararía una alarma por cuerpo.
            if (e.SeccionCortada) return new VxSurcoEstado(SeccionOff, false);

            // 2) Silenciado por config: se sigue mostrando la lectura en gris,
            //    pero no alarma ni cuenta como falla.
            if (e.Silenciado) return new VxSurcoEstado(Silenciado, false);

            // 3) Sin telemetría. Mientras se siembra, un sensor de siembra mudo
            //    ES una falla productiva (nodo caído, cable cortado): no es un
            //    gris neutro. Fuera de siembra, informativo.
            if (e.SinDatos)
                return new VxSurcoEstado(SinDatos, e.SembrandoActivo && e.EsSiembra);

            // 4) Sensores on/off: los umbrales de densidad no aplican. La única
            //    falla productiva es tolva vacía.
            if (e.EsEstado)
            {
                if (e.EsTolvaVacia && e.Valor >= 0.5) return new VxSurcoEstado(Alerta, true);
                return new VxSurcoEstado(Ok, false);
            }

            // 5) Pulso que no es de siembra y sin objetivo propio (turbina,
            //    rotación): informativo. Compararlo contra la densidad de siembra
            //    daría falsas alarmas todo el tiempo.
            if (!e.EsSiembra && e.ObjetivoSpm <= 0) return new VxSurcoEstado(Ok, false);

            // 6) Sin objetivo cargado no hay contra qué comparar.
            if (e.ObjetivoSpm <= 0) return new VxSurcoEstado(Ok, false);

            // 7) Hay telemetría pero no caen semillas: bajada bloqueada. Es la
            //    alarma que más plata salva.
            if (e.Spm <= SpmMinimo) return new VxSurcoEstado(Tapado, true);

            if (e.Spm < e.LimiteBajo) return new VxSurcoEstado(Bajo, true);

            // Exceso: se marca, pero no es falla productiva — de más siembra,
            // no de menos. Frenar el tractor por esto sería peor que seguir.
            if (e.Spm > e.LimiteAlto) return new VxSurcoEstado(Exceso, false);

            return new VxSurcoEstado(Ok, false);
        }
    }
}
