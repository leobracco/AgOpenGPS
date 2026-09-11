# CoreX — Páginas web de configuración (Fase 3 del spec)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replicar como páginas web los diálogos de configuración WinForms de CoreX (puertos serie, NTRIP, red UDP, módulos on/off), con endpoints REST que reusan la lógica existente de FormLoop. Con esto la UI vieja (hoy oculta) deja de ser necesaria para configurar.

**Architecture:** Mismo patrón que el dashboard: páginas estáticas en `wwwroot-corex/pages/`, un controller nuevo `CoreXConfigController` en `/api/corex/config/*`, y puentes en FormLoop que ejecutan en el hilo UI (`BeginInvoke` + `TaskCompletionSource` cuando el endpoint necesita el resultado). La semántica de cada guardado replica 1:1 el form WinForms equivalente, incluido cuándo se reinicia CoreX (`Program.Restart()`).

**Tech Stack:** C# net48, EmbedIO 3.5.2, AgpControllerBase (`ReadJsonBodyAsync<T>`/`WriteJsonAsync` — wire snake_case vía AgpJson), design system PilotX ya presente en `wwwroot-corex/` (theme.css, layout.css, js/modal.js, js/keyboard.js).

**Semántica de guardado (extraída de los forms — NO inventar otra):**

| Área | Form origen | Aplica | Reinicia |
|---|---|---|---|
| Puertos serie | FormCommSetGPS | Open/Close por canal aplica y persiste en caliente (`OpenXPort` ya guarda `setPort_*` + `Save()`) | Nunca |
| NTRIP | FormNtrip (btnSerialOK_Click:214-269) | Guarda 17 claves + `ConfigureNTRIP()` | SÍ si cambió `is_on` o `send_to_serial`↔`send_to_udp` |
| Red UDP on/off | FormEthernet:49-70 / FormUDP btnUDPOff:330-343 | Guarda `setUDP_isOn` | SIEMPRE |
| Subnet módulos | FormUDP btnSendSubnet:217-290 | Broadcast UDP a módulos + `epModule` en caliente + `Save()` | Nunca |
| Módulos IMU/Steer/Machine | Controls.Designer.cs:201-218 + SetModulesOnOff (FormLoop.cs:722) | En caliente, persiste solo | Nunca |

**Fuera de alcance (declarado):** FormRadio (canales radio — poco uso, queda en la UI vieja), FormSerialPass, ISOBUS. El teclado virtual: los inputs usan `js/keyboard.js` ya copiado (operario táctil — regla del proyecto: nunca osk.exe).

**Reglas transversales:**
1. Build: `taskkill //IM CoreX.exe //F 2>/dev/null; dotnet build "SourceCode/AgIO/Source/AgIO.csproj" -c Debug -v m` → `0 Errores`.
2. Commits solo de archivos propios (nunca `git add -A`).
3. Wire snake_case: DTOs C# en PascalCase; verificar contra un GET real con curl (la deserialización de AgpJson ya la usan ImplementoController y FirmwaresController — copiar ese patrón si hay dudas).
4. En las páginas: valores con `textContent`/`value` (nunca innerHTML con datos), botones grandes táctiles, sin atajos de teclado.
5. Todas las páginas linkean de vuelta al dashboard (`/index.html`) y el index las linkea a ellas.

---

### Task 1: Puente UI-thread + página y endpoints de puertos serie

**Files:**
- Create: `SourceCode/AgIO/Source/Web/CoreXConfigController.cs`
- Create: `SourceCode/AgIO/Source/wwwroot-corex/pages/serial.html`
- Create: `SourceCode/AgIO/Source/wwwroot-corex/js/serial.js`
- Modify: `SourceCode/AgIO/Source/Web/CoreXWebHost.cs` (registrar controller)
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs` (puentes)
- Modify: `SourceCode/AgIO/Source/wwwroot-corex/index.html` (nav)

- [ ] **Step 1: Puente genérico al hilo UI en FormLoop.CoreXSnapshot.cs**

Agregar al partial (debajo de los toggles existentes):

```csharp
// Ejecuta fn en el hilo UI y devuelve el resultado al endpoint web.
// Los SerialPort/labels/Settings de FormLoop no son thread-safe: todo
// lo que venga de EmbedIO pasa por acá.
public System.Threading.Tasks.Task<T> RunOnUiAsync<T>(Func<T> fn)
{
    var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
    BeginInvoke((MethodInvoker)(() =>
    {
        try { tcs.SetResult(fn()); }
        catch (Exception ex) { tcs.SetException(ex); }
    }));
    return tcs.Task;
}
```

- [ ] **Step 2: Puentes de serial en FormLoop.CoreXSnapshot.cs**

Los 6 canales usan los `OpenXPort/CloseXPort` existentes de SerialComm.Designer.cs (ya persisten `setPort_*` + `wasXConnected` + `Save()` al abrir OK — no duplicar ese guardado). Los nombres de puerto/baud son los campos `static` `portNameGPS/baudRateGPS/...`:

```csharp
// channel: "gps"|"gps2"|"rtcm"|"imu"|"steer"|"machine"
public bool OpenSerialFromWeb(string channel, string port, int baud)
{
    switch (channel)
    {
        case "gps": portNameGPS = port; baudRateGPS = baud; OpenGPSPort(); return spGPS.IsOpen;
        case "gps2": portNameGPS2 = port; baudRateGPS2 = baud; OpenGPS2Port(); return spGPS2.IsOpen;
        case "rtcm": portNameRtcm = port; baudRateRtcm = baud; OpenRtcmPort(); return spRtcm.IsOpen;
        case "imu": portNameIMU = port; OpenIMUPort(); return spIMU.IsOpen;
        case "steer": portNameSteerModule = port; OpenSteerModulePort(); return spSteerModule.IsOpen;
        case "machine": portNameMachineModule = port; OpenMachineModulePort(); return spMachineModule.IsOpen;
        default: throw new ArgumentException("canal desconocido: " + channel);
    }
}

public void CloseSerialFromWeb(string channel)
{
    switch (channel)
    {
        case "gps": CloseGPSPort(); break;
        case "gps2": CloseGPS2Port(); break;
        case "rtcm": CloseRtcmPort(); break;
        case "imu": CloseIMUPort(); break;
        case "steer": CloseSteerModulePort(); break;
        case "machine": CloseMachineModulePort(); break;
        default: throw new ArgumentException("canal desconocido: " + channel);
    }
}
```

**Verificar por grep los nombres reales** de `OpenGPS2Port/OpenRtcmPort/CloseRtcmPort` etc. en SerialComm.Designer.cs antes de escribir (el patrón existe 6 veces pero el casing puede variar). Si IMU/Steer/Machine tienen baud fijo (38400) no aceptar baud para esos canales.

- [ ] **Step 3: Controller con GET de estado serial + POST open/close**

```csharp
using System;
using System.Threading.Tasks;
using AgroParallel.WebHost.Controllers;
using EmbedIO;
using EmbedIO.Routing;

namespace AgIO
{
    /// <summary>
    /// Config web de CoreX (Fase 3): serial, NTRIP, red, módulos.
    /// Replica la semántica de los forms WinForms — mismo guardado,
    /// mismos criterios de reinicio.
    /// </summary>
    public sealed class CoreXConfigController : AgpControllerBase
    {
        private readonly FormLoop _form;
        public CoreXConfigController(FormLoop form) { _form = form; }

        [Route(HttpVerbs.Get, "/corex/config/serial")]
        public async Task GetSerial()
        {
            var snap = await _form.RunOnUiAsync(() => new
            {
                ports_available = System.IO.Ports.SerialPort.GetPortNames(),
                channels = new
                {
                    gps = new { port = FormLoop.portNameGPS, baud = FormLoop.baudRateGPS, is_open = _form.spGPS.IsOpen },
                    gps2 = new { port = FormLoop.portNameGPS2, baud = FormLoop.baudRateGPS2, is_open = _form.spGPS2.IsOpen },
                    rtcm = new { port = FormLoop.portNameRtcm, baud = FormLoop.baudRateRtcm, is_open = _form.spRtcm.IsOpen },
                    imu = new { port = FormLoop.portNameIMU, baud = 38400, is_open = _form.spIMU.IsOpen },
                    steer = new { port = FormLoop.portNameSteerModule, baud = 38400, is_open = _form.spSteerModule.IsOpen },
                    machine = new { port = FormLoop.portNameMachineModule, baud = 38400, is_open = _form.spMachineModule.IsOpen },
                }
            });
            await WriteJsonAsync(snap);
        }

        public sealed class SerialOpenReq { public string Channel { get; set; } public string Port { get; set; } public int Baud { get; set; } }

        [Route(HttpVerbs.Post, "/corex/serial/open")]
        public async Task OpenSerial()
        {
            var req = await ReadJsonBodyAsync<SerialOpenReq>();
            bool ok = await _form.RunOnUiAsync(() => _form.OpenSerialFromWeb(req.Channel, req.Port, req.Baud));
            await WriteJsonAsync(new { ok });
        }

        [Route(HttpVerbs.Post, "/corex/serial/close")]
        public async Task CloseSerial()
        {
            var req = await ReadJsonBodyAsync<SerialOpenReq>();
            await _form.RunOnUiAsync<object>(() => { _form.CloseSerialFromWeb(req.Channel); return null; });
            await WriteJsonAsync(new { ok = true });
        }
    }
}
```

Nota: los campos `spGPS`/`portNameGPS` etc. son public/static hoy — verificar accesibilidad; si alguno es private, exponer el snapshot desde un método en FormLoop.CoreXSnapshot.cs en vez de leer los campos desde el controller (mantener el controller sin conocimiento de internals). Los objetos anónimos ya están en snake_case a mano — si AgpJson re-serializa PascalCase→snake, usar nombres PascalCase en los anónimos y dejar que AgpJson los baje (verificar con curl cuál de las dos produce el wire correcto, y usar el mismo criterio en TODO el controller).

- [ ] **Step 4: Registrar el controller en CoreXWebHost.Start()**

```csharp
.WithWebApi("/api", m =>
{
    m.WithController(() => new CoreXStatusController());
    m.WithController(() => new CoreXCommandController(_form));
    m.WithController(() => new CoreXConfigController(_form));
});
```

- [ ] **Step 5: Página serial.html + serial.js**

`pages/serial.html` — misma estructura/clases que `index.html` (header con título "Puertos serie", link "← Dashboard" a `/index.html`). Una card por canal (GPS, GPS 2, RTCM, IMU, Steer, Machine), cada una con: `<select>` de puerto (id `port-gps`, ...), `<select>` de baud solo para gps/gps2/rtcm (4800/9600/19200/38400/57600/115200), dot de estado (`is_open`), y botón Abrir/Cerrar (id `btn-gps`, texto según estado). Incluir `js/modal.js` y `js/serial.js`.

`js/serial.js`:

```javascript
// Config de puertos serie CoreX — espejo web de FormCommSetGPS.
const CHANNELS = ['gps', 'gps2', 'rtcm', 'imu', 'steer', 'machine'];
const HAS_BAUD = { gps: true, gps2: true, rtcm: true, imu: false, steer: false, machine: false };

async function load() {
  const r = await fetch('/api/corex/config/serial');
  const cfg = await r.json();
  for (const ch of CHANNELS) {
    const c = cfg.channels[ch];
    const sel = document.getElementById('port-' + ch);
    sel.innerHTML = '';
    for (const p of cfg.ports_available) {
      const o = document.createElement('option');
      o.value = o.textContent = p;
      if (p === c.port) o.selected = true;
      sel.appendChild(o);
    }
    if (HAS_BAUD[ch]) document.getElementById('baud-' + ch).value = String(c.baud);
    document.getElementById('dot-' + ch).className = 'dot ' + (c.is_open ? 'on' : 'off');
    const btn = document.getElementById('btn-' + ch);
    btn.textContent = c.is_open ? 'Cerrar' : 'Abrir';
    btn.dataset.open = c.is_open ? '1' : '';
    sel.disabled = c.is_open;
    if (HAS_BAUD[ch]) document.getElementById('baud-' + ch).disabled = c.is_open;
  }
}

async function toggle(ch) {
  const btn = document.getElementById('btn-' + ch);
  if (btn.dataset.open) {
    await fetch('/api/corex/serial/close', { method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ channel: ch }) });
  } else {
    const body = { channel: ch, port: document.getElementById('port-' + ch).value,
      baud: HAS_BAUD[ch] ? parseInt(document.getElementById('baud-' + ch).value, 10) : 0 };
    const r = await fetch('/api/corex/serial/open', { method: 'POST',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const res = await r.json();
    if (!res.ok) AgpModal.alert('No se pudo abrir ' + body.port + ' — ¿en uso o desconectado?');
  }
  await load();
}

for (const ch of CHANNELS)
  document.getElementById('btn-' + ch).addEventListener('click', () => toggle(ch));
load();
```

(Si el body del POST viaja snake_case, `channel/port/baud` ya lo son. Verificar contra `ReadJsonBodyAsync` con un curl antes de dar por buena la página.)

- [ ] **Step 6: Nav en index.html**

Agregar bajo el header una fila de links tipo botón: `Puertos serie → /pages/serial.html` (las demás páginas suman su link en sus tareas).

- [ ] **Step 7: Build + smoke**

Build (regla 1). Lanzar `bin/Debug/CoreX.exe` y:
```bash
curl -s http://127.0.0.1:5181/api/corex/config/serial          # 200, ports_available[], 6 canales
curl -s -X POST http://127.0.0.1:5181/api/corex/serial/open -d '{"channel":"gps","port":"COM99","baud":9600}'  # ok:false (COM inexistente, no crashea)
```
Abrir `http://127.0.0.1:5181/pages/serial.html` en la ventana CoreX: combos poblados, botón responde.

- [ ] **Step 8: Commit**

```bash
git add SourceCode/AgIO/Source/Web/CoreXConfigController.cs SourceCode/AgIO/Source/Web/CoreXWebHost.cs SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs SourceCode/AgIO/Source/wwwroot-corex/pages/serial.html SourceCode/AgIO/Source/wwwroot-corex/js/serial.js SourceCode/AgIO/Source/wwwroot-corex/index.html
git commit -m "feat(corex-web): página de puertos serie (config + open/close por canal)"
```

---

### Task 2: Página y endpoints NTRIP

**Files:**
- Modify: `SourceCode/AgIO/Source/Web/CoreXConfigController.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs` (puente save NTRIP)
- Create: `SourceCode/AgIO/Source/wwwroot-corex/pages/ntrip.html`
- Create: `SourceCode/AgIO/Source/wwwroot-corex/js/ntrip.js`
- Modify: `SourceCode/AgIO/Source/wwwroot-corex/index.html` (link)

- [ ] **Step 1: DTO + puente de guardado replicando btnSerialOK_Click (FormNtrip:214-269)**

DTO (en el controller):

```csharp
public sealed class NtripConfigDto
{
    public bool IsOn { get; set; }
    public string CasterUrl { get; set; }
    public string CasterIp { get; set; }
    public int CasterPort { get; set; }
    public string Mount { get; set; }
    public string UserName { get; set; }
    public string UserPassword { get; set; }
    public int SendGgaInterval { get; set; }
    public bool IsGgaManual { get; set; }
    public double ManualLat { get; set; }
    public double ManualLon { get; set; }
    public bool IsTcp { get; set; }
    public bool IsHttp10 { get; set; }
    public int PacketSize { get; set; }
    public bool SendToSerial { get; set; }
    public bool SendToUdp { get; set; }
    public int SendToUdpPort { get; set; }
}
```

Puente en FormLoop.CoreXSnapshot.cs — misma secuencia que el form, con el criterio de reinicio calculado ANTES de pisar los settings:

```csharp
// Devuelve true si CoreX debe reiniciarse (mismo criterio que FormNtrip:
// cambió is_on, o cambió el destino serial<->udp).
public bool SaveNtripConfigFromWeb(CoreXConfigController.NtripConfigDto d)
{
    var s = Properties.Settings.Default;
    bool restart = (d.IsOn != s.setNTRIP_isOn)
        || (d.SendToSerial != s.setNTRIP_sendToSerial)
        || (d.SendToUdp != s.setNTRIP_sendToUDP);

    s.setNTRIP_isOn = d.IsOn;
    if (d.IsOn)
    {
        s.setRadio_isOn = isRadio_RequiredOn = false;
        s.setPass_isOn = isSerialPass_RequiredOn = false;
    }
    s.setNTRIP_casterURL = d.CasterUrl;
    s.setNTRIP_casterIP = d.CasterIp;
    s.setNTRIP_casterPort = d.CasterPort;
    s.setNTRIP_mount = d.Mount;
    s.setNTRIP_userName = d.UserName;
    s.setNTRIP_userPassword = d.UserPassword;
    s.setNTRIP_sendGGAInterval = d.SendGgaInterval;
    s.setNTRIP_isGGAManual = d.IsGgaManual;
    s.setNTRIP_manualLat = d.ManualLat;
    s.setNTRIP_manualLon = d.ManualLon;
    s.setNTRIP_isTCP = d.IsTcp;
    s.setNTRIP_isHTTP10 = d.IsHttp10;
    s.setNTRIP_packetSize = d.PacketSize;
    s.setNTRIP_sendToSerial = isSendToSerial = d.SendToSerial;
    s.setNTRIP_sendToUDP = isSendToUDP = d.SendToUdp;
    s.setNTRIP_sendToUDPPort = d.SendToUdpPort;
    packetSizeNTRIP = d.PacketSize;
    s.Save();

    if (!restart) ConfigureNTRIP();   // aplica en caliente, igual que el form
    return restart;
}

// Reinicio diferido: la respuesta HTTP tiene que salir antes.
public void RestartFromWeb()
{
    var t = new Timer { Interval = 800 };
    t.Tick += (s2, e2) => { t.Stop(); Program.Restart(); };
    t.Start();
}
```

(`Timer` = System.Windows.Forms.Timer — estamos en el partial del form, hilo UI. Verificar la firma real de `Program.Restart()` — la usan FormNtrip:267 y FormEthernet:68.)

- [ ] **Step 2: Endpoints GET/POST**

En el controller:

```csharp
[Route(HttpVerbs.Get, "/corex/config/ntrip")]
public async Task GetNtrip()
{
    var dto = await _form.RunOnUiAsync(() =>
    {
        var s = AgIO.Properties.Settings.Default;
        return new NtripConfigDto
        {
            IsOn = s.setNTRIP_isOn, CasterUrl = s.setNTRIP_casterURL, CasterIp = s.setNTRIP_casterIP,
            CasterPort = s.setNTRIP_casterPort, Mount = s.setNTRIP_mount, UserName = s.setNTRIP_userName,
            UserPassword = s.setNTRIP_userPassword, SendGgaInterval = s.setNTRIP_sendGGAInterval,
            IsGgaManual = s.setNTRIP_isGGAManual, ManualLat = s.setNTRIP_manualLat, ManualLon = s.setNTRIP_manualLon,
            IsTcp = s.setNTRIP_isTCP, IsHttp10 = s.setNTRIP_isHTTP10, PacketSize = s.setNTRIP_packetSize,
            SendToSerial = s.setNTRIP_sendToSerial, SendToUdp = s.setNTRIP_sendToUDP,
            SendToUdpPort = s.setNTRIP_sendToUDPPort,
        };
    });
    await WriteJsonAsync(dto);
}

[Route(HttpVerbs.Post, "/corex/config/ntrip")]
public async Task SaveNtrip()
{
    var dto = await ReadJsonBodyAsync<NtripConfigDto>();
    // Validación de IP: misma regla que FormNtrip.CheckIPValid (4 octetos 0-255).
    if (!string.IsNullOrEmpty(dto.CasterIp) && !IsValidIp(dto.CasterIp))
    {
        await WriteErrorAsync(400, "AGP-CFG-002", "IP del caster inválida", dto.CasterIp);
        return;
    }
    bool restart = await _form.RunOnUiAsync(() => _form.SaveNtripConfigFromWeb(dto));
    await WriteJsonAsync(new { ok = true, restart });
    if (restart) await _form.RunOnUiAsync<object>(() => { _form.RestartFromWeb(); return null; });
}

private static bool IsValidIp(string ip)
{
    var parts = ip.Split('.');
    if (parts.Length != 4) return false;
    foreach (var p in parts)
        if (!int.TryParse(p, out int v) || v < 0 || v > 255) return false;
    return true;
}
```

- [ ] **Step 3: Página ntrip.html + ntrip.js**

Cards: **Conexión** (toggle is_on, caster_url, caster_ip, caster_port, mount, user_name, user_password), **GGA** (send_gga_interval, select fija/GPS para is_gga_manual, manual_lat, manual_lon), **Avanzado** (is_tcp, select HTTP 1.0/1.1, packet_size select 256/512/1024, radio de destino serial/UDP, send_to_udp_port). Botón único **Guardar**. Inputs de texto con clase para keyboard.js (mismo hook que usan las páginas del Hub — verificar cómo se activa en `js/keyboard.js` copiado).

`js/ntrip.js`:

```javascript
// Config NTRIP — espejo web de FormNtrip. Guardar puede reiniciar CoreX
// (mismo criterio que el form: cambió on/off o el destino serial<->UDP).
const F = id => document.getElementById(id);

async function load() {
  const d = await (await fetch('/api/corex/config/ntrip')).json();
  F('is_on').checked = d.is_on;
  F('caster_url').value = d.caster_url || '';
  F('caster_ip').value = d.caster_ip || '';
  F('caster_port').value = d.caster_port;
  F('mount').value = d.mount || '';
  F('user_name').value = d.user_name || '';
  F('user_password').value = d.user_password || '';
  F('send_gga_interval').value = d.send_gga_interval;
  F('is_gga_manual').value = d.is_gga_manual ? 'manual' : 'gps';
  F('manual_lat').value = d.manual_lat;
  F('manual_lon').value = d.manual_lon;
  F('is_tcp').checked = d.is_tcp;
  F('http_ver').value = d.is_http10 ? '1.0' : '1.1';
  F('packet_size').value = String(d.packet_size);
  (d.send_to_serial ? F('dest_serial') : F('dest_udp')).checked = true;
  F('send_to_udp_port').value = d.send_to_udp_port;
}

async function save() {
  const body = {
    is_on: F('is_on').checked,
    caster_url: F('caster_url').value.trim(),
    caster_ip: F('caster_ip').value.trim(),
    caster_port: parseInt(F('caster_port').value, 10),
    mount: F('mount').value.trim(),
    user_name: F('user_name').value.trim(),
    user_password: F('user_password').value,
    send_gga_interval: parseInt(F('send_gga_interval').value, 10),
    is_gga_manual: F('is_gga_manual').value === 'manual',
    manual_lat: parseFloat(F('manual_lat').value),
    manual_lon: parseFloat(F('manual_lon').value),
    is_tcp: F('is_tcp').checked,
    is_http10: F('http_ver').value === '1.0',
    packet_size: parseInt(F('packet_size').value, 10),
    send_to_serial: F('dest_serial').checked,
    send_to_udp: F('dest_udp').checked,
    send_to_udp_port: parseInt(F('send_to_udp_port').value, 10),
  };
  const r = await fetch('/api/corex/config/ntrip', { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!r.ok) { const e = await r.json(); AgpModal.alert(e.friendly || 'Error al guardar'); return; }
  const res = await r.json();
  if (res.restart) waitForRestart();
  else AgpModal.alert('Guardado. Aplicado en caliente.');
}

function waitForRestart() {
  AgpModal.alert('CoreX se está reiniciando para aplicar el cambio…');
  const iv = setInterval(async () => {
    try { const r = await fetch('/api/corex/status', { cache: 'no-store' });
      if (r.ok) { clearInterval(iv); location.reload(); } } catch (_) { /* aún abajo */ }
  }, 2000);
}

F('btn-save').addEventListener('click', save);
load();
```

- [ ] **Step 4: Link "NTRIP" en el nav de index.html.**

- [ ] **Step 5: Build + smoke**

```bash
curl -s http://127.0.0.1:5181/api/corex/config/ntrip                       # 200, campos poblados
# guardar SIN tocar is_on/destino → restart:false, y el status refleja el cambio:
curl -s -X POST http://127.0.0.1:5181/api/corex/config/ntrip -d '<mismo json cambiando solo mount>'
```
En la página: guardar cambiando solo el mount → "aplicado en caliente"; cambiar is_on → modal de reinicio, CoreX se relanza, la página vuelve sola.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgIO/Source/Web/CoreXConfigController.cs SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs SourceCode/AgIO/Source/wwwroot-corex/pages/ntrip.html SourceCode/AgIO/Source/wwwroot-corex/js/ntrip.js SourceCode/AgIO/Source/wwwroot-corex/index.html
git commit -m "feat(corex-web): página NTRIP (config completa + criterio de reinicio del form)"
```

---

### Task 3: Página y endpoints de red UDP

**Files:**
- Modify: `SourceCode/AgIO/Source/Web/CoreXConfigController.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs` (puentes)
- Create: `SourceCode/AgIO/Source/wwwroot-corex/pages/red.html`
- Create: `SourceCode/AgIO/Source/wwwroot-corex/js/red.js`
- Modify: `SourceCode/AgIO/Source/wwwroot-corex/index.html` (link)

- [ ] **Step 1: Puentes en FormLoop.CoreXSnapshot.cs**

```csharp
// UDP on/off: igual que FormEthernet/FormUDP — guarda y SIEMPRE reinicia.
public void SetUdpOnOffFromWeb(bool on)
{
    var s = Properties.Settings.Default;
    s.setUDP_isOn = on;
    if (!on) s.setUDP_isSendNMEAToUDP = false;
    s.Save();
    AgLibrary.Logging.Log.EventWriter("Program Reset: UDP on/off desde la web");
    RestartFromWeb();
}

// Subnet módulos: réplica de FormUDP.btnSendSubnet_Click (217-290) —
// broadcast del comando de subnet a los módulos + epModule en caliente.
// COPIAR el cuerpo real del form (arma sendIPToModules con los 3 octetos
// y lo manda por cada NIC a epModuleSet), NO reinventarlo.
public void SendSubnetFromWeb(byte o1, byte o2, byte o3)
{
    /* cuerpo copiado de FormUDP:217-290, reemplazando ipNew[] por o1/o2/o3
       y los labels por nada (la web relee el GET) */
}
```

- [ ] **Step 2: Endpoints**

```csharp
[Route(HttpVerbs.Get, "/corex/config/red")]
public async Task GetRed()
{
    var dto = await _form.RunOnUiAsync(() =>
    {
        var s = AgIO.Properties.Settings.Default;
        return new
        {
            udp_is_on = s.setUDP_isOn,
            subnet = new[] { s.etIP_SubnetOne, s.etIP_SubnetTwo, s.etIP_SubnetThree },
            ip_actual = _form.GetLocalIpForWeb(),   // exponer ipCurrent o equivalente
        };
    });
    await WriteJsonAsync(dto);
}

public sealed class UdpOnOffReq { public bool On { get; set; } }
public sealed class SubnetReq { public byte O1 { get; set; } public byte O2 { get; set; } public byte O3 { get; set; } }

[Route(HttpVerbs.Post, "/corex/config/red/udp")]
public async Task SetUdp()
{
    var req = await ReadJsonBodyAsync<UdpOnOffReq>();
    await _form.RunOnUiAsync<object>(() => { _form.SetUdpOnOffFromWeb(req.On); return null; });
    await WriteJsonAsync(new { ok = true, restart = true });
}

[Route(HttpVerbs.Post, "/corex/config/red/subnet")]
public async Task SendSubnet()
{
    var req = await ReadJsonBodyAsync<SubnetReq>();
    await _form.RunOnUiAsync<object>(() => { _form.SendSubnetFromWeb(req.O1, req.O2, req.O3); return null; });
    await WriteJsonAsync(new { ok = true, restart = false });
}
```

(`GetLocalIpForWeb()`: devolver la IP que hoy muestra `lblIP` — buscar de dónde sale en `LoadUDPNetwork`/`TenSecondLoop` y exponerla. La subnet nueva se propaga a TODOS los módulos de la LAN: la página tiene que advertirlo antes de mandar — `AgpModal.confirm`.)

- [ ] **Step 3: Página red.html + red.js**

Cards: **Estado** (UDP on/off con toggle — al tocar, `AgpModal.confirm('CoreX se reinicia para aplicar. ¿Continuar?')` → POST /red/udp → `waitForRestart()` idéntico al de ntrip.js), **Subnet de módulos** (3 inputs numéricos 0-255 + IP actual como referencia + botón "Enviar a módulos" con confirm: "Esto cambia la subnet de TODOS los nodos de la LAN. ¿Continuar?"). JS con la misma estructura de load/save que ntrip.js (fetch GET → poblar; POST con body `{on}` / `{o1,o2,o3}`; validar octetos 0-255 antes de mandar).

- [ ] **Step 4: Link "Red" en index.html. Build + smoke**

```bash
curl -s http://127.0.0.1:5181/api/corex/config/red    # 200, subnet [192,168,5], udp_is_on
```
NO postear /red/udp en el smoke automatizado si CoreX está en uso (reinicia el proceso) — probarlo una vez a mano al final y verificar que vuelve solo.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgIO/Source/Web/CoreXConfigController.cs SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs SourceCode/AgIO/Source/wwwroot-corex/pages/red.html SourceCode/AgIO/Source/wwwroot-corex/js/red.js SourceCode/AgIO/Source/wwwroot-corex/index.html
git commit -m "feat(corex-web): página de red UDP (on/off con reinicio + subnet a módulos)"
```

---

### Task 4: Página de módulos + validación integral

**Files:**
- Modify: `SourceCode/AgIO/Source/Web/CoreXConfigController.cs`
- Modify: `SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs` (puente)
- Create: `SourceCode/AgIO/Source/wwwroot-corex/pages/modulos.html`
- Create: `SourceCode/AgIO/Source/wwwroot-corex/js/modulos.js`
- Modify: `SourceCode/AgIO/Source/wwwroot-corex/index.html` (link)

- [ ] **Step 1: Puente**

```csharp
// Módulos on/off: igual que los checkboxes de FormLoop — en caliente,
// SetModulesOnOff() ya persiste si hubo cambio.
public void SetModulesFromWeb(bool imu, bool steer, bool machine)
{
    isConnectedIMU = cboxIsIMUModule.Checked = imu;
    isConnectedSteer = cboxIsSteerModule.Checked = steer;
    isConnectedMachine = cboxIsMachineModule.Checked = machine;
    SetModulesOnOff();
}
```

- [ ] **Step 2: Endpoints**

```csharp
public sealed class ModulesReq { public bool Imu { get; set; } public bool Steer { get; set; } public bool Machine { get; set; } }

[Route(HttpVerbs.Get, "/corex/config/modulos")]
public async Task GetModules()
{
    var dto = await _form.RunOnUiAsync(() => new
    {
        imu = _form.isConnectedIMU,
        steer = _form.isConnectedSteer,
        machine = _form.isConnectedMachine,
    });
    await WriteJsonAsync(dto);
}

[Route(HttpVerbs.Post, "/corex/config/modulos")]
public async Task SetModules()
{
    var req = await ReadJsonBodyAsync<ModulesReq>();
    await _form.RunOnUiAsync<object>(() => { _form.SetModulesFromWeb(req.Imu, req.Steer, req.Machine); return null; });
    await WriteJsonAsync(new { ok = true });
}
```

- [ ] **Step 3: Página modulos.html + modulos.js**

Card única con 3 toggles (IMU / Steer / Machine — nombres de producto, sin wording de chip) que muestran además el hello vivo (leer `/api/corex/status` → `modules.*_hello` con el mismo dot on/bad del dashboard). Cambiar un toggle → POST inmediato (sin botón guardar — igual que los checkboxes del form) → recargar.

- [ ] **Step 4: Link "Módulos" en index.html.**

- [ ] **Step 5: Build Release + Build/ + validación integral**

```bash
taskkill //IM PilotX.exe //F 2>/dev/null; taskkill //IM CoreX.exe //F 2>/dev/null; taskkill //IM msedgewebview2.exe //F 2>/dev/null
powershell -ExecutionPolicy Bypass -File build.ps1
cd Build && powershell -Command "Start-Process .\CoreX.exe -WorkingDirectory ." && powershell -Command "Start-Process .\PilotX.exe -WorkingDirectory ."
```

Checklist:
1. Dashboard 200 + los 4 links del nav responden 200.
2. `GET /api/corex/config/serial|ntrip|red|modulos` → 200 con datos reales.
3. Serial: abrir/cerrar un canal con hardware (o COM inexistente → ok:false sin crash).
4. NTRIP: guardar cambio de mount → `restart:false` y el GET lo devuelve; toggle is_on → CoreX se reinicia y vuelve.
5. Módulos: toggle IMU → `modules.imu_configured` cambia en `/api/corex/status`; la UI vieja (ShowLegacyUi) refleja el checkbox.
6. Todo lo del checklist previo sigue PASS (broker, GPS, loopback a PilotX).

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgIO/Source/Web/CoreXConfigController.cs SourceCode/AgIO/Source/Forms/FormLoop.CoreXSnapshot.cs SourceCode/AgIO/Source/wwwroot-corex/pages/modulos.html SourceCode/AgIO/Source/wwwroot-corex/js/modulos.js SourceCode/AgIO/Source/wwwroot-corex/index.html
git commit -m "feat(corex-web): página de módulos (IMU/Steer/Machine on-off en caliente)"
```

---

## Notas para el ejecutor

- **Verificar accesibilidad de campos** antes de escribir cada endpoint: `spGPS`, `portNameGPS`, `isConnectedIMU`, `cboxIsIMUModule` — algunos son public/static, otros pueden ser private. Regla: si es private, el acceso va dentro del puente en FormLoop.CoreXSnapshot.cs (partial = acceso total), NUNCA hacer público un campo solo para el controller.
- **snake_case del wire**: resolver UNA vez en Task 1 (¿AgpJson baja PascalCase→snake en serialize Y deserialize?) con un curl, y aplicar el mismo criterio en todas las tareas. Recordar: case-insensitive NO cubre underscores.
- **RELEASE cachea estáticos**: al probar cambios de HTML/JS desde Build/, reiniciar CoreX.exe (los estáticos del wwwroot-corex se sirven sin cache, pero el dashboard puede tener la página vieja abierta — F5 no alcanza si el exe no se recopiló).
- **`Program.Restart()`**: verificar que relanza el exe correcto cuando corre desde Build/ (lo usan FormNtrip/FormEthernet hoy — mismo comportamiento).
- Página de config nueva ⇒ mismo design system: NO inventar estilos; reusar clases de `index.html`/theme.css.
- Este plan convive con el de extracción de servicios (`2026-07-06-corex-services-extraction.md`, pendiente): los puentes de FormLoop.CoreXSnapshot.cs son la costura — cuando los servicios existan, los puentes delegan en ellos sin tocar el controller ni las páginas.
