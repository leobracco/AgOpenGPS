// ============================================================================
// NumeroJs.cs — aritmética y formato "como los hace JavaScript", en UN SOLO
// lugar. Los números que ve el operario en el panel nativo tienen que ser los
// MISMOS que veía en la página del Hub (que sigue viva para el celular): si el
// panel dice 0,15 y el celular 0,14, el que está en la cabina no sabe a cuál
// creerle.
//
// Lo usan los cuatro gráficos (GraficoLineal + Grafico*Panel) y la calculadora
// de siembra (CalculadoraSiembraPanel). Antes cada uno tenía su copia y las dos
// redondeaban distinto.
//
// ── Por qué NINGÚN formato de .NET es toFixed ───────────────────────────────
// `toFixed(f)` está definido sobre el valor EXACTO del double: busca el entero
// n que minimiza |n/10^f − x| y, si hay empate, se queda con el MAYOR (o sea,
// medio hacia arriba sobre el valor absoluto, porque el signo se saca antes).
//
//   · `ToString("F"+f)` redondea AL PAR: 0.5625 con 3 da 0.562 y JS da 0.563;
//     (2.5).toFixed(0) da 3 y "F0" da 2.
//   · El formato custom ("0.000") redondea sobre la representación decimal MÁS
//     CORTA que round-trippea el double, no sobre el valor exacto: para 0.145
//     ve "0.145", empata y sube a 0.15 — pero el double 0.145 vale en realidad
//     0.14499999999999999000798…, así que JS baja a 0.14. Lo mismo con 2.675
//     (JS 2.67) y 1.45 (JS 1.4). Ese era el bug: con el motor mandando 3
//     decimales y el panel mostrando 2, ~5 % de las lecturas de Corrección
//     mostraban un centímetro de más.
//
// La receta que sí da los dos grupos de casos: pedir los dígitos del valor
// binario REAL, pasarlos a `decimal` (base 10, sin más error de
// representación) y recién ahí redondear medio-arriba sobre el valor absoluto.
//
// OJO CON G17 — no alcanza. "G17" es el mínimo para round-trippear, pero
// REDONDEA a 17 dígitos significativos, y ese redondeo puede volver a fabricar
// el empate que se quería evitar: 1.45 vale 1.4499999999999999555910790…, y en
// G17 sale "1.45" (empata, sube a 1.5) cuando JS da 1.4. Por eso van 25
// dígitos ("G25", exacto desde .NET Core 3.0): 25 dígitos alcanzan para ver la
// cola de cualquier lectura de estas pantallas, y un empate de verdad (0.5625,
// 2.5, 10.25, 3.625 — fracciones binarias exactas) se escribe entero mucho
// antes de ese largo, así que sigue empatando y sigue subiendo.
//
// Verificado contra Node (los 8 casos de la auditoría):
//   0.5625@3→0.563 · 2.5@0→3 · 0.145@2→0.14 · 2.675@2→2.67 · 1.45@1→1.4
//   10.25@1→10.3   · 3.625@2→3.63 · −2.5@0→−3
//
// Bordes cubiertos:
//   · NaN / ±Infinity: JS devuelve "NaN" / "Infinity" / "-Infinity" (sin
//     decimales), no "NaN,00".
//   · Signo: se saca ANTES de redondear, igual que el paso 5 del algoritmo del
//     estándar. Así (−2.5).toFixed(0) da "-3" y (−0.001).toFixed(2) da "-0.00",
//     mientras que el −0 puro sale "0.00" (−0 no es < 0).
//   · Notación científica (magnitudes muy chicas o muy grandes, donde "G25"
//     escribe 9.99…E-08): la parsea igual con NumberStyles.Float.
//   · Fuera del rango de `decimal` (|v| ≳ 7,9e28): no hay a dónde convertir, se
//     cae al formato custom de antes. Ninguna de las pantallas que usan esto
//     (metros, grados, cm, kg/ha) llega ahí ni por error de tipeo.
// ============================================================================

using System;
using System.Globalization;

namespace PilotX.Desktop.Views;

internal static class NumeroJs
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>`v.toFixed(dec)`.</summary>
    public static string Fijo(double v, int dec)
    {
        // JS ignora los decimales para los no-finitos.
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";

        if (dec < 0) dec = 0;

        // El signo sale primero: el redondeo trabaja siempre sobre el módulo
        // (así el empate va "hacia arriba" en valor absoluto, como el estándar).
        bool negativo = v < 0;      // el −0 NO entra acá, igual que en JS
        double abs = negativo ? -v : v;

        string cuerpo;
        try
        {
            // G25 muestra el valor binario REAL (0.145 → 0.1449999999999999900079928),
            // que es sobre lo que decide toFixed. Con G17 el propio formato
            // redondearía 1.45 a "1.45" y volvería a empatar.
            decimal exacto = decimal.Parse(abs.ToString("G25", Inv), NumberStyles.Float, Inv);
            decimal redondeado = Math.Round(exacto, dec > 28 ? 28 : dec, MidpointRounding.AwayFromZero);
            cuerpo = redondeado.ToString(Patron(dec), Inv);
        }
        catch (OverflowException)
        {
            // Magnitud fuera de `decimal`: se formatea como antes.
            cuerpo = abs.ToString(Patron(dec), Inv);
        }
        catch (FormatException)
        {
            cuerpo = abs.ToString(Patron(dec), Inv);
        }

        return negativo ? "-" + cuerpo : cuerpo;
    }

    private static string Patron(int dec) => dec <= 0 ? "0" : "0." + new string('0', dec);

    /// <summary>`Math.round(v)` de JavaScript (medio hacia arriba, no al par:
    /// el de .NET es bancario y 2,5 daría 2).</summary>
    public static double Redondear(double v) => Math.Floor(v + 0.5);

    /// <summary>`String(v)` para los números que el JS volcaba directo al DOM
    /// (las etiquetas de eje de dirección y XTE, siempre enteros).</summary>
    public static string Texto(double v)
    {
        if (v == 0) return "0";                     // evita el "-0" de los negativos chicos
        return v.ToString("0.################", Inv);
    }

    /// <summary>`Number(x) || 0`: null y NaN caen en 0.</summary>
    public static double ANumero(double? v)
        => (v.HasValue && !double.IsNaN(v.Value)) ? v.Value : 0.0;
}
