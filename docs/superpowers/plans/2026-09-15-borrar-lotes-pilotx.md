# Borrar lotes en PilotX — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el operario pueda borrar un lote que ya no usa desde la pantalla de Lote de la cabina, y que el borrado le llegue a OrbitX aunque en el momento no haya señal.

**Architecture:** El borrado local ya existe (`POST /api/lotes/delete`). Este plan lo conecta a `LotePanel.cs` —la pantalla nativa que el operario abre de verdad— con el patrón de doble toque que el repo ya usa para borrar guías y contornos. El aviso al cloud se encola en disco y `OrbitXSync` lo drena cuando hay señal; hasta que el cloud confirme, un tombstone local impide que el sync reponga el lote borrado.

**Tech Stack:** C# / .NET 9, Avalonia, NUnit.

## Global Constraints

- Castellano rioplatense en UI, logs, comentarios nuevos y mensajes de commit. Código y términos técnicos en inglés.
- En UI y logs se dice **PilotX / OrbitX / Agro Parallel**, nunca AgOpenGPS/AOG/AgIO.
- **Nada de diálogos modales.** Regla de cabina: el repo usa doble toque con `"¿Seguro?"`, ya implementado en `ContornoPanel.cs:896`. Se reusa ese patrón, no se inventa otro.
- El namespace de `SourceCode/PilotX.UI/Views/` es **`PilotX.Desktop.Views`** (el proyecto tiene `AssemblyName = PilotX.UI` pero `RootNamespace = PilotX.Desktop`).
- `AgroParallel.Services` es **netstandard2.0**: nada de APIs de net8+.
- El lote **abierto** no se borra: el backend ya lo rechaza, y la UI no le ofrece el botón.
- Es código de una máquina que siembra: un error cuesta plata en el lote. Acá además se **borran datos del cliente**, que no se recuperan.

---

## Contexto que conviene leer antes de empezar

**El borrado ya está escrito y es inalcanzable.** `wwwroot/js/lote-rapido.js` tiene
borrado de a uno (`:120`) y masivo (`:156`), pero **nadie abre `lote-rapido.html`**.
Ese widget **se deja como está** — no se toca ni se borra. Este plan lleva la
función a la pantalla nativa, que es la que el cockpit del PC abre
(`MainWindow.axaml.cs:6523`, `case "lote_menu"`).

**El tombstone no es opcional.** `ResolutorLoteCloud.Resolver` devuelve `Crear`
cuando la carpeta no existe, así que borrar un lote espejo del cloud sin más
hace que **el sync lo reponga en el ciclo siguiente**.

---

### Task 1: Cola de borrados con tombstone

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/OrbitX/ColaLotesBorrados.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/ColaLotesBorradosTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `sealed class ColaLotesBorrados` con `ColaLotesBorrados(string rutaArchivo)`, `void Encolar(string loteNombre)`, `IReadOnlyList<string> Pendientes()`, `bool EstaBorrado(string loteNombre)`, `void Confirmar(string loteNombre)`, `void Descartar(string loteNombre)`.

- [ ] **Step 1: Escribir los tests que fallan**

Crear `ColaLotesBorradosTests.cs`:

```csharp
// ============================================================================
// ColaLotesBorradosTests.cs — la cola de lotes borrados que esperan aviso al
// cloud, y que mientras tanto hace de tombstone.
//
// El tombstone es lo que impide que el sync REPONGA un lote recién borrado:
// ResolutorLoteCloud devuelve Crear cuando la carpeta no existe, así que sin
// esto el operario borra un lote y le reaparece en el ciclo siguiente.
// ============================================================================

using System.IO;
using AgroParallel.Services.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class ColaLotesBorradosTests
    {
        private string _dir;
        private string _archivo;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pilotx_cola_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            _archivo = Path.Combine(_dir, "lotes_borrados.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Test]
        public void ColaNueva_EstaVacia()
        {
            var c = new ColaLotesBorrados(_archivo);

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.That(c.EstaBorrado("Lote 12"), Is.False);
        }

        [Test]
        public void Encolar_QuedaPendienteYCuentaComoBorrado()
        {
            var c = new ColaLotesBorrados(_archivo);

            c.Encolar("Lote 12");

            Assert.That(c.Pendientes(), Is.EquivalentTo(new[] { "Lote 12" }));
            Assert.That(c.EstaBorrado("Lote 12"), Is.True);
        }

        // Sin esto el tombstone se pierde al reiniciar PilotX y el sync repone
        // el lote borrado.
        [Test]
        public void Encolar_SobreviveAlReinicio()
        {
            new ColaLotesBorrados(_archivo).Encolar("Lote 12");

            var otra = new ColaLotesBorrados(_archivo);

            Assert.That(otra.EstaBorrado("Lote 12"), Is.True);
        }

        [Test]
        public void EncolarDosVeces_NoDuplica()
        {
            var c = new ColaLotesBorrados(_archivo);

            c.Encolar("Lote 12");
            c.Encolar("Lote 12");

            Assert.That(c.Pendientes().Count, Is.EqualTo(1));
        }

        // El cloud confirmó: ya no hay nada que reponer, el tombstone se levanta.
        [Test]
        public void Confirmar_SacaElLoteDeLaCola()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            c.Confirmar("Lote 12");

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.That(c.EstaBorrado("Lote 12"), Is.False);
        }

        [Test]
        public void Confirmar_PersisteEnDisco()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");
            c.Confirmar("Lote 12");

            var otra = new ColaLotesBorrados(_archivo);

            Assert.That(otra.EstaBorrado("Lote 12"), Is.False);
        }

        [Test]
        public void Descartar_SacaElLoteIgualQueConfirmar()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            c.Descartar("Lote 12");

            Assert.That(c.Pendientes(), Is.Empty);
        }

        [Test]
        public void VariosLotes_SeManejanIndependientes()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");
            c.Encolar("Lote 13");

            c.Confirmar("Lote 12");

            Assert.That(c.EstaBorrado("Lote 12"), Is.False);
            Assert.That(c.EstaBorrado("Lote 13"), Is.True);
        }

        [Test]
        public void NombreConOtraCapitalizacion_CuentaComoElMismoLote()
        {
            var c = new ColaLotesBorrados(_archivo);
            c.Encolar("Lote 12");

            Assert.That(c.EstaBorrado("LOTE 12"), Is.True);
        }

        // Un archivo corrupto no puede dejar a PilotX sin poder borrar: se
        // arranca con la cola vacía y se sigue.
        [Test]
        public void ArchivoCorrupto_ArrancaVaciaYNoExplota()
        {
            File.WriteAllText(_archivo, "{ esto no es json");

            var c = new ColaLotesBorrados(_archivo);

            Assert.That(c.Pendientes(), Is.Empty);
            Assert.DoesNotThrow(() => c.Encolar("Lote 12"));
            Assert.That(c.EstaBorrado("Lote 12"), Is.True);
        }

        [Test]
        public void NombreVacio_SeIgnora()
        {
            var c = new ColaLotesBorrados(_archivo);

            c.Encolar("");
            c.Encolar(null);

            Assert.That(c.Pendientes(), Is.Empty);
        }
    }
}
```

- [ ] **Step 2: Correr los tests para verificar que fallan**

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj" --filter "FullyQualifiedName~ColaLotesBorrados"`

Expected: FAIL de compilación — `The type or namespace name 'ColaLotesBorrados' could not be found`.

- [ ] **Step 3: Implementar la cola**

Crear `ColaLotesBorrados.cs`:

```csharp
// ============================================================================
// ColaLotesBorrados.cs — los lotes que el operario borró y todavía no se le
// pudieron avisar al cloud.
//
// Hace dos cosas con la misma lista:
//  · COLA: lo que OrbitXSync tiene que mandar cuando haya señal. En el lote no
//    hay WiFi, así que el borrado no puede depender de que la haya.
//  · TOMBSTONE: mientras el lote esté acá, el sync NO lo repone.
//    ResolutorLoteCloud devuelve Crear cuando la carpeta no existe, así que sin
//    este chequeo el operario borra un lote y le reaparece en el ciclo
//    siguiente.
//
// Persiste en disco a propósito: si el tombstone viviera en memoria, un
// reinicio de PilotX bastaría para que el lote volviera.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AgroParallel.Services.OrbitX
{
    public sealed class ColaLotesBorrados
    {
        private readonly string _rutaArchivo;
        private readonly object _candado = new object();
        private readonly HashSet<string> _pendientes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ColaLotesBorrados(string rutaArchivo)
        {
            _rutaArchivo = rutaArchivo;
            Cargar();
        }

        public void Encolar(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return;
            lock (_candado)
            {
                if (_pendientes.Add(loteNombre.Trim())) Guardar();
            }
        }

        /// <summary>Lo que falta avisarle al cloud.</summary>
        public IReadOnlyList<string> Pendientes()
        {
            lock (_candado) return new List<string>(_pendientes);
        }

        /// <summary>El tombstone: true mientras el lote no se haya podido avisar.</summary>
        public bool EstaBorrado(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return false;
            lock (_candado) return _pendientes.Contains(loteNombre.Trim());
        }

        /// <summary>El cloud confirmó el borrado: ya no hay nada que reponer.</summary>
        public void Confirmar(string loteNombre) => Sacar(loteNombre);

        /// <summary>Se abandona el aviso (demasiados intentos fallidos).</summary>
        public void Descartar(string loteNombre) => Sacar(loteNombre);

        private void Sacar(string loteNombre)
        {
            if (string.IsNullOrWhiteSpace(loteNombre)) return;
            lock (_candado)
            {
                if (_pendientes.Remove(loteNombre.Trim())) Guardar();
            }
        }

        private void Cargar()
        {
            try
            {
                if (!File.Exists(_rutaArchivo)) return;
                var leidos = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_rutaArchivo));
                if (leidos == null) return;
                foreach (var n in leidos)
                    if (!string.IsNullOrWhiteSpace(n)) _pendientes.Add(n.Trim());
            }
            catch
            {
                // Archivo roto: se arranca con la cola vacía. Es preferible
                // perder avisos pendientes a dejar a PilotX sin poder borrar.
            }
        }

        private void Guardar()
        {
            try
            {
                string dir = Path.GetDirectoryName(_rutaArchivo);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_rutaArchivo, JsonSerializer.Serialize(new List<string>(_pendientes)));
            }
            catch
            {
                // Si no se puede escribir, la cola sigue viva en memoria hasta
                // el próximo reinicio. No vale frenar el borrado por esto.
            }
        }
    }
}
```

- [ ] **Step 4: Correr los tests para verificar que pasan**

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj" --filter "FullyQualifiedName~ColaLotesBorrados"`

Expected: PASS, 12 tests.

- [ ] **Step 5: Correr la batería completa**

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj"`

Expected: los 12 nuevos pasan. Hay **1 falla preexistente y ajena** (`SoporteRemotoTests`): no la arregles, sólo confirmá que sigue siendo esa y sólo esa.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/OrbitX/ColaLotesBorrados.cs SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/ColaLotesBorradosTests.cs
git commit -m "feat(lotes): cola de borrados con tombstone

Los lotes que el operario borro y todavia no se le pudieron avisar al cloud. La
misma lista hace de cola (lo que OrbitXSync tiene que mandar cuando haya senal)
y de tombstone (mientras el lote este aca, el sync NO lo repone).

El tombstone no es opcional: ResolutorLoteCloud devuelve Crear cuando la carpeta
no existe, asi que sin esto el operario borra un lote y le reaparece en el ciclo
siguiente. Persiste en disco porque en memoria un reinicio bastaria para que
volviera.

Prueba: FileDelete"
```

---

### Task 2: El sync no repone lo borrado y avisa al cloud

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/OrbitX/OrbitXSync.cs`

**Interfaces:**
- Consumes: `ColaLotesBorrados` con `Encolar`, `Pendientes`, `EstaBorrado`, `Confirmar`, `Descartar` (Task 1).
- Produces: en `OrbitXSync`, la propiedad pública `ColaLotesBorrados LotesBorrados { get; }` para que el host encole desde la UI, y el drenado automático en el tick.

- [ ] **Step 1: Agregar la cola como campo**

En `OrbitXSync.cs`, junto a los otros campos de la clase (cerca de `private readonly Queue<SyncItem> _queue`):

```csharp
        // Lotes borrados por el operario que esperan aviso al cloud. Ver
        // ColaLotesBorrados: la misma lista hace de tombstone para que el sync
        // no reponga lo que el operario acaba de borrar.
        private readonly ColaLotesBorrados _lotesBorrados = new ColaLotesBorrados(
            Path.Combine(AgroParallel.Common.AgpPaths.ConfigRoot, "data", "lotes_borrados.json"));

        /// <summary>Cola de borrados pendientes de avisar al cloud. La UI encola
        /// acá al borrar un lote.</summary>
        public ColaLotesBorrados LotesBorrados => _lotesBorrados;
```

- [ ] **Step 2: Cortar la reposición en `GuardarArchivoDeLote`**

En `GuardarArchivoDeLote`, **inmediatamente después** de la línea que resuelve
`string lote = partes[0];` y antes de cualquier escritura:

```csharp
                // El operario borró este lote y todavía no se le pudo avisar al
                // cloud. Si lo escribiéramos, reaparecería solo en el próximo
                // ciclo: ResolutorLoteCloud devuelve Crear cuando la carpeta no
                // existe. Se ackea el pendiente para que el server no lo
                // reencole eternamente.
                if (_lotesBorrados.EstaBorrado(lote))
                {
                    Trace("[LOTE] '" + lote + "' fue borrado en la cabina — no se repone");
                    return true;
                }
```

- [ ] **Step 3: Drenar la cola en el tick**

En `SyncTick`, junto a donde ya se dispara el resto del trabajo periódico
(buscá la línea `if (_cfg.SyncAOG) EnqueueAOGFiles();` y ponelo después):

```csharp
                await AvisarLotesBorrados();
```

- [ ] **Step 4: Implementar el aviso**

Agregar el método, junto a los otros métodos de red de la clase (por ejemplo
después de `SendTracking`):

```csharp
        // Cuántas veces se reintenta avisar un borrado antes de abandonarlo.
        // Mismo criterio que MaxIntentosPorItem de la cola de subida: un aviso
        // que el server rechaza SIEMPRE no puede bloquear a los demás.
        private const int MaxIntentosBorrado = 5;
        private readonly Dictionary<string, int> _intentosBorrado =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Le avisa al cloud de los lotes que el operario borró. Es best-effort:
        /// en el lote no hay WiFi, así que lo que no sale ahora sale en el
        /// próximo tick. El tombstone se levanta recién cuando el cloud confirma.
        /// </summary>
        private async Task AvisarLotesBorrados()
        {
            var pendientes = _lotesBorrados.Pendientes();
            if (pendientes.Count == 0) return;

            foreach (var lote in pendientes)
            {
                string url = _cfg.ServerUrl.TrimEnd('/') + "/api/aog/lote/borrado";
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Add("X-Device-ID", _cfg.DeviceId);
                    req.Headers.Add("X-Auth-Token", _cfg.DeviceToken);
                    if (!string.IsNullOrEmpty(_cfg.EstabSlug))
                        req.Headers.Add("X-Estab-Slug", _cfg.EstabSlug);
                    req.Content = new StringContent(
                        JsonSerializer.Serialize(new Dictionary<string, object> { ["lote"] = lote }),
                        Encoding.UTF8, "application/json");

                    var resp = await _http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        _lotesBorrados.Confirmar(lote);
                        _intentosBorrado.Remove(lote);
                        Trace("[LOTE] borrado avisado al cloud: '" + lote + "'");
                        continue;
                    }

                    // 404 = el cloud no lo tiene: el borrado ya está logrado.
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _lotesBorrados.Confirmar(lote);
                        _intentosBorrado.Remove(lote);
                        Trace("[LOTE] '" + lote + "' no estaba en el cloud — nada que borrar");
                        continue;
                    }

                    ContarFalloBorrado(lote, "HTTP " + (int)resp.StatusCode);
                }
                catch (Exception ex)
                {
                    // Sin señal: se reintenta en el próximo tick, sin contar
                    // como fallo permanente.
                    Trace("[LOTE] no se pudo avisar el borrado de '" + lote + "': " + ex.Message);
                }
            }
        }

        private void ContarFalloBorrado(string lote, string motivo)
        {
            int n;
            _intentosBorrado.TryGetValue(lote, out n);
            n++;
            _intentosBorrado[lote] = n;
            Trace("[LOTE] aviso de borrado de '" + lote + "' rechazado (" + motivo + "), intento " + n);
            if (n >= MaxIntentosBorrado)
            {
                _lotesBorrados.Descartar(lote);
                _intentosBorrado.Remove(lote);
                Trace("[LOTE] se abandona el aviso de borrado de '" + lote + "' tras " + n + " intentos");
            }
        }
```

- [ ] **Step 5: Verificar los `using`**

`OrbitXSync.cs` ya tiene `System`, `System.Collections.Generic`, `System.IO`,
`System.Net.Http`, `System.Text`, `System.Text.Json` y `System.Threading.Tasks`.
Agregá `using AgroParallel.Services.OrbitX;` sólo si el archivo **no** está ya en
ese namespace — fijate antes; si ya está, no hace falta nada.

- [ ] **Step 6: Compilar y correr la batería**

Run: `dotnet build "SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj" -c Debug`
Expected: Build succeeded, 0 errores.

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj"`
Expected: PASS salvo la falla preexistente de `SoporteRemotoTests`.

- [ ] **Step 7: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/OrbitX/OrbitXSync.cs
git commit -m "feat(lotes): el sync avisa los borrados y no repone lo borrado

Dos cambios que van juntos:
 · GuardarArchivoDeLote corta si el lote esta en la cola de borrados. Sin esto
   el sync lo REPONE en el ciclo siguiente, porque ResolutorLoteCloud devuelve
   Crear cuando la carpeta no existe.
 · El tick drena la cola contra POST /api/aog/lote/borrado. Es best-effort: en
   el lote no hay WiFi, lo que no sale ahora sale despues. Un 404 cuenta como
   exito (el cloud no lo tiene, el borrado ya esta logrado) y a los 5 rechazos
   permanentes se abandona el aviso, mismo criterio que la cola de subida.

Prueba: FileDelete"
```

---

### Task 3: El botón de borrar en la pantalla de cabina

**Files:**
- Modify: `SourceCode/PilotX.UI/Views/LotePanel.cs`

**Interfaces:**
- Consumes: `POST /api/lotes/delete?name=` (ya existe, devuelve `{ ok }`).
- Produces: nada que otras tareas consuman.

**El patrón de confirmación es el de la casa, no uno nuevo.** `ContornoPanel.cs:896`
tiene `PedirConfirmacion(Button b, string etiqueta)`: el primer toque cambia el
texto a `"¿Seguro?"`, el segundo dentro de 3 s confirma, y un timer lo desarma
solo. Se replica esa mecánica acá. **Nada de modales**: en cabina están
prohibidos.

- [ ] **Step 1: Agregar los campos del doble toque y del aviso de lista**

En `LotePanel.cs`, junto a los otros campos de la clase:

```csharp
    // Doble toque de confirmación para borrar: botón → (timer de 3 s, etiqueta
    // original). Mismo mecanismo que ContornoPanel y GuiasPanel — en cabina no
    // se usan modales.
    private readonly Dictionary<Button, (DispatcherTimer Timer, string Etiqueta)> _confirmandoBorrado = new();
```

Y los `using` que hagan falta (`Avalonia.Threading` para `DispatcherTimer`,
`System.Collections.Generic` si no está).

- [ ] **Step 2: Agregar los helpers de confirmación**

Junto a los otros métodos privados de la clase:

```csharp
    /// <summary>
    /// Doble toque de confirmación, sin modales (regla de cabina): el primer
    /// toque cambia el texto a "¿Seguro?"; el segundo dentro de 3 s confirma.
    /// Devuelve true cuando hay que ejecutar el borrado.
    /// </summary>
    private bool PedirConfirmacionBorrado(Button b, string etiqueta)
    {
        if (_confirmandoBorrado.ContainsKey(b))
        {
            CancelarConfirmacionBorrado(b);
            return true;
        }
        b.Content = PilotX.Cockpit.Bars.Traductor.T("¿Seguro?");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _confirmandoBorrado[b] = (t, etiqueta);
        t.Tick += (_, _) => CancelarConfirmacionBorrado(b);
        t.Start();
        return false;
    }

    private void CancelarConfirmacionBorrado(Button b)
    {
        if (!_confirmandoBorrado.TryGetValue(b, out var c)) return;
        try { c.Timer.Stop(); } catch { }
        _confirmandoBorrado.Remove(b);
        b.Content = PilotX.Cockpit.Bars.Traductor.T(c.Etiqueta);
    }

    private void CancelarConfirmacionesBorrado()
    {
        foreach (var b in new List<Button>(_confirmandoBorrado.Keys)) CancelarConfirmacionBorrado(b);
    }
```

- [ ] **Step 3: Sumar la columna del botón en las filas de la lista**

En `CargarListaAsync`, cambiar la definición de la grilla de la fila:

```csharp
            var fila = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 48 };
```

por:

```csharp
            // Tercera columna para el botón de borrar. La segunda sigue siendo
            // las hectáreas.
            var fila = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 48 };
```

- [ ] **Step 4: Agregar el botón por fila**

En `CargarListaAsync`, **después** del bloque `if (l.AreaHa != null) { … }` y
**antes** de `_listaFilas.Children.Add(new Border { … })`:

```csharp
            // Borrar: sólo en modo lista normal (en modo plantilla se elige un
            // lote para clonar, no para borrarlo) y nunca para el lote abierto,
            // que el backend rechaza igual.
            if (_modoLista != "plantilla" && nombre != _actual)
            {
                var btnBorrar = new Button
                {
                    Content = PilotX.Cockpit.Bars.Traductor.T("Borrar"),
                    Foreground = new SolidColorBrush(Color.Parse("#C0504A")),
                    Background = BgFila,
                    BorderThickness = new Thickness(0),
                    FontSize = 14,
                    Height = 48,
                    Padding = new Thickness(12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                btnBorrar.Click += async (_, __) =>
                {
                    if (!PedirConfirmacionBorrado(btnBorrar, "Borrar")) return;
                    var r = await PostAsync("/api/lotes/delete?name=" + Uri.EscapeDataString(nombre));
                    if (EsOk(r))
                    {
                        AvisarEnLista(PilotX.Cockpit.Bars.Traductor.T("Lote borrado") + ": " + nombre);
                        await CargarListaAsync();
                    }
                    else
                    {
                        AvisarEnLista(PilotX.Cockpit.Bars.Traductor.T("No se pudo borrar. ¿Está abierto? Cerralo primero."));
                    }
                };
                Grid.SetColumn(btnBorrar, 2);
                fila.Children.Add(btnBorrar);
            }
```

- [ ] **Step 5: Desarmar las confirmaciones al recargar o salir**

Al principio de `CargarListaAsync`, junto al `_listaFilas.Children.Clear()`:

```csharp
        // Los botones de la lista se van a recrear: cualquier "¿Seguro?" armado
        // apuntaría a un botón que ya no existe.
        CancelarConfirmacionesBorrado();
```

Y en el método que cambia de pantalla (`Mostrar`), al principio, la misma llamada:
así salir de la lista cancela un borrado a medio confirmar.

- [ ] **Step 6: Agregar el aviso de la pantalla de lista**

El banner que ya existe (`_avisoBox`/`_avisoTxt`) vive dentro de la pantalla
"nombre" (`ArmarNombre`), así que **no se ve desde la lista**. Hace falta uno
propio.

En `ArmarLista`, agregar un `Border` + `TextBlock` con `IsVisible = false`
siguiendo exactamente el mismo patrón visual que el de `ArmarNombre` (mirá cómo
está armado ahí y replicalo), guardarlos en campos `_avisoListaBox` /
`_avisoListaTxt`, y agregar:

```csharp
    private void AvisarEnLista(string texto)
    {
        _avisoListaTxt.Text = texto;
        _avisoListaBox.IsVisible = true;
    }

    private void OcultarAvisoLista() => _avisoListaBox.IsVisible = false;
```

Llamá `OcultarAvisoLista()` al entrar a la lista, para que no quede el mensaje
de la vez anterior.

- [ ] **Step 7: Compilar**

Run: `dotnet build "SourceCode/PilotX.UI/PilotX.UI.csproj" -c Debug`
Expected: Build succeeded, 0 errores. Hay **16 advertencias preexistentes** en
otros archivos; ninguna nueva en `LotePanel.cs`.

- [ ] **Step 8: Commit**

```bash
git add SourceCode/PilotX.UI/Views/LotePanel.cs
git commit -m "feat(lotes): borrar un lote desde la pantalla de cabina

El borrado ya existia en el backend (POST /api/lotes/delete) y en un widget web
que NADIE abre (lote-rapido.html). Ahora esta en LotePanel, que es la pantalla
que el cockpit del PC abre de verdad.

Doble toque con '¿Seguro?' y timer de 3 s, el mismo patron que ContornoPanel y
GuiasPanel: en cabina no se usan modales. El lote abierto se lista sin boton
(el backend lo rechaza igual) y en modo plantilla tampoco aparece, que ahi se
elige un lote para clonar.

Prueba: FileDelete"
```

---

### Task 4: Borrar todos, con doble confirmación

**Files:**
- Modify: `SourceCode/PilotX.UI/Views/LotePanel.cs`

**Interfaces:**
- Consumes: los helpers de la Task 3 (`PedirConfirmacionBorrado`, `AvisarEnLista`, `CancelarConfirmacionesBorrado`), `PostAsync`, `EsOk`.
- Produces: nada.

**Decisión del usuario (2026-09-15):** entra, pero con **doble confirmación** y
diciendo cuántos lotes se van a borrar. Sirve para preparar una pantalla nueva o
limpiar un equipo de demo sin ir por el explorador de Windows.

- [ ] **Step 1: Agregar el botón al pie de la lista**

En `ArmarLista`, después del `ScrollViewer` de las filas:

```csharp
        // Borrar todos: NO es para el día a día, es para preparar una pantalla
        // nueva o limpiar un equipo de demo. Por eso va al pie, en rojo, y pide
        // DOS confirmaciones: la primera dice cuántos lotes son.
        btnBorrarTodos = new Button
        {
            Content = PilotX.Cockpit.Bars.Traductor.T("Borrar todos"),
            Foreground = new SolidColorBrush(Color.Parse("#C0504A")),
            Background = BgFila,
            BorderBrush = new SolidColorBrush(Color.Parse("#C0504A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            FontSize = 14,
            Height = 44,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 0),
        };
        p.Children.Add(btnBorrarTodos);
```

Declaralo como campo `private readonly Button _btnBorrarTodos;` y devolvelo por
`out` desde `ArmarLista`, siguiendo el patrón que ese método ya usa para
`_listaTitulo` y `_listaFilas`.

- [ ] **Step 2: Implementar las dos confirmaciones**

```csharp
    // Segundo paso del borrado masivo: true cuando la primera confirmación ya
    // se dio y estamos esperando la segunda.
    private bool _borradoMasivoArmado;

    private async void OnBorrarTodos()
    {
        var borrables = _lotes
            .Select(l => l.Name ?? "")
            .Where(n => n.Length > 0 && n != _actual)
            .ToList();

        if (borrables.Count == 0)
        {
            AvisarEnLista(_actual != null
                ? PilotX.Cockpit.Bars.Traductor.T("No hay lotes para borrar (el abierto no se borra)")
                : PilotX.Cockpit.Bars.Traductor.T("No hay lotes para borrar"));
            return;
        }

        // Primera confirmación: decir CUÁNTOS y qué queda afuera.
        if (!_borradoMasivoArmado)
        {
            _borradoMasivoArmado = true;
            _btnBorrarTodos.Content = PilotX.Cockpit.Bars.Traductor.T("Confirmar borrado");
            AvisarEnLista(string.Format(
                PilotX.Cockpit.Bars.Traductor.T("Se van a borrar {0} lotes. No se puede deshacer."),
                borrables.Count)
                + (_actual != null
                    ? " " + PilotX.Cockpit.Bars.Traductor.T("El lote abierto no se toca.")
                    : ""));
            return;
        }

        // Segunda: ejecuta.
        _borradoMasivoArmado = false;
        _btnBorrarTodos.Content = PilotX.Cockpit.Bars.Traductor.T("Borrar todos");

        int fallas = 0;
        foreach (var nombre in borrables)
        {
            var r = await PostAsync("/api/lotes/delete?name=" + Uri.EscapeDataString(nombre));
            if (!EsOk(r)) fallas++;
        }

        AvisarEnLista(fallas == 0
            ? string.Format(PilotX.Cockpit.Bars.Traductor.T("Se borraron {0} lotes"), borrables.Count)
            : string.Format(PilotX.Cockpit.Bars.Traductor.T("Se borraron {0}, {1} no se pudieron"),
                            borrables.Count - fallas, fallas));

        await CargarListaAsync();
    }
```

Cablealo con `_btnBorrarTodos.Click += (_, __) => OnBorrarTodos();` donde se
cablean los demás botones.

- [ ] **Step 3: Desarmar el borrado masivo al salir o recargar**

En `CancelarConfirmacionesBorrado()`, agregar al final:

```csharp
        if (_borradoMasivoArmado)
        {
            _borradoMasivoArmado = false;
            _btnBorrarTodos.Content = PilotX.Cockpit.Bars.Traductor.T("Borrar todos");
        }
```

Así salir de la pantalla o recargar la lista desarma la confirmación pendiente,
igual que con los botones de fila.

- [ ] **Step 4: Ocultar el botón en modo plantilla**

En `CargarListaAsync`, junto a donde se decide el título:

```csharp
        // En modo plantilla se elige un lote para clonar: borrar no aplica.
        _btnBorrarTodos.IsVisible = _modoLista != "plantilla";
```

- [ ] **Step 5: Compilar**

Run: `dotnet build "SourceCode/PilotX.UI/PilotX.UI.csproj" -c Debug`
Expected: Build succeeded, 0 errores, ninguna advertencia nueva en `LotePanel.cs`.

Agregá `using System.Linq;` si no está.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.UI/Views/LotePanel.cs
git commit -m "feat(lotes): borrar todos los lotes, con doble confirmacion

Para preparar una pantalla nueva o limpiar un equipo de demo sin ir por el
explorador de Windows. NO es para el dia a dia: va al pie, en rojo, y pide DOS
confirmaciones — la primera dice cuantos lotes son y que el abierto no se toca.

Salir de la pantalla o recargar la lista desarma la confirmacion pendiente.

Prueba: FileDelete"
```

---

### Task 5: Encolar el aviso al cloud cuando se borra

**Files:**
- Modify: `SourceCode/PilotX.GuidanceEngine/Adapters/EngineLotesService.cs` (`DeleteFieldAsync`)
- Modify: `SourceCode/PilotX.GuidanceEngine/EngineWebHost.cs` (cableado)

**Interfaces:**
- Consumes: `OrbitXSync.LotesBorrados` con `Encolar(string)` (Task 2).
- Produces: nada.

Hasta acá el borrado es sólo local. Esta tarea es la que hace que el cloud se
entere.

- [ ] **Step 1: Agregar el hook en `EngineLotesService`**

Junto a los otros campos de la clase:

```csharp
        /// <summary>Se llama al borrar un lote, para que el sync le avise al
        /// cloud. Lo inyecta el host; si no está cableado, el borrado es sólo
        /// local (que es el comportamiento de antes).</summary>
        public Action<string> AlBorrarLote;
```

- [ ] **Step 2: Dispararlo en `DeleteFieldAsync`**

En `DeleteFieldAsync`, **después** de `Directory.Delete(dir, true);` y antes del
`return Task.FromResult(true);`:

```csharp
                // Avisarle al sync para que lo propague al cloud. Va DESPUÉS del
                // borrado real: si el Delete tira, no hay nada que avisar.
                try { AlBorrarLote?.Invoke(name); } catch { /* el aviso no puede tumbar el borrado */ }

                // La entrada cacheada del lote borrado queda colgada si no se
                // saca: ListFields la seguiría usando para un directorio que ya
                // no existe.
                lock (_boundaryCacheLock) { _boundaryCache.Remove(dir); }
```

- [ ] **Step 3: Cablearlo en el host**

En `EngineWebHost.cs`, **en los mismos dos lugares** donde hoy se cablea
`_orbitxSync.ImportarLoteDesdeKml = lotes.CrearLoteDesdeKmlSinAbrir;`, agregar
inmediatamente después:

```csharp
                lotes.AlBorrarLote = nombre => _orbitxSync.LotesBorrados.Encolar(nombre);
```

Buscalos con:
`grep -n "ImportarLoteDesdeKml" SourceCode/PilotX.GuidanceEngine/EngineWebHost.cs`

Hay **dos** sitios. Si encontrás un número distinto, **pará y reportá**.

- [ ] **Step 4: Compilar**

Run: `dotnet build "SourceCode/PilotX.GuidanceEngine/PilotX.GuidanceEngine.csproj" -c Debug`
Expected: Build succeeded, 0 errores, 0 advertencias.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/PilotX.GuidanceEngine/Adapters/EngineLotesService.cs SourceCode/PilotX.GuidanceEngine/EngineWebHost.cs
git commit -m "feat(lotes): al borrar un lote se encola el aviso al cloud

DeleteFieldAsync dispara un hook que el host cablea contra la cola de borrados
de OrbitXSync. Va DESPUES del Directory.Delete: si el borrado tira, no hay nada
que avisar. Si el hook no esta cableado, el borrado es solo local, que es el
comportamiento de antes.

De paso se saca del _boundaryCache la entrada del lote borrado, que hasta ahora
quedaba colgada apuntando a un directorio inexistente.

Prueba: FileDelete"
```

---

### Task 6: Manual de cabina

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

**Interfaces:**
- Consumes: el comportamiento de las tareas 3 y 4.
- Produces: nada.

Lo dispara la regla del `CLAUDE.md`: aparece una función nueva que el operario
ve, y además borra datos que no se recuperan.

- [ ] **Step 1: Confirmar la ruta real**

Run: `grep -n "<h2>Lote</h2>" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

La sección de Lote tiene la ruta `<b>Menú izquierdo</b> <i>›</i> <b>LOTE</b>`.
Verificala en el archivo antes de escribir, no de memoria.

- [ ] **Step 2: Agregar las entradas**

Dentro de `<section id="lote">`, después del `<div class="clave">` sobre lotes
`(OrbitX)` que ya está ahí:

```html
          <div class="clave">Para <strong>borrar un lote</strong>: <strong>Abrir</strong>, y tocá <strong>Borrar</strong> en la fila del lote. Pide <strong>dos toques</strong>: el primero deja el botón en <strong>¿Seguro?</strong> y el segundo, dentro de 3 segundos, lo borra. Si tocás otra cosa o pasan los 3 segundos, se cancela solo.</div>
          <div class="aviso">El lote <strong>abierto no se puede borrar</strong>: cerralo primero. Y lo borrado <strong>no se recupera</strong> — si el lote estaba en OrbitX, también se borra allá cuando la pantalla tenga señal.</div>
```

- [ ] **Step 3: Documentar el borrado masivo**

A continuación:

```html
          <h3>Borrar todos los lotes</h3>
          <p>Al pie de la lista de <strong>Abrir</strong> hay un <strong>Borrar todos</strong> en rojo. Es para <strong>preparar una pantalla nueva</strong> o limpiar un equipo de demostración, no para el día a día.</p>
          <div class="aviso">Pide <strong>dos confirmaciones</strong> y te dice cuántos lotes va a borrar antes de hacerlo. El lote abierto no se toca. <strong>No se puede deshacer</strong>: si hay lotes del cliente, sacá una copia antes.</div>
```

- [ ] **Step 4: Verificar que el manual no quede mintiendo**

Run: `grep -n -i "borrar lote\|borrar un lote\|eliminar lote" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

Revisar cada resultado: si alguno dice que no se pueden borrar lotes, o describe
otra forma de hacerlo, corregilo en este mismo commit y decilo en el reporte.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html
git commit -m "docs(ayuda): borrar lotes"
```

---

## Verificación final (en banco, antes de declarar nada)

1. Borrar un lote desde **Abrir** → desaparece de la lista; confirmar en disco
   que `Fields/<nombre>/` ya no está.
2. Tocar **Borrar** y esperar 4 segundos → el botón vuelve a decir "Borrar" solo.
3. Tocar **Borrar** en un lote y después en otro → el primero se desarma.
4. Intentar borrar con un lote abierto → ese lote no muestra botón.
5. Borrar **sin señal** → el lote desaparece igual y el aviso queda encolado en
   `<ConfigRoot>/data/lotes_borrados.json`.
6. Volver la señal → el lote desaparece del panel de OrbitX y el archivo de cola
   queda vacío.
7. **La prueba que más importa:** borrar un lote que vino de OrbitX, dejar correr
   dos ciclos de sync completos, y confirmar que **no reaparece**.
8. **Borrar todos** con 3 lotes y uno abierto → pide dos confirmaciones, dice "3
   lotes", y al terminar queda sólo el abierto.
