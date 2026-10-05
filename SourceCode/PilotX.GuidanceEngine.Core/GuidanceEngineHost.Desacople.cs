// ============================================================================
// GuidanceEngineHost.Desacople.cs — desenganche automático del piloto, por
// reloj (ver VigiaDesacople), y el aviso a la cabina de POR QUÉ se soltó.
//
// El pipeline del motor corre solo cuando llega un fix. Si el GPS se corta del
// todo no corre nada: el módulo frena el volante por su watchdog, pero el
// piloto queda "puesto" y al volver la señal engancharía solo. Por eso el
// vigía tiene su propio timer.
// ============================================================================

using System;
using System.Threading;
using AgLibrary.Logging;

namespace AgOpenGPS
{
    public sealed partial class GuidanceEngineHost
    {
        /// <summary>Último aviso de desenganche automático. Seq crece con cada
        /// aviso: la cabina muestra el cartel una vez por Seq nuevo.</summary>
        public sealed class AvisoPiloto
        {
            public long Seq { get; }
            public string Motivo { get; }
            public AvisoPiloto(long seq, string motivo) { Seq = seq; Motivo = motivo; }
        }

        private const int VigiaPeriodoMs = 250;

        private readonly VigiaDesacople _vigiaDesacople = new VigiaDesacople();
        private Timer _vigiaTimer;
        private long _avisoPilotoSeq;

        // Reloj del último fix en segundos de _relojArranque (NaN = nunca hubo).
        // double no es atómico en 32 bits: se lee/escribe con Volatile/Interlocked.
        private long _ultimoFixBits = BitConverter.DoubleToInt64Bits(double.NaN);

        /// <summary>Último aviso (null = ninguno todavía). Se reemplaza entero, así
        /// que leerlo desde otro hilo es seguro.</summary>
        public AvisoPiloto UltimoAvisoPiloto { get; private set; }

        /// <summary>Publica el motivo de un desenganche para que la cabina lo diga.</summary>
        internal void AvisarPiloto(string motivo)
        {
            long seq = Interlocked.Increment(ref _avisoPilotoSeq);
            UltimoAvisoPiloto = new AvisoPiloto(seq, motivo);
            Log.EventWriter(motivo);
        }

        private void MarcarFixParaVigia()
        {
            Interlocked.Exchange(ref _ultimoFixBits,
                BitConverter.DoubleToInt64Bits(_relojArranque.Elapsed.TotalSeconds));
        }

        /// <summary>Arranca el vigía (idempotente). Lo llaman Start() y el primer
        /// fix, así corre también en modo simulador, que no pasa por Start().</summary>
        private void ArrancarVigiaPiloto()
        {
            if (_vigiaTimer != null) return;
            var t = new Timer(_ => TickVigiaPiloto(), null, VigiaPeriodoMs, VigiaPeriodoMs);
            if (Interlocked.CompareExchange(ref _vigiaTimer, t, null) != null) t.Dispose();
        }

        private void TickVigiaPiloto()
        {
            try
            {
                var s = Properties.Settings.Default;
                _vigiaDesacople.MaxSinPosicionSeg = s.setAS_desacopleSinGpsSeg;
                _vigiaDesacople.MaxDistanciaM = s.setAS_desacopleMaxDistanciaM;

                // Nada configurado: ni se toma el lock del pipeline.
                if (_vigiaDesacople.MaxSinPosicionSeg <= 0 && _vigiaDesacople.MaxDistanciaM <= 0) return;

                // Mismo lock que el pipeline de fix: el desenganche no puede
                // pisarse con un fix que se está procesando.
                ProcesarFixSerializado(() =>
                {
                    double ultimoFix = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _ultimoFixBits));
                    double distancia = HayGuiaActiva() && Vehicle != null ? Vehicle.modeActualXTE : double.NaN;

                    string motivo = _vigiaDesacople.Evaluar(
                        _relojArranque.Elapsed.TotalSeconds, isBtnAutoSteerOn, ultimoFix, distancia);

                    if (motivo != null && isBtnAutoSteerOn)
                    {
                        ((IAutoSteerHost)this).PerformAutoSteerClick();
                        AvisarPiloto(motivo);
                    }
                });
            }
            catch (Exception ex)
            {
                // Un timer que tira mata el proceso: se loguea y sigue vigilando.
                Log.EventWriter("Vigía del piloto: " + ex.Message);
            }
        }

        private bool HayGuiaActiva()
            => Trk != null && Trk.idx >= 0 && Trk.idx < Trk.gArr.Count;
    }
}
