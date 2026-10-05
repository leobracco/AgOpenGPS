// ============================================================================
// QxAgro.cs — port 1:1 de wwwroot/js/qx-agro.js.
//
// QUÉ QUEDÓ NATIVO: toda la conversión de telemetría cruda (pps) a unidades
// agronómicas que usa el editor nativo de QuantiX (QuantiXEditorPanel).
// QUÉ SIGUE EN HTML: qx-agro.js sigue existiendo tal cual porque la página
// pages/quantix.html la sigue usando el celular/PWA (regla del repo: las
// páginas HTML no se borran).
//
// POR QUÉ DUPLICADO: las fórmulas ya viven en QuantiXMotorBridge.cs (camino
// dosis→pps) y en qx-agro.js (camino inverso, para pantalla). Esta es la
// tercera copia, la del panel nativo. Si se toca el criterio en el bridge,
// hay que tocarlo acá también — ver aviso cruzado en QuantiXMotorBridge.cs.
//
// REGLA DE PRODUCTO: el operario JAMÁS ve pps. Ve sem/m, sem/ha o kg/ha, y
// rpm para saber si el motor responde.
// ============================================================================

using System;
using System.Globalization;

namespace PilotX.Desktop.Services;

/// <summary>Contexto agronómico de la máquina: velocidad, ancho de labor y
/// distancia entre hileras. Sale del implemento central pisado por
/// /api/tool (PilotX es la fuente de verdad del ancho).</summary>
public sealed class QxAgroCtx
{
    public double SpeedKmh   { get; set; }
    public double ToolWidthM { get; set; }
    public double RowSpacingM { get; set; } = 0.525;
}

/// <summary>Resultado de la conversión. Mode: "sem" o "kg".</summary>
public sealed class QxAgroUnits
{
    public string Mode  { get; set; } = "kg";
    public bool   Valid { get; set; }
    public double SemM  { get; set; }
    public double Sem10m { get; set; }
    public double SemHa { get; set; }
    public double KgHa  { get; set; }
}

public static class QxAgro
{
    /// <summary>Pulsos por vuelta del motor (dientes_engranaje). 24 es el
    /// default histórico del JS cuando el motor no lo trae.</summary>
    private static double Ppr(QxMotorConfig? m)
        => (m != null && m.DientesEngranaje > 0) ? m.DientesEngranaje : 24.0;

    /// <summary>pps → rpm del eje del dosificador.</summary>
    public static double PpsToRpm(QxMotorConfig? m, double pps)
    {
        double ppr = Ppr(m);
        return ppr > 0 ? (pps / ppr * 60.0) : 0.0;
    }

    public static QxAgroCtx CtxFrom(QxImplemento? impl, double speedKmh)
    {
        double spacing = (impl != null && impl.DistanciaEntreSurcosM > 0)
            ? impl.DistanciaEntreSurcosM : 0.525;
        return new QxAgroCtx
        {
            SpeedKmh    = speedKmh,
            ToolWidthM  = impl?.AnchoTotalM ?? 0,
            RowSpacingM = spacing,
        };
    }

    public static QxAgroUnits Units(QxMotorConfig? motor, double ppsReal, QxAgroCtx? ctx)
    {
        ctx ??= new QxAgroCtx();
        double vMs = ctx.SpeedKmh / 3.6;
        double pps = ppsReal;

        if (motor != null && string.Equals(motor.UnidadDosis, "sem_m", StringComparison.Ordinal))
        {
            // semillas/pulso = semillas por vuelta / pulsos del encoder por vuelta.
            double semPorPulso = motor.SemillasVuelta / Ppr(motor);
            int surcos = (motor.Cortes != null && motor.Cortes.Count > 0) ? motor.Cortes.Count : 1;
            double spacing = ctx.RowSpacingM > 0 ? ctx.RowSpacingM : 0.525;
            double semM = vMs > 0 ? (pps * semPorPulso / (vMs * surcos)) : 0.0;
            return new QxAgroUnits
            {
                Mode   = "sem",
                Valid  = vMs > 0,
                SemM   = semM,
                Sem10m = semM * 10.0,
                SemHa  = spacing > 0 ? (semM * 10000.0 / spacing) : 0.0,
                KgHa   = 0,
            };
        }

        // kg/ha: meter_cal = GRAMOS por pulso. Ancho = ancho total de labor.
        double meterCal = motor != null ? motor.MeterCal : 50.0;
        double ancho = ctx.ToolWidthM > 0 ? ctx.ToolWidthM : 0.0;
        double kgHa = (vMs > 0 && ancho > 0) ? (pps * meterCal * 10.0 / (ancho * vMs)) : 0.0;
        return new QxAgroUnits { Mode = "kg", Valid = vMs > 0 && ancho > 0, KgHa = kgHa };
    }

    /// <summary>Valor grande + unidad ("3.5" / "sem/m").</summary>
    public static (string V, string U) Primary(QxAgroUnits? u)
    {
        if (u == null) return ("—", "");
        if (u.Mode == "sem")
            return (u.Valid ? u.SemM.ToString("0.0", CultureInfo.InvariantCulture) : "—", "sem/m");
        return (u.Valid ? u.KgHa.ToString("0.0", CultureInfo.InvariantCulture) : "—", "kg/ha");
    }

    /// <summary>Línea compacta de lista/overlay.</summary>
    public static string Label(QxAgroUnits? u)
    {
        if (u == null) return "—";
        if (!u.Valid) return "sin velocidad";
        if (u.Mode == "sem")
            return u.SemM.ToString("0.0", CultureInfo.InvariantCulture) + " sem/m · "
                 + Math.Round(u.SemHa).ToString("0", CultureInfo.InvariantCulture) + " sem/ha";
        return u.KgHa.ToString("0.0", CultureInfo.InvariantCulture) + " kg/ha";
    }
}
