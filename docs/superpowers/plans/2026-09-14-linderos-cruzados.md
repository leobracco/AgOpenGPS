# Linderos cruzados — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un lote bajado de OrbitX nunca pise el lindero de un lote hecho por el operario, y que crear un lote con un nombre que ya existe avise en vez de fallar mudo.

**Architecture:** La decisión "a qué carpeta va el lote del cloud y si hay que escribir el lindero" se extrae a una clase pura en `AgroParallel.Services` (`ResolutorLoteCloud`), testeable sin el motor. `EngineLotesService.CrearLoteDesdeKmlSinAbrir` pasa a consultarla en vez de escribir directo. Un marcador `.orbitx` dentro de la carpeta del lote distingue el espejo del cloud del lote del operario, y su SHA evita reescribir cuando el lindero no cambió.

**Tech Stack:** C# (netstandard2.0 para Services, net9.0 para el engine), NUnit 4.3.2, JS vanilla en la WebUI.

## Global Constraints

- Castellano rioplatense en UI, logs, comentarios nuevos y mensajes de commit. Código y términos técnicos en inglés.
- En UI y logs se dice **PilotX / OrbitX / Agro Parallel**, nunca AgOpenGPS/AOG/AgIO.
- Tests: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj"` desde la raíz del repo.
- `AgroParallel.Services` es **netstandard2.0**: nada de APIs de net8+. `System.Text.Json` 8.0.5 está disponible.
- El proyecto de tests **no referencia** `PilotX.GuidanceEngine`. Todo lo que tenga que testearse va en `AgroParallel.Services`.
- Sufijo tope: 20 intentos. Nada de recursión sobre el sufijo.
- Ningún commit declara `Cierra:` en el tablero salvo que se haya probado en cabina. Si sólo hay código, va `Prueba:`.

---

## Antecedente que conviene leer antes de empezar

`AgroParallel.Services.Tests/PrescripcionLoteTests.cs` documenta el reporte del
2026-08-10: *"creé un lote nuevo y levantó el shape de Las de atras"*. Es el
**mismo patrón de bug** que este plan arregla, con prescripciones en vez de
linderos, y se resolvió atando el recurso al lote en que se activó. Leer esos
tests antes de escribir los de acá: el estilo de fixture con directorio
temporal y `[NonParallelizable]` ya está resuelto ahí.

---

### Task 1: ResolutorLoteCloud — decidir destino y acción

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/OrbitX/ResolutorLoteCloud.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/ResolutorLoteCloudTests.cs`

**Interfaces:**
- Consumes: nada (primera tarea).
- Produces:
  - `enum AccionLoteCloud { Crear, ActualizarEspejo, SinCambios, SinLugar }`
  - `sealed class DestinoLoteCloud { string Directorio; string NombreCarpeta; AccionLoteCloud Accion; }`
  - `static class ResolutorLoteCloud`:
    - `static DestinoLoteCloud Resolver(string fieldsRoot, string nombreCloud, string shaKml)`
    - `static void EscribirMarcador(string directorio, string nombreCloud, string shaKml)`
    - `static string CalcularSha(string contenido)`
    - `static string LimpiarNombre(string nombre)`

- [ ] **Step 1: Escribir los tests que fallan**

Crear `ResolutorLoteCloudTests.cs`:

```csharp
// ============================================================================
// ResolutorLoteCloudTests.cs — un lote que baja de OrbitX NUNCA pisa el lindero
// de un lote que hizo el operario (reporte 2026-09-12: "creé un lote y levantó
// un lindero que creé con OrbitX"). El marcador .orbitx es lo que distingue el
// lote espejo del cloud del lote local; sin él, "guardar aparte" degeneraría en
// (OrbitX 2), (OrbitX 3)… en cada ciclo de sync.
// ============================================================================

using System.IO;
using AgroParallel.Services.OrbitX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    public class ResolutorLoteCloudTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "pilotx_lotes_" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // Crea una carpeta de lote "del operario": tiene Field.txt pero NO .orbitx.
        private string CrearLoteLocal(string nombre)
        {
            string dir = Path.Combine(_root, nombre);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Field.txt"), "$FieldDir\n");
            return dir;
        }

        [Test]
        public void LoteNuevo_SeCreaConElNombrePedido()
        {
            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.Crear));
            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12"));
            Assert.That(d.Directorio, Is.EqualTo(Path.Combine(_root, "Lote 12")));
        }

        [Test]
        public void LoteDelOperarioConEseNombre_NoSeTocaYVaConSufijo()
        {
            CrearLoteLocal("Lote 12");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.Crear));
            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX)"));
        }

        [Test]
        public void EspejoConMismoSha_NoSeReescribe()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            ResolutorLoteCloud.EscribirMarcador(dir, "Lote 12", "sha-a");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.SinCambios));
            Assert.That(d.Directorio, Is.EqualTo(dir));
        }

        [Test]
        public void EspejoConShaDistinto_SeActualizaEnElMismoDirectorio()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            ResolutorLoteCloud.EscribirMarcador(dir, "Lote 12", "sha-vieja");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-nueva");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.ActualizarEspejo));
            Assert.That(d.Directorio, Is.EqualTo(dir));
        }

        [Test]
        public void NombreYSufijoOcupadosPorElOperario_VaAlSufijoNumerado()
        {
            CrearLoteLocal("Lote 12");
            CrearLoteLocal("Lote 12 (OrbitX)");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.Crear));
            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX 2)"));
        }

        // La idempotencia es el punto del marcador: sin esto cada ciclo de sync
        // dejaría un lote nuevo.
        [Test]
        public void DosSyncsSeguidosSinCambios_NoDuplicanElLote()
        {
            CrearLoteLocal("Lote 12");

            var primera = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");
            Directory.CreateDirectory(primera.Directorio);
            ResolutorLoteCloud.EscribirMarcador(primera.Directorio, "Lote 12", "sha-a");

            var segunda = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(segunda.Accion, Is.EqualTo(AccionLoteCloud.SinCambios));
            Assert.That(segunda.Directorio, Is.EqualTo(primera.Directorio));
            Assert.That(Directory.GetDirectories(_root).Length, Is.EqualTo(2));
        }

        [Test]
        public void MarcadorDeOtroLoteCloud_NoSeConsideraEspejo()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            ResolutorLoteCloud.EscribirMarcador(dir, "Otro lote", "sha-x");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX)"));
        }

        [Test]
        public void MarcadorIlegible_SeTrataComoLoteDelOperario()
        {
            string dir = Path.Combine(_root, "Lote 12");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".orbitx"), "{ esto no es json");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote 12 (OrbitX)"));
        }

        [Test]
        public void VeinteSufijosOcupados_DevuelveSinLugar()
        {
            CrearLoteLocal("Lote 12");
            CrearLoteLocal("Lote 12 (OrbitX)");
            for (int i = 2; i <= 20; i++) CrearLoteLocal("Lote 12 (OrbitX " + i + ")");

            var d = ResolutorLoteCloud.Resolver(_root, "Lote 12", "sha-a");

            Assert.That(d.Accion, Is.EqualTo(AccionLoteCloud.SinLugar));
        }

        [Test]
        public void ShaEsEstableYDistingueContenido()
        {
            Assert.That(ResolutorLoteCloud.CalcularSha("hola"),
                        Is.EqualTo(ResolutorLoteCloud.CalcularSha("hola")));
            Assert.That(ResolutorLoteCloud.CalcularSha("hola"),
                        Is.Not.EqualTo(ResolutorLoteCloud.CalcularSha("chau")));
        }

        // Sin literal esperado: los caracteres inválidos de nombre de archivo
        // NO son los mismos en Windows y Linux (en Linux ':' y '*' son válidos)
        // y el repo compila para los dos. Se verifica la propiedad, no la cadena.
        [Test]
        public void NombreConCaracteresInvalidos_SeLimpia()
        {
            string sucio = "Lote" + new string(Path.GetInvalidFileNameChars()) + "12";

            var d = ResolutorLoteCloud.Resolver(_root, sucio, "sha-a");

            Assert.That(d.NombreCarpeta, Is.EqualTo("Lote12"));
            Assert.That(d.NombreCarpeta.IndexOfAny(Path.GetInvalidFileNameChars()), Is.EqualTo(-1));
        }
    }
}
```

- [ ] **Step 2: Correr los tests para verificar que fallan**

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj" --filter "FullyQualifiedName~ResolutorLoteCloud"`

Expected: FAIL de compilación — `The type or namespace name 'ResolutorLoteCloud' could not be found`.

- [ ] **Step 3: Implementar `ResolutorLoteCloud`**

Crear `ResolutorLoteCloud.cs`:

```csharp
// ============================================================================
// ResolutorLoteCloud.cs — decide DÓNDE cae un lote que baja de OrbitX y si hay
// que escribirle el lindero.
//
// El trabajo del operario no se pisa nunca: si ya existe una carpeta con ese
// nombre y NO es espejo del cloud, el lote de OrbitX entra como
// "<nombre> (OrbitX)". Antes se hacía BoundaryFiles.Save() directo sobre la
// carpeta existente y el lindero recorrido en cabina se perdía sin aviso
// (reporte 2026-09-12).
//
// El marcador .orbitx es lo que hace posible distinguir el espejo del cloud del
// lote local. Sin él, "guardar aparte" crearía un lote nuevo en CADA ciclo de
// sync. El SHA del KML además evita reescribir cuando el lindero no cambió.
//
// Lógica pura de archivos a propósito: vive acá y no en el motor para que sea
// testeable — AgroParallel.Services.Tests no referencia PilotX.GuidanceEngine.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgroParallel.Services.OrbitX
{
    public enum AccionLoteCloud
    {
        /// <summary>No hay carpeta: crear el lote y escribir el marcador.</summary>
        Crear,
        /// <summary>Es el espejo del cloud y el lindero cambió: reescribir.</summary>
        ActualizarEspejo,
        /// <summary>Es el espejo y el KML es el mismo: no tocar el disco.</summary>
        SinCambios,
        /// <summary>Se agotaron los sufijos. El caller loguea y descarta.</summary>
        SinLugar,
    }

    public sealed class DestinoLoteCloud
    {
        public string Directorio { get; set; }
        public string NombreCarpeta { get; set; }
        public AccionLoteCloud Accion { get; set; }
    }

    public static class ResolutorLoteCloud
    {
        public const string NombreMarcador = ".orbitx";
        private const int MaxSufijos = 20;

        public static DestinoLoteCloud Resolver(string fieldsRoot, string nombreCloud, string shaKml)
        {
            string limpio = LimpiarNombre(nombreCloud);
            if (string.IsNullOrEmpty(limpio) || string.IsNullOrEmpty(fieldsRoot))
                return new DestinoLoteCloud { Accion = AccionLoteCloud.SinLugar };

            for (int intento = 0; intento <= MaxSufijos; intento++)
            {
                string candidato = NombreCandidato(limpio, intento);
                string dir = Path.Combine(fieldsRoot, candidato);

                if (!Directory.Exists(dir))
                {
                    return new DestinoLoteCloud
                    {
                        Directorio = dir,
                        NombreCarpeta = candidato,
                        Accion = AccionLoteCloud.Crear,
                    };
                }

                var marcador = LeerMarcador(dir);
                bool esEspejoDeEsteLote = marcador != null &&
                    string.Equals(marcador.LoteCloud, nombreCloud, StringComparison.OrdinalIgnoreCase);

                if (esEspejoDeEsteLote)
                {
                    return new DestinoLoteCloud
                    {
                        Directorio = dir,
                        NombreCarpeta = candidato,
                        Accion = string.Equals(marcador.ShaKml, shaKml, StringComparison.Ordinal)
                            ? AccionLoteCloud.SinCambios
                            : AccionLoteCloud.ActualizarEspejo,
                    };
                }

                // Ocupado por el operario (o por el espejo de OTRO lote cloud):
                // no se toca, se prueba el sufijo siguiente.
            }

            return new DestinoLoteCloud { Accion = AccionLoteCloud.SinLugar };
        }

        // intento 0 = el nombre pelado; 1 = "(OrbitX)"; 2+ = "(OrbitX N)".
        private static string NombreCandidato(string limpio, int intento)
        {
            if (intento == 0) return limpio;
            if (intento == 1) return limpio + " (OrbitX)";
            return limpio + " (OrbitX " + intento.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public static void EscribirMarcador(string directorio, string nombreCloud, string shaKml)
        {
            var doc = new MarcadorOrbitX
            {
                LoteCloud = nombreCloud,
                ShaKml = shaKml,
                Ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            File.WriteAllText(Path.Combine(directorio, NombreMarcador),
                              JsonSerializer.Serialize(doc));
        }

        private static MarcadorOrbitX LeerMarcador(string directorio)
        {
            try
            {
                string ruta = Path.Combine(directorio, NombreMarcador);
                if (!File.Exists(ruta)) return null;
                return JsonSerializer.Deserialize<MarcadorOrbitX>(File.ReadAllText(ruta));
            }
            catch
            {
                // Marcador roto = no sabemos de quién es la carpeta. Se trata como
                // del operario: no pisar nunca es la regla.
                return null;
            }
        }

        public static string CalcularSha(string contenido)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(contenido ?? ""));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Saca los caracteres que no valen como nombre de carpeta.
        /// Mismo criterio que EngineLotesService.CleanName: se eliminan, no se
        /// reemplazan.</summary>
        public static string LimpiarNombre(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return "";
            var invalidos = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(nombre.Length);
            foreach (char c in nombre)
                if (Array.IndexOf(invalidos, c) < 0) sb.Append(c);
            return sb.ToString().Trim();
        }

        private sealed class MarcadorOrbitX
        {
            [System.Text.Json.Serialization.JsonPropertyName("lote_cloud")]
            public string LoteCloud { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sha_kml")]
            public string ShaKml { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("ts")]
            public string Ts { get; set; }
        }
    }
}
```

- [ ] **Step 4: Correr los tests para verificar que pasan**

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj" --filter "FullyQualifiedName~ResolutorLoteCloud"`

Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/OrbitX/ResolutorLoteCloud.cs SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/ResolutorLoteCloudTests.cs
git commit -m "feat(lotes): resolutor de destino para lotes que bajan de OrbitX

El lote del cloud no pisa mas el lindero de un lote del operario: si el nombre
esta ocupado entra como '<nombre> (OrbitX)'. El marcador .orbitx distingue el
espejo del cloud del lote local y su SHA evita reescribir cuando el KML no
cambio -- sin eso cada ciclo de sync dejaria un duplicado.

Logica pura en Services y no en el engine porque el proyecto de tests no
referencia PilotX.GuidanceEngine.

Prueba: Boundary"
```

---

### Task 2: Cablear el motor al resolutor

**Files:**
- Modify: `SourceCode/PilotX.GuidanceEngine/Adapters/EngineLotesService.cs:435-487` (`CrearLoteDesdeKmlSinAbrir`)

**Interfaces:**
- Consumes: `ResolutorLoteCloud.Resolver/EscribirMarcador/CalcularSha`, `AccionLoteCloud` (Task 1).
- Produces: `CrearLoteDesdeKmlSinAbrir(string nombre, string kmlContenido) -> bool` con la misma firma de hoy (la consume `OrbitXSync.ImportarLoteDesdeKml`, que no cambia).

- [ ] **Step 1: Reemplazar el cuerpo de `CrearLoteDesdeKmlSinAbrir`**

Reemplazar el bloque que hoy resuelve `dir` y origen (desde `string root = RegistrySettings.fieldsDirectory;` hasta el `BoundaryFiles.Save(dir, lista);` inclusive) por:

```csharp
                string root = RegistrySettings.fieldsDirectory;
                if (string.IsNullOrEmpty(root)) return false;

                // El destino lo decide el resolutor: si la carpeta con ese nombre
                // es de un lote del operario, el del cloud entra aparte. Nunca se
                // pisa un lindero que no sea espejo de este mismo lote cloud.
                string sha = ResolutorLoteCloud.CalcularSha(kmlContenido);
                var destino = ResolutorLoteCloud.Resolver(root, nombre, sha);

                if (destino.Accion == AccionLoteCloud.SinLugar)
                {
                    Log.EventWriter($"GuidanceEngine: lote '{nombre}' de OrbitX sin lugar (20 sufijos ocupados)");
                    return false;
                }

                if (destino.Accion == AccionLoteCloud.SinCambios)
                {
                    Log.EventWriter($"GuidanceEngine: lote '{destino.NombreCarpeta}' de OrbitX ya al dia (mismo KML)");
                    return true;
                }

                // El guard de "lote abierto" se revalida contra el destino REAL:
                // el de arriba miro el nombre pedido, no el resuelto.
                if (_host.IsJobStarted &&
                    string.Equals(_host.currentFieldDirectory, destino.NombreCarpeta, StringComparison.OrdinalIgnoreCase))
                    return false;

                string dir = destino.Directorio;

                AgOpenGPS.Core.Models.Wgs84 origen;
                if (destino.Accion == AccionLoteCloud.ActualizarEspejo &&
                    File.Exists(Path.Combine(dir, "Field.txt")))
                {
                    // Espejo ya existente: se conserva SU origen; pisarlo
                    // desfasaria guias y cobertura locales.
                    origen = AgOpenGPS.IO.FieldPlaneFiles.LoadOrigin(dir);
                }
                else
                {
                    Directory.CreateDirectory(dir);
                    origen = anillos[0][0];
                    AgOpenGPS.IO.FieldPlaneFiles.Save(dir, DateTime.Now, origen);
                }

                var plano = new AgOpenGPS.Core.Models.LocalPlane(
                    origen, new AgOpenGPS.Core.Models.SharedFieldProperties());

                var lista = new List<CBoundaryList>();
                foreach (var anillo in anillos)
                {
                    var linde = new CBoundaryList();
                    foreach (var p in anillo)
                        linde.fenceLine.Add(new vec3(plano.ConvertWgs84ToGeoCoord(p)));
                    linde.CalculateFenceArea(lista.Count);
                    linde.FixFenceLine(lista.Count);
                    lista.Add(linde);
                }

                AgOpenGPS.IO.BoundaryFiles.Save(dir, lista);
                ResolutorLoteCloud.EscribirMarcador(dir, nombre, sha);

                if (!string.Equals(destino.NombreCarpeta, nombre, StringComparison.Ordinal))
                    Log.EventWriter($"GuidanceEngine: '{nombre}' de OrbitX entro como '{destino.NombreCarpeta}' (ya habia un lote con ese nombre)");
                else
                    Log.EventWriter($"GuidanceEngine: lote '{destino.NombreCarpeta}' desde OrbitX ({lista.Count} anillos, sin abrir)");
                return true;
```

- [ ] **Step 2: Agregar el `using` del resolutor**

En la cabecera de `EngineLotesService.cs`, junto a los `using` existentes:

```csharp
using AgroParallel.Services.OrbitX;
```

- [ ] **Step 3: Actualizar el comentario XML del método**

El `<summary>` de hoy dice *"Lote existente → se conserva SU origen y solo se reemplaza el lindero"*, que ya no es cierto. Reemplazar esa línea por:

```csharp
        /// Lote nuevo → Field.txt con origen en el primer punto + Boundary.
        /// Ya existe y es espejo de ESTE lote cloud → se conserva su origen y se
        /// reemplaza el lindero solo si cambió el KML.
        /// Ya existe pero es del operario → NO se toca: el lote del cloud entra
        /// como "&lt;nombre&gt; (OrbitX)".
        /// Lote ABIERTO → false (el caller avisa).
```

- [ ] **Step 4: Compilar**

Run: `dotnet build "SourceCode/PilotX.GuidanceEngine/PilotX.GuidanceEngine.csproj" -c Debug`

Expected: Build succeeded, 0 errores.

- [ ] **Step 5: Correr toda la batería de tests**

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj"`

Expected: PASS. Ningún test existente se rompe.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.GuidanceEngine/Adapters/EngineLotesService.cs
git commit -m "fix(lotes): el lote de OrbitX no pisa mas el lindero del operario

CrearLoteDesdeKmlSinAbrir hacia BoundaryFiles.Save() directo sobre la carpeta
existente: el lindero recorrido en cabina se perdia sin aviso. Ahora el destino
lo decide ResolutorLoteCloud y el guard de lote abierto se revalida contra el
directorio resuelto, no contra el nombre pedido.

Prueba: Boundary"
```

---

### Task 3: Crear lote con nombre repetido avisa

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/ILotesService.cs:29` (firma de `CreateFieldAsync`)
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Models/ResultadoCrearLote.cs`
- Modify: `SourceCode/PilotX.GuidanceEngine/Adapters/EngineLotesService.cs:174-204` (`CreateFieldAsync`)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/LotesController.cs:59-64` (`Create`)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/lote.html` (pantalla `scNew`)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/lote.js:143-150` (`btnCreate`)

**Interfaces:**
- Consumes: nada de las tareas anteriores.
- Produces: `enum MotivoCrearLote { Ok, YaExiste, NombreInvalido, SinDirectorioDeLotes, Error }`; `ILotesService.CreateFieldAsync(string) -> Task<ResultadoCrearLote>`; el endpoint devuelve `{ ok: bool, motivo: string }` en snake_case del enum (`"ya_existe"`, etc.).

`EngineLotesService` es la **única** implementación de `ILotesService` (el
`FormGpsLotesService` que menciona el comentario de la interfaz ya no existe), y
`LotesController.Create` es el único caller. Cambiar la firma no rompe nada más.

- [ ] **Step 1: Crear el tipo de resultado**

Crear `ResultadoCrearLote.cs`:

```csharp
// ResultadoCrearLote.cs — por qué falló crear un lote.
// Antes CreateFieldAsync devolvía bool y "ya existe" era indistinguible de
// "error": la pantalla de lote cerraba muda y el operario creía que había
// creado su lote cuando en realidad quedaba abierto otro (reporte 2026-09-12).

namespace AgroParallel.Models
{
    public enum MotivoCrearLote
    {
        Ok,
        YaExiste,
        NombreInvalido,
        SinDirectorioDeLotes,
        Error,
    }

    public sealed class ResultadoCrearLote
    {
        public bool Ok { get; set; }
        public MotivoCrearLote Motivo { get; set; }

        public static ResultadoCrearLote Bien()
            => new ResultadoCrearLote { Ok = true, Motivo = MotivoCrearLote.Ok };

        public static ResultadoCrearLote Falla(MotivoCrearLote motivo)
            => new ResultadoCrearLote { Ok = false, Motivo = motivo };

        /// <summary>snake_case para el JSON de la UI: "ya_existe", "nombre_invalido"…</summary>
        public string MotivoTexto()
        {
            switch (Motivo)
            {
                case MotivoCrearLote.Ok:                   return "ok";
                case MotivoCrearLote.YaExiste:             return "ya_existe";
                case MotivoCrearLote.NombreInvalido:       return "nombre_invalido";
                case MotivoCrearLote.SinDirectorioDeLotes: return "sin_directorio_de_lotes";
                default:                                   return "error";
            }
        }
    }
}
```

- [ ] **Step 2: Cambiar la firma en la interfaz**

En `ILotesService.cs`, reemplazar:

```csharp
        /// <summary>Crea un lote nuevo con <paramref name="name"/> y lo deja abierto.</summary>
        Task<bool> CreateFieldAsync(string name);
```

por:

```csharp
        /// <summary>Crea un lote nuevo con <paramref name="name"/> y lo deja
        /// abierto. El resultado dice POR QUÉ falló: la pantalla de lote
        /// necesita distinguir "ya existe" de "error" para poder avisar.</summary>
        Task<ResultadoCrearLote> CreateFieldAsync(string name);
```

- [ ] **Step 3: Adaptar `EngineLotesService.CreateFieldAsync`**

Reemplazar el método completo por:

```csharp
        public Task<ResultadoCrearLote> CreateFieldAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Task.FromResult(ResultadoCrearLote.Falla(MotivoCrearLote.NombreInvalido));
            string clean = CleanName(name);
            if (string.IsNullOrEmpty(clean))
                return Task.FromResult(ResultadoCrearLote.Falla(MotivoCrearLote.NombreInvalido));

            string root = RegistrySettings.fieldsDirectory;
            if (string.IsNullOrEmpty(root))
                return Task.FromResult(ResultadoCrearLote.Falla(MotivoCrearLote.SinDirectorioDeLotes));
            string dir = Path.Combine(root, clean);
            if (Directory.Exists(dir))
                return Task.FromResult(ResultadoCrearLote.Falla(MotivoCrearLote.YaExiste));

            try
            {
                // Cerrar el lote actual (si hay) antes de crear el nuevo.
                if (_host.IsJobStarted) _host.CloseField();

                Directory.CreateDirectory(dir);

                // Field.txt con el origen del plano local = lat/lon actual del GPS.
                var origin = _host.AppModelField.CurrentLatLon;
                AgOpenGPS.IO.FieldPlaneFiles.Save(dir, DateTime.Now, origin);

                // Abrir el lote recién creado (define plano local, IsJobStarted=true).
                return Task.FromResult(_host.OpenField(clean)
                    ? ResultadoCrearLote.Bien()
                    : ResultadoCrearLote.Falla(MotivoCrearLote.Error));
            }
            catch
            {
                // Limpieza best-effort si quedó a medio crear.
                try { if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir); } catch { }
                return Task.FromResult(ResultadoCrearLote.Falla(MotivoCrearLote.Error));
            }
        }
```

- [ ] **Step 4: Adaptar el controller**

En `LotesController.cs`, reemplazar el método `Create` por:

```csharp
        [Route(HttpVerbs.Post, "/lotes/create")]
        public async Task Create([QueryField] string name)
        {
            if (_lotes == null)
            {
                await WriteJsonAsync(new { ok = false, motivo = "error" });
                return;
            }
            var r = await _lotes.CreateFieldAsync(name);
            await WriteJsonAsync(new { ok = r.Ok, motivo = r.MotivoTexto() });
        }
```

Y actualizar la línea del encabezado del archivo:

```csharp
//   POST /api/lotes/create?name= → { ok: bool, motivo: string }
```

- [ ] **Step 5: Agregar el cartel de aviso al HTML**

En `lote.html`, dentro de `<div class="screen" id="scNew">`, entre el `<input>` y el `<div class="lt-name-actions">`:

```html
          <div id="ltNewMsg" style="display:none;font-size:13px;font-weight:600;
               color:var(--agp-state-bad,#C0504A);padding:2px 0;"></div>
```

- [ ] **Step 6: Que `lote.js` mire el `ok`**

Reemplazar el handler `btnCreate` por:

```js
  // ---- crear nuevo lote ----
  // Mirar el ok NO es opcional: si el lote ya existe el backend no crea nada, y
  // cerrar la ventana igual dejaba al operario creyendo que habia creado su
  // lote cuando en realidad quedaba el que bajo de OrbitX con ese nombre — con
  // el lindero de OrbitX (reporte 2026-09-12).
  var MOTIVOS = {
    ya_existe:               'Ya existe un lote con ese nombre',
    nombre_invalido:         'Ese nombre no se puede usar',
    sin_directorio_de_lotes: 'No esta configurada la carpeta de lotes',
    error:                   'No se pudo crear el lote'
  };

  $('btnCreate').onclick = async function () {
    var msg = $('ltNewMsg');
    msg.style.display = 'none';

    var name = $('inpNewName').value.trim();
    if (!name) {
      msg.textContent = 'Pone un nombre para el lote';
      msg.style.display = 'block';
      return;
    }

    var r = await jpost('/api/lotes/create?name=' + encodeURIComponent(name));
    if (r && r.ok) { closeWin(); return; }

    msg.textContent = MOTIVOS[(r && r.motivo) || 'error'] || MOTIVOS.error;
    msg.style.display = 'block';
  };
```

Y limpiar el cartel al entrar a la pantalla: en el handler de `btnNew`, reemplazar

```js
  $('btnNew').onclick = function () { $('inpNewName').value = ''; show('new'); };
```

por

```js
  $('btnNew').onclick = function () {
    $('inpNewName').value = '';
    $('ltNewMsg').style.display = 'none';
    show('new');
  };
```

- [ ] **Step 7: Arreglar `jpost`, que hoy devuelve mal**

`jpost` tiene un bug de precedencia: `return await r.json ? await r.json() : null;`
evalúa `await r.json` (la función, siempre truthy) y recién ahí llama `r.json()`.
Funciona de casualidad y rompe si el body no es JSON. Reemplazar por:

```js
  async function jpost(url) {
    try {
      var r = await fetch(url, { method: 'POST' });
      return await r.json();
    } catch (e) { return null; }
  }
```

- [ ] **Step 8: Compilar y correr tests**

Run: `dotnet build "SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgroParallel.WebHost.csproj" -c Debug`
Expected: Build succeeded.

Run: `dotnet test "SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Models/ResultadoCrearLote.cs SourceCode/AgroParallel/Core/AgroParallel.Services/Abstractions/ILotesService.cs SourceCode/PilotX.GuidanceEngine/Adapters/EngineLotesService.cs SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/LotesController.cs SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/lote.html SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/lote.js
git commit -m "fix(lotes): crear un lote con nombre repetido avisa en vez de cerrar mudo

CreateFieldAsync devolvia bool y la pantalla de lote ignoraba el resultado: si
el nombre ya existia no se creaba nada, la ventana se cerraba igual y quedaba
abierto el lote que habia bajado de OrbitX con ese nombre. Ahora el resultado
dice el motivo y la pantalla lo muestra sin cerrarse.

De paso, jpost() tenia un bug de precedencia (await r.json ? ...) que funcionaba
de casualidad.

Prueba: FileNew"
```

---

### Task 4: Limpiar antes de cargar al abrir lote

**Files:**
- Modify: `SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Job.cs:70-86`

**Interfaces:**
- Consumes: nada.
- Produces: nada (cambio interno de `OpenField`).

- [ ] **Step 1: Mover los `Clear()` antes de los `Load()`**

En `OpenField`, reemplazar los dos bloques `try` de tracks y boundaries por:

```csharp
            // El Clear va ANTES del Load: estaba después, así que si el Load
            // tiraba (TrackLines.txt / Boundary.txt corrupto) las guías y el
            // lindero del lote ANTERIOR quedaban vivos sobre el lote nuevo.
            // CloseField normalmente ya limpió, pero no hay que depender de eso
            // para no mostrar datos de otro lote.
            Trk.gArr.Clear();
            Trk.idx = -1;
            try
            {
                var tracks = TrackFiles.Load(dir);
                Trk.gArr.AddRange(tracks);
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: TrackLines.txt: " + ex.Message); }

            Bnd.bndList.Clear();
            try
            {
                var boundaries = BoundaryFiles.Load(dir);
                Bnd.bndList.AddRange(boundaries);
                Bnd.BuildTurnLines();
            }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: Boundary.txt: " + ex.Message); }
```

- [ ] **Step 2: Compilar**

Run: `dotnet build "SourceCode/PilotX.GuidanceEngine.Core/PilotX.GuidanceEngine.Core.csproj" -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Job.cs
git commit -m "fix(lotes): limpiar guias y lindero ANTES de cargar los del lote nuevo

Los Clear() estaban despues de los Load(): con un Boundary.txt o TrackLines.txt
corrupto el Load tiraba, el Clear no corria y quedaban vivos el lindero y las
guias del lote anterior sobre el lote recien abierto.

Prueba: Boundary"
```

---

### Task 5: Manual de cabina

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

**Interfaces:**
- Consumes: el comportamiento de las tareas 2 y 3.
- Produces: nada.

Lo dispara la regla del CLAUDE.md del repo: cambia un requisito que el operario
sufre (crear con nombre repetido ahora falla con cartel) y aparece algo nuevo en
la pantalla de lote (lotes con sufijo `(OrbitX)`).

- [ ] **Step 1: Confirmar que la sección sigue donde dice el plan**

Run: `grep -n "<h2>Lote</h2>" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

Expected: una línea (al momento de escribir el plan, la 338). Si se movió, usar
la que devuelva el grep. La ruta de esa sección es
`<b>Menú izquierdo</b> <i>›</i> <b>LOTE</b>` — verificarla en el archivo antes
de escribir, no de memoria.

- [ ] **Step 2: Agregar las dos entradas**

En `ayuda.html`, dentro de `<section id="lote">`, **después** del `<div class="clave">`
de "Borrar pintado" y **antes** del `<p>` que empieza con "Para ver hectáreas":

```html
          <div class="aviso">Si ya tenés un lote con ese nombre, <strong>Nuevo</strong> no lo crea: aparece un cartel que dice <strong>Ya existe un lote con ese nombre</strong> y la pantalla queda abierta para que pongas otro. Nunca se pisa un lote que ya tenías.</div>
          <div class="clave">Un lote dibujado en <strong>OrbitX</strong> que tenga el mismo nombre que uno tuyo baja a la lista como <strong>&lt;nombre&gt; (OrbitX)</strong>. Tu lote <strong>no se toca</strong>: quedan los dos y vos elegís cuál abrir.</div>
```

- [ ] **Step 3: Verificar que el manual no quedó mintiendo en otro lado**

Run: `grep -n "Nuevo\b" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

Revisar que ninguna otra mención de crear lote prometa que siempre funciona. Si
la hay, corregirla en este mismo commit.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html
git commit -m "docs(ayuda): nombre de lote repetido y lotes '(OrbitX)' en la lista"
```

---

## Verificación final (en banco, antes de declarar nada)

Nada de esto se marca `Cierra:` en el tablero hasta correrlo en cabina o banco:

1. Crear en PilotX un lote con el nombre de uno que ya existe → la ventana no se
   cierra y avisa "Ya existe un lote con ese nombre".
2. Dibujar un lote en OrbitX con el nombre de uno que el operario ya tiene →
   baja como `<nombre> (OrbitX)` y el `Boundary.txt` del lote propio queda igual
   (comparar el archivo antes y después).
3. Correr el sync dos veces sin cambiar el lindero en el cloud → no aparece
   ningún duplicado y el `Boundary.txt` no cambia de fecha.
4. Cambiar el lindero en OrbitX y sincronizar → el espejo se actualiza **en su
   misma carpeta**, sin crear otra.
5. Abrir un lote con `Boundary.txt` corrupto a propósito → no aparece el lindero
   del lote anterior.
