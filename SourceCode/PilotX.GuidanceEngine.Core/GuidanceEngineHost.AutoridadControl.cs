// ============================================================================
// GuidanceEngineHost.AutoridadControl.cs — desenganche pedido por la
// autoridad de control (AgroParallel.Services.Control).
//
// Cuando un celular/tablet que tenía el control deja de latir (o lo suelta)
// habiendo enganchado el piloto, el WebHost llama acá: se suelta el piloto y
// la cabina muestra el MISMO cartel que los otros desenganches automáticos
// (AvisarPiloto → PilotoAviso del snapshot → toast con "Qué hacer").
//
// Archivo nuevo, aditivo: el head Android corre este mismo host.
// ============================================================================

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Desengancha el piloto (si está puesto) y avisa el motivo a la
        /// cabina. Mismo lock que el pipeline de fix. true si lo desenganchó.</summary>
        public bool DesengancharPilotoPorControl(string motivo)
        {
            bool solto = false;
            ProcesarFixSerializado(() =>
            {
                if (!isBtnAutoSteerOn) return;
                ((IAutoSteerHost)this).PerformAutoSteerClick();
                AvisarPiloto(string.IsNullOrWhiteSpace(motivo)
                    ? "Piloto desenganchado: la pantalla que tenía el control dejó de responder."
                    : motivo);
                solto = true;
            });
            return solto;
        }
    }
}
