# Registro de PID de QuantiX — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Registrar a 5 Hz, por motor, rpm real vs target contra velocidad y PWM, para poder diagnosticar el PID de los 14 motores de QuantiX en el campo.

**Architecture:** El bridge de QuantiX ya calcula todo en su tick de 200 ms; se le suma una llamada que encola la muestra en memoria. Un `QxPidRecorder` aislado vacía esa cola a disco cada segundo, un CSV por motor, sesiones de 1 hora con retención de 20. Un panel Avalonia lee el buffer rodante del mismo recorder por HTTP.

**Tech Stack:** C# 7.3 / netstandard2.0 (`AgroParallel.Services`, `AgroParallel.Models`), NUnit 4 (`AgroParallel.Services.Tests`), EmbedIO (`AgroParallel.WebHost`), Avalonia (`PilotX.UI`).

## Global Constraints

- `AgroParallel.Services` y `AgroParallel.Models` son **netstandard2.0 con `LangVersion 7.3`**: sin nullable reference types, sin records, sin switch expressions, sin `using` declarations. Usar `var`, `out` inline y tuplas está bien.
- Todo número que va al CSV o al JSON se formatea con **`CultureInfo.InvariantCulture`**. La PC del tractor corre Windows en español: sin esto los decimales salen con coma y el CSV queda partido en columnas equivocadas.
- El wire HTTP va en **snake_case vía `AgpJson`**, como todo el resto del repo.
- **El tick del bridge nunca hace I/O.** `Registrar()` encola y vuelve. Todo lo que toque disco corre en el timer de flush.
- Los tests van en `AgroParallel.Services.Tests` con NUnit (`[TestFixture]` / `[Test]` / `Assert.That`).
- Nombres de producto en logs y UI: **PilotX, QuantiX, Agro Parallel**. Nunca AOG, AgOpenGPS ni AgIO.

---

### Task 1: `MotorLive.LoadPct` — el dato que ya viaja y se descarta

El nodo manda `load_pct` (% de PWM sobre 4095) en cada `status_live`
(`MQTT_Custom.cpp:792`). Hoy se tira. Sin él no se distingue "el PID oscila" de
"el motor está saturado y no puede seguir".

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Models/MotorLive.cs`
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/NodoRegistryService.cs:447-457` (copia defensiva) y `:689-694` (parseo)
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs` (nuevo, arranca acá)

**Interfaces:**
- Consumes: nada.
- Produces: `MotorLive.LoadPct` (`int`), leído por Task 5.

- [ ] **Step 1: Escribir el test que falla**

En `QxPidRecorderTests.cs` (archivo nuevo):

```csharp
// ============================================================================
// QxPidRecorderTests.cs — registro de PID de QuantiX.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using AgroParallel.Models;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class QxPidRecorderTests
    {
        [Test]
        public void MotorLive_expone_load_pct()
        {
            var m = new MotorLive { Id = 0, LoadPct = 86 };
            Assert.That(m.LoadPct, Is.EqualTo(86));
        }
    }
}
```

- [ ] **Step 2: Correr el test y verificar que falla**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter MotorLive_expone_load_pct
```

Esperado: FALLA al compilar — `'MotorLive' does not contain a definition for 'LoadPct'`.

- [ ] **Step 3: Agregar la propiedad al modelo**

En `MotorLive.cs`, después de `public int Rpm { get; set; }`:

```csharp
        /// <summary>Carga del motor: PWM sobre 4095, en %. Lo manda el nodo en
        /// status_live. Saturado (100) significa que el motor NO puede seguir al
        /// target — eso no se arregla con Kp, es techo mecánico.</summary>
        public int LoadPct { get; set; }
```

- [ ] **Step 4: Parsear el campo del payload**

En `NodoRegistryService.cs`, después de `m.Pulsos = ExtractJsonLong(payload, "pulsos");`:

```csharp
                    m.LoadPct = ExtractJsonInt(payload, "load_pct");
```

- [ ] **Step 5: Agregarlo a la copia defensiva**

En `NodoRegistryService.cs`, dentro del `new MotorLive { ... }` de `GetAll()`, después de `Rpm = m.Rpm,`:

```csharp
                                LoadPct = m.LoadPct,
```

- [ ] **Step 6: Correr el test y verificar que pasa**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter MotorLive_expone_load_pct
```

Esperado: PASA.

- [ ] **Step 7: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Models/MotorLive.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services/NodoRegistryService.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs
git commit -m "feat(quantix): parsear load_pct del nodo, que ya viajaba y se descartaba"
```

---

### Task 2: `QxPidSample` y el CSV por motor

El núcleo del registro: recibir muestras sin bloquear y escribirlas a un CSV por
motor, con el UID del nodo en el nombre porque el índice de motor solo es único
dentro de su nodo (`QuantiXMotorBridge.cs:350`).

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidSample.cs`
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs`

**Interfaces:**
- Consumes: `MotorLive.LoadPct` de Task 1.
- Produces:
  - `struct QxPidSample` con campos públicos `Uid` (string), `MotorIdx` (int), `Nombre` (string), `RpmReal` (double?), `RpmTarget` (double), `PpsReal` (double), `PpsTarget` (double), `Pwm` (int), `LoadPct` (int), `VelMotorKmh` (double), `VelGpsKmh` (double), `Dosis` (double), `SeccionOn` (bool).
  - `QxPidRecorder` con `QxPidRecorder(string baseDir)`, `void AbrirSesion()`, `void Registrar(QxPidSample m)`, `void Marcar(string texto)`, `void CerrarSesion()`, `void FlushAhora()`, `string SesionDir { get; }`, `static QxPidRecorder Instance { get; set; }`.

`RpmReal` es `double?` a propósito: **vacío** cuando el nodo dejó de publicar,
que no es lo mismo que **cero** (motor quieto).

- [ ] **Step 1: Escribir el test que falla**

Agregar a `QxPidRecorderTests.cs`:

```csharp
        private static string DirTemp()
        {
            string d = Path.Combine(Path.GetTempPath(), "qxpid_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static QxPidSample Muestra(string uid, int idx)
        {
            return new QxPidSample
            {
                Uid = uid, MotorIdx = idx, Nombre = "Cuerpo 1",
                RpmReal = 44, RpmTarget = 45,
                PpsReal = 15.0, PpsTarget = 15.2,
                Pwm = 2050, LoadPct = 50,
                VelMotorKmh = 6.2, VelGpsKmh = 6.2,
                Dosis = 7.0, SeccionOn = true
            };
        }

        [Test]
        public void Escribe_un_csv_por_motor_con_uid_en_el_nombre()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.Registrar(Muestra("A4CF12AB9E30", 1));
            rec.Registrar(Muestra("7B2E0091CC14", 0));
            rec.FlushAhora();

            string sesion = rec.SesionDir;
            Assert.That(File.Exists(Path.Combine(sesion, "A4CF12AB9E30_m0.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(sesion, "A4CF12AB9E30_m1.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(sesion, "7B2E0091CC14_m0.csv")), Is.True);
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void La_cabecera_y_la_fila_salen_en_el_orden_declarado()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.FlushAhora();

            string[] lineas = File.ReadAllLines(Path.Combine(rec.SesionDir, "A4CF12AB9E30_m0.csv"));
            Assert.That(lineas[0], Is.EqualTo(
                "t_s,rpm_real,rpm_target,pps_real,pps_target,pwm,load_pct,vel_motor,vel_gps,dosis,sec_on,marca"));
            string[] c = lineas[1].Split(',');
            Assert.That(c.Length, Is.EqualTo(12));
            Assert.That(c[1], Is.EqualTo("44"));      // rpm_real
            Assert.That(c[2], Is.EqualTo("45"));      // rpm_target
            Assert.That(c[5], Is.EqualTo("2050"));    // pwm
            Assert.That(c[6], Is.EqualTo("50"));      // load_pct
            Assert.That(c[10], Is.EqualTo("1"));      // sec_on
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void Nodo_caido_escribe_vacio_no_cero()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            var m = Muestra("A4CF12AB9E30", 0);
            m.RpmReal = null;                 // el nodo dejó de publicar
            rec.Registrar(m);
            rec.FlushAhora();

            string[] lineas = File.ReadAllLines(Path.Combine(rec.SesionDir, "A4CF12AB9E30_m0.csv"));
            string[] c = lineas[1].Split(',');
            Assert.That(c[1], Is.EqualTo(""), "cero significa motor quieto, vacio significa sin dato");
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void Los_decimales_van_con_punto_aunque_el_locale_use_coma()
        {
            var previo = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("es-AR");
            try
            {
                string dir = DirTemp();
                var rec = new QxPidRecorder(dir);
                rec.AbrirSesion();
                rec.Registrar(Muestra("A4CF12AB9E30", 0));
                rec.FlushAhora();

                string[] lineas = File.ReadAllLines(Path.Combine(rec.SesionDir, "A4CF12AB9E30_m0.csv"));
                Assert.That(lineas[1], Does.Contain("6.20"));
                Assert.That(lineas[1].Split(',').Length, Is.EqualTo(12));
                rec.CerrarSesion();
                Directory.Delete(dir, true);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previo; }
        }

        [Test]
        public void Sesion_sin_muestras_no_deja_carpeta_huerfana()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            string sesion = rec.SesionDir;
            rec.CerrarSesion();
            Assert.That(Directory.Exists(sesion), Is.False);
            Directory.Delete(dir, true);
        }

        [Test]
        public void Registrar_no_explota_si_el_directorio_no_existe()
        {
            var rec = new QxPidRecorder(Path.Combine(Path.GetTempPath(),
                "qxpid_inexistente_" + Guid.NewGuid().ToString("N"), "sub", "sub"));
            rec.AbrirSesion();
            Assert.DoesNotThrow(() => rec.Registrar(Muestra("A4CF12AB9E30", 0)));
            Assert.DoesNotThrow(() => rec.FlushAhora());
        }
```

- [ ] **Step 2: Correr los tests y verificar que fallan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: FALLA al compilar — `The type or namespace name 'QxPidRecorder' could not be found`.

- [ ] **Step 3: Crear `QxPidSample.cs`**

```csharp
// ============================================================================
// QxPidSample.cs — una muestra del registro de PID, de un motor, en un tick.
//
// Struct a propósito: el bridge arma 14 de estas cada 200 ms y las encola. Sin
// asignaciones en el heap no hay presión de GC en el lazo que comanda motores.
// ============================================================================

namespace AgroParallel.QuantiX
{
    public struct QxPidSample
    {
        /// <summary>UID del nodo. Va en el NOMBRE del archivo, no en cada fila:
        /// MotorIdx solo es unico dentro de su nodo.</summary>
        public string Uid;

        /// <summary>Indice del motor DENTRO del nodo (0..N-1 de nodo.Motores).</summary>
        public int MotorIdx;

        /// <summary>Nombre que le puso el operario. Va al sidecar, no al CSV.</summary>
        public string Nombre;

        /// <summary>RPM medidas por el encoder. null = el nodo no publica (sin
        /// dato). Cero = el motor esta quieto. NO son lo mismo.</summary>
        public double? RpmReal;

        /// <summary>RPM que PilotX le pide al motor.</summary>
        public double RpmTarget;

        public double PpsReal;
        public double PpsTarget;
        public int Pwm;

        /// <summary>PWM sobre 4095, en %. 100 = saturado: el motor no puede
        /// seguir al target y el problema no es de ganancias.</summary>
        public int LoadPct;

        /// <summary>Velocidad de las secciones que cubre ESTE motor. En curva no
        /// es la del tractor (ver MotorSpeedKmh en QuantiXMotorBridge).</summary>
        public double VelMotorKmh;

        /// <summary>Velocidad del tractor segun GPS.</summary>
        public double VelGpsKmh;

        /// <summary>Dosis objetivo resuelta (manual > mapa > fija).</summary>
        public double Dosis;

        public bool SeccionOn;
    }
}
```

- [ ] **Step 4: Crear `QxPidRecorder.cs` con lo mínimo que hace pasar los tests**

```csharp
// ============================================================================
// QxPidRecorder.cs — registro de PID de QuantiX: un CSV por motor a 5 Hz.
//
// Para que se pueda contestar en el lote la pregunta que importa: si la
// velocidad se mantiene estable y las rpm suben y bajan, hay que corregir el
// PID. Con el detalle de que "velocidad estable" tiene que ser la del MOTOR
// (vel_motor), no la del tractor: en curva no son la misma y culpar al PID de
// un cambio de target legitimo es el error facil de cometer.
//
// REGLA QUE NO SE NEGOCIA: Registrar() NUNCA toca el disco. El tick de 200 ms
// que lo llama es el que comanda los motores; si se queda esperando un pendrive
// trabado, el motor se queda sin comando en el medio del lote. Encola en
// memoria y vuelve. El disco lo escribe el timer de flush, y si falla, se
// pierde el registro y el motor sigue andando.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Timers;

namespace AgroParallel.QuantiX
{
    public sealed class QxPidRecorder : IDisposable
    {
        public const string Cabecera =
            "t_s,rpm_real,rpm_target,pps_real,pps_target,pwm,load_pct,vel_motor,vel_gps,dosis,sec_on,marca";

        /// <summary>Instancia viva, para que el controller HTTP llegue al buffer
        /// sin sumarle otro parametro al constructor de AgpWebHost.</summary>
        public static QxPidRecorder Instance { get; set; }

        private readonly string _baseDir;
        private readonly object _lock = new object();
        private readonly Queue<QxPidSample> _cola = new Queue<QxPidSample>();
        private readonly Dictionary<string, StreamWriter> _writers =
            new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);

        private Timer _flushTimer;
        private DateTime _inicio;
        private bool _abierta;
        private bool _huboMuestras;
        private string _marcaPendiente;
        private bool _avisoFalloDisco;

        public string SesionDir { get; private set; }

        public QxPidRecorder(string baseDir)
        {
            _baseDir = baseDir;
        }

        public void AbrirSesion()
        {
            lock (_lock)
            {
                if (_abierta) return;
                _inicio = DateTime.Now;
                SesionDir = Path.Combine(_baseDir, "pid-quantix",
                    _inicio.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture));
                _huboMuestras = false;
                _abierta = true;
            }

            _flushTimer = new Timer { Interval = 1000, AutoReset = true };
            _flushTimer.Elapsed += (s, e) => FlushAhora();
            _flushTimer.Start();
        }

        /// <summary>Encola la muestra. No toca disco. No bloquea.</summary>
        public void Registrar(QxPidSample m)
        {
            lock (_lock)
            {
                if (!_abierta) return;
                _cola.Enqueue(m);
            }
        }

        /// <summary>Clava una marca en la proxima fila de TODOS los motores, para
        /// alinear "aca toque Kp" con lo que hicieron los 14 a la vez.</summary>
        public void Marcar(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return;
            lock (_lock) _marcaPendiente = texto.Replace(',', ' ').Replace('\n', ' ');
        }

        public void FlushAhora()
        {
            QxPidSample[] lote;
            string marca;
            lock (_lock)
            {
                if (_cola.Count == 0) return;
                lote = _cola.ToArray();
                _cola.Clear();
                marca = _marcaPendiente;
                _marcaPendiente = null;
            }

            try
            {
                for (int i = 0; i < lote.Length; i++)
                {
                    var w = WriterDe(lote[i].Uid, lote[i].MotorIdx);
                    if (w == null) return;
                    w.WriteLine(Fila(lote[i], marca));
                }
                _huboMuestras = true;
            }
            catch (Exception ex)
            {
                // Disco lleno o pendrive desconectado: se corta el registro y se
                // avisa UNA vez. Los motores siguen comandados.
                if (!_avisoFalloDisco)
                {
                    _avisoFalloDisco = true;
                    System.Diagnostics.Trace.WriteLine(
                        "[QuantiX-PID] registro detenido por error de disco: " + ex.Message);
                }
            }
        }

        private StreamWriter WriterDe(string uid, int motorIdx)
        {
            if (string.IsNullOrEmpty(uid)) uid = "sin-uid";
            string clave = uid + "_m" + motorIdx.ToString(CultureInfo.InvariantCulture);
            StreamWriter w;
            if (_writers.TryGetValue(clave, out w)) return w;

            if (!Directory.Exists(SesionDir)) Directory.CreateDirectory(SesionDir);
            string path = Path.Combine(SesionDir, clave + ".csv");
            bool nuevo = !File.Exists(path);
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            w = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            if (nuevo) w.WriteLine(Cabecera);
            _writers[clave] = w;
            return w;
        }

        private string Fila(QxPidSample m, string marca)
        {
            var inv = CultureInfo.InvariantCulture;
            double t = (DateTime.Now - _inicio).TotalSeconds;
            var sb = new StringBuilder(96);
            sb.Append(t.ToString("F1", inv)).Append(',');
            sb.Append(m.RpmReal.HasValue ? m.RpmReal.Value.ToString("F0", inv) : "").Append(',');
            sb.Append(m.RpmTarget.ToString("F0", inv)).Append(',');
            sb.Append(m.PpsReal.ToString("F2", inv)).Append(',');
            sb.Append(m.PpsTarget.ToString("F2", inv)).Append(',');
            sb.Append(m.Pwm.ToString(inv)).Append(',');
            sb.Append(m.LoadPct.ToString(inv)).Append(',');
            sb.Append(m.VelMotorKmh.ToString("F2", inv)).Append(',');
            sb.Append(m.VelGpsKmh.ToString("F2", inv)).Append(',');
            sb.Append(m.Dosis.ToString("F2", inv)).Append(',');
            sb.Append(m.SeccionOn ? '1' : '0').Append(',');
            sb.Append(marca ?? "");
            return sb.ToString();
        }

        public void CerrarSesion()
        {
            if (_flushTimer != null)
            {
                _flushTimer.Stop();
                _flushTimer.Dispose();
                _flushTimer = null;
            }
            FlushAhora();

            foreach (var kv in _writers)
            {
                try { kv.Value.Flush(); kv.Value.Dispose(); } catch { } // silencioso: fallback de I/O del propio logger
            }
            _writers.Clear();

            lock (_lock) _abierta = false;

            // Sesion sin una sola muestra: no dejar carpeta huerfana.
            if (!_huboMuestras && !string.IsNullOrEmpty(SesionDir))
            {
                try { if (Directory.Exists(SesionDir)) Directory.Delete(SesionDir, true); }
                catch { } // silencioso: fallback de I/O del propio logger
            }
        }

        public void Dispose() { CerrarSesion(); }
    }
}
```

- [ ] **Step 5: Correr los tests y verificar que pasan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: los 6 tests PASAN.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidSample.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs
git commit -m "feat(quantix): registro de PID, un CSV por motor a 5 Hz"
```

---

### Task 3: Sesión de 1 hora, retención de 20 y protección de las marcadas

Sin corte horario una jornada de 8 h sería una sesión de ~56 MB y veinte de esas
1,1 GB. Con el corte, "20 sesiones" son "las últimas 20 horas" y el techo son
~140 MB. Mismo patrón que `DebugLogService.cs:334`.

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs`

**Interfaces:**
- Consumes: `QxPidRecorder` de Task 2.
- Produces: `const int MaxSesiones = 20;` y el archivo centinela `.marcada` dentro de la carpeta de sesión.

- [ ] **Step 1: Escribir los tests que fallan**

```csharp
        [Test]
        public void Purga_deja_las_ultimas_veinte_sesiones()
        {
            string dir = DirTemp();
            string raiz = Path.Combine(dir, "pid-quantix");
            Directory.CreateDirectory(raiz);
            for (int i = 0; i < 25; i++)
            {
                string d = Path.Combine(raiz, "2026-09-01_" + i.ToString("00") + "00");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "x.csv"), "x");
            }

            var rec = new QxPidRecorder(dir);
            rec.Purgar();

            Assert.That(Directory.GetDirectories(raiz).Length, Is.EqualTo(QxPidRecorder.MaxSesiones));
            Assert.That(Directory.Exists(Path.Combine(raiz, "2026-09-01_0000")), Is.False, "la mas vieja se borra");
            Assert.That(Directory.Exists(Path.Combine(raiz, "2026-09-01_2400")), Is.True, "la mas nueva queda");
            Directory.Delete(dir, true);
        }

        [Test]
        public void Purga_nunca_borra_una_sesion_marcada()
        {
            string dir = DirTemp();
            string raiz = Path.Combine(dir, "pid-quantix");
            Directory.CreateDirectory(raiz);
            for (int i = 0; i < 25; i++)
            {
                string d = Path.Combine(raiz, "2026-09-01_" + i.ToString("00") + "00");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "x.csv"), "x");
            }
            // La mas vieja de todas, marcada a proposito.
            File.WriteAllText(Path.Combine(raiz, "2026-09-01_0000", ".marcada"), "kp=3");

            var rec = new QxPidRecorder(dir);
            rec.Purgar();

            Assert.That(Directory.Exists(Path.Combine(raiz, "2026-09-01_0000")), Is.True,
                "si alguien la marco, esa corrida importa");
            Directory.Delete(dir, true);
        }

        [Test]
        public void Marcar_deja_el_centinela_en_la_sesion()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.Marcar("kp=3");
            rec.FlushAhora();
            Assert.That(File.Exists(Path.Combine(rec.SesionDir, ".marcada")), Is.True);
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }
```

- [ ] **Step 2: Correr y verificar que fallan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: FALLA al compilar — `'QxPidRecorder' does not contain a definition for 'Purgar'`.

- [ ] **Step 3: Agregar corte horario, purga y centinela**

En `QxPidRecorder.cs`, agregar el campo y la constante junto a los otros:

```csharp
        /// <summary>Cuantas sesiones se conservan. Una sesion es 1 hora (ver
        /// CorteHoraMs), asi que son las ultimas 20 horas de registro.</summary>
        public const int MaxSesiones = 20;

        private const double CorteHoraMs = 60 * 60 * 1000;
        private Timer _corteTimer;
        private bool _marcada;
```

En `AbrirSesion()`, antes de arrancar `_flushTimer`, purgar y armar el corte:

```csharp
            Purgar();

            _corteTimer = new Timer { Interval = CorteHoraMs, AutoReset = false };
            _corteTimer.Elapsed += (s, e) => Rotar();
            _corteTimer.Start();
```

Agregar los métodos:

```csharp
        /// <summary>Cierra la sesion en curso y abre la siguiente. Las corridas
        /// dentro de una sesion se separan con marcas, no con archivos: el corte
        /// horario existe para que el techo de retencion sea predecible.</summary>
        private void Rotar()
        {
            CerrarSesion();
            AbrirSesion();
        }

        /// <summary>Deja las ultimas MaxSesiones. Las marcadas no cuentan para el
        /// tope y no se borran nunca — si no, el tope terminaria borrando justo
        /// lo que se quiso guardar.</summary>
        public void Purgar()
        {
            try
            {
                string raiz = Path.Combine(_baseDir, "pid-quantix");
                if (!Directory.Exists(raiz)) return;

                var candidatas = new List<string>();
                foreach (string d in Directory.GetDirectories(raiz))
                    if (!File.Exists(Path.Combine(d, ".marcada"))) candidatas.Add(d);

                candidatas.Sort(StringComparer.OrdinalIgnoreCase);
                int sobran = candidatas.Count - MaxSesiones;
                for (int i = 0; i < sobran; i++)
                {
                    try { Directory.Delete(candidatas[i], true); }
                    catch { } // silencioso: fallback de I/O del propio logger
                }
            }
            catch { } // silencioso: fallback de I/O del propio logger
        }
```

En `Marcar()`, dejar el centinela además de la marca en la fila:

```csharp
        public void Marcar(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return;
            lock (_lock)
            {
                _marcaPendiente = texto.Replace(',', ' ').Replace('\n', ' ');
                _marcada = true;
            }
        }
```

Y en `FlushAhora()`, después de `_huboMuestras = true;`:

```csharp
                if (_marcada && !string.IsNullOrEmpty(SesionDir)
                    && !File.Exists(Path.Combine(SesionDir, ".marcada")))
                    File.WriteAllText(Path.Combine(SesionDir, ".marcada"), marca ?? "1");
```

En `CerrarSesion()`, parar también el timer de corte, antes de `FlushAhora()`:

```csharp
            if (_corteTimer != null)
            {
                _corteTimer.Stop();
                _corteTimer.Dispose();
                _corteTimer = null;
            }
```

Y resetear `_marcada` junto a `_abierta`:

```csharp
            lock (_lock) { _abierta = false; _marcada = false; }
```

- [ ] **Step 4: Correr y verificar que pasan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: los 9 tests PASAN.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs
git commit -m "feat(quantix): sesiones de 1 h, retencion de 20 y proteccion de las marcadas"
```

---

### Task 4: `sesion.json` — config al abrir, resumen al cerrar

Nadie va a abrir 14 archivos para encontrar cuál se porta distinto. El resumen
dice de un vistazo cuál tiene el triple de desvío y vive saturado — y eso no se
arregla con Kp.

**Files:**
- Create: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidResumen.cs`
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs`

**Interfaces:**
- Consumes: `QxPidSample` de Task 2.
- Produces: `QxPidResumen` con `Uid`, `MotorIdx`, `Nombre`, `RpmProm`, `RpmDesvio`, `ErrorMedioPct`, `LoadPromPct`, `TiempoSaturadoPct`; y `QxPidResumen.Calcular(IList<QxPidSample>)` que devuelve uno. `QxPidRecorder.EscribirSidecar(string json)`.

- [ ] **Step 1: Escribir los tests que fallan**

```csharp
        [Test]
        public void Resumen_calcula_desvio_y_tiempo_saturado()
        {
            var ms = new List<QxPidSample>();
            for (int i = 0; i < 10; i++)
            {
                var m = Muestra("A4CF12AB9E30", 1);
                m.RpmReal = (i < 5) ? 40 : 50;       // promedio 45, desvio 5
                m.RpmTarget = 45;
                m.LoadPct = (i < 3) ? 100 : 80;      // 30% del tiempo saturado
                ms.Add(m);
            }

            var r = QxPidResumen.Calcular(ms);
            Assert.That(r.RpmProm, Is.EqualTo(45).Within(0.01));
            Assert.That(r.RpmDesvio, Is.EqualTo(5).Within(0.01));
            Assert.That(r.TiempoSaturadoPct, Is.EqualTo(30).Within(0.01));
            Assert.That(r.LoadPromPct, Is.EqualTo(86).Within(0.01));
        }

        [Test]
        public void Resumen_ignora_las_muestras_sin_dato()
        {
            var ms = new List<QxPidSample>();
            var a = Muestra("A4CF12AB9E30", 0); a.RpmReal = 40; ms.Add(a);
            var b = Muestra("A4CF12AB9E30", 0); b.RpmReal = null; ms.Add(b);
            var c = Muestra("A4CF12AB9E30", 0); c.RpmReal = 50; ms.Add(c);

            var r = QxPidResumen.Calcular(ms);
            Assert.That(r.RpmProm, Is.EqualTo(45).Within(0.01), "la muestra sin dato no promedia como cero");
        }

        [Test]
        public void La_sesion_deja_el_sidecar_json()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.FlushAhora();
            string sesion = rec.SesionDir;
            rec.CerrarSesion();

            string json = File.ReadAllText(Path.Combine(sesion, "sesion.json"));
            Assert.That(json, Does.Contain("\"motores\""));
            Assert.That(json, Does.Contain("A4CF12AB9E30"));
            Directory.Delete(dir, true);
        }
```

- [ ] **Step 2: Correr y verificar que fallan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: FALLA al compilar — `The type or namespace name 'QxPidResumen' could not be found`.

- [ ] **Step 3: Crear `QxPidResumen.cs`**

```csharp
// ============================================================================
// QxPidResumen.cs — estadistica por motor de una sesion de registro.
//
// Existe para no tener que abrir 14 CSV buscando cual se porta distinto. Un
// motor con el triple de desvio y load_pct promedio de 86% con un tercio del
// tiempo saturado NO se arregla con Kp: es fuerza mecanica de mas.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.QuantiX
{
    public sealed class QxPidResumen
    {
        public string Uid { get; set; }
        public int MotorIdx { get; set; }
        public string Nombre { get; set; }

        /// <summary>RPM reales promedio (ignora las muestras sin dato).</summary>
        public double RpmProm { get; set; }

        /// <summary>Desvio estandar de las RPM reales. Es el numero que delata
        /// al PID mal sintonizado cuando la velocidad estuvo estable.</summary>
        public double RpmDesvio { get; set; }

        /// <summary>|real - target| / target promedio, en %.</summary>
        public double ErrorMedioPct { get; set; }

        public double LoadPromPct { get; set; }

        /// <summary>% de muestras con load_pct >= 99. Si es alto, el motor vive
        /// contra el techo y el PID no tiene margen para corregir.</summary>
        public double TiempoSaturadoPct { get; set; }

        public static QxPidResumen Calcular(IList<QxPidSample> ms)
        {
            var r = new QxPidResumen();
            if (ms == null || ms.Count == 0) return r;

            r.Uid = ms[0].Uid;
            r.MotorIdx = ms[0].MotorIdx;
            r.Nombre = ms[0].Nombre;

            double sumaRpm = 0, sumaLoad = 0, sumaErr = 0;
            int nRpm = 0, nErr = 0, nSat = 0;

            for (int i = 0; i < ms.Count; i++)
            {
                sumaLoad += ms[i].LoadPct;
                if (ms[i].LoadPct >= 99) nSat++;
                if (!ms[i].RpmReal.HasValue) continue;   // sin dato no promedia como cero
                sumaRpm += ms[i].RpmReal.Value;
                nRpm++;
                if (ms[i].RpmTarget > 0)
                {
                    sumaErr += Math.Abs(ms[i].RpmReal.Value - ms[i].RpmTarget) / ms[i].RpmTarget * 100.0;
                    nErr++;
                }
            }

            r.LoadPromPct = sumaLoad / ms.Count;
            r.TiempoSaturadoPct = nSat * 100.0 / ms.Count;
            if (nErr > 0) r.ErrorMedioPct = sumaErr / nErr;
            if (nRpm == 0) return r;

            r.RpmProm = sumaRpm / nRpm;

            double sumaSq = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                if (!ms[i].RpmReal.HasValue) continue;
                double d = ms[i].RpmReal.Value - r.RpmProm;
                sumaSq += d * d;
            }
            r.RpmDesvio = Math.Sqrt(sumaSq / nRpm);
            return r;
        }
    }
}
```

- [ ] **Step 4: Acumular por motor y escribir el sidecar**

En `QxPidRecorder.cs`, agregar el acumulador junto a `_writers`:

```csharp
        private readonly Dictionary<string, List<QxPidSample>> _porMotor =
            new Dictionary<string, List<QxPidSample>>(StringComparer.OrdinalIgnoreCase);
```

En `FlushAhora()`, dentro del `for` que escribe filas, después del `w.WriteLine(...)`:

```csharp
                    string clave = (lote[i].Uid ?? "sin-uid") + "_m" +
                        lote[i].MotorIdx.ToString(CultureInfo.InvariantCulture);
                    List<QxPidSample> acc;
                    if (!_porMotor.TryGetValue(clave, out acc))
                    {
                        acc = new List<QxPidSample>();
                        _porMotor[clave] = acc;
                    }
                    acc.Add(lote[i]);
```

En `CerrarSesion()`, después de cerrar los writers y antes de borrar la carpeta huérfana:

```csharp
            if (_huboMuestras) EscribirSidecar();
```

Y el método:

```csharp
        /// <summary>sesion.json: cuando arranco, con que version, y el resumen por
        /// motor. Es lo que permite decir con certeza "esta curva salio con Kp=2"
        /// tres semanas despues.</summary>
        private void EscribirSidecar()
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"inicio\": \"").Append(_inicio.ToString("o", inv)).Append("\",\n");
                sb.Append("  \"fin\": \"").Append(DateTime.Now.ToString("o", inv)).Append("\",\n");
                sb.Append("  \"hz\": 5,\n");
                sb.Append("  \"motores\": [\n");

                bool primero = true;
                foreach (var kv in _porMotor)
                {
                    var r = QxPidResumen.Calcular(kv.Value);
                    if (!primero) sb.Append(",\n");
                    primero = false;
                    sb.Append("    { \"uid\": \"").Append(r.Uid ?? "").Append("\"");
                    sb.Append(", \"m\": ").Append(r.MotorIdx.ToString(inv));
                    sb.Append(", \"nombre\": \"").Append(r.Nombre ?? "").Append("\"");
                    sb.Append(", \"rpm_prom\": ").Append(r.RpmProm.ToString("F1", inv));
                    sb.Append(", \"rpm_desvio\": ").Append(r.RpmDesvio.ToString("F1", inv));
                    sb.Append(", \"error_medio_pct\": ").Append(r.ErrorMedioPct.ToString("F1", inv));
                    sb.Append(", \"load_prom_pct\": ").Append(r.LoadPromPct.ToString("F0", inv));
                    sb.Append(", \"tiempo_saturado_pct\": ").Append(r.TiempoSaturadoPct.ToString("F0", inv));
                    sb.Append(" }");
                }
                sb.Append("\n  ]\n}\n");

                File.WriteAllText(Path.Combine(SesionDir, "sesion.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { } // silencioso: fallback de I/O del propio logger
        }
```

Limpiar `_porMotor` junto a `_writers.Clear()`:

```csharp
            _porMotor.Clear();
```

(Ojo con el orden: `EscribirSidecar()` va **antes** de `_porMotor.Clear()`.)

- [ ] **Step 5: Guardar la config del PID de cada motor en el sidecar**

Esto es lo que el spec pide y sin lo cual el registro pierde la mitad del valor:
**el resumen dice cómo se portó el motor, la config dice con qué ganancias.**
Sin ella, tres semanas después no se puede saber si esa curva salió con Kp=2 o
con Kp=3, que es exactamente la comparación que se quiere hacer.

Agregar el overload de `AbrirSesion` en `QxPidRecorder.cs`:

```csharp
        private MotoresConfig _cfgSesion;

        /// <summary>Igual que AbrirSesion(), guardando ademas la config con la que
        /// corrio esta sesion: es lo que permite decir "esta curva salio con
        /// Kp=2" cuando se la mira semanas despues.</summary>
        public void AbrirSesion(MotoresConfig cfg)
        {
            _cfgSesion = cfg;
            AbrirSesion();
        }
```

Y en `EscribirSidecar()`, entre `"hz": 5,` y `"motores": [`:

```csharp
                sb.Append("  \"config\": [\n");
                bool primeroCfg = true;
                if (_cfgSesion != null && _cfgSesion.Nodos != null)
                {
                    foreach (var nodo in _cfgSesion.Nodos)
                    {
                        if (nodo == null || nodo.Motores == null) continue;
                        for (int i = 0; i < nodo.Motores.Length; i++)
                        {
                            var mc = nodo.Motores[i];
                            if (mc == null) continue;
                            if (!primeroCfg) sb.Append(",\n");
                            primeroCfg = false;
                            sb.Append("    { \"uid\": \"").Append(nodo.Uid ?? "").Append("\"");
                            sb.Append(", \"m\": ").Append(i.ToString(inv));
                            sb.Append(", \"nombre\": \"").Append(mc.Nombre ?? "").Append("\"");
                            sb.Append(", \"kp\": ").Append(mc.Kp.ToString("F2", inv));
                            sb.Append(", \"ki\": ").Append(mc.Ki.ToString("F2", inv));
                            sb.Append(", \"kd\": ").Append(mc.Kd.ToString("F2", inv));
                            sb.Append(", \"pwm_min\": ").Append(mc.PwmMin.ToString(inv));
                            sb.Append(", \"pwm_max\": ").Append(mc.PwmMax.ToString(inv));
                            sb.Append(", \"deadband\": ").Append(mc.Deadband.ToString(inv));
                            sb.Append(", \"slew_rate\": ").Append(mc.SlewRate.ToString(inv));
                            sb.Append(", \"ff_gain\": ").Append(mc.FFGain.ToString("F2", inv));
                            sb.Append(", \"alpha\": ").Append(mc.Alpha.ToString("F2", inv));
                            sb.Append(", \"max_integral\": ").Append(mc.MaxIntegral.ToString("F2", inv));
                            sb.Append(", \"target_slew_hz_s\": ").Append(mc.TargetSlewHzPerSec.ToString("F0", inv));
                            sb.Append(", \"pid_time\": ").Append(mc.PIDTime.ToString(inv));
                            sb.Append(", \"dientes\": ").Append(mc.DientesEngranaje.ToString(inv));
                            sb.Append(", \"sem_vuelta\": ").Append(mc.SemillasVuelta.ToString("F1", inv));
                            sb.Append(", \"meter_cal\": ").Append(mc.MeterCal.ToString("F3", inv));
                            sb.Append(" }");
                        }
                    }
                }
                sb.Append("\n  ],\n");
```

Test que lo fija:

```csharp
        [Test]
        public void El_sidecar_guarda_las_ganancias_con_las_que_corrio()
        {
            string dir = DirTemp();
            var cfg = new MotoresConfig();
            cfg.Nodos.Add(new QxNodoConfig
            {
                Uid = "A4CF12AB9E30",
                Motores = new[] { new QxMotorConfig { Nombre = "Cuerpo 1", Kp = 2, Ki = 30, Kd = 0 } }
            });

            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion(cfg);
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.FlushAhora();
            string sesion = rec.SesionDir;
            rec.CerrarSesion();

            string json = File.ReadAllText(Path.Combine(sesion, "sesion.json"));
            Assert.That(json, Does.Contain("\"kp\": 2.00"), "sin las ganancias no se puede comparar una corrida con otra");
            Directory.Delete(dir, true);
        }
```

En Task 5, el bridge pasa la config al abrir:

```csharp
                _pid.AbrirSesion(_motores);
```

- [ ] **Step 6: Correr y verificar que pasan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: los 13 tests PASAN.

- [ ] **Step 7: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidResumen.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs
git commit -m "feat(quantix): resumen por motor y ganancias del PID en sesion.json"
```

---

### Task 5: Enganchar el bridge

El tick ya calculó todo. Se le suma armar la muestra y encolarla.

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs` (campo + `Start()` + `Stop()` + el tick después del bloque de log detallado, ~`:553`)

**Interfaces:**
- Consumes: `QxPidRecorder`, `QxPidSample` (Task 2-4), `MotorLive.LoadPct` (Task 1).
- Produces: `QxPidRecorder.Instance` con una sesión abierta, consumido por Task 6.

- [ ] **Step 1: Agregar `GetMotorLive` junto a `GetPpsReal`**

`GetPpsReal` ya recorre el registry pero devuelve solo el pps. Para el registro
hacen falta rpm, pwm y load_pct del mismo `MotorLive`, y no tiene sentido
recorrer tres veces.

En `QuantiXMotorBridge.cs`, después de `GetPpsReal`:

```csharp
        /// <summary>El MotorLive completo del registry, o null si el nodo no
        /// publica. null NO es lo mismo que un motor quieto: por eso el registro
        /// escribe vacio y no cero cuando esto devuelve null.</summary>
        public AgroParallel.Models.MotorLive GetMotorLive(string uid, int motorIdx)
        {
            if (_nodos == null || string.IsNullOrEmpty(uid)) return null;
            try
            {
                var all = _nodos.GetAll();
                for (int i = 0; i < all.Count; i++)
                {
                    var n = all[i];
                    if (n == null || !string.Equals(n.Uid, uid, StringComparison.OrdinalIgnoreCase)) continue;
                    var ml = n.MotorsLive;
                    if (ml != null)
                        for (int m = 0; m < ml.Count; m++)
                            if (ml[m] != null && ml[m].Id == motorIdx) return ml[m];
                    return null;
                }
            }
            catch { }
            return null;
        }
```

- [ ] **Step 2: Abrir la sesión al arrancar el bridge**

En `QuantiXMotorBridge.cs`, agregar el campo junto a `_timer`:

```csharp
        private QxPidRecorder _pid;
```

En `Start()`, justo antes de `_timer.Start()` (después del `Log("Iniciado con ...")`):

```csharp
            // Registro de PID: siempre activo, los 14 motores. El diagnostico se
            // hace comparando motores entre si, asi que un motor quieto mientras
            // los otros giran tambien es dato.
            try
            {
                _pid = new QxPidRecorder(AgroParallel.Common.AgpPaths.ConfigRoot);
                _pid.AbrirSesion();
                QxPidRecorder.Instance = _pid;
            }
            catch (Exception ex) { Log("registro de PID no disponible: " + ex.Message); }
```

En `Stop()`, junto al `Dispose()` de los timers:

```csharp
            if (_pid != null) { _pid.CerrarSesion(); QxPidRecorder.Instance = null; _pid = null; }
```

- [ ] **Step 3: Registrar la muestra en el tick**

En `QuantiXMotorBridge.cs`, inmediatamente después del bloque
`if (MessagesSent % 25 == 0 && mi == 0) { ... }` (el log detallado) y antes de
`// Filtrado + banda muerta`:

```csharp
                        // Registro de PID (no toca disco: encola y vuelve).
                        if (_pid != null)
                        {
                            var live = GetMotorLive(nodo.Uid, mi);
                            _pid.Registrar(new QxPidSample
                            {
                                Uid = nodo.Uid,
                                MotorIdx = mi,
                                Nombre = motor.Nombre,
                                RpmReal = live != null ? (double?)live.Rpm : null,
                                RpmTarget = QxPulseCalculator.Rpm(pps, motor.DientesEngranaje),
                                PpsReal = live != null ? live.PpsReal : 0,
                                PpsTarget = pps,
                                Pwm = live != null ? live.Pwm : 0,
                                LoadPct = live != null ? live.LoadPct : 0,
                                VelMotorKmh = velMotorKmh,
                                VelGpsKmh = snap.AvgSpeed,
                                Dosis = dosisEfectiva,
                                SeccionOn = seccionOn,
                            });
                        }
```

- [ ] **Step 4: Compilar**

```
dotnet build SourceCode/AgroParallel/Core/AgroParallel.Services/AgroParallel.Services.csproj
```

Esperado: `Build succeeded`, 0 errores.

- [ ] **Step 5: Correr toda la suite**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj
```

Esperado: todos PASAN (los previos del repo incluidos).

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs
git commit -m "feat(quantix): el bridge registra la muestra de PID en cada tick"
```

---

### Task 6: Buffer rodante y endpoint `/api/quantix/graph-pid`

El panel en vivo lee del mismo recorder, sin volver a leer el disco.

**Files:**
- Modify: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs`
- Create: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/QuantiXPidController.cs`
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs:344`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs`

**Interfaces:**
- Consumes: `QxPidRecorder.Instance` de Task 5.
- Produces: `GET /api/quantix/graph-pid?uid=<uid>&m=<idx>` → `{ ok, motores:[{uid,m,nombre}], muestras:[{t_s,rpm_real,rpm_target,vel_motor,load_pct}] }`; `POST /api/quantix/pid-marca` con `{ "texto": "kp=3" }`.

- [ ] **Step 1: Escribir el test que falla**

```csharp
        [Test]
        public void El_buffer_rodante_guarda_las_ultimas_300_muestras()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            for (int i = 0; i < 400; i++)
            {
                var m = Muestra("A4CF12AB9E30", 0);
                m.RpmReal = i;
                rec.Registrar(m);
            }
            rec.FlushAhora();

            var buf = rec.BufferDe("A4CF12AB9E30", 0);
            Assert.That(buf.Count, Is.EqualTo(QxPidRecorder.MaxBuffer));
            Assert.That(buf[buf.Count - 1].RpmReal.Value, Is.EqualTo(399), "la ultima es la mas nueva");
            Assert.That(buf[0].RpmReal.Value, Is.EqualTo(100), "las viejas se descartan");
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void Motores_lista_lo_que_se_vio_en_la_sesion()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.Registrar(Muestra("7B2E0091CC14", 1));
            rec.FlushAhora();

            var ms = rec.Motores();
            Assert.That(ms.Count, Is.EqualTo(2));
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }
```

- [ ] **Step 2: Correr y verificar que fallan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: FALLA al compilar — `'QxPidRecorder' does not contain a definition for 'BufferDe'`.

- [ ] **Step 3: Agregar el buffer rodante**

En `QxPidRecorder.cs`, junto a `_porMotor`:

```csharp
        /// <summary>Muestras que ve el panel en vivo: 300 a 5 Hz son 60 segundos,
        /// suficiente para ver un ciclo de oscilacion lento.</summary>
        public const int MaxBuffer = 300;

        private readonly Dictionary<string, Queue<QxPidSample>> _buffer =
            new Dictionary<string, Queue<QxPidSample>>(StringComparer.OrdinalIgnoreCase);
```

En `FlushAhora()`, junto al acumulador `_porMotor` (mismo `for`, misma `clave`):

```csharp
                    lock (_lock)
                    {
                        Queue<QxPidSample> q;
                        if (!_buffer.TryGetValue(clave, out q))
                        {
                            q = new Queue<QxPidSample>(MaxBuffer);
                            _buffer[clave] = q;
                        }
                        q.Enqueue(lote[i]);
                        while (q.Count > MaxBuffer) q.Dequeue();
                    }
```

**El `lock` no es decorativo.** `FlushAhora()` corre en el timer de flush y
`BufferDe()` en el hilo del servidor HTTP cuando el panel pide datos. Sin el
lock, una petición del panel que caiga justo durante un flush puede leer el
`Queue` mientras se le está haciendo `Enqueue`/`Dequeue` — `InvalidOperationException`
o peor. Los otros diccionarios (`_writers`, `_porMotor`) los toca solo el hilo
de flush, así que no lo necesitan.

Y los dos accesores:

```csharp
        /// <summary>Ultimas muestras de un motor, de la mas vieja a la mas nueva.</summary>
        public List<QxPidSample> BufferDe(string uid, int motorIdx)
        {
            string clave = (uid ?? "sin-uid") + "_m" + motorIdx.ToString(CultureInfo.InvariantCulture);
            lock (_lock)
            {
                Queue<QxPidSample> q;
                if (!_buffer.TryGetValue(clave, out q)) return new List<QxPidSample>();
                return new List<QxPidSample>(q);
            }
        }

        /// <summary>Motores vistos en esta sesion, para el selector del panel.</summary>
        public List<QxPidSample> Motores()
        {
            var res = new List<QxPidSample>();
            lock (_lock)
            {
                foreach (var kv in _buffer)
                    if (kv.Value.Count > 0) res.Add(kv.Value.Peek());
            }
            return res;
        }
```

Limpiar `_buffer` junto a `_porMotor.Clear()`:

```csharp
            _buffer.Clear();
```

- [ ] **Step 4: Correr y verificar que pasan**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj --filter QxPidRecorderTests
```

Esperado: los 14 tests PASAN.

- [ ] **Step 5: Crear el controller**

`SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/QuantiXPidController.cs`:

```csharp
// ============================================================================
// QuantiXPidController.cs
//   GET  /api/quantix/graph-pid?uid=<uid>&m=<idx>  -> ultimas muestras del motor
//   POST /api/quantix/pid-marca                    -> clava una marca en los 14
//
// Lee del buffer en memoria del recorder, no del disco: es el mismo dato que se
// esta escribiendo, sin volver a leerlo.
// ============================================================================

using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.QuantiX;
using EmbedIO;
using EmbedIO.Routing;

namespace AgroParallel.WebHost.Controllers
{
    public sealed class QuantiXPidController : AgpControllerBase
    {
        public sealed class MarcaDto
        {
            public string Texto { get; set; }
        }

        [Route(HttpVerbs.Get, "/quantix/graph-pid")]
        public Task GetGraphPid([QueryField] string uid, [QueryField] int m)
        {
            var rec = QxPidRecorder.Instance;
            if (rec == null) return WriteJsonAsync(new { ok = false, motivo = "sin_registro" });

            var motores = new List<object>();
            foreach (var s in rec.Motores())
                motores.Add(new { uid = s.Uid, m = s.MotorIdx, nombre = s.Nombre });

            var muestras = new List<object>();
            if (!string.IsNullOrEmpty(uid))
            {
                var buf = rec.BufferDe(uid, m);
                for (int i = 0; i < buf.Count; i++)
                    muestras.Add(new
                    {
                        rpm_real = buf[i].RpmReal,
                        rpm_target = buf[i].RpmTarget,
                        vel_motor = buf[i].VelMotorKmh,
                        vel_gps = buf[i].VelGpsKmh,
                        load_pct = buf[i].LoadPct,
                    });
            }

            return WriteJsonAsync(new { ok = true, motores = motores, muestras = muestras });
        }

        [Route(HttpVerbs.Post, "/quantix/pid-marca")]
        public async Task PostMarca()
        {
            var dto = await HttpContext.GetRequestDataAsync<MarcaDto>().ConfigureAwait(false);
            var rec = QxPidRecorder.Instance;
            if (rec == null) { await WriteJsonAsync(new { ok = false, motivo = "sin_registro" }).ConfigureAwait(false); return; }
            rec.Marcar(dto != null ? dto.Texto : null);
            await WriteJsonAsync(new { ok = true }).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 6: Registrar el controller**

En `AgpWebHost.cs`, en la cadena de `.WithController(...)`, después de
`.WithController(() => new SectionXController(_sectionxCfg))`:

```csharp
                 .WithController(() => new QuantiXPidController())
```

- [ ] **Step 7: Compilar y verificar**

```
dotnet build SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgroParallel.WebHost.csproj
```

Esperado: `Build succeeded`, 0 errores.

- [ ] **Step 8: Commit**

```bash
git add SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QxPidRecorder.cs \
        SourceCode/AgroParallel/Web/AgroParallel.WebHost/Controllers/QuantiXPidController.cs \
        SourceCode/AgroParallel/Web/AgroParallel.WebHost/AgpWebHost.cs \
        SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/QxPidRecorderTests.cs
git commit -m "feat(quantix): endpoint del grafico de PID sobre buffer en memoria"
```

---

### Task 7: Panel en vivo con selector de motor y botón de marca

**Files:**
- Modify: `SourceCode/PilotX.UI/Views/GraficoLineal.cs:85` (volver `MaxPuntos` configurable)
- Create: `SourceCode/PilotX.UI/Views/GraficoQuantiXPanel.axaml` y `.axaml.cs`
- Modify: `SourceCode/PilotX.UI/Services/GraficosClient.cs` (DTO + GET)
- Modify: `SourceCode/PilotX.UI/MainWindow.axaml:1247` y `MainWindow.axaml.cs:420`
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

**Interfaces:**
- Consumes: `GET /api/quantix/graph-pid`, `POST /api/quantix/pid-marca` de Task 6.
- Produces: panel `GraficoQuantiXPanel` con `x:Name="GrafQuantiXHost"`.

- [ ] **Step 1: Volver `MaxPuntos` configurable por instancia**

En `GraficoLineal.cs`, `MaxPuntos` es `const int = 120` — a 5 Hz son 24 s, poco
para ver un ciclo lento. `SerieGrafico` lo usa como tope del buffer.

Reemplazar la constante por una propiedad de instancia con el mismo default, y
que `SerieGrafico` reciba su tope en el constructor:

```csharp
    /// <summary>Tope del buffer de la serie. Default 120 (el MAX_POINTS
    /// historico de los cuatro graficos); el de QuantiX lo sube a 300 porque a
    /// 5 Hz eso son 60 s y un ciclo de oscilacion lento no entra en 24.</summary>
    public const int MaxPuntosDefault = 120;
```

`SerieGrafico` pasa a:

```csharp
    private readonly int _tope;

    public SerieGrafico(int tope = GraficoLineal.MaxPuntosDefault) { _tope = tope; }

    public void Empujar(double v)
    {
        _datos.Add(v);
        while (_datos.Count > _tope) _datos.RemoveAt(0);
    }
```

y en `GraficoLineal`:

```csharp
    /// <summary>Divisor del eje X: cambia la escala de tiempo del grafico.</summary>
    public int MaxPuntos { get; set; } = MaxPuntosDefault;
```

Sustituir los usos internos de `MaxPuntos` como constante por la propiedad.

- [ ] **Step 2: Verificar que los cuatro paneles existentes siguen compilando igual**

```
dotnet build SourceCode/PilotX.UI/PilotX.UI.csproj
```

Esperado: `Build succeeded`, 0 errores. Los cuatro paneles no pasan `tope`, así
que siguen en 120.

- [ ] **Step 3: Agregar el DTO y el GET al cliente**

En `GraficosClient.cs`, junto a los otros DTO:

```csharp
/// <summary>Una muestra del grafico de PID de QuantiX.</summary>
public sealed class PidGraphSampleDto
{
    [JsonPropertyName("rpm_real")]   public double? RpmReal   { get; set; }
    [JsonPropertyName("rpm_target")] public double? RpmTarget { get; set; }
    [JsonPropertyName("vel_motor")]  public double? VelMotor  { get; set; }
    [JsonPropertyName("load_pct")]   public int?    LoadPct   { get; set; }
}

/// <summary>Un motor del selector.</summary>
public sealed class PidMotorDto
{
    [JsonPropertyName("uid")]    public string Uid    { get; set; }
    [JsonPropertyName("m")]      public int    M      { get; set; }
    [JsonPropertyName("nombre")] public string Nombre { get; set; }
}

public sealed class PidGraphRespDto
{
    [JsonPropertyName("ok")]       public bool                 Ok       { get; set; }
    [JsonPropertyName("motores")]  public List<PidMotorDto>    Motores  { get; set; }
    [JsonPropertyName("muestras")] public List<PidGraphSampleDto> Muestras { get; set; }
}
```

Y el método, siguiendo el patrón de los otros GET del archivo (devuelve `null`
si el motor no contesta o contesta != 2xx):

```csharp
    public Task<PidGraphRespDto> GetPidAsync(string uid, int m, CancellationToken ct)
    {
        string q = "/api/quantix/graph-pid?uid=" + Uri.EscapeDataString(uid ?? "") + "&m=" + m;
        return GetAsync<PidGraphRespDto>(q, ct);
    }
```

- [ ] **Step 4: Crear el panel**

`GraficoQuantiXPanel.axaml` copia la estructura de `GraficoXtePanel.axaml`
(mismo encabezado, mismo `GraficoLineal`, mismos tokens del design system:
`#4ABA3E` verde, `#E2E7E2` borde, `#F5F7F4` fondo, `#101612` texto, `#535E54`
texto suave), y le agrega arriba un `ComboBox x:Name="SelectorMotor"` y un
`Button x:Name="BtnMarca"` con el texto `Marcar`.

En `GraficoQuantiXPanel.axaml.cs`, tres series sobre un `GraficoLineal` con
`MaxPuntos = 300` y `Modo = ModoEjeGrafico.RangoAuto` (las rpm son valores
absolutos, no van centradas en cero):

```csharp
    private readonly SerieGrafico _rpmReal   = new SerieGrafico(300) { Color = Color.Parse("#4ABA3E") };
    private readonly SerieGrafico _rpmTarget = new SerieGrafico(300) { Color = Color.Parse("#535E54") };
    private readonly SerieGrafico _velMotor  = new SerieGrafico(300) { Color = Color.Parse("#C5CFC5") };
```

El polling sigue el patrón de los otros cuatro paneles (mismo `GraficosClient`,
mismo `DispatcherTimer`). **El `HttpClient.Timeout` no se usa para cortar el
polling**: el `catch` va con `when (ct.IsCancellationRequested)` como en el
resto del archivo, si no el timeout mata el lazo.

El botón hace `POST /api/quantix/pid-marca` con el texto
`"marca " + DateTime.Now.ToString("HH:mm:ss")`.

- [ ] **Step 5: Montar el panel en la ventana**

En `MainWindow.axaml`, junto a los otros cuatro (después de
`<vw:GraficoCorreccionPanel .../>`):

```xml
                <vw:GraficoQuantiXPanel x:Name="GrafQuantiXHost" Grid.Row="1" ZIndex="10"
                                        IsVisible="False" />
```

Y en `MainWindow.axaml.cs`, junto a los otros cuatro campos:

```csharp
    private GraficoQuantiXPanel? _grafQuantiXHost;
```

resolviéndolo en el mismo lugar donde se resuelven `_grafXteHost` y compañía.

- [ ] **Step 6: Actualizar el manual de cabina**

`ayuda.html` es el manual que dice **dónde se hace cada cosa**. Este cambio
agrega una pantalla nueva que el operario ve, así que la ayuda se actualiza en
el mismo commit (regla del CLAUDE.md del repo).

Verificar la ruta real en `MainWindow.axaml` antes de escribirla — no de
memoria — y agregar la entrada del gráfico de QuantiX junto a las de los otros
cuatro gráficos, explicando qué mira: **si la velocidad del motor está estable y
las rpm oscilan alrededor del target, hay que corregir el PID; si el motor está
al 100% de carga, no alcanza con tocar las ganancias.**

- [ ] **Step 7: Compilar**

```
dotnet build SourceCode/PilotX.UI/PilotX.UI.csproj
```

Esperado: `Build succeeded`, 0 errores.

- [ ] **Step 8: Commit**

```bash
git add SourceCode/PilotX.UI/ SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html
git commit -m "feat(quantix): panel en vivo del PID con selector de motor y marca"
```

---

### Task 8: Build completo y verificación de arranque

Un `dotnet build` verde no prueba que PilotX arranque. Esto maneja una máquina
que siembra: hay que verlo levantar.

- [ ] **Step 1: Build completo**

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Esperado: termina sin errores y actualiza `Build\Engine\` y `Build\Desktop\`.

- [ ] **Step 2: Suite completa de tests**

```
dotnet test SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/AgroParallel.Services.Tests.csproj
```

Esperado: todos PASAN.

- [ ] **Step 3: Chequear que los puertos estén libres antes de lanzar**

```
netstat -ano | Select-String ":5180|:1883"
```

Esperado: sin resultados. Si aparece `python.exe` en 5180 o un `node.exe` en
1883, matarlos primero — pelean por el puerto.

- [ ] **Step 4: Levantar el Engine y la pantalla**

```
Build\Engine\PilotX.GuidanceEngine.exe --webhost --corex
Build\Desktop\PilotX.Desktop.exe
```

Esperado: broker en 1883, API en 5180, panel en 5181, ventana "PilotX"
respondiendo.

- [ ] **Step 5: Verificar el endpoint nuevo**

```
Invoke-WebRequest http://127.0.0.1:5180/api/quantix/graph-pid -UseBasicParsing
```

Esperado: 200 con `{"ok":true,...}` (o `{"ok":false,"motivo":"sin_registro"}` si
el bridge no arrancó por no haber nodos configurados — también es válido).

- [ ] **Step 6: Verificar que la sesión se creó en disco**

```
Get-ChildItem Build\Engine\pid-quantix -Recurse
```

Esperado: una carpeta `yyyy-MM-dd_HHmm`. **Sin nodos QuantiX reales no va a
haber CSV** — el recorder solo escribe cuando el bridge le pasa muestras, y sin
nodos no hay muestras. Eso es correcto, no un error. Decirlo así en el reporte
en vez de dar por probado lo que no se probó.

- [ ] **Step 7: Capturar pantalla y mirarla**

Screenshot de la ventana. Un frame en blanco es un fallo de arranque, no un
éxito.

- [ ] **Step 8: Commit del build**

```bash
git add Installer/VERSION
git commit -m "chore: build con el registro de PID de QuantiX"
```

---

## Verificación honesta antes de cerrar

Lo que **sí** queda probado con esto: que compila, que los tests pasan, que
PilotX levanta, que el endpoint contesta y que la sesión se crea.

Lo que **no** queda probado y hay que decirlo: que el CSV tenga datos reales de
los 14 motores, que el `load_pct` del firmware llegue con el valor esperado, y
que el panel dibuje las curvas. Eso necesita nodos QuantiX conectados y se
valida en el banco o en LAS GRINGAS. **No declarar cerrado lo que no se probó
en cabina** — un ítem del tablero marcado "anda" sin probar es peor que dejarlo
pendiente.
