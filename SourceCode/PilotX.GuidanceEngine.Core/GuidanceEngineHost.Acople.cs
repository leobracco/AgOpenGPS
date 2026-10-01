// ============================================================================
// GuidanceEngineHost.Acople.cs — condiciones para ACOPLAR el piloto.
//
// El motor es el que decide (no la pantalla): el piloto se prende desde el
// botón de la cabina, desde las barras, por MQTT o con el botón físico del
// módulo, y la regla tiene que valer para todos. La pantalla solo lee el
// motivo (MotivoNoAcopla, viaja en el state) para decirlo en criollo.
//
// Solo frena el ACOPLE. Desacoplar siempre se puede, sin condiciones.
// Los límites vienen en 0 (apagados) hasta validarlos en lote.
// ============================================================================

using AgLibrary.Logging;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Por qué AHORA no se podría acoplar el piloto (distancia /
        /// ángulo a la guía). null = se puede, o no hay guía activa (eso lo
        /// informa otro requisito).</summary>
        public string MotivoNoAcopla()
        {
            if (Trk == null || Trk.idx < 0 || Trk.idx >= Trk.gArr.Count || Vehicle == null)
                return null;

            var s = Properties.Settings.Default;
            return CondicionesAcople.PorQueNoAcopla(
                Vehicle.modeActualXTE, Vehicle.modeActualHeadingError,
                s.setAS_acopleMaxDistanciaM, s.setAS_acopleMaxAnguloDeg);
        }

        /// <summary>Alterna el piloto respetando las condiciones de acople.
        /// Devuelve false si se negó a acoplar.</summary>
        private bool AlternarPilotoConCondiciones()
        {
            if (!isBtnAutoSteerOn)
            {
                string motivo = MotivoNoAcopla();
                if (motivo != null)
                {
                    Log.EventWriter("Piloto NO acopla: " + motivo);
                    return false;
                }
            }

            ((IAutoSteerHost)this).PerformAutoSteerClick();
            return true;
        }
    }
}
