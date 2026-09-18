// ============================================================================
// qx-agro.js — conversión de telemetría cruda (pps) a unidades agronómicas.
//
// El operario NO ve pps. Ve, según el tipo de motor:
//   · motor de semilla  (unidad_dosis === 'sem_m')  → sem/m, sem/10m, sem/ha
//   · motor fertilizante (unidad_dosis === 'kg_ha') → kg/ha
//
// Fórmulas inversas verificadas contra QuantiXMotorBridge.cs (la dosis objetivo
// se convierte a pps en el bridge; acá hacemos el camino inverso con el pps real).
// La calibración (semillas_vuelta/dientes_engranaje, meter_cal) es la que define
// cuántas semillas o gramos tira cada pulso — NO el peso de la semilla.
// ============================================================================
(function (global) {
  'use strict';

  // motor: config del motor (unidad_dosis, semillas_vuelta, dientes_engranaje,
  //        meter_cal, cortes).
  // ppsReal: pulsos/seg medidos por el encoder del motor.
  // ctx: { speedKmh, toolWidthM, rowSpacingM }.
  // Devuelve { mode:'sem'|'kg', valid, sem_m, sem_10m, sem_ha, kg_ha }.
  function units(motor, ppsReal, ctx) {
    motor = motor || {};
    ctx = ctx || {};
    var vMs = (ctx.speedKmh || 0) / 3.6;
    var pps = ppsReal || 0;

    if (motor.unidad_dosis === 'sem_m') {
      // semillas/pulso = semillas por vuelta / pulsos del encoder por vuelta.
      var ppvSem = (motor.dientes_engranaje > 0) ? motor.dientes_engranaje : 24;
      var semPorPulso = (motor.semillas_vuelta || 0) / ppvSem;
      var surcos = (motor.cortes && motor.cortes.length) ? motor.cortes.length : 1;
      var spacing = (ctx.rowSpacingM > 0) ? ctx.rowSpacingM : 0.525;
      var semM = (vMs > 0) ? (pps * semPorPulso / (vMs * surcos)) : 0;
      return {
        mode: 'sem',
        valid: vMs > 0,
        sem_m: semM,
        sem_10m: semM * 10,
        sem_ha: (spacing > 0) ? (semM * 10000 / spacing) : 0,
        kg_ha: 0
      };
    }

    // kg/ha: meter_cal = GRAMOS por pulso. Ancho = ancho total de labor (eje sólido).
    var meterCal = (typeof motor.meter_cal === 'number') ? motor.meter_cal : 50;
    var ancho = (ctx.toolWidthM > 0) ? ctx.toolWidthM : 0;
    var kgHa = (vMs > 0 && ancho > 0) ? (pps * meterCal * 10 / (ancho * vMs)) : 0;
    return {
      mode: 'kg',
      valid: vMs > 0 && ancho > 0,
      sem_m: 0, sem_10m: 0, sem_ha: 0,
      kg_ha: kgHa
    };
  }

  // Valor + unidad "principal" de un motor (lo que va en cajita grande / objetivo).
  function primary(u) {
    if (!u) return { v: '\u2014', u: '' };
    if (u.mode === 'sem') {
      return { v: u.valid ? u.sem_m.toFixed(1) : '\u2014', u: 'sem/m' };
    }
    return { v: u.valid ? u.kg_ha.toFixed(1) : '\u2014', u: 'kg/ha' };
  }

  // Línea compacta para listas/overlay (sem → sem/m + sem/ha; kg → kg/ha).
  function label(u) {
    if (!u) return '\u2014';
    if (!u.valid) return 'sin velocidad';
    if (u.mode === 'sem') {
      return u.sem_m.toFixed(1) + ' sem/m \xb7 ' + Math.round(u.sem_ha) + ' sem/ha';
    }
    return u.kg_ha.toFixed(1) + ' kg/ha';
  }

  // Construye el ctx a partir del estado del implemento central + velocidad PilotX.
  function ctxFrom(implCentral, speedKmh) {
    implCentral = implCentral || {};
    return {
      speedKmh: speedKmh || 0,
      toolWidthM: implCentral.ancho_total_m || 0,
      rowSpacingM: implCentral.distancia_entre_surcos_m || 0.525
    };
  }

  global.qxAgro = { units: units, primary: primary, label: label, ctxFrom: ctxFrom };
})(window);
