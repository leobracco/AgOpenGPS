# Luces de banderillero — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Barra de 15 luces sobre el mapa que, con un tilde, muestra los centímetros de desvío y hacia qué lado corregir, para manejar a mano siguiendo la guía.

**Architecture:** La lógica pura (cm → cuántas luces, de qué lado, de qué color) vive en `PilotX.Cockpit.Bars`, que es el único proyecto que `PilotX.UI` referencia y tiene su propio proyecto de tests. El control visual es Avalonia y se cuelga como hermano del cluster "A LA LÍNEA" que ya existe. El tilde y los cm por luz viajan en `/api/aog/state`, que la UI ya poleá. El lightbar OpenGL, que es código muerto verificado, se borra.

**Tech Stack:** C# / .NET 9, Avalonia, NUnit 4.3.2.

## Global Constraints

- Castellano rioplatense en UI, logs, comentarios nuevos y mensajes de commit. Código y términos técnicos en inglés.
- En UI y logs se dice **PilotX / OrbitX / Agro Parallel**, nunca AgOpenGPS/AOG/AgIO.
- Tests: `dotnet test "SourceCode/PilotX.Cockpit.Bars.Tests/PilotX.Cockpit.Bars.Tests.csproj"` desde la raíz del repo. Framework **NUnit**.
- Estado conocido de la batería de `AgroParallel.Services.Tests`: **343 pasan, 1 falla** (`SoporteRemotoTests`, preexistente y ajena). No arreglarla.
- Colores exactos: verde `#4ABA3E`, amarillo `#E8C81E`, naranja `#F07E12`, rojo `#ED4848`.
- Cortes de color **en centímetros**, no en número de luces: 5 / 15 / 25.
- 7 luces por lado + 1 central. Default 5 cm por luz.
- El tilde manda solo: **no** se mira `IsAutoSteerOn` en ningún lado.
- Es código de una máquina que siembra: un error cuesta plata en el lote.

---

### Task 1: EscalaDesvio — la lógica pura

**Files:**
- Create: `SourceCode/PilotX.Cockpit.Bars/EscalaDesvio.cs`
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/EscalaDesvioTests.cs`

**Interfaces:**
- Consumes: nada (primera tarea).
- Produces:
  - `enum NivelDesvio { Verde, Amarillo, Naranja, Rojo }`
  - `readonly struct LecturaDesvio` con `int LucesEncendidas`, `bool HaciaLaIzquierda`, `NivelDesvio Nivel`, `double Centimetros`, `bool HayDato`
  - `static class EscalaDesvio` con `const int LucesPorLado = 7`, `const double CmPorLuzPorDefecto = 5.0`, `static LecturaDesvio Leer(double xteMetros, double cmPorLuz)`, `static string ColorHex(NivelDesvio nivel)`

- [ ] **Step 1: Escribir los tests que fallan**

Crear `EscalaDesvioTests.cs`:

```csharp
// ============================================================================
// EscalaDesvioTests.cs — la escala de las luces de banderillero.
//
// Dos reglas que se rompen fácil y por eso están cubiertas una por una:
//  · el color se decide en CENTÍMETROS (5/15/25), no en cantidad de luces —
//    si el operario cambia los cm por luz, "naranja" tiene que seguir
//    significando "te fuiste 15-25 cm";
//  · las luces se prenden del lado al que hay que IR, que es el contrario al
//    lado al que te fuiste.
// ============================================================================

using NUnit.Framework;
using PilotX.Cockpit.Bars;

namespace PilotX.Cockpit.Bars.Tests
{
    public class EscalaDesvioTests
    {
        private const double Cm5 = 5.0;

        // --- cantidad de luces (default 5 cm por luz) ---

        [TestCase(0.00, 0)]
        [TestCase(0.04, 0)]
        [TestCase(0.05, 1)]
        [TestCase(0.09, 1)]
        [TestCase(0.10, 2)]
        [TestCase(0.20, 4)]
        [TestCase(0.34, 6)]
        [TestCase(0.35, 7)]
        public void CantidadDeLuces(double xte, int esperadas)
        {
            Assert.That(EscalaDesvio.Leer(xte, Cm5).LucesEncendidas, Is.EqualTo(esperadas));
        }

        [Test]
        public void MasAlladeLaEscala_TopeEnSiete()
        {
            Assert.That(EscalaDesvio.Leer(3.0, Cm5).LucesEncendidas, Is.EqualTo(7));
        }

        // --- color, por centímetros ---

        [TestCase(0.00, NivelDesvio.Verde)]
        [TestCase(0.049, NivelDesvio.Verde)]
        [TestCase(0.05, NivelDesvio.Amarillo)]
        [TestCase(0.149, NivelDesvio.Amarillo)]
        [TestCase(0.15, NivelDesvio.Naranja)]
        [TestCase(0.249, NivelDesvio.Naranja)]
        [TestCase(0.25, NivelDesvio.Rojo)]
        [TestCase(1.00, NivelDesvio.Rojo)]
        public void NivelPorCentimetros(double xte, NivelDesvio esperado)
        {
            Assert.That(EscalaDesvio.Leer(xte, Cm5).Nivel, Is.EqualTo(esperado));
        }

        // El color NO puede depender de la escala: con 10 cm por luz, 20 cm
        // son 2 luces en vez de 4, pero siguen siendo naranja.
        [Test]
        public void ElColorNoCambiaAlCambiarLosCmPorLuz()
        {
            var conCinco = EscalaDesvio.Leer(0.20, 5.0);
            var conDiez  = EscalaDesvio.Leer(0.20, 10.0);

            Assert.That(conCinco.LucesEncendidas, Is.EqualTo(4));
            Assert.That(conDiez.LucesEncendidas,  Is.EqualTo(2));
            Assert.That(conDiez.Nivel, Is.EqualTo(NivelDesvio.Naranja));
            Assert.That(conDiez.Nivel, Is.EqualTo(conCinco.Nivel));
        }

        // --- lado ---

        // Desviado a la DERECHA de la línea (xte > 0) => corregir a la izquierda.
        // Mismo criterio que la flecha del cluster (MainWindow.axaml.cs).
        [Test]
        public void DesviadoALaDerecha_PrendeLasDeLaIzquierda()
        {
            Assert.That(EscalaDesvio.Leer(0.20, Cm5).HaciaLaIzquierda, Is.True);
        }

        [Test]
        public void DesviadoALaIzquierda_PrendeLasDeLaDerecha()
        {
            Assert.That(EscalaDesvio.Leer(-0.20, Cm5).HaciaLaIzquierda, Is.False);
        }

        // --- centímetros informados ---

        [Test]
        public void CentimetrosSiempreEnPositivo()
        {
            Assert.That(EscalaDesvio.Leer(-0.23, Cm5).Centimetros, Is.EqualTo(23.0).Within(0.001));
        }

        // --- defensa ---

        [Test]
        public void SinDato_NoExplotaYNoPrendeNada()
        {
            var r = EscalaDesvio.Leer(double.NaN, Cm5);

            Assert.That(r.HayDato, Is.False);
            Assert.That(r.LucesEncendidas, Is.EqualTo(0));
            Assert.That(r.Nivel, Is.EqualTo(NivelDesvio.Verde));
        }

        [Test]
        public void ConDato_HayDatoEsVerdadero()
        {
            Assert.That(EscalaDesvio.Leer(0.10, Cm5).HayDato, Is.True);
        }

        // Un cm-por-luz inválido guardado en Settings no puede dividir por cero
        // ni dejar la barra muerta: cae al default.
        [TestCase(0.0)]
        [TestCase(-3.0)]
        public void CmPorLuzInvalido_CaeAlDefault(double cmPorLuz)
        {
            var r = EscalaDesvio.Leer(0.20, cmPorLuz);
            Assert.That(r.LucesEncendidas, Is.EqualTo(4));
        }

        // --- colores ---

        [Test]
        public void CadaNivelTieneSuColor()
        {
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Verde),    Is.EqualTo("#4ABA3E"));
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Amarillo), Is.EqualTo("#E8C81E"));
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Naranja),  Is.EqualTo("#F07E12"));
            Assert.That(EscalaDesvio.ColorHex(NivelDesvio.Rojo),     Is.EqualTo("#ED4848"));
        }
    }
}
```

- [ ] **Step 2: Correr los tests para verificar que fallan**

Run: `dotnet test "SourceCode/PilotX.Cockpit.Bars.Tests/PilotX.Cockpit.Bars.Tests.csproj" --filter "FullyQualifiedName~EscalaDesvio"`

Expected: FAIL de compilación — `The type or namespace name 'EscalaDesvio' could not be found`.

- [ ] **Step 3: Implementar `EscalaDesvio`**

Crear `EscalaDesvio.cs`:

```csharp
// ============================================================================
// EscalaDesvio.cs — cuántas luces se prenden, de qué lado y de qué color, para
// un desvío dado. Es la escala de las luces de banderillero.
//
// Vive acá y no en PilotX.UI porque PilotX.UI no tiene proyecto de tests y este
// proyecto sí: la regla del color y la del lado son fáciles de romper sin que se
// note en pantalla.
//
// Dos decisiones que NO son obvias leyendo el código:
//  · El color se decide en CENTÍMETROS (5/15/25) y no en cantidad de luces. Si
//    el operario cambia los cm por luz, "naranja" tiene que seguir queriendo
//    decir "te fuiste entre 15 y 25 cm", no "prendiste la tercera luz".
//  · Las luces se prenden del lado al que hay que IR, no del lado al que te
//    fuiste. Es el mismo criterio de la flecha del cluster y el de las barras
//    de banderillero de toda la vida.
// ============================================================================

using System;

namespace PilotX.Cockpit.Bars
{
    public enum NivelDesvio { Verde, Amarillo, Naranja, Rojo }

    public readonly struct LecturaDesvio
    {
        public int LucesEncendidas { get; }
        public bool HaciaLaIzquierda { get; }
        public NivelDesvio Nivel { get; }
        public double Centimetros { get; }
        /// <summary>false cuando no hay guía (XTE NaN): no se dibuja nada.</summary>
        public bool HayDato { get; }

        public LecturaDesvio(int luces, bool haciaLaIzquierda, NivelDesvio nivel,
                             double centimetros, bool hayDato)
        {
            LucesEncendidas = luces;
            HaciaLaIzquierda = haciaLaIzquierda;
            Nivel = nivel;
            Centimetros = centimetros;
            HayDato = hayDato;
        }
    }

    public static class EscalaDesvio
    {
        public const int LucesPorLado = 7;
        public const double CmPorLuzPorDefecto = 5.0;

        // Cortes en centímetros. Ver la nota de arriba: son cm, no luces.
        private const double CmAmarillo = 5.0;
        private const double CmNaranja  = 15.0;
        private const double CmRojo     = 25.0;

        public static LecturaDesvio Leer(double xteMetros, double cmPorLuz)
        {
            if (double.IsNaN(xteMetros) || double.IsInfinity(xteMetros))
                return new LecturaDesvio(0, false, NivelDesvio.Verde, 0.0, false);

            // Un valor inválido guardado en Settings no puede dividir por cero
            // ni dejar la barra muerta.
            if (cmPorLuz <= 0.0 || double.IsNaN(cmPorLuz)) cmPorLuz = CmPorLuzPorDefecto;

            double cm = Math.Abs(xteMetros) * 100.0;

            int luces = (int)Math.Floor(cm / cmPorLuz);
            if (luces > LucesPorLado) luces = LucesPorLado;

            NivelDesvio nivel = cm < CmAmarillo ? NivelDesvio.Verde
                              : cm < CmNaranja  ? NivelDesvio.Amarillo
                              : cm < CmRojo     ? NivelDesvio.Naranja
                                                : NivelDesvio.Rojo;

            // xte > 0 = el tractor está a la derecha de la línea => ir a la izquierda.
            bool haciaLaIzquierda = xteMetros > 0;

            return new LecturaDesvio(luces, haciaLaIzquierda, nivel, cm, true);
        }

        public static string ColorHex(NivelDesvio nivel)
        {
            switch (nivel)
            {
                case NivelDesvio.Verde:    return "#4ABA3E";
                case NivelDesvio.Amarillo: return "#E8C81E";
                case NivelDesvio.Naranja:  return "#F07E12";
                default:                   return "#ED4848";
            }
        }
    }
}
```

- [ ] **Step 4: Correr los tests para verificar que pasan**

Run: `dotnet test "SourceCode/PilotX.Cockpit.Bars.Tests/PilotX.Cockpit.Bars.Tests.csproj" --filter "FullyQualifiedName~EscalaDesvio"`

Expected: PASS, 26 casos (los `[TestCase]` cuentan de a uno). Salida sin warnings.

- [ ] **Step 5: Correr la batería completa del proyecto**

Run: `dotnet test "SourceCode/PilotX.Cockpit.Bars.Tests/PilotX.Cockpit.Bars.Tests.csproj"`

Expected: PASS. Ningún test existente se rompe.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars/EscalaDesvio.cs SourceCode/PilotX.Cockpit.Bars.Tests/EscalaDesvioTests.cs
git commit -m "feat(cabina): escala de las luces de banderillero

Cuantas luces se prenden, de que lado y de que color para un desvio dado.

El color se decide en CENTIMETROS (5/15/25) y no en cantidad de luces: si el
operario cambia los cm por luz, naranja tiene que seguir queriendo decir 'te
fuiste 15-25 cm'. Y las luces se prenden del lado al que hay que IR, no del lado
al que te fuiste.

En Cockpit.Bars y no en PilotX.UI porque este proyecto tiene tests y aquel no.

Prueba: Lightbar"
```

---

### Task 2: El tilde y los cm por luz viajan en el estado

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Models/AogStateSnapshot.cs` (junto a `CrossTrackErrorM`, línea ~367)
- Modify: `SourceCode/PilotX.GuidanceEngine/Adapters/EngineStateProvider.cs` (junto a `snap.CrossTrackErrorM`, línea ~154)
- Modify: `SourceCode/PilotX.UI/Services/HudPoller.cs` (clase `HudSnapshot`, junto a `CrossTrackErrorM`)

**Interfaces:**
- Consumes: nada de Task 1.
- Produces: en `HudSnapshot`, `bool MostrarLuces { get; set; }` y `double LucesCmPorLuz { get; set; }`. El JSON viaja como `mostrar_luces` y `luces_cm_por_luz` (la política `SnakeCaseLower` lo mapea sola, igual que el resto).

- [ ] **Step 1: Agregar las propiedades al snapshot del servidor**

En `AogStateSnapshot.cs`, inmediatamente después de la propiedad `CrossTrackErrorM`:

```csharp
        /// <summary>Tilde "Mostrar barra en pantalla" (setMenu_isLightbarOn).
        /// Prende las luces de banderillero. No depende del piloto.</summary>
        public bool MostrarLuces { get; set; }

        /// <summary>Centímetros que representa cada luz
        /// (setDisplay_lightbarCmPerPixel). 0 o negativo = usar el default.</summary>
        public double LucesCmPorLuz { get; set; }
```

- [ ] **Step 2: Llenarlas desde Settings en el motor**

En `EngineStateProvider.cs`, inmediatamente después de la línea
`snap.CrossTrackErrorM = _host.crossTrackError / 1000.0;`:

```csharp
                // Las luces de banderillero se prenden con el tilde y nada más:
                // NO se mira isBtnAutoSteerOn. El operario que las quiere con el
                // piloto puesto las tiene.
                snap.MostrarLuces  = global::AgOpenGPS.Properties.Settings.Default.setMenu_isLightbarOn;
                snap.LucesCmPorLuz = global::AgOpenGPS.Properties.Settings.Default.setDisplay_lightbarCmPerPixel;
```

- [ ] **Step 3: Agregar las propiedades al snapshot del cliente**

En `HudPoller.cs`, dentro de `HudSnapshot`, inmediatamente después de
`public double CrossTrackErrorM { get; set; }`:

```csharp
    // ---- Luces de banderillero -----------------------------------------
    // Las prende el tilde de Configuración › Dirección › Barra guía. Llegan por
    // el MISMO /api/aog/state que el resto; SnakeCaseLower las mapea desde
    // mostrar_luces / luces_cm_por_luz.
    public bool MostrarLuces { get; set; }
    public double LucesCmPorLuz { get; set; }
```

- [ ] **Step 4: Compilar los tres proyectos**

Run: `dotnet build "SourceCode/PilotX.GuidanceEngine/PilotX.GuidanceEngine.csproj" -c Debug`
Expected: Build succeeded, 0 errores.

Run: `dotnet build "SourceCode/PilotX.UI/PilotX.UI.csproj" -c Debug`
Expected: Build succeeded, 0 errores.

- [ ] **Step 5: Verificar que el estado sale por el endpoint**

El campo tiene que aparecer en el JSON. Con el engine corriendo:

Run: `curl -s http://127.0.0.1:5180/api/aog/state | grep -o "mostrar_luces[^,]*"`
Expected: `mostrar_luces":true` o `:false`.

Si el engine no está levantado en el entorno, decilo en el reporte y dejá este
paso sin marcar — no lo declares hecho.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Models/AogStateSnapshot.cs SourceCode/PilotX.GuidanceEngine/Adapters/EngineStateProvider.cs SourceCode/PilotX.UI/Services/HudPoller.cs
git commit -m "feat(cabina): el tilde de las luces viaja en /api/aog/state

setMenu_isLightbarOn y setDisplay_lightbarCmPerPixel existian en Settings y
tenian controles en la pantalla de Direccion, pero NADIE los consumia. Ahora
llegan a la UI por el mismo estado que el resto del HUD.

El tilde manda solo: no se mira isBtnAutoSteerOn.

Prueba: Lightbar"
```

---

### Task 3: El control LucesBanderillero

**Files:**
- Create: `SourceCode/PilotX.UI/Views/LucesBanderillero.cs`

**Interfaces:**
- Consumes: `EscalaDesvio.Leer(double, double)`, `LecturaDesvio`, `NivelDesvio`, `EscalaDesvio.ColorHex(NivelDesvio)`, `EscalaDesvio.LucesPorLado` (Task 1).
- Produces: `public sealed class LucesBanderillero : Border` con `public void Actualizar(double xteMetros, double cmPorLuz)`. Task 4 la instancia y la llama.

La carpeta `PilotX.UI/Views/` usa **C# puro** para los paneles (`LotePanel.cs`,
`CabeceraPanel.cs`), no `.axaml`. Este control sigue esa convención.

- [ ] **Step 1: Crear el control**

Crear `LucesBanderillero.cs`:

```csharp
// ============================================================================
// LucesBanderillero.cs — la barra de luces para manejar a mano siguiendo la
// guía, como las barras de banderillero.
//
// 15 luces: 7 a cada lado y una central. Se prenden del centro hacia el lado al
// que hay que IR. La CANTIDAD prendida dice cuánto te fuiste sin que haga falta
// leer el número, que es lo que permite manejarla de reojo — por eso la escala
// de 4 colores es segura acá y no lo sería en un número suelto: el color es
// refuerzo, no el único canal.
//
// La luz central es aparte y no cuenta como luz de desvío: verde cuando estás
// en la línea, apagada en cuanto se prende la primera lateral, para que el ojo
// siga al grupo que se mueve y no al centro.
//
// Toda la aritmética está en EscalaDesvio (Cockpit.Bars, con tests). Acá sólo
// se pinta.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PilotX.Cockpit.Bars;

// OJO con el namespace: este proyecto tiene AssemblyName = PilotX.UI pero
// RootNamespace = PilotX.Desktop (está documentado en el .csproj; el rename
// quedó diferido). Los 65 archivos de Views/ usan PilotX.Desktop.Views.
namespace PilotX.Desktop.Views;

public sealed class LucesBanderillero : Border
{
    private const int AnchoLuz = 26;
    private const int AltoLuz = 30;
    private const int AnchoCentral = 10;

    private static readonly IBrush Apagada = new SolidColorBrush(Color.Parse("#2A3329"));
    private static readonly IBrush BordeLuz = new SolidColorBrush(Color.Parse("#66FFFFFF"));

    // Índice 0 = la más lejana al centro. Se prenden de adentro hacia afuera.
    private readonly Border[] _izquierda = new Border[EscalaDesvio.LucesPorLado];
    private readonly Border[] _derecha = new Border[EscalaDesvio.LucesPorLado];
    private readonly Border _central;
    private readonly TextBlock _flecha;
    private readonly TextBlock _numero;
    private readonly TextBlock _unidad;

    public LucesBanderillero()
    {
        // Mismo fondo oscuro translúcido que el cluster del piloto: sobre el
        // piso texturado un panel claro lava los colores de las luces.
        Background = new SolidColorBrush(Color.Parse("#D9101612"));
        BorderBrush = new SolidColorBrush(Color.Parse("#66FFFFFF"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12, 8);
        IsVisible = false;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Center;

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // CONVENCIÓN DE ÍNDICES, en los dos arreglos: el índice 0 es la luz
        // MÁS CERCANA AL CENTRO. Así `i < LucesEncendidas` se lee igual para
        // los dos lados en Actualizar(). El orden de AGREGADO al panel es otra
        // cosa: de izquierda a derecha en pantalla.

        // Izquierda: en pantalla va primero la más lejana (índice 6) y última
        // la pegada al centro (índice 0).
        for (int i = EscalaDesvio.LucesPorLado - 1; i >= 0; i--)
        {
            _izquierda[i] = NuevaLuz(AnchoLuz);
            fila.Children.Add(_izquierda[i]);
        }

        _central = NuevaLuz(AnchoCentral);
        fila.Children.Add(_central);

        // Derecha: al revés, primero la pegada al centro (índice 0).
        for (int i = 0; i < EscalaDesvio.LucesPorLado; i++)
        {
            _derecha[i] = NuevaLuz(AnchoLuz);
            fila.Children.Add(_derecha[i]);
        }

        _flecha = new TextBlock
        {
            FontSize = 26,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F5F7F4")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _numero = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F5F7F4")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _unidad = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#C5CFC5")),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var pie = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        pie.Children.Add(_flecha);
        pie.Children.Add(_numero);
        pie.Children.Add(_unidad);

        var raiz = new StackPanel { Orientation = Orientation.Vertical };
        raiz.Children.Add(fila);
        raiz.Children.Add(pie);
        Child = raiz;
    }

    private static Border NuevaLuz(int ancho) => new Border
    {
        Width = ancho,
        Height = AltoLuz,
        CornerRadius = new CornerRadius(3),
        Background = Apagada,
        BorderBrush = BordeLuz,
        BorderThickness = new Thickness(1),
    };

    /// <summary>Repinta con el desvío actual. xteMetros NaN = sin guía: el
    /// control se esconde solo.</summary>
    public void Actualizar(double xteMetros, double cmPorLuz)
    {
        var l = EscalaDesvio.Leer(xteMetros, cmPorLuz);
        if (!l.HayDato) { IsVisible = false; return; }
        IsVisible = true;

        var encendida = new SolidColorBrush(Color.Parse(EscalaDesvio.ColorHex(l.Nivel)));

        // La central sólo vive cuando no hay ninguna lateral prendida.
        _central.Background = l.LucesEncendidas == 0 ? encendida : Apagada;

        for (int i = 0; i < EscalaDesvio.LucesPorLado; i++)
        {
            // El índice 0 es la pegada al centro en los dos arreglos, así que
            // `i < encendidas` vale igual para los dos lados.
            bool prende = i < l.LucesEncendidas;
            _izquierda[i].Background = (prende &&  l.HaciaLaIzquierda) ? encendida : Apagada;
            _derecha[i].Background   = (prende && !l.HaciaLaIzquierda) ? encendida : Apagada;
        }

        _flecha.Text = l.LucesEncendidas == 0 ? "" : (l.HaciaLaIzquierda ? "◀" : "▶");
        _numero.Foreground = encendida;

        // Mismo formato que el cluster: cm enteros bajo el metro, metros con un
        // decimal de ahí en adelante. No se inventa un formato nuevo.
        if (l.Centimetros < 100)
        {
            _numero.Text = l.Centimetros.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            _unidad.Text = "cm";
        }
        else
        {
            _numero.Text = (l.Centimetros / 100.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            _unidad.Text = "m";
        }
    }
}
```

- [ ] **Step 2: Compilar**

Run: `dotnet build "SourceCode/PilotX.UI/PilotX.UI.csproj" -c Debug`
Expected: Build succeeded, 0 errores.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/PilotX.UI/Views/LucesBanderillero.cs
git commit -m "feat(cabina): control de las luces de banderillero

15 luces (7 por lado + central) que se prenden del centro hacia el lado al que
hay que ir, con la flecha y los cm abajo. La central es aparte: verde cuando
estas en la linea, apagada apenas se prende la primera lateral.

Toda la aritmetica esta en EscalaDesvio; aca solo se pinta.

Prueba: Lightbar"
```

---

### Task 4: Cablearlo al mapa y alinear el cluster

**Files:**
- Modify: `SourceCode/PilotX.UI/MainWindow.axaml` (junto al `<Border Name="PilotoCluster"` de la línea ~557)
- Modify: `SourceCode/PilotX.UI/MainWindow.axaml.cs` (campos ~98, `FindControl` ~493, `ActualizarClusterPiloto` ~6792-6945)

**Interfaces:**
- Consumes: `LucesBanderillero.Actualizar(double, double)` (Task 3); `HudSnapshot.MostrarLuces` y `HudSnapshot.LucesCmPorLuz` (Task 2); `EscalaDesvio.Leer` y `EscalaDesvio.ColorHex` (Task 1).
- Produces: nada.

- [ ] **Step 1: Declarar el control en el XAML**

En `MainWindow.axaml`, **inmediatamente antes** del `<Border Name="PilotoCluster"`,
agregar el host del control (se instancia desde code-behind para no depender de
un namespace XAML nuevo):

```xml
            <!-- Luces de banderillero: van en el MISMO lugar que el cluster
                 (arriba-centro, debajo de la barra superior de 52 px). Los dos
                 nunca se ven juntos: manda el tilde. -->
            <ContentControl Name="LucesHost" Grid.Row="2"
                            VerticalAlignment="Top" HorizontalAlignment="Center"
                            Margin="0,64,0,0"/>
```

- [ ] **Step 2: Declarar los campos y resolverlos**

En `MainWindow.axaml.cs`, junto a `private Border? _pilotoCluster;` (línea ~98):

```csharp
    private ContentControl? _lucesHost;
    private PilotX.Desktop.Views.LucesBanderillero? _luces;
```

Y junto a `_pilotoCluster = this.FindControl<Border>("PilotoCluster");` (línea ~493):

```csharp
        _lucesHost = this.FindControl<ContentControl>("LucesHost");
        if (_lucesHost != null)
        {
            _luces = new PilotX.Desktop.Views.LucesBanderillero();
            _lucesHost.Content = _luces;
        }
```

- [ ] **Step 3: Elegir qué se muestra en `ActualizarClusterPiloto`**

Reemplazar el bloque que hoy decide la visibilidad (desde `bool hayGuia =` hasta
la línea `_mapHost?.SetLightbarVisible(!visible);` inclusive) por:

```csharp
        // Hay guía = el poller de guidance trae XTE (NaN sin guía activa).
        bool hayGuia = !double.IsNaN(_lastXteMeters);

        // El tilde manda: con las luces puestas se muestran ELLAS y el cluster
        // se esconde. Los dos dibujan el mismo dato y tenerlos juntos sólo
        // genera dudas de dónde mirar. NO se mira el estado del piloto: si el
        // operario quiere las luces con el piloto puesto, las tiene.
        bool luces = s.MostrarLuces && hayGuia;
        bool cluster = hayGuia && !luces;

        _pilotoCluster.IsVisible = cluster;
        if (_luces != null)
        {
            if (luces) _luces.Actualizar(_lastXteMeters, s.LucesCmPorLuz);
            else _luces.IsVisible = false;
        }

        if (!cluster)
        {
            if (_pcGiroSentido != null) _pcGiroSentido.IsVisible = false;
            return;
        }
```

Nota: el `return` temprano conserva el comportamiento de hoy (cuando el cluster
no se muestra, el resto de la función no corre). El `SetLightbarVisible` se
elimina acá; la Task 5 borra el método.

- [ ] **Step 4: Alinear el color del cluster a los cuatro cortes**

En la misma función, reemplazar la línea que arma el brush del número:

```csharp
            // Mismos umbrales que el lightbar: verde centrado, amarillo, rojo.
            var brush = cm < 5 ? "#4ABA3E" : (cm < 20 ? "#D9A916" : "#ED4848");
```

por:

```csharp
            // Los MISMOS cuatro cortes que las luces (EscalaDesvio). Si no, el
            // mismo desvío cambiaría de color al tildar o destildar las luces.
            var brush = PilotX.Cockpit.Bars.EscalaDesvio.ColorHex(
                PilotX.Cockpit.Bars.EscalaDesvio.Leer(xte, s.LucesCmPorLuz).Nivel);
```

- [ ] **Step 5: Compilar**

Run: `dotnet build "SourceCode/PilotX.UI/PilotX.UI.csproj" -c Debug`
Expected: Build succeeded, 0 errores.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.UI/MainWindow.axaml SourceCode/PilotX.UI/MainWindow.axaml.cs
git commit -m "feat(cabina): las luces de banderillero en el mapa

Con el tilde puesto se muestran las luces y el cluster 'A LA LINEA' se esconde:
dibujan el mismo dato y tenerlos juntos solo genera dudas de donde mirar. El
tilde manda solo, no se mira el estado del piloto.

El color del cluster pasa a salir de EscalaDesvio, los mismos cuatro cortes que
las luces: antes tenia tres (verde<5 amarillo<20 rojo) y el mismo desvio habria
cambiado de color al tildar y destildar.

Prueba: Lightbar"
```

---

### Task 5: Borrar el lightbar GL muerto

**Files:**
- Modify: `SourceCode/PilotX.UI/Views/MapGlSurface.cs` (método `DrawLightbar` ~1803-1860, su llamada ~1333, `SetLightbarVisible` y `_lightbarOn` ~257-258)
- Modify: `SourceCode/PilotX.UI/Views/MapPanel.cs` (`SetLightbarVisible` ~389-394, `_lightbarOn` ~394, la propagación ~326)

**Interfaces:**
- Consumes: nada.
- Produces: nada. `SetLightbarVisible` deja de existir en las dos clases; su
  único caller ya se eliminó en la Task 4.

**Por qué es código muerto, verificado:** `DrawLightbar` corta si `_xte` es `NaN`
o si `!_lightbarOn`. El único que apagaba el flag lo hacía con `!hayGuia`, así
que con guía el flag quedaba en `false` y sin guía el XTE era `NaN`. Las dos
ramas terminaban sin dibujar. Nunca se vio en pantalla.

- [ ] **Step 1: Confirmar que no quedó ningún caller**

Run: `grep -rn "SetLightbarVisible\|DrawLightbar\|_lightbarOn" --include=*.cs SourceCode/ | grep -v "/bin/\|/obj/"`

Expected: sólo las definiciones en `MapGlSurface.cs` y `MapPanel.cs`. Si aparece
algún caller fuera de esos dos archivos, **pará y reportá**: la Task 4 no lo
eliminó y hay que revisarlo antes de borrar.

- [ ] **Step 2: Borrar de `MapGlSurface.cs`**

Eliminar:
1. el campo `private volatile bool _lightbarOn = true;` y el método
   `public void SetLightbarVisible(bool visible) => _lightbarOn = visible;`
2. la llamada `DrawLightbar();` dentro del render
3. el método `DrawLightbar()` completo, con su bloque de comentario

- [ ] **Step 3: Borrar de `MapPanel.cs`**

Eliminar el método `SetLightbarVisible`, el campo `private bool _lightbarOn = true;`
y la línea `nueva.SetLightbarVisible(_lightbarOn);` de la propagación.

- [ ] **Step 4: Compilar**

Run: `dotnet build "SourceCode/PilotX.UI/PilotX.UI.csproj" -c Debug`
Expected: Build succeeded, 0 errores, 0 advertencias nuevas.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/PilotX.UI/Views/MapGlSurface.cs SourceCode/PilotX.UI/Views/MapPanel.cs
git commit -m "refactor(mapa): borrar el lightbar GL, que nunca se dibujaba

DrawLightbar cortaba si el XTE era NaN o si el flag estaba apagado. El unico que
tocaba el flag lo hacia con !hayGuia: CON guia el flag quedaba en false y SIN
guia el XTE era NaN. Las dos ramas terminaban sin dibujar, asi que esa barra no
se vio nunca en pantalla.

Lo reemplazan las luces de banderillero, que son Avalonia (en GL el numero de cm
sale caro y feo).

Prueba: Lightbar"
```

---

### Task 6: Manual de cabina

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

**Interfaces:**
- Consumes: el comportamiento de las tareas 1 a 4.
- Produces: nada.

Lo dispara la regla del `CLAUDE.md` del repo: aparece algo nuevo que el operario
ve, y un tilde que hasta hoy no hacía nada pasa a hacer algo.

- [ ] **Step 1: Confirmar la ruta real en la UI, no de memoria**

Run: `grep -n "displayLightbar\|Mostrar barra en pantalla\|Barra guía" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/direccion.html`

Anotar el rótulo **exacto** del tilde y de la pestaña. La ruta del manual se
escribe con esos textos.

- [ ] **Step 2: Confirmar dónde va la sección**

Run: `grep -n "<h2>Piloto automático</h2>\|<section id=\"piloto\">" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

La sección nueva va **después** de `</section>` de Piloto automático, porque es
lo que se usa cuando NO se usa el piloto y así quedan juntos los dos modos de
seguir la guía.

- [ ] **Step 3: Escribir la sección**

Insertar, con el formato de las secciones vecinas (`<p class="ruta">` para la
ruta, `<div class="clave">` para lo que hay que recordar):

```html
        <section id="luces">
          <h2>Luces de banderillero</h2>
          <p class="ruta"><b>Menú izquierdo</b> <i>›</i> <b>Configuración</b> <i>›</i> <b>Dirección</b> <i>›</i> <b>Barra guía</b> <i>›</i> <b>Mostrar barra en pantalla</b></p>
          <p>Para manejar <strong>a mano</strong> siguiendo la guía, como con el banderillero de antes. Con el tilde puesto, arriba del mapa aparece una barra de luces que te dice <strong>para qué lado ir</strong> y <strong>cuánto te fuiste</strong>.</p>
          <ul>
            <li>Las luces se prenden <strong>del lado al que tenés que ir</strong>, no del lado al que te fuiste.</li>
            <li><strong>Cuántas</strong> luces prendidas: cuánto te corriste. Con todas apagadas y la del medio en verde, estás en la línea.</li>
            <li>Abajo, la flecha y los centímetros exactos.</li>
          </ul>
          <p>Los colores van de menos a más: <strong>verde</strong> (estás en la línea), <strong>amarillo</strong>, <strong>naranja</strong> y <strong>rojo</strong>.</p>
          <div class="clave">Las luces <strong>reemplazan</strong> al recuadro de <strong>A LA LÍNEA</strong> mientras están puestas: muestran el mismo dato y tenerlos juntos confunde. Destildá y vuelve el recuadro de siempre.</div>
          <div class="aviso">Andan con el piloto puesto o sin él: el tilde manda. Si no hay ninguna guía elegida no aparece nada, porque no hay línea contra la cual medir.</div>
        </section>
```

- [ ] **Step 4: Agregarla al índice**

Run: `grep -n "href=\"#piloto\"" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

Agregar, inmediatamente después de esa línea, con la misma indentación:

```html
        <a href="#luces">Luces de banderillero</a>
```

- [ ] **Step 5: Verificar que el manual no quedó mintiendo en otro lado**

Run: `grep -n -i "lightbar\|barra de guía\|barra guía" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

Revisar cada resultado: si alguno describe la barra vieja de otra forma, o
promete algo que el tilde no hacía, corregirlo en este mismo commit y decirlo en
el reporte.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html
git commit -m "docs(ayuda): luces de banderillero"
```

---

## Verificación final (en banco, antes de declarar nada)

Nada de esto se marca `Cierra:` en el tablero hasta verlo en cabina o banco:

1. Tildar "Mostrar barra en pantalla" con una guía activa → aparecen las luces y
   desaparece el recuadro "A LA LÍNEA".
2. Destildar → vuelve el recuadro y desaparecen las luces.
3. Cruzar la línea de un lado al otro → las luces cambian de lado y la flecha
   también, y la del medio queda verde al pasar por el cero.
4. Alejarse despacio → el color pasa verde → amarillo → naranja → rojo, y el
   cambio cae justo cuando se prende una luz más.
5. Con el piloto **puesto** y el tilde puesto → las luces se siguen viendo
   (el tilde manda).
6. Cambiar los cm por luz en Dirección → cambia cuántas luces se prenden para el
   mismo desvío, pero **no** el color.
7. Cerrar el lote / quedarse sin guía → no queda nada dibujado arriba del mapa.
