# CoreX — Extracción de servicios UI-agnósticos de FormLoop

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extraer la lógica de comunicación (MQTT, NMEA, NTRIP, UDP, Serial) de los partials de FormLoop a clases de servicio UI-agnósticas en `Services/`, dejando FormLoop como host delgado que delega y pinta labels desde el estado de los servicios.

**Architecture:** Extracción mecánica (spec 2026-07-05, decisión 3): la lógica se mueve tal cual; donde hoy toca un label/botón, el servicio expone estado o dispara un evento y FormLoop (suscripto) pinta. Acoplamiento por campos compartidos → acoplamiento por eventos/propiedades. Sin WinForms en `Services/`. La UI vieja sigue funcionando (validación A/B, spec decisión 4) — hoy corre oculta detrás de FormWebShell.

**Tech Stack:** C# / net48, MQTTnet, System.IO.Ports, sockets async. Sin framework de tests en AgIO: la verificación por tarea es `dotnet build` + smoke run contra el dashboard :5181.

**Referencia obligatoria:** el reporte de acoplamiento está resumido en cada tarea con file:line. Los archivos fuente:
- `SourceCode/AgIO/Source/Forms/MQTT.Designer.cs` (332 líneas)
- `SourceCode/AgIO/Source/Forms/NMEA.Designer.cs` (1078)
- `SourceCode/AgIO/Source/Forms/NTRIPComm.Designer.cs` (811)
- `SourceCode/AgIO/Source/Forms/UDP.designer.cs` (472)
- `SourceCode/AgIO/Source/Forms/SerialComm.Designer.cs` (941)
- `SourceCode/AgIO/Source/Forms/FormLoop.cs` (812) — orquestador

**Reglas transversales (aplican a TODAS las tareas):**
1. Los servicios van en `SourceCode/AgIO/Source/Services/`, namespace `AgIO`, sin `using System.Windows.Forms`.
2. `Properties.Settings` se sigue leyendo/escribiendo desde los servicios (misma assembly; extracción mecánica, no rediseño).
3. Donde el código movido hacía `MessageBox.Show`/`TimedMessageBox`: el servicio expone `event Action<string, string> UserNotice;` (título, mensaje) y FormLoop lo suscribe mostrando `TimedMessageBox(3000, titulo, msg)` vía `BeginInvoke`.
4. Donde el código movido escribía labels/botones: el servicio expone el dato como propiedad (o evento) y FormLoop lo pinta en su tick de 1 s (mismo refresco que hoy) o en el handler del evento con `BeginInvoke`.
5. Los `BeginInvoke((MethodInvoker)(ReceiveX))` de callbacks de socket/serial se ELIMINAN dentro del servicio: el procesamiento corre en el thread del callback y el estado compartido se protege con `lock` (riesgo declarado en el spec). Solo FormLoop usa `BeginInvoke` para pintar.
6. Compilar SIEMPRE con: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m` — esperado `0 Errores`.
7. Commits SOLO de archivos propios de la tarea (`git add <paths>`, NUNCA `git add -A` — el working tree tiene archivos ajenos de la rama Codex UI).
8. Los partials viejos (`*.Designer.cs`) quedan compilando: se les vacía la lógica movida dejando delegaciones al servicio, no se borran los archivos.

---

### Task 1: MqttBrokerService (el más aislado — valida el patrón)

**Files:**
- Create: `SourceCode/AgIO/Source/Services/MqttBrokerService.cs`
- Modify: `SourceCode/AgIO/Source/Forms/MQTT.Designer.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs` (leer del servicio)

- [ ] **Step 1: Crear el servicio moviendo campos y lógica**

Mover de `MQTT.Designer.cs` a la nueva clase: los campos `_mqttServer, _mqttRunning, _mqttPort, _mqttClientsConnected, _mqttMessagesTotal, _mqttStartTime, _mqttRecentTopics, _mqttLock` (líneas 21-30) y los métodos `StartMqttBroker()` (33), `StopMqttBroker()` (89), `AddRecentTopic(string)` (129), con esta superficie pública:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgIO
{
    /// <summary>
    /// Broker MQTT embebido (MQTTnet :1883) extraído de MQTT.Designer.cs.
    /// UI-agnóstico: FormLoop pinta btnMQTT/lblMQTTStatus leyendo IsRunning.
    /// </summary>
    public sealed class MqttBrokerService
    {
        public const int Port = 1883;

        public bool IsRunning { get; private set; }
        public int ClientsConnected { get { /* leer bajo lock */ } }
        public long MessagesTotal { get { /* leer bajo lock */ } }
        public DateTime StartTime { get; private set; }

        // Título + mensaje para TimedMessageBox (regla transversal 3).
        public event Action<string, string> UserNotice;

        public async System.Threading.Tasks.Task StartAsync() { /* cuerpo de StartMqttBroker() tal cual */ }
        public async System.Threading.Tasks.Task StopAsync() { /* cuerpo de StopMqttBroker() tal cual */ }

        public List<string> RecentTopics(int max)
        {
            lock (_lock) return _recentTopics.Take(max).ToList();
        }
    }
}
```

Dentro de `StartAsync`, el `TimedMessageBox` de MQTT.Designer.cs:83-84 (puerto en uso) se reemplaza por `UserNotice?.Invoke("MQTT", "...mismo texto...")`. Los handlers de MQTTnet (`ClientConnected`, `InterceptingPublish`) se mueven tal cual, escribiendo a los campos privados bajo `_lock`.

- [ ] **Step 2: MQTT.Designer.cs queda como fachada UI**

El partial conserva: `btnMQTT_Click` (toggle → `_ = mqttService.StartAsync()/StopAsync()`), `btnMQTT_DoubleClick` + `ShowMqttMonitor()` (el form monitor lee `mqttService.RecentTopics(200)`), `UpdateMqttUI()` (líneas 140-160, pinta desde `mqttService.IsRunning/ClientsConnected`), `DoMqttStatus()`. Agregar el campo `public MqttBrokerService mqttService = new MqttBrokerService();` y en FormLoop_Load (donde hoy llama StartMqttBroker, FormLoop.cs:302) suscribir:

```csharp
mqttService.UserNotice += (t, m) => BeginInvoke((MethodInvoker)(() => TimedMessageBox(3000, t, m)));
```

Mantener wrappers `StartMqttBroker()`/`StopMqttBroker()` de una línea para no tocar los call-sites de FormLoop.cs:302/352.

- [ ] **Step 3: CoreXSnapshot lee del servicio**

En `FormLoop.CoreXSnapshot.cs`, reemplazar los campos `_mqtt*` por el servicio:

```csharp
Mqtt = new CoreXMqttDto
{
    Running = mqttService.IsRunning,
    Port = MqttBrokerService.Port,
    Clients = mqttService.ClientsConnected,
    Messages = mqttService.MessagesTotal,
    UptimeSec = mqttService.IsRunning
        ? (long)(DateTime.Now - mqttService.StartTime).TotalSeconds : 0,
    RecentTopics = mqttService.RecentTopics(20),
},
```

- [ ] **Step 4: Build**

Run: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m`
Expected: `0 Errores`.

- [ ] **Step 5: Smoke**

Lanzar `SourceCode/AgIO/Source/bin/Debug/CoreX.exe`, esperar 10 s, verificar:
```bash
curl -s http://127.0.0.1:5181/api/corex/status   # mqtt.running=true, port=1883
netstat -ano | grep ":1883.*LISTEN"              # broker escuchando
```
Expected: `"running":true` y listener en :1883. Toggle web `POST /api/corex/mqtt/toggle` apaga/prende.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgIO/Source/Services/MqttBrokerService.cs SourceCode/AgIO/Source/Forms/MQTT.Designer.cs SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs SourceCode/AgIO/Source/AgIO.csproj
git commit -m "refactor(corex): extraer MqttBrokerService de MQTT.Designer.cs (UI-agnóstico)"
```
(El .csproj solo si el SDK-style requiere el include explícito — con globs no hace falta.)

---

### Task 2: NmeaParserService (parseo puro + GpsState)

**Files:**
- Create: `SourceCode/AgIO/Source/Services/NmeaParserService.cs`
- Modify: `SourceCode/AgIO/Source/Forms/NMEA.Designer.cs`

- [ ] **Step 1: Crear el servicio**

Mover de `NMEA.Designer.cs` TODOS los campos de estado (líneas 9-35: `rawBuffer`, `latitude/longitude`, `headingTrue/speed/altitude/roll/hdopData`, `imu*`, `fixQualityData/satellitesData/...`, `ggaSentence/vtgSentence/...`) y TODOS los métodos de parseo (`ParseNMEA`, `Parse`, `ValidateChecksum`, `ParseGGA`, `ParseVTG`, `ParseHDT`, `ParseAVR`, `ParseKSXT`, `ParseHPD`, `ParseOGI`, `ParsePANDA`, etc. — mover el archivo entero de lógica) a:

```csharp
namespace AgIO
{
    /// <summary>
    /// Parser NMEA extraído de NMEA.Designer.cs. Estado GPS thread-safe:
    /// lo escriben los callbacks de serial/UDP y lo leen NTRIP (GGA) y
    /// el tick de FormLoop (labels + snapshot).
    /// </summary>
    public sealed class NmeaParserService
    {
        private readonly object _lock = new object();
        private string rawBuffer = "";

        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public byte FixQuality { get; private set; }
        public byte Satellites { get; private set; }
        public float HdopData { get; private set; }
        public string GgaSentence { get; private set; }
        // ...resto de propiedades espejo de los campos movidos...

        // Flags que hoy viven en FormLoop y condicionan el parseo:
        public bool IsGpsSentencesOn { get; set; }   // era isGPSSentencesOn
        public bool IsLogNmea { get; set; }          // era isLogNMEA

        // Sentencia NMEA cruda lista para log/monitor (reemplaza los
        // accesos a isLogMonitorOn + logNMEASentence de FormLoop).
        public event Action<string> SentenceLogged;

        /// <summary>Agrega texto crudo al buffer y parsea lo completo.</summary>
        public void Append(string data)
        {
            lock (_lock)
            {
                rawBuffer += data;
                ParseNMEA(ref rawBuffer);   // cuerpo movido tal cual
            }
        }
    }
}
```

Los cuerpos de parseo se mueven tal cual; las escrituras a `latitude = ...` pasan a `Latitude = ...` (dentro del lock de `Append`). Donde el parseo consultaba `isLogMonitorOn`/`isLogNMEA` para armar `logNMEASentence`, disparar `SentenceLogged?.Invoke(sentencia)` y que FormLoop decida si loguea.

- [ ] **Step 2: NMEA.Designer.cs queda como shim**

El partial queda con SOLO esto (para no romper los ~30 call-sites de otros partials mientras duren las Tasks 3-5):

```csharp
public partial class FormLoop
{
    public NmeaParserService nmea = new NmeaParserService();

    // Shims de compatibilidad — se retiran cuando las Tasks 3-5
    // migren los call-sites a leer nmea.* directo.
    public double latitude => nmea.Latitude;
    public double longitude => nmea.Longitude;
    public string ggaSentence => nmea.GgaSentence;
    // ...una propiedad de solo-lectura por cada campo que otros partials leen
    //    (verificar con grep antes de borrar cualquiera)...

    public void ParseNMEA(ref string buffer)
    {
        // Compat: los callers viejos concatenaban a rawBuffer y llamaban acá.
        nmea.Append(buffer);
        buffer = "";
    }
}
```

**Atención:** FormLoop.cs:380-381 y 789-790 escriben `lblCurrentLat.Text`/`lblCurentLon.Text` desde `latitude/longitude` — siguen andando vía los shims. `FormGPSData` lee las sentencias (`ggaSentence` etc.) — verificar con `grep -n "ggaSentence\|vtgSentence\|pandaSentence" SourceCode/AgIO/Source --include=*.cs -r` y cubrir cada uno con shim.

- [ ] **Step 3: Conectar el flag y el log en FormLoop**

En `btnGPSData_Click` (Controls.Designer.cs:96) donde se togglea `isGPSSentencesOn`, agregar `nmea.IsGpsSentencesOn = isGPSSentencesOn;`. En FormLoop_Load suscribir `nmea.SentenceLogged += s => { if (isLogNMEA) /* mismo log que hoy */; };` reproduciendo el comportamiento actual de NMEA.Designer.cs:131-135.

- [ ] **Step 4: Build + smoke**

Build igual que Task 1. Smoke: con GPS activo, `curl -s http://127.0.0.1:5181/api/corex/status` debe mostrar `gps.alive:true` y lat/lon moviéndose (dos curls separados por 5 s con valores distintos).

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgIO/Source/Services/NmeaParserService.cs SourceCode/AgIO/Source/Forms/NMEA.Designer.cs SourceCode/AgIO/Source/Forms/Controls.Designer.cs SourceCode/AgIO/Source/Forms/FormLoop.cs
git commit -m "refactor(corex): extraer NmeaParserService (parseo NMEA UI-agnóstico)"
```

---### Task 3: NtripClientService

**Files:**
- Create: `SourceCode/AgIO/Source/Services/NtripClientService.cs`
- Modify: `SourceCode/AgIO/Source/Forms/NTRIPComm.Designer.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.cs` (tick + labels)
- Modify: `SourceCode/AgIO/Source/Forms/Controls.Designer.cs` (btnStartStopNtrip_Click)
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs`

- [ ] **Step 1: Crear el servicio**

Mover de `NTRIPComm.Designer.cs`: campos 20-57 (`ntripCounter, clientSocket, casterRecBuffer, broadCasterIP/Port, mount, username, password, GGASentence, sendGGAInterval, tripBytes, NTRIP_Watchdog, isNTRIP_*, isRadio_RequiredOn, isSerialPass_RequiredOn, isRunGGAInterval, rList, aList, rawTrip, spRadio`) y métodos `StartNTRIP` (202), `ReconnectRequest` (341), `IncrementNTRIPWatchDog` (356), `SendAuthorization` (369), `OnAddMessage` (422), `SendNTRIP` (534), `SendGGA` (549), `NTRIPtick` (578), `OnConnect` (583), `OnRecievedData` (597), `ShutDownNTRIP` (663), `SettingsShutDownNTRIP` (691), `BuildGGA` (735), y la parte NO-UI de `DoNTRIPSecondRoutine` (60) y `ConfigureNTRIP` (156).

Superficie pública:

```csharp
namespace AgIO
{
    /// <summary>
    /// Cliente NTRIP (+radio/serial-pass) extraído de NTRIPComm.Designer.cs.
    /// GGA sale de NmeaParserService (inyectado); el RTCM se entrega por
    /// evento y FormLoop lo rutea a serial/UDP como hoy.
    /// </summary>
    public sealed class NtripClientService
    {
        private readonly NmeaParserService _nmea;   // para BuildGGA (lat/lon)

        public NtripClientService(NmeaParserService nmea) { _nmea = nmea; }

        // Estado (era isNTRIP_* / tripBytes / ntripCounter / broadCasterIP):
        public bool RequiredOn { get; set; }
        public bool Connected { get; private set; }
        public bool Connecting { get; private set; }
        public bool Sending { get; private set; }
        public bool RadioRequiredOn { get; set; }
        public bool SerialPassRequiredOn { get; set; }
        public long TripBytes { get; set; }          // set: lblNTRIPBytes_Click lo resetea
        public int Counter { get; private set; }     // era ntripCounter
        public string CasterIp { get; private set; } // era broadCasterIP
        public string WatchText { get; private set; }// era lblWatch.Text ("Waiting"/"Listening NTRIP"/...)
        public string ButtonCountdownText { get; private set; } // era btnStartStopNtrip.Text
        public string MessagesText { get; private set; }        // era lblMessages.Text (UI avanzada)

        public event Action<string, string> UserNotice;
        // RTCM listo para rutear (reemplaza las llamadas directas a
        // SendRTCMPort/SendUDPMessage desde SendNTRIP):
        public event Action<byte[], int> RtcmForSerial;
        public event Action<byte[], int> RtcmForUdp;

        public void Configure() { /* parte no-UI de ConfigureNTRIP: leer settings */ }
        public void SecondRoutine() { /* parte no-UI de DoNTRIPSecondRoutine, escribe WatchText/ButtonCountdownText en vez de labels */ }
        public void Start() { /* StartNTRIP */ }
        public void Shutdown() { /* ShutDownNTRIP */ }
        public void SettingsShutdown() { /* SettingsShutDownNTRIP */ }
    }
}
```

Reemplazos concretos dentro del código movido:
- `lblWatch.Text = "..."` (líneas 114-139) → `WatchText = "..."`.
- `btnStartStopNtrip.Text = ...` (107-109, 150-152) → `ButtonCountdownText = ...`.
- `lblNTRIPBytes.Text`, `lblMessages.Text`, `lblNTRIP_IP.Text`, `lblMount.Text` → propiedades string equivalentes (FormLoop pinta en su tick).
- `this.latitude / this.longitude` en `BuildGGA` (747) → `_nmea.Latitude / _nmea.Longitude`.
- En `SendNTRIP` (534): `if (isSendToSerial) SendRTCMPort(...)` → `RtcmForSerial?.Invoke(...)`; `if (isSendToUDP) SendUDPMessage(..., epNtrip)` → `RtcmForUdp?.Invoke(...)`. Los flags `isSendToSerial/isSendToUDP` se leen de Properties.Settings dentro del servicio (mismo origen que hoy).
- El `MessageBox` de Controls.Designer.cs:87 (NTRIP no configurado) NO se mueve: es del handler UI.

- [ ] **Step 2: NTRIPComm.Designer.cs queda como fachada de pintado**

Conservar en el partial: `DoNTRIPSecondRoutine()` reducido a `ntrip.SecondRoutine();` + el bloque de pintado (lee `ntrip.WatchText/ButtonCountdownText/TripBytes` y escribe los labels — mismas condiciones `isViewAdvanced` que hoy), y `ConfigureNTRIP()` reducido a `ntrip.Configure();` + visibilidad de botones (líneas 180-196). Campo nuevo: `public NtripClientService ntrip;` — se instancia en FormLoop_Load ANTES de `ConfigureNTRIP()` (FormLoop.cs:190): `ntrip = new NtripClientService(nmea);` + suscripciones:

```csharp
ntrip.UserNotice += (t, m) => BeginInvoke((MethodInvoker)(() => TimedMessageBox(3000, t, m)));
ntrip.RtcmForSerial += (d, len) => SendRTCMPort(d, len);
ntrip.RtcmForUdp += (d, len) => SendUDPMessage(d, epNtrip);   // firma real: verificar en UDP.designer.cs:271
```

Shims de compatibilidad para los lectores externos (grep obligatorio: `isNTRIP_RequiredOn|isNTRIP_Connected|tripBytes|broadCasterIP|ntripCounter` — hay usos en FormLoop.cs:101,147,223,385,442,451-456 y CoreXSnapshot):

```csharp
public bool isNTRIP_RequiredOn { get => ntrip.RequiredOn; set => ntrip.RequiredOn = value; }
public bool isNTRIP_Connected => ntrip.Connected;
// ...etc...
```

- [ ] **Step 3: CoreXSnapshot lee del servicio**

`FormLoop.CoreXSnapshot.cs`: `Ntrip = new CoreXNtripDto { RequiredOn = ntrip.RequiredOn, Connected = ntrip.Connected, Connecting = ntrip.Connecting, KbTotal = ntrip.TripBytes >> 10, CasterIp = ntrip.CasterIp ?? "" }`.

- [ ] **Step 4: Build + smoke**

Build igual. Smoke: dashboard muestra `ntrip.required_on` acorde a settings; `POST /api/corex/ntrip/toggle` dos veces (A/B) cambia `required_on` en el status. Si hay caster configurado, verificar `connected:true` y `kb_total` creciendo.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgIO/Source/Services/NtripClientService.cs SourceCode/AgIO/Source/Forms/NTRIPComm.Designer.cs SourceCode/AgIO/Source/Forms/FormLoop.cs SourceCode/AgIO/Source/Forms/Controls.Designer.cs SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs
git commit -m "refactor(corex): extraer NtripClientService (cliente NTRIP UI-agnóstico)"
```

---

### Task 4: UdpNetworkService (loopback + broadcast + traffic/scanReply)

**Files:**
- Create: `SourceCode/AgIO/Source/Services/UdpNetworkService.cs`
- Modify: `SourceCode/AgIO/Source/Forms/UDP.designer.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.cs`

- [ ] **Step 1: Crear el servicio**

Mover de `UDP.designer.cs`: las clases `CTraffic` y `CScanReply` (van como top-level en el archivo del servicio, mismo namespace), los campos 39-78 (`loopBackSocket, UDPSocket, endPoints, epAgOpen/epModule/epModuleSet/epNtrip, traffic, scanReply, buffer, helloFromAgIO, ipCurrent`) y los métodos `LoadUDPNetwork` (80), `LoadLoopback` (135), `SendToLoopBackMessageAOG` (156), `SendDataToLoopBack` (161), `ReceiveFromLoopBack` (192), `ReceiveDataLoopAsync` (246), `SendUDPMessage` (271), `ReceiveDataUDPAsync` (321), `ReceiveFromUDP` (343).

Superficie pública:

```csharp
namespace AgIO
{
    public sealed class UdpNetworkService
    {
        private readonly NmeaParserService _nmea;

        public UdpNetworkService(NmeaParserService nmea) { _nmea = nmea; }

        public CTraffic Traffic { get; } = new CTraffic();
        public CScanReply ScanReply { get; } = new CScanReply();
        public string IpCurrent { get; private set; }
        public bool UdpUp { get; private set; }   // era btnUDP verde/rojo

        public event Action<string, string> UserNotice;
        // PGNs que hoy van a serial (ReceiveFromLoopBack, UDP:192):
        public event Action<byte[], int> PgnForSteerSerial;
        public event Action<byte[], int> PgnForMachineSerial;
        // Telemetría UI avanzada (PGN 126/123 — era lblPing/lblSteerAngle/...):
        public event Action<SteerTelemetry> SteerTelemetryReceived;
        public event Action<MachineTelemetry> MachineTelemetryReceived;

        public void LoadUdpNetwork() { }
        public void LoadLoopback() { }
        public void SendToLoopBackMessageAOG(byte[] data) { }
        public void SendUdpMessage(byte[] data, System.Net.IPEndPoint ep) { }
        public void SendToModules(byte[] data) { }   // SendUdpMessage(data, epModule)
        public void SendToNtripEp(byte[] data) { }   // SendUdpMessage(data, epNtrip)
        public void Shutdown() { }                   // Shutdown+Close de ambos sockets (hoy FormClosing:327-343)
    }

    public sealed class SteerTelemetry
    { public int PingMs; public double SteerAngle; public int WasCounts; public int SwitchStatus; public int WorkSwitch; }
    public sealed class MachineTelemetry
    { public int PingMs; public byte Sw1To8; public byte Sw9To16; }
}
```

Reemplazos dentro del código movido:
- Los dos `BeginInvoke` (UDP:260, 335) se eliminan: `ReceiveFromLoopBack`/`ReceiveFromUDP` corren en el thread del callback (regla 5).
- UDP:359-365 (labels steer) → armar `SteerTelemetry` y `SteerTelemetryReceived?.Invoke(t)`. Ídem 376-378 con `MachineTelemetry`.
- Rama NMEA de `ReceiveFromUDP` (UDP:452-455: `rawBuffer += ...; ParseNMEA(...)`) → `_nmea.Append(asciiString)`.
- Llamadas a `SendSteerModulePort/SendMachineModulePort` dentro de `ReceiveFromLoopBack` → `PgnForSteerSerial?.Invoke(...)` / `PgnForMachineSerial?.Invoke(...)`.
- `lblIP.Text` (84, 92) → `IpCurrent = ...`; `btnUDP.BackColor` (114, 130-131) → `UdpUp = true/false`.
- MessageBox de UDP:128-129 y 150 → `UserNotice`.
- Los flags de logging (`isUDPMonitorOn`, `isNTRIPLogOn`, `isGPSLogOn`, UDP:275-285) siguen siendo campos de FormLoop: exponer en el servicio `public Func<bool> IsUdpMonitorOn = () => false;` (y equivalentes) que FormLoop setea en Load con `() => isUDPMonitorOn` — mecánico y sin reddiseñar el logging.

- [ ] **Step 2: UDP.designer.cs queda como fachada**

Campo `public UdpNetworkService udp;` (instanciar en FormLoop_Load ANTES de LoadUDPNetwork/LoadLoopback, FormLoop.cs:89-128: `udp = new UdpNetworkService(nmea);`). Shims para lectores externos (grep `traffic\.|scanReply\.|SendToLoopBackMessageAOG|SendUDPMessage|epModule|epNtrip` — hay usos en SerialComm 81,300,508,793, FormLoop 481, NTRIP fachada, CoreXSnapshot):

```csharp
public CTraffic traffic => udp.Traffic;
public CScanReply scanReply => udp.ScanReply;
public void SendToLoopBackMessageAOG(byte[] d) => udp.SendToLoopBackMessageAOG(d);
// SendUDPMessage(data, ep) → udp.SendUdpMessage(data, ep); epModule/epNtrip
// quedan expuestos por el servicio o via métodos SendToModules/SendToNtripEp.
```

Suscripciones en FormLoop_Load:

```csharp
udp.UserNotice += (t, m) => BeginInvoke((MethodInvoker)(() => TimedMessageBox(3000, t, m)));
udp.PgnForSteerSerial += (d, len) => SendSteerModulePort(d, len);
udp.PgnForMachineSerial += (d, len) => SendMachineModulePort(d, len);
udp.SteerTelemetryReceived += t => { if (isViewAdvanced) BeginInvoke((MethodInvoker)(() => {
    lblPing.Text = t.PingMs.ToString(); /* ...resto igual que UDP:359-365... */ })); };
udp.MachineTelemetryReceived += t => { if (isViewAdvanced) BeginInvoke((MethodInvoker)(() => {
    lblPingMachine.Text = t.PingMs.ToString(); /* ...UDP:376-378... */ })); };
```

FormClosing (FormLoop.cs:327-343): reemplazar el shutdown manual de sockets por `udp.Shutdown();`. El tick que pinta `lblIP`/`btnUDP` lee `udp.IpCurrent`/`udp.UdpUp`.

- [ ] **Step 3: Build + smoke**

Build igual. Smoke crítico (es el camino de datos AOG↔módulos): con PilotX corriendo, verificar en el dashboard que `modules.*_hello` siguen true y que los nodos MQTT siguen anunciándose; en PilotX el GPS sigue entrando (mapa se mueve).

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgIO/Source/Services/UdpNetworkService.cs SourceCode/AgIO/Source/Forms/UDP.designer.cs SourceCode/AgIO/Source/Forms/FormLoop.cs
git commit -m "refactor(corex): extraer UdpNetworkService (loopback+broadcast UI-agnóstico)"
```

---

### Task 5: SerialComService (6 puertos)

**Files:**
- Create: `SourceCode/AgIO/Source/Services/SerialComService.cs`
- Modify: `SourceCode/AgIO/Source/Forms/SerialComm.Designer.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.cs`

- [ ] **Step 1: Crear el servicio**

Mover de `SerialComm.Designer.cs` los 6 juegos de campos (`portNameX, baudRateX, spX` — quitar `static` al mover) y por cada puerto los métodos `OpenXPort/CloseXPort/SendXPort/sp_DataReceivedX` (GPS:735-886, Steer:304-479, Machine:503-691, IMU/RTCM/GPS2: análogos). Los `ReceiveXPort` se integran al final de cada `sp_DataReceivedX` (sin BeginInvoke, regla 5).

```csharp
namespace AgIO
{
    public sealed class SerialComService
    {
        private readonly NmeaParserService _nmea;
        private readonly UdpNetworkService _udp;

        public SerialComService(NmeaParserService nmea, UdpNetworkService udp)
        { _nmea = nmea; _udp = udp; }

        public event Action<string, string> UserNotice;
        // Nombre de puerto por canal para los labels (era lblGPS1Comm etc.);
        // null/"-" = cerrado. FormLoop pinta en su tick.
        public string GpsPortLabel { get; private set; }
        public string Gps2PortLabel { get; private set; }
        public string RtcmPortLabel { get; private set; }
        public string ImuPortLabel { get; private set; }
        public string SteerPortLabel { get; private set; }
        public string MachinePortLabel { get; private set; }

        public bool IsGpsOpen { get; }   // spGPS?.IsOpen — ídem resto

        public void OpenGpsPort(string portName, int baud) { }
        public void CloseGpsPort() { }
        public void SendSteerPort(byte[] data, int len) { }
        public void SendMachinePort(byte[] data, int len) { }
        public void SendRtcmPort(byte[] data, int len) { }
        public void SendImuPort(byte[] data, int len) { }
        // ...Open/Close para los 6 canales, mismas firmas que los actuales...
        public void CloseAll() { }
    }
}
```

Reemplazos dentro del código movido:
- `SendToLoopBackMessageAOG(...)` → `_udp.SendToLoopBackMessageAOG(...)`.
- `traffic.helloFromIMU = 0` (81) / `helloFromAutoSteer = 0` (300) / `helloFromMachine = 0` (508) / `cntrGPSOut +=` (793) → `_udp.Traffic.helloFromIMU = 0` etc.
- Parseo NMEA del GPS (`ReceiveGPSPort`, 788: `rawBuffer += s; ParseNMEA(...)`) → `_nmea.Append(s)`.
- `lbl*Comm.Text = ...` (138, 180, 361, 385, 573, 597, 769, 783) → escribir la propiedad `*PortLabel`.
- `MessageBox.Show` (118, 160, 344, 556) → `UserNotice`.
- Escrituras a `Properties.Settings.Default.setPort_*` (121, 135, 347, 559, 163, 765-767): quedan tal cual dentro del servicio (regla 2), pero el `Save()` lo sigue haciendo quien lo hacía.

- [ ] **Step 2: SerialComm.Designer.cs queda como fachada**

Campo `public SerialComService serial;` (instanciar en FormLoop_Load ANTES del bloque de apertura de puertos, FormLoop.cs:129: `serial = new SerialComService(nmea, udp);` + `serial.UserNotice += ...`). Shims para call-sites externos (grep `SendSteerModulePort|SendMachineModulePort|SendRTCMPort|OpenGPSPort|CloseGPSPort|spGPS|portNameGPS` — hay usos en FormLoop_Load 129-204, FormCommSetGPS, FormSerialPass, TenSecondLoop:500):

```csharp
public void SendSteerModulePort(byte[] d, int len) => serial.SendSteerPort(d, len);
public void SendMachineModulePort(byte[] d, int len) => serial.SendMachinePort(d, len);
public void SendRTCMPort(byte[] d, int len) => serial.SendRtcmPort(d, len);
// Open/Close por canal → delegaciones 1:1.
```

El tick de 1 s (o `TenSecondLoop`, donde se pinte hoy) copia `serial.*PortLabel` a los labels. **Ojo con `FormCommSetGPS`/`FormSerialPass`/`FormUDPMonitor`**: acceden a campos `static` (`portNameGPS`, `spGPS`...) — grep primero; si acceden directo, exponer shims con el MISMO nombre y firma en la fachada para no tocar esos forms en esta tarea.

- [ ] **Step 3: Build + smoke**

Build igual. Smoke: si hay hardware serial conectado, verificar apertura al arrancar (labels de puerto en UI vieja mostrando el COM — `ShowLegacyUi` temporal o log). Sin hardware: verificar que arranca sin excepción y el dashboard sigue vivo, y que `wasGPSConnectedLastRun=false` no intenta abrir.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/AgIO/Source/Services/SerialComService.cs SourceCode/AgIO/Source/Forms/SerialComm.Designer.cs SourceCode/AgIO/Source/Forms/FormLoop.cs
git commit -m "refactor(corex): extraer SerialComService (6 puertos UI-agnósticos)"
```

---

### Task 6: FormLoop delgado + snapshot 100% servicios + validación integral

**Files:**
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs`

- [ ] **Step 1: Ordenar el arranque/parada en FormLoop**

FormLoop_Load queda con este orden explícito (constructores primero, todos los servicios existen antes de cualquier Open/Start):

```csharp
// 1. Servicios (sin efectos)
nmea = new NmeaParserService();
udp = new UdpNetworkService(nmea);
serial = new SerialComService(nmea, udp);
ntrip = new NtripClientService(nmea);
mqttService = new MqttBrokerService();
// 2. Suscripciones UserNotice / eventos (todas juntas acá)
// 3. Efectos, mismo orden que hoy: LoadUDPNetwork → loopback → puertos
//    serial → ConfigureNTRIP → timer → broker MQTT → web host → FormWebShell
```

FormClosing en orden inverso: `StopMqttBroker() → udp.Shutdown() → serial.CloseAll() → ntrip.Shutdown() → corexWebHost.Dispose()` (conservando la persistencia de settings de 317-321).

- [ ] **Step 2: UpdateCoreXSnapshot lee SOLO de servicios**

Verificar que `FormLoop.CoreXSnapshot.cs` ya no toca ningún campo de los partials viejos: `Gps` desde `nmea.*` + `lastHelloGPS`, `Ntrip` desde `ntrip.*`, `Mqtt` desde `mqttService.*`, `Modules` desde `isConnected*` + `udp.Traffic.*`. Si algún shim quedó sin uso tras las Tasks 2-5 (verificar con grep), borrarlo.

- [ ] **Step 3: Build Release + Build/ + validación integral**

```bash
taskkill //IM PilotX.exe //F 2>/dev/null; taskkill //IM CoreX.exe //F 2>/dev/null; taskkill //IM msedgewebview2.exe //F 2>/dev/null
powershell -ExecutionPolicy Bypass -File build.ps1
cd Build && powershell -Command "Start-Process .\CoreX.exe -WorkingDirectory ." && powershell -Command "Start-Process .\PilotX.exe -WorkingDirectory ."
```

Checklist (criterio de éxito del spec — TODO debe dar PASS):
1. `curl http://127.0.0.1:5181/api/corex/status` → 200, `ok:true`.
2. `gps.alive:true`, lat/lon cambiando entre dos curls (5 s).
3. `mqtt.running:true` + `netstat :1883` LISTEN + `mqtt.clients` > 0 con nodos vivos.
4. `mqtt.recent_topics` con tráfico `agp/*`.
5. Toggle MQTT A/B por web (`POST /api/corex/mqtt/toggle` x2) → running false→true.
6. Toggle NTRIP A/B por web → `ntrip.required_on` cambia.
7. `modules.*_hello` coherentes con los nodos conectados.
8. PilotX recibe GPS (loopback AOG vivo): mapa se mueve.
9. Cerrar la ventana web → UI vieja reaparece (escape) → reabrir no rompe nada.

- [ ] **Step 4: Commit final**

```bash
git add SourceCode/AgIO/Source/Forms/FormLoop.cs SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs
git commit -m "refactor(corex): FormLoop como host delgado — arranque/parada ordenado sobre los 5 servicios"
```

---

## Notas para el ejecutor

- **Grep antes de borrar**: cada campo/método que se mueve puede tener lectores en forms secundarios (FormNtrip, FormUDP, FormCommSetGPS, FormSerialPass, FormGPSData, FormUDPMonitor, FormSerialMonitor). La regla es: shim con el mismo nombre en la fachada > tocar el form secundario.
- **`static` en SerialComm**: los campos de puerto son `static` hoy; al moverlos al servicio dejan de serlo. Si algún form secundario los usa como `FormLoop.portNameGPS`, el shim debe ser `static` también o el form se ajusta (decidir por grep, preferir shim).
- **No tocar**: los PGN, checksums, tamaños de buffer, puertos, intervalos. Extracción mecánica.
- **La UI vieja debe seguir funcional** al final de cada tarea (corre oculta; `ShowLegacyUi` la trae para inspección A/B).
