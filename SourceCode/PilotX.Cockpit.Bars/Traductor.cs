// ============================================================================
// Traductor.cs — traducción al vuelo de la pantalla nativa (Avalonia).
//
// PilotX está escrito en castellano: los textos están literales en el XAML.
// En vez de reemplazarlos por claves (tocar 50 archivos y romper pantallas),
// se traducen recorriendo el árbol de controles contra un diccionario cuya
// clave ES el texto castellano:
//
//     "Cabecera" -> { en: "Headland", pt: "Cabeceira" }
//
// EL PUNTO FINO — se guarda el texto ORIGINAL la primera vez que se toca cada
// control. Sin eso, pasar de inglés a portugués buscaría "Headland" en un
// diccionario que solo conoce "Cabecera", y a partir del segundo cambio la
// pantalla quedaba mitad en un idioma y mitad en otro. Siempre se traduce
// DESDE el castellano, nunca de un idioma al otro.
//
// Los originales van en una ConditionalWeakTable: si un control se destruye
// (paneles que se crean y tiran), su entrada se va sola con él.
//
// Lo que NO está en el diccionario queda como está. Por eso los nombres de
// lote, los números y las unidades (km/h, ha, sem/m) pasan intactos: no hay
// entrada que los matchee.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace PilotX.Cockpit.Bars;

public static class Traductor
{
    /// <summary>Idioma vigente ("es" = sin traducir, la UI ya está en castellano).</summary>
    public static string Idioma { get; private set; } = "es";

    /// <summary>Se dispara cuando cambió el idioma y hay que repintar.</summary>
    public static event Action? IdiomaCambio;

    // texto castellano -> { "en": "...", "pt": "..." }
    private static Dictionary<string, Dictionary<string, string>>? _dic;

    private sealed class Originales
    {
        public string? Text;
        public object? Content;
        public object? Tip;
        public string? Watermark;
        public bool TieneText, TieneContent, TieneTip, TieneWatermark;
    }

    private static readonly ConditionalWeakTable<Control, Originales> _orig = new();

    // ---------------------------------------------------------------- carga

    /// <summary>
    /// Baja el diccionario del wwwroot que sirve el engine. Se llama una vez
    /// al arrancar; si el engine todavía no levantó, no pasa nada: queda en
    /// castellano y el próximo cambio de idioma lo vuelve a intentar.
    /// </summary>
    public static async Task CargarDiccionarioAsync(HttpClient http, string baseUrl)
    {
        if (_dic != null) return;
        try
        {
            string json = await http.GetStringAsync(baseUrl.TrimEnd('/') + "/idiomas.json")
                                    .ConfigureAwait(false);
            // El BOM que deja el Notepad hace explotar a System.Text.Json, y el
            // sintoma seria "toda la pantalla volvio a castellano" sin ninguna
            // pista. El archivo esta hecho para editarse a mano en la cabina.
            // (escapes explicitos: como literales serian invisibles en el fuente)
            json = json.TrimStart('\uFEFF', '\u200B').TrimStart();
            _dic = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
        }
        catch { /* sin diccionario se sigue en castellano; nunca voltea la pantalla */ }
    }

    public static bool HayDiccionario => _dic != null && _dic.Count > 0;

    // ------------------------------------------------------------ traducción

    /// <summary>Traduce un texto suelto. Devuelve el original si no hay entrada.</summary>
    public static string T(string? castellano)
    {
        if (string.IsNullOrWhiteSpace(castellano)) return castellano ?? "";
        if (Idioma == "es" || _dic == null) return castellano!;

        if (_dic.TryGetValue(castellano!.Trim(), out var trads)
            && trads.TryGetValue(Idioma, out var t)
            && !string.IsNullOrWhiteSpace(t))
        {
            // Se respetan los espacios de guarda del original (hay textos con
            // sangría en el XAML que se usan para alinear).
            string izq = castellano.Substring(0, castellano.Length - castellano.TrimStart().Length);
            string der = castellano.Substring(castellano.TrimEnd().Length);
            return izq + t + der;
        }
        return castellano!;
    }

    /// <summary>
    /// Cambia el idioma y retraduce. Devuelve true si efectivamente cambió.
    /// </summary>
    public static bool CambiarIdioma(string codigo)
    {
        string nuevo = (codigo ?? "es").Trim().ToLowerInvariant();
        if (nuevo != "es" && nuevo != "en" && nuevo != "pt") nuevo = "es";
        if (nuevo == Idioma) return false;
        Idioma = nuevo;
        IdiomaCambio?.Invoke();
        return true;
    }

    /// <summary>
    /// Traduce un control y todo lo que cuelga de él. Idempotente: se puede
    /// llamar cuantas veces se quiera, y hay que llamarla de nuevo sobre
    /// cualquier UI armada en runtime (paneles que se crean al vuelo).
    /// </summary>
    public static void Aplicar(Control? raiz)
    {
        if (raiz == null) return;
        try
        {
            Uno(raiz);
            // Árbol LÓGICO, no visual: los paneles con IsVisible=false (menús,
            // overlays) todavía no tienen árbol visual y se salteaban — se
            // abrían en castellano hasta que se cambiaba el idioma de nuevo.
            foreach (var l in raiz.GetLogicalDescendants())
                if (l is Control c) Uno(c);

            // Los ItemsControl con plantilla materializan por el visual.
            foreach (var v in raiz.GetVisualDescendants())
                if (v is Control c) Uno(c);
        }
        catch { /* traducir jamás puede voltear la pantalla principal */ }
    }

    private static void Uno(Control c)
    {
        var o = _orig.GetValue(c, _ => new Originales());

        switch (c)
        {
            case TextBlock tb:
                if (!o.TieneText) { o.Text = tb.Text; o.TieneText = true; }
                tb.Text = T(o.Text);
                break;

            case TextBox tx:
                if (!o.TieneWatermark) { o.Watermark = tx.Watermark; o.TieneWatermark = true; }
                tx.Watermark = T(o.Watermark);
                break;

            // Button, CheckBox, TabItem, Label… todo lo que tenga Content de
            // texto. Si el Content es otro control (íconos, StackPanels), no
            // se toca: sus hijos ya los agarra el recorrido.
            case ContentControl cc:
                if (!o.TieneContent) { o.Content = cc.Content; o.TieneContent = true; }
                if (o.Content is string s) cc.Content = T(s);
                break;
        }

        // Tooltips: son de todos los controles, no de un tipo en particular.
        var tip = ToolTip.GetTip(c);
        if (!o.TieneTip) { o.Tip = tip; o.TieneTip = true; }
        if (o.Tip is string ts) ToolTip.SetTip(c, T(ts));
    }
}
