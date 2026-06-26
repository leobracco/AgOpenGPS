// ============================================================================
// FormGpsQuantiXRuntimeService.cs
// Adaptador IQuantiXRuntimeService → PilotX. Combina:
//   · MotoresConfig (quantiX_motores.json) leído por QuantiXMotorBridge
//   · Snapshot PilotX (velocidad, ancho, secciones, dosis shape activa)
// y emite el techo operativo + targetPps con la MISMA fórmula que el bridge
// usa al publicar a MQTT. Single source of truth — el bridge sigue siendo
// quien efectivamente comanda; este service expone los mismos números al view.
// ============================================================================

using System.Collections.Generic;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;

    public sealed class FormGpsQuantiXRuntimeService : IQuantiXRuntimeService
    {
        private readonly FormGPS _form;
        private readonly IAogStateProvider _state;

        public FormGpsQuantiXRuntimeService(FormGPS form, IAogStateProvider state)
        {
            _form = form;
            _state = state;
        }

        public QuantiXRuntimeSnapshot GetSnapshot()
        {
            var snap = new QuantiXRuntimeSnapshot
            {
                Motores = new List<QuantiXMotorRuntime>(),
                CurrentSpeedKmh = 0,
                CurrentToolWidthM = 0
            };
            if (_form == null) return snap;

            var aog = _state != null ? _state.GetSnapshot() : null;
            double vel = aog != null ? aog.AvgSpeed : 0;
            double anchoTotal = aog != null && aog.ToolWidth > 0 ? aog.ToolWidth : 0;
            snap.CurrentSpeedKmh = vel;
            snap.CurrentToolWidthM = anchoTotal;

            // Tomamos MotoresConfig directo del archivo (mismo que carga el bridge).
            MotoresConfig mc;
            try { mc = MotoresConfig.Load(); }
            catch { mc = new MotoresConfig(); }
            if (mc == null || mc.Nodos == null) return snap;

            foreach (var nodo in mc.Nodos)
            {
                if (!nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;
                if (nodo.Motores == null) continue;
                for (int mi = 0; mi < nodo.Motores.Length; mi++)
                {
                    var motor = nodo.Motores[mi];
                    if (motor == null) continue;

                    // Dosis objetivo: misma prioridad que el bridge.
                    // 1) Manual (widget pantalla principal) override total.
                    // 2) Dosis fija de config (fuera de mapa default).
                    // 3) Campo del shapefile.
                    // 4) Shape activa.
                    double dosis;
                    if (motor.ManualMode)
                    {
                        dosis = motor.ManualDosis;
                    }
                    else
                    {
                        dosis = motor.DosisFija;
                        if (dosis <= 0)
                        {
                            if (!string.IsNullOrEmpty(motor.CampoDosis) && _state != null)
                                dosis = _state.GetShapeFieldDose(motor.CampoDosis);
                            else if (aog != null && aog.ShapeIsInside)
                                dosis = aog.ShapeCurrentDose;
                        }
                    }

                    double meterCal = motor.MeterCal > 0 ? motor.MeterCal : 1;
                    double ppr = motor.DientesEngranaje > 0 ? motor.DientesEngranaje : 24;
                    double maxHz = motor.MaxHz > 0 ? motor.MaxHz : 0;

                    // Target PPS si hubiera sección ON: usamos anchoTotal (siempre).
                    double targetPps = 0;
                    if (dosis > 0 && vel > 0.5 && anchoTotal > 0)
                    {
                        double velMs = vel / 3.6;
                        double gPorSeg = (dosis * 1000.0 * anchoTotal * velMs) / 10000.0;
                        targetPps = gPorSeg / meterCal;
                    }

                    var rt = new QuantiXMotorRuntime
                    {
                        NodoUid = nodo.Uid,
                        MotorIndex = mi,
                        Nombre = motor.Nombre,
                        Habilitado = (motor.Cortes != null && motor.Cortes.Count > 0)
                                     || motor.DosisFija > 0
                                     || !string.IsNullOrEmpty(motor.CampoDosis),
                        DosisObjetivo = dosis,
                        TargetPps = targetPps,
                        TargetRpm = ppr > 0 ? targetPps * 60.0 / ppr : 0,
                        MaxHz = maxHz,
                        MaxRpm = (maxHz > 0 && ppr > 0) ? maxHz * 60.0 / ppr : 0,
                        MaxOutputPerSec = maxHz * meterCal,
                        MaxDoseAtCurrentSpeed = (maxHz > 0 && anchoTotal > 0 && vel > 0.5)
                            ? (maxHz * meterCal * 36.0) / (anchoTotal * vel)
                            : -1,
                        MaxDoseCurve = BuildDoseCurve(maxHz, meterCal, anchoTotal)
                    };
                    snap.Motores.Add(rt);
                }
            }
            return snap;
        }

        // Tabla de dosis máxima a velocidades típicas (5/7/10/12 km/h) para que
        // la UI muestre el envelope sin recalcular en JS.
        private static List<QuantiXMaxDosePoint> BuildDoseCurve(double maxHz, double meterCal, double ancho)
        {
            var list = new List<QuantiXMaxDosePoint>();
            if (maxHz <= 0 || meterCal <= 0 || ancho <= 0) return list;
            double[] speeds = { 5, 7, 10, 12, 15 };
            foreach (var v in speeds)
            {
                double max = (maxHz * meterCal * 36.0) / (ancho * v);
                list.Add(new QuantiXMaxDosePoint { SpeedKmh = v, MaxDose = max });
            }
            return list;
        }
    }
}
