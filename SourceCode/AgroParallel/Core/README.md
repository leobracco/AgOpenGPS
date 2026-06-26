# AgroParallel.Core — Contrato público

Núcleo agnóstico de UI de la plataforma AgroParallel.
Implementa los servicios de los productos X-* (QuantiX, VistaX, SectionX,
OrbitX, Cámaras, etc.) **sin depender** de WinForms, del proyecto GPS upstream, ni de
ningún host concreto.

Este README es el contrato. Mientras se respete, podemos hostear estos
servicios desde **cualquier shell**:

- Hoy: `PilotX.exe` (net48 + WinForms) → adapters en `SourceCode/GPS/AgroParallel/Common/FormGps*.cs`
- Mañana: shell Avalonia (`AgroParallel.Shell.Avalonia`, net10 + Avalonia 11) → adapters propios
- Futuro: cualquier headless / test runner / CLI

> **Regla de oro:** ningún archivo bajo `AgroParallel.Core/**` puede tener
> `using AgOpenGPS;` ni `using System.Windows.Forms;`. Si necesitás algo
> del shell GPS, agregalo como método nuevo a una abstracción y que el shell
> implemente el método en su adapter.

---

## Targets

| Proyecto                   | TFM             | Por qué                                    |
|----------------------------|-----------------|---------------------------------------------|
| `AgroParallel.Models`      | netstandard2.0  | Interop con net48 (PilotX) + net8/10 (Avalonia)|
| `AgroParallel.Services`    | netstandard2.0  | Idem                                        |

Dependencias externas:
- `MQTTnet 4.3.x` (servicio MQTT embebido)
- `System.Text.Json 6.0.x` (DTOs serializables)

---

## Estructura

```
AgroParallel/Core/
├── AgroParallel.Models/         POCOs y DTOs (sin lógica)
│   ├── AogStateSnapshot.cs      Snapshot del shell GPS que consume el resto
│   ├── QuantiXDtos.cs           Config + runtime de motores
│   ├── VistaXDtos.cs            Sensores de siembra
│   ├── SectionXConfigDto.cs     Config de secciones
│   ├── OrbitXConfigDto.cs       Cloud config
│   ├── CamaraDto.cs             Hikvision ISAPI snapshot
│   ├── LoteDto.cs               Campos PilotX
│   ├── VehicleToolDtos.cs       Config vehículo/implemento
│   ├── ShapefileUploadDto.cs    Prescripción .shp/.dbf/.shx
│   ├── PilotCoreDtos.cs         Cobertura/sections runtime
│   ├── PilotXUpdateDtos.cs      OTA del propio PilotX
│   ├── DebugDtos.cs             Log estructurado
│   ├── NodoStatus.cs            Estado MQTT live de un nodo
│   ├── NodoDiagDto.cs           Diagnóstico de nodos
│   ├── MotorLive.cs             Telemetría motor (RPM/PWM/PID)
│   └── AutoTuneResult.cs        Resultado auto-tune PID
│
└── AgroParallel.Services/       Lógica + abstracciones
    ├── Abstractions/            Interfaces que el shell implementa
    └── *.cs                     Implementaciones puras (no piden host)
```

---

## El contrato

### Provided BY the shell (adapters que el host implementa)

Estas son las puertas que el host (PilotX hoy, Avalonia mañana) tiene que
implementar para que el resto del Core funcione. Hay **15 adapters**
escritos hoy en `SourceCode/GPS/AgroParallel/Common/FormGps*.cs`.

| Interfaz                       | Responsabilidad                                            |
|--------------------------------|------------------------------------------------------------|
| `IAogStateProvider`            | Snapshot tipado del estado del shell GPS (pose, sections, shape) |
| `IAogCommandSink`              | Comandos hacia el shell GPS (start job, set autosteer, etc.) |
| `ILotesService`                | Listar/abrir/cerrar/crear lotes (campos)                  |
| `IVehicleToolService`          | Config dura vehículo + implemento                          |
| `IShapefileService`            | Upload + activación de prescripción                        |
| `ICoverageService`             | Persistencia de cobertura entre samples                    |
| `ISectionControlService`       | Estado runtime de secciones                                |
| `IQuantiXRuntimeService`       | Telemetría runtime motores QuantiX                         |
| `IGuidanceCalculator`          | AB-line, U-turn, headland (delegado al shell GPS nativo)   |
| `IPilotXUpdateService`         | OTA del propio PilotX (ZIP → Updater.exe)                  |

> El motivo de delegar guiado y cobertura al shell vs implementarlo
> nativamente en Core: la matemática de cobertura/guiado vive en el shell GPS
> hace años, es matura y específica de GL/GeoLib. Reusarla vía adapter
> evita re-implementar un kernel completo.

### Provided BY the Core (implementaciones puras, sin host)

Estas las trae el propio Core y el shell solo las instancia. **No** piden
adapter, son self-contained:

| Servicio                       | Qué hace                                                     |
|--------------------------------|--------------------------------------------------------------|
| `NodoRegistryService`          | Auto-descubrimiento MQTT de nodos (announcement/status_live) |
| `QuantiXConfigService`         | Persistencia + sync MQTT de config motores QuantiX            |
| `VistaXConfigService`          | Persistencia de config de hileras VistaX                      |
| `VistaXLiveService`            | Agregador de status_live de sensores VistaX                   |
| `SectionXConfigService`        | Persistencia de config nodos SectionX                          |
| `CamarasConfigService`         | Persistencia de config Hikvision ISAPI                         |
| `OrbitXConfigService`          | Persistencia de config cloud (URL/token)                       |
| `DebugLogService`              | Ring buffer de log estructurado para `/debug`                  |
| `SistemaService`               | Info del sistema (versión PilotX, brillo, etc. via adapter)    |

Algunos servicios usan **otros adapters** del shell para tareas
específicas (`SistemaService` consulta brillo via `IBrightnessAdapter`,
no incluido en esta tabla pero descrito en el código).

### Interfaces nominadas pero sin implementación en este repo

Heredadas del primer scaffolding; existen como contrato a futuro pero
no se enchufan hoy:

- `IQuantiXService`, `ISectionXService`, `IOrbitXSyncService`, `IMqttService`

Se mantienen porque pueden mover lógica hoy duplicada en `OrbitXSync.cs`
y `MqttBroker` del proyecto GPS al Core sin breaking changes.

---

## Cómo un shell nuevo enchufa el Core

Patrón mostrado por el shell actual (`FormGPS.cs:1968`):

```csharp
// 1. Implementar los adapters específicos del host
var state         = new MyShellAogStateProvider(this);
var lotes         = new MyShellLotesService(this);
var vehicleTool   = new MyShellVehicleToolService(this);
var shapefile     = new MyShellShapefileService(this);
var coverage      = new MyShellCoverageService(this);
var sectionsCore  = new MyShellSectionControlService(this);
var quantixRT     = new MyShellQuantiXRuntimeService(this, state);
var guidance      = new MyShellGuidanceCalculator(this);
var pilotxUpdate  = new MyShellPilotXUpdateService();

// 2. Instanciar servicios puros del Core
var nodos         = new NodoRegistryService();
var quantixCfg    = new QuantiXConfigService(nodos);
var vistaxCfg     = new VistaXConfigService();
var vistaxLive    = new VistaXLiveService(nodos, vistaxCfg);
// ... etc.

// 3. Levantar el WebHost (EmbedIO) con todas las dependencias
var host = new AgpWebHost(
    state, sistema, nodos,
    orbitxCfg, sectionxCfg, camarasCfg, quantixCfg, vistaxCfg, vistaxLive,
    debug, lotes, vehicleTool, shapefile, coverage, sectionsCore,
    quantixRT, guidance, pilotxUpdate,
    wwwroot, port);
host.Start();

// 4. Apuntar un WebView (WinForms WebView2 / Avalonia.WebView / …) a http://127.0.0.1:5180/
```

`AgpWebHost` vive en `AgroParallel.WebHost` (netstandard2.0). Es portátil
también. El único proyecto que **no** lo es son los adapters concretos
(viven en `SourceCode/GPS/AgroParallel/Common/` y son net48 por tocar
FormGPS).

---

## Decisiones que NO cambian aunque migremos el shell

- **MQTT vive en CoreX**, no en el shell. Cualquier host que reuse este
  Core asume que ya hay un broker MQTT en `tcp://127.0.0.1:1883`.
  `NodoRegistryService.Start(broker, port)` se le pasa esa IP.
- **Cloud (OrbitX) habla por HTTPS**, no por MQTT. El sync vive en el
  shell hoy (`OrbitXSync.cs` en proyecto GPS). Para portarlo, mover a
  `AgroParallel.Services` (es código sin Forms).
- **Archivos del shell GPS (lotes/boundary/recpath)** son convención del
  shell GPS upstream, no del Core. Los servicios `ILotesService` y
  `IShapefileService` esconden esa convención detrás de DTOs.

---

## Reglas para evitar lock-in

1. **Cualquier `using AgOpenGPS;` en `AgroParallel/Core/**`** es un bug.
   Si CI llegara a existir, debería fallar en este chequeo.
2. **No agregar `<PackageReference Include="System.Windows.Forms" />`**
   ni similar a los .csproj del Core. La validación es: ambos proyectos
   deben compilar standalone con `dotnet build` desde un terminal con
   solo el SDK .NET (sin VS WinForms designer).
3. **Cambios de contrato (interfaces)** son breaking para todos los
   shells. Antes de tocarlos, considerar: ¿alcanza con agregar un método
   nuevo opcional?
4. **DTOs serializables**: todo lo que cruza la frontera HTTP/MQTT debe
   ser POCO + `[JsonPropertyName]` con casing snake/lowercase. EmbedIO
   trae Swan por defecto que serializa PascalCase — el host debe usar
   `System.Text.Json` manualmente. Ver `CamarasController.cs` como
   ejemplo del workaround.

---

## Verificación rápida

```bash
# Desde la raíz del repo:
dotnet build SourceCode/AgroParallel/Core/AgroParallel.Models/AgroParallel.Models.csproj
dotnet build SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj

# Si compila standalone, el contrato está sano. Cualquier shell puede
# tomar estos .dll y construir su propio AgpWebHost+UI encima.
```
