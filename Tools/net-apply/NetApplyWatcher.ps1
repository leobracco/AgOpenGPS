# ============================================================
# Copia endurecida de ViewX/Software/TabletTools/NetApplyWatcher.ps1 — si
# cambia el protocolo, actualizar las dos.
# Va embebida en PilotX-Instalador.exe y la sirve el provision-server (red.ps1).
#
# NetApplyWatcher (corre como SYSTEM)
#
# Aplica configuracion de IP (DHCP o fija) que PilotX (usuario
# LIMITADO) no puede aplicar solo. PilotX deja un pedido en
#   C:\PilotX\netconfig\request.json
# este watcher (SYSTEM, admin) lo aplica y responde en
#   C:\PilotX\netconfig\result.json
#
# Pedido (JSON):
#   { "id":"<unico>", "ifIndex":10, "mode":"dhcp"|"static"|"primary",
#     "ip":"192.168.5.10", "prefix":24, "gateway":"192.168.5.1",
#     "dns":["8.8.8.8","1.1.1.1"] }
# Respuesta (JSON):
#   { "id":"<unico>", "ok":true|false, "error":"...", "ts":... }
#
# Instancia unica (mutex). Tarea SYSTEM al boot, sin limite.
# Log: %ProgramFiles%\PilotX Instalador\red\netapply.log (solo admin)
#
# Endurecimiento respecto del original (C:\PilotX es del usuario limitado
# pilotx: puede borrar/renombrar carpetas y poner junctions/symlinks):
#  1. Antes de CADA lectura/borrado/escritura se verifica que C:\PilotX,
#     C:\PilotX\netconfig y el archivo NO sean reparse points (Get-Item
#     -Force de cada componente). Si alguno lo es, se saltea el ciclo y se
#     avisa en el log de admin; no se toca nada.
#  2. Ademas, mientras se trabaja, las dos carpetas quedan abiertas sin
#     permitir borrarlas/renombrarlas, y se comprueba con el handle que la
#     ruta final es exactamente la esperada (no se puede cambiar por un
#     enlace a mitad de camino).
#  3. Los archivos se abren por ruta exacta sin seguir enlaces
#     (FILE_FLAG_OPEN_REPARSE_POINT) y se rechazan los reparse points y los
#     hardlinks (mas de un nombre). El pedido se borra por el mismo handle
#     con el que se leyo. result.json se escribe en un temporal nuevo
#     (CREATE_NEW) dentro de la carpeta verificada y se renombra encima.
#  4. El watcher NO crea C:\PilotX\netconfig (lo crea el instalador o PilotX).
#  5. El log va a una carpeta solo-admin; si falta, se crea con permisos
#     solo para Administradores y SYSTEM.
#  6. Se validan los datos: ifIndex entero de un adaptador existente, mode en
#     {dhcp, static, primary}, IPv4 con ida y vuelta exacta, prefijo 1..32.
#     Si algo no da, se responde ok=false con el motivo y no se toca la red.
#
# -SoloFunciones: define las funciones y vuelve, sin arrancar el ciclo (lo
# usa Probar-NetApplyWatcher.ps1 con dot-sourcing).
# ============================================================
param([switch]$SoloFunciones)

$Raiz = 'C:\PilotX'
$Dir = Join-Path $Raiz 'netconfig'
$Req = Join-Path $Dir 'request.json'
$Res = Join-Path $Dir 'result.json'
$DirLog = Join-Path $env:ProgramFiles 'PilotX Instalador\red'
$Log = Join-Path $DirLog 'netapply.log'
$MaxPedido = 64KB

# ── Validacion (pura, testeada en Probar-NetApplyWatcher.ps1) ────────────────

# IPv4 en forma canonica: parsea, es InterNetwork y al volver a texto da
# exactamente lo mismo (descarta "192.168.5", "010.0.0.1", "1.2.3.4 x", IPv6...).
function Test-IPv4Exacta($s) {
    if (-not ($s -is [string])) { return $false }
    if ($s.Length -lt 7 -or $s.Length -gt 15) { return $false }
    $a = $null
    if (-not [System.Net.IPAddress]::TryParse($s, [ref]$a)) { return $false }
    if ($a.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    return ($a.ToString() -ceq $s)
}

function Test-Entero($v) {
    return ($v -is [int] -or $v -is [long] -or $v -is [int16] -or $v -is [byte])
}

# prefijo (/24) -> mascara punteada (255.255.255.0)
function MascaraDe([int]$p) {
    if ($p -lt 1 -or $p -gt 32) { throw "prefijo invalido: $p" }
    $bits = ('1' * $p).PadRight(32, '0')
    $oct = for ($i = 0; $i -lt 4; $i++) { [Convert]::ToInt32($bits.Substring($i * 8, 8), 2) }
    return ($oct -join '.')
}

# id del pedido: se devuelve tal cual en result.json (PilotX lo compara).
function Test-IdPedido($id) {
    return ($id -is [string] -and $id -cmatch '^[A-Za-z0-9._-]{1,64}$')
}

# Valida el pedido ya parseado. $indices = ifIndex de los adaptadores que
# existen. Devuelve @{ ok; error; pedido } con el pedido normalizado (solo
# los campos que se usan, ya validados).
function Test-Pedido($r, $indices) {
    $mal = { param($m) @{ ok = $false; error = $m; pedido = $null } }
    if ($null -eq $r -or -not ($r -is [System.Management.Automation.PSCustomObject])) { return (& $mal 'pedido invalido: no es un objeto JSON') }
    if (-not (Test-Entero $r.ifIndex)) { return (& $mal 'ifIndex invalido: tiene que ser un numero entero') }
    $idx = [long]$r.ifIndex
    if ($idx -lt 1 -or $idx -gt [int]::MaxValue) { return (& $mal "ifIndex invalido: $idx") }
    if (-not (@($indices) -contains [int]$idx)) { return (& $mal "no existe un adaptador de red con ifIndex $idx") }
    $mode = $r.mode
    if (-not ($mode -is [string]) -or -not (@('dhcp', 'static', 'primary') -ccontains $mode)) {
        return (& $mal 'modo invalido: tiene que ser dhcp, static o primary')
    }
    $p = @{ ifIndex = [int]$idx; mode = $mode; ip = $null; prefix = 0; gateway = $null; dns = @() }
    if ($mode -ne 'static') { return @{ ok = $true; error = $null; pedido = $p } }

    $ip = if ($r.ip -is [string]) { $r.ip.Trim() } else { $r.ip }
    if (-not (Test-IPv4Exacta $ip)) { return (& $mal 'IP invalida: tiene que ser una IPv4 como 192.168.5.10') }
    if (-not (Test-Entero $r.prefix) -or [long]$r.prefix -lt 1 -or [long]$r.prefix -gt 32) {
        return (& $mal 'prefijo invalido: tiene que ser un entero de 1 a 32')
    }
    $gw = $r.gateway
    if ($gw -is [string]) { $gw = $gw.Trim() }
    if ($null -ne $gw -and $gw -ne '') {
        if (-not (Test-IPv4Exacta $gw)) { return (& $mal 'puerta de enlace invalida: tiene que ser una IPv4 como 192.168.5.1') }
    } else { $gw = $null }
    $dns = @()
    if ($null -ne $r.dns) {
        if (-not ($r.dns -is [array])) { return (& $mal 'DNS invalido: tiene que ser una lista') }
        if ($r.dns.Count -gt 8) { return (& $mal 'DNS invalido: como maximo 8 servidores') }
        foreach ($d in $r.dns) {
            $dd = if ($d -is [string]) { $d.Trim() } else { $d }
            if (-not (Test-IPv4Exacta $dd)) { return (& $mal 'DNS invalido: cada servidor tiene que ser una IPv4 como 8.8.8.8') }
            $dns += $dd
        }
    }
    $p.ip = $ip; $p.prefix = [int]$r.prefix; $p.gateway = $gw; $p.dns = $dns
    return @{ ok = $true; error = $null; pedido = $p }
}

# ── Rutas sin enlaces ────────────────────────────────────────────────────────

# $null si ninguna de las rutas es un reparse point (junction, symlink, mount
# point...); si no, el motivo. Las que no existen se reportan aparte
# ($faltaEsError) para distinguir "no hay pedido" de "algo raro".
function Get-ProblemaEnlace([string[]]$rutas) {
    foreach ($p in $rutas) {
        $it = Get-Item -LiteralPath $p -Force -EA SilentlyContinue
        if ($null -eq $it) { continue }
        if (($it.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            return "$p es un enlace (junction/symlink)"
        }
    }
    return $null
}

# Helper nativo: abre carpetas y archivos sin seguir enlaces y verifica por
# handle (atributos, cantidad de nombres y ruta final). C# 5 (PS 5.1).
$CodigoSeguro = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PilotXNetApply
{
    public static class Seguro
    {
        const uint FILE_LIST_DIRECTORY = 0x1, FILE_READ_ATTRIBUTES = 0x80, DELETE = 0x10000, SYNCHRONIZE = 0x100000;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint SHARE_READ = 1, SHARE_WRITE = 2, SHARE_DELETE = 4;
        const uint CREATE_NEW = 1, OPEN_EXISTING = 3;
        const uint FLAG_BACKUP_SEMANTICS = 0x02000000, FLAG_OPEN_REPARSE_POINT = 0x00200000, FLAG_WRITE_THROUGH = 0x80000000;
        const uint ATTR_DIRECTORY = 0x10, ATTR_REPARSE = 0x400;
        const uint MOVEFILE_REPLACE_EXISTING = 1, MOVEFILE_WRITE_THROUGH = 8;
        const int FileDispositionInfo = 4;
        const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_SHARING_VIOLATION = 32;

        [StructLayout(LayoutKind.Sequential)]
        struct INFO
        {
            public uint Atributos;
            public uint Creado1, Creado2, Accedido1, Accedido2, Escrito1, Escrito2;   // FILETIME = 2 DWORD (alineado a 4)
            public uint Volumen, TamAlto, TamBajo, Nombres, IndiceAlto, IndiceBajo;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string nombre, uint acceso, uint compartir, IntPtr sa, uint disp, uint flags, IntPtr plantilla);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle h, out INFO info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint largo, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int clase, ref byte borrar, uint largo);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileExW(string origen, string destino, uint flags);

        static IOException Error(string que, int codigo)
        {
            return new IOException(que + " (error " + codigo + ")");
        }

        static string RutaFinal(SafeFileHandle h)
        {
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) throw Error("no se pudo leer la ruta final", Marshal.GetLastWin32Error());
            var s = sb.ToString();
            if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return s;
            if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s.Substring(4);
            return s;
        }

        static INFO Info(SafeFileHandle h)
        {
            INFO i;
            if (!GetFileInformationByHandle(h, out i)) throw Error("no se pudieron leer los atributos", Marshal.GetLastWin32Error());
            return i;
        }

        // Verifica lo abierto: no es enlace, es (o no) carpeta, un solo nombre
        // (archivos) y la ruta final es EXACTAMENTE la pedida.
        static void Verificar(SafeFileHandle h, string ruta, bool carpeta)
        {
            var i = Info(h);
            if ((i.Atributos & ATTR_REPARSE) != 0) throw new IOException(ruta + " es un enlace (junction/symlink)");
            if (carpeta != ((i.Atributos & ATTR_DIRECTORY) != 0)) throw new IOException(ruta + (carpeta ? " no es una carpeta" : " es una carpeta"));
            if (!carpeta && i.Nombres != 1) throw new IOException(ruta + " tiene " + i.Nombres + " nombres (hardlink)");
            var final = RutaFinal(h);
            if (!string.Equals(final, Path.GetFullPath(ruta), StringComparison.OrdinalIgnoreCase))
                throw new IOException(ruta + " apunta a otro lado (" + final + ")");
        }

        // Abre la carpeta SIN dejar que se borre o renombre mientras el
        // handle este abierto (sin FILE_SHARE_DELETE) y la verifica.
        public static SafeFileHandle FijarCarpeta(string ruta)
        {
            var h = CreateFileW(ruta, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_READ | SHARE_WRITE,
                                IntPtr.Zero, OPEN_EXISTING, FLAG_BACKUP_SEMANTICS | FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) throw Error("no se pudo abrir " + ruta, Marshal.GetLastWin32Error());
            try { Verificar(h, ruta, true); }
            catch { h.Dispose(); throw; }
            return h;
        }

        static void Borrar(SafeFileHandle h)
        {
            byte si = 1;
            if (!SetFileInformationByHandle(h, FileDispositionInfo, ref si, 1))
                throw Error("no se pudo borrar", Marshal.GetLastWin32Error());
        }

        // Lee el archivo y lo borra por el MISMO handle (se borra lo que se
        // leyo, no lo que haya en la ruta despues). null si no esta o si
        // todavia lo esta escribiendo otro (se reintenta en el proximo ciclo).
        public static byte[] LeerYBorrar(string ruta, int maximo)
        {
            var h = CreateFileW(ruta, GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES, SHARE_READ, IntPtr.Zero, OPEN_EXISTING,
                                FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error();
                h.Dispose();
                if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND || e == ERROR_SHARING_VIOLATION) return null;
                throw Error("no se pudo abrir " + ruta, e);
            }
            using (h)
            {
                Verificar(h, ruta, false);
                var i = Info(h);
                long largo = ((long)i.TamAlto << 32) | i.TamBajo;
                byte[] datos = null;
                if (largo <= maximo)
                {
                    datos = new byte[largo];
                    using (var fs = new FileStream(new SafeFileHandle(h.DangerousGetHandle(), false), FileAccess.Read, 1))
                    {
                        int leidos = 0;
                        while (leidos < datos.Length)
                        {
                            int n = fs.Read(datos, leidos, datos.Length - leidos);
                            if (n <= 0) break;
                            leidos += n;
                        }
                        if (leidos != datos.Length) Array.Resize(ref datos, leidos);
                    }
                }
                Borrar(h);
                if (datos == null) throw new IOException(ruta + " es demasiado grande (" + largo + " bytes)");
                return datos;
            }
        }

        // Escribe en un temporal NUEVO de la carpeta (CREATE_NEW: nunca abre
        // algo que ya exista) y lo renombra encima del destino. Si el destino
        // existe y es un enlace o tiene otro nombre, no se toca y se avisa.
        public static void EscribirAtomico(string ruta, byte[] datos)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(ruta));
            var tmp = Path.Combine(dir, Path.GetFileName(ruta) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            RevisarDestino(ruta);
            var h = CreateFileW(tmp, GENERIC_WRITE | DELETE | FILE_READ_ATTRIBUTES, 0, IntPtr.Zero, CREATE_NEW,
                                FLAG_OPEN_REPARSE_POINT | FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (h.IsInvalid) throw Error("no se pudo crear " + tmp, Marshal.GetLastWin32Error());
            bool listo = false;
            try
            {
                Verificar(h, tmp, false);
                using (var fs = new FileStream(new SafeFileHandle(h.DangerousGetHandle(), false), FileAccess.Write, 1))
                {
                    fs.Write(datos, 0, datos.Length);
                    fs.Flush(true);
                }
                listo = true;
            }
            finally
            {
                if (!listo) { try { Borrar(h); } catch { } }
                h.Dispose();
            }
            RevisarDestino(ruta);
            if (!MoveFileExW(tmp, ruta, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            {
                int e = Marshal.GetLastWin32Error();
                BorrarTemporal(tmp);
                throw Error("no se pudo dejar " + ruta, e);
            }
        }

        static void RevisarDestino(string ruta)
        {
            var h = CreateFileW(ruta, FILE_READ_ATTRIBUTES, SHARE_READ | SHARE_WRITE | SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
                                FLAG_OPEN_REPARSE_POINT | FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error();
                h.Dispose();
                if (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND) return;
                throw Error("no se pudo revisar " + ruta, e);
            }
            using (h) Verificar(h, ruta, false);
        }

        static void BorrarTemporal(string tmp)
        {
            var h = CreateFileW(tmp, DELETE | FILE_READ_ATTRIBUTES, 0, IntPtr.Zero, OPEN_EXISTING, FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return; }
            using (h)
            {
                try { if ((Info(h).Atributos & ATTR_REPARSE) == 0) Borrar(h); } catch { }
            }
        }
    }
}
'@

function Initialize-Seguro {
    if (-not ('PilotXNetApply.Seguro' -as [type])) { Add-Type -TypeDefinition $CodigoSeguro -Language CSharp -EA Stop }
}

# Carpeta del log: solo Administradores y SYSTEM. Si ya existe se respetan sus
# permisos (los pone el instalador o red.ps1); si falta se crea con esos.
function New-CarpetaSoloAdmin([string]$ruta, [bool]$usuariosLeen) {
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $sistema = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $usuarios = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $hereda = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $ninguna = [System.Security.AccessControl.PropagationFlags]::None
    $permitir = [System.Security.AccessControl.AccessControlType]::Allow
    $ds = New-Object System.Security.AccessControl.DirectorySecurity
    $ds.SetOwner($admins)
    $ds.SetAccessRuleProtection($true, $false)
    $ds.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $hereda, $ninguna, $permitir)))
    $ds.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sistema, 'FullControl', $hereda, $ninguna, $permitir)))
    if ($usuariosLeen) {
        $ds.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($usuarios, 'ReadAndExecute', $hereda, $ninguna, $permitir)))
    }
    [void][System.IO.Directory]::CreateDirectory($ruta, $ds)
}

function Initialize-CarpetaLog {
    $padre = Split-Path $DirLog -Parent
    if (-not (Test-Path -LiteralPath $padre)) { New-CarpetaSoloAdmin $padre $true }
    if (-not (Test-Path -LiteralPath $DirLog)) { New-CarpetaSoloAdmin $DirLog $false }
    $p = Get-ProblemaEnlace @($padre, $DirLog)
    if ($p) { throw "carpeta del log insegura: $p" }
}

$script:LogOk = $false
function L($m) {
    if (-not $script:LogOk) { return }
    try {
        Add-Content -LiteralPath $Log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) -Encoding utf8
        if ((Get-Item -LiteralPath $Log).Length -gt 200KB) { Set-Content -LiteralPath $Log (Get-Content -LiteralPath $Log -Tail 400) -Encoding utf8 }
    } catch {}
}

# Avisos repetidos (p.ej. un enlace que queda puesto): uno cada 10 minutos.
$script:UltimoAviso = $null
$script:UltimoAvisoHora = [datetime]::MinValue
function Aviso($m) {
    if ($m -eq $script:UltimoAviso -and ((Get-Date) - $script:UltimoAvisoHora).TotalMinutes -lt 10) { return }
    $script:UltimoAviso = $m
    $script:UltimoAvisoHora = Get-Date
    L ("AVISO: " + $m + " — se saltea el ciclo, no se toca nada")
}

if ($SoloFunciones) { return }

# ── Ciclo ────────────────────────────────────────────────────────────────────

$creado = $false
$mtx = New-Object System.Threading.Mutex($true, 'Global\ViewX_NetApplyWatcher', [ref]$creado)
if (-not $creado) { return }

try { Initialize-CarpetaLog; $script:LogOk = $true } catch { }
try { Initialize-Seguro }
catch {
    L ("no se pudo preparar el acceso seguro a archivos: " + $_.Exception.Message + " — el watcher no atiende pedidos")
    exit 1
}

function Netsh([string[]]$cmd) {
    $out = & netsh.exe @cmd 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("netsh " + ($cmd -join ' ') + " -> " + ($out -join ' ')) }
}

# $p: pedido ya validado y normalizado por Test-Pedido.
function Aplicar($p) {
    $idx = $p.ifIndex
    $nic = Get-NetAdapter -InterfaceIndex $idx -EA Stop
    $name = $nic.Name
    L ("aplicando a [{0}] {1} modo={2}" -f $idx, $name, $p.mode)

    # netsh hace todo ATOMICO (apaga DHCP + fija en un solo comando) y no dispara
    # el error "Inconsistent parameters PolicyStore PersistentStore and Dhcp" que
    # tira New-NetIPAddress cuando el DHCP todavia figura habilitado.
    if ($p.mode -eq 'dhcp') {
        Netsh @('interface', 'ipv4', 'set', 'address', "name=$name", 'source=dhcp')
        Netsh @('interface', 'ipv4', 'set', 'dnsservers', "name=$name", 'source=dhcp')
        return
    }

    if ($p.mode -eq 'static') {
        $mask = MascaraDe $p.prefix
        if ($p.gateway) {
            Netsh @('interface', 'ipv4', 'set', 'address', "name=$name", 'static', $p.ip, $mask, $p.gateway)
        } else {
            Netsh @('interface', 'ipv4', 'set', 'address', "name=$name", 'static', $p.ip, $mask)
        }
        if ($p.dns.Count -gt 0) {
            Netsh @('interface', 'ipv4', 'set', 'dnsservers', "name=$name", 'static', $p.dns[0], 'primary')
            for ($i = 1; $i -lt $p.dns.Count; $i++) {
                Netsh @('interface', 'ipv4', 'add', 'dnsservers', "name=$name", $p.dns[$i], ("index=" + ($i + 1)))
            }
        } else {
            Netsh @('interface', 'ipv4', 'set', 'dnsservers', "name=$name", 'source=dhcp')
        }
        return
    }

    if ($p.mode -eq 'primary') {
        # Elegir por que adaptador sale a internet = darle la MENOR metrica de
        # interfaz (gana la ruta por defecto). Al elegido 10; a los demas Up 60.
        foreach ($n2 in (Get-NetAdapter -Physical -EA SilentlyContinue | Where-Object { $_.Status -eq 'Up' })) {
            $m = if ($n2.ifIndex -eq $idx) { 10 } else { 60 }
            Set-NetIPInterface -InterfaceIndex $n2.ifIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric $m -EA Stop
        }
        return
    }
    throw "modo desconocido: $($p.mode)"
}

L "NetApplyWatcher iniciado (SYSTEM, copia endurecida)"

while ($true) {
    Start-Sleep -Seconds 2
    # Intento de abrir la carpeta del log si al arrancar no se pudo.
    if (-not $script:LogOk) { try { Initialize-CarpetaLog; $script:LogOk = $true } catch { } }

    # Chequeo barato: sin pedido no se abre nada.
    if (-not (Test-Path -LiteralPath $Req)) { continue }

    $hRaiz = $null; $hDir = $null
    try {
        # 1) Ningun componente es un enlace (antes de tocar nada).
        $p = Get-ProblemaEnlace @($Raiz, $Dir, $Req, $Res)
        if ($p) { Aviso $p; continue }

        # 2) Fijar las dos carpetas (no se pueden borrar/renombrar mientras
        #    tanto) y comprobar por handle que son las reales.
        try {
            $hRaiz = [PilotXNetApply.Seguro]::FijarCarpeta($Raiz)
            $hDir = [PilotXNetApply.Seguro]::FijarCarpeta($Dir)
        } catch { Aviso $_.Exception.Message; continue }

        # 3) Volver a mirar con las carpetas fijas.
        $p = Get-ProblemaEnlace @($Raiz, $Dir, $Req, $Res)
        if ($p) { Aviso $p; continue }

        # 4) Leer y consumir el pedido por el mismo handle.
        $bytes = $null
        try { $bytes = [PilotXNetApply.Seguro]::LeerYBorrar($Req, $MaxPedido) }
        catch { Aviso $_.Exception.Message; continue }
        if ($null -eq $bytes) { continue }   # lo esta escribiendo PilotX: proximo ciclo

        $ts = [int][double]::Parse((Get-Date -UFormat %s))
        $out = [ordered]@{ id = $null; ok = $false; error = $null; ts = $ts }
        $r = $null
        try {
            $raw = [System.Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)
            $r = $raw | ConvertFrom-Json -EA Stop
        } catch { $out.error = 'pedido invalido: no es JSON' }

        if ($null -ne $r -and -not $out.error) {
            if (Test-IdPedido $r.id) { $out.id = $r.id }
            $indices = @(Get-NetAdapter -EA SilentlyContinue | ForEach-Object { [int]$_.ifIndex })
            $v = Test-Pedido $r $indices
            if (-not $v.ok) {
                $out.error = $v.error
            } elseif ($null -eq $out.id) {
                $out.error = 'pedido invalido: falta el id o no es valido'
            } else {
                try {
                    Aplicar $v.pedido
                    $out.ok = $true
                } catch {
                    $out.error = $_.Exception.Message
                }
            }
        }
        if ($out.ok) { L ("OK id=" + $out.id) } else { L ("FALLO id=" + $out.id + ": " + $out.error) }

        # 5) Responder: temporal nuevo + renombrar, en la carpeta fija y
        #    verificada de nuevo.
        $p = Get-ProblemaEnlace @($Raiz, $Dir, $Res)
        if ($p) { Aviso $p; continue }
        $json = (New-Object PSObject -Property $out) | ConvertTo-Json -Compress
        try { [PilotXNetApply.Seguro]::EscribirAtomico($Res, (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)) }
        catch { Aviso $_.Exception.Message }
    } catch {
        L ("error en ciclo: " + $_.Exception.Message)
    } finally {
        if ($hDir) { $hDir.Dispose() }
        if ($hRaiz) { $hRaiz.Dispose() }
    }
}
