; =============================================================================
;  AgroParallel Piloto (AgOpenGPS + AgIO) - Installer
;  Compilar con Inno Setup 6 (https://jrsoftware.org/isdl.php)
;
;  Uso:
;    1) Compilar binarios:    powershell -File ..\build.ps1
;    2) Compilar este .iss:   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" AgroParallelPilot.iss
;    3) Salida:               .\Output\AgroParallel-Piloto-Setup-x.y.z.exe
;
;  Resuelve:
;    - Carpetas de datos faltantes (Documents\AgOpenGPS\{Fields,Vehicles,Logs})
;      que provocan: System.IO.DirectoryNotFoundException 'Fields'
;    - Registry HKCU\SOFTWARE\AgOpenGPS\workingDirectory apuntando a rutas
;      stale de pantallas viejas. Lo limpiamos en install/uninstall.
;    - Verificacion de .NET Framework 4.8.
; =============================================================================

#define AppName       "PilotX"
#define AppShortName  "PilotX"
#define AppPublisher  "AgroParallel"
#define AppURL        "https://agroparallel.com"
; AppVersion: pasar por CLI con /DAppVersion=x.y.z; si no, default 1.0.0
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExeAOG     "PilotX.exe"
#define AppExeAgIO    "CoreX.exe"

; Carpeta donde build.ps1 dejo los binarios mergeados (AOG + AgIO)
#define BuildDir "..\Build"

[Setup]
; GUID estable de la app - NO cambiar entre versiones (rompe upgrades)
AppId={{8E7B2C4E-9F1A-4B2C-9D3E-A6701A8E7B2C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}

DefaultDirName={autopf}\AgroParallel\PilotX
DefaultGroupName=AgroParallel
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeAOG}

OutputDir=Output
OutputBaseFilename=PilotX-Setup-{#AppVersion}
Compression=lzma2/ultra
SolidCompression=yes
LZMAUseSeparateProcess=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

WizardStyle=modern
ShowLanguageDialog=auto

; Icono del instalador (usamos el AOG)
SetupIconFile={#BuildDir}\AOG.ico

; Cerrar AOG/AgIO si estan corriendo antes de actualizar
CloseApplications=force
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";   Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "agioicon";      Description: "Crear acceso directo a AgIO en el escritorio"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart";     Description: "Iniciar AgOpenGPS automaticamente al arrancar Windows"; GroupDescription: "Inicio automatico:"; Flags: checkedonce
Name: "cleaninstall";  Description: "Reinstalar / Limpiar instalacion previa (registry + settings stale)"; GroupDescription: "Mantenimiento:"; Flags: checkedonce
Name: "wipesettings";  Description: "Eliminar TAMBIEN aog_settings.json y configs de modulos (no toca lotes)"; GroupDescription: "Mantenimiento:"; Flags: unchecked
Name: "embedmode";     Description: "Activar modo EMBED (kiosko anti-tanques: AutoLogon, sin desktop, watchdog, servicios off)"; GroupDescription: "Modo de uso:"; Flags: unchecked

[Files]
; Todo el contenido de Build/ va a {app} (incluye PilotX.exe, CoreX.exe, DLLs, runtimes, idiomas)
; NO incluimos JSONs de estado/config que pueden tener paths personales del dev
; (working_directory, lotes abiertos, etc). Si el usuario quiere migrar config,
; lo hace manualmente.
Source: "{#BuildDir}\*"; DestDir: "{app}"; \
  Excludes: "aog_settings.json,gps_status.json,vistaX.json,quantiX.json,quantiX_motores.json,sectionX.json,orbitX.json,qx_pid.csv,qx_bridge.log,*.log"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; aog_settings.json LIMPIO (sin paths del dev) - solo se crea si no existe.
Source: "aog_settings_default.json"; DestDir: "{app}"; DestName: "aog_settings.json"; \
  Flags: onlyifdoesntexist

; ffmpeg.exe — necesario para el modulo de Camaras (push RTSP a MediaMTX cloud).
; Bajalo con: powershell -File scripts\download-ffmpeg.ps1   (no se commitea)
Source: "assets\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion

; Scripts de modo embed (kiosko) + wrappers .cmd que se auto-elevan via UAC
Source: "scripts\embed-enable.ps1";  DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\embed-disable.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\embed-status.ps1";  DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\embed-enable.cmd";  DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\embed-disable.cmd"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\embed-status.cmd";  DestDir: "{app}\scripts"; Flags: ignoreversion

[Dirs]
; Carpetas de datos del usuario - se crean en %USERPROFILE%\Documents\AgOpenGPS
; Esto previene el DirectoryNotFoundException al primer arranque.
Name: "{userdocs}\AgOpenGPS";          Permissions: users-modify
Name: "{userdocs}\AgOpenGPS\Fields";   Permissions: users-modify
Name: "{userdocs}\AgOpenGPS\Vehicles"; Permissions: users-modify
Name: "{userdocs}\AgOpenGPS\Logs";     Permissions: users-modify
Name: "{userdocs}\AgOpenGPS\Tools";    Permissions: users-modify

[Icons]
Name: "{group}\PilotX";     Filename: "{app}\{#AppExeAOG}";  WorkingDir: "{app}"; IconFilename: "{app}\AOG.ico"
Name: "{group}\AgIO";       Filename: "{app}\{#AppExeAgIO}"; WorkingDir: "{app}"
Name: "{group}\Desinstalar PilotX"; Filename: "{uninstallexe}"
; Atajos diagnostico modo embed (utiles desde "Iniciar > AgroParallel" para soporte).
; Estos los lanzamos como wrappers .cmd que se auto-elevan via UAC (ver scripts\*.cmd)
Name: "{group}\Diagnostico\Estado modo EMBED";    Filename: "{app}\scripts\embed-status.cmd"; \
  WorkingDir: "{app}"; IconFilename: "{sys}\shell32.dll"; IconIndex: 23
Name: "{group}\Diagnostico\Activar modo EMBED";   Filename: "{app}\scripts\embed-enable.cmd"; \
  WorkingDir: "{app}"; IconFilename: "{sys}\shell32.dll"; IconIndex: 77
Name: "{group}\Diagnostico\Desactivar modo EMBED"; Filename: "{app}\scripts\embed-disable.cmd"; \
  WorkingDir: "{app}"; IconFilename: "{sys}\shell32.dll"; IconIndex: 132

Name: "{autodesktop}\PilotX"; Filename: "{app}\{#AppExeAOG}"; WorkingDir: "{app}"; IconFilename: "{app}\AOG.ico"; Tasks: desktopicon
Name: "{autodesktop}\AgIO";   Filename: "{app}\{#AppExeAgIO}"; WorkingDir: "{app}"; Tasks: agioicon

; Inicio automatico al arrancar Windows (shortcut en carpeta Startup, comun a todos los usuarios)
Name: "{commonstartup}\PilotX"; Filename: "{app}\{#AppExeAOG}"; WorkingDir: "{app}"; IconFilename: "{app}\AOG.ico"; Tasks: autostart

[Run]
; ***IMPORTANTE*** Limpieza del registry HKCU\SOFTWARE\AgOpenGPS DEL USUARIO REAL.
; El setup corre elevado (admin), por eso usamos "runasoriginaluser" para que
; HKCU apunte al usuario que va a usar PilotX, NO al admin elevado.
; Sin este flag, las modificaciones a HKCU se aplican al perfil del admin y
; PilotX al arrancar lee el HKCU del usuario y se encuentra con la ruta vieja.
Filename: "{cmd}"; \
  Parameters: "/C reg delete ""HKCU\SOFTWARE\AgOpenGPS"" /v workingDirectory /f & reg delete ""HKCU\SOFTWARE\AgOpenGPS"" /v vehicleFileName /f & exit /b 0"; \
  StatusMsg: "Limpiando rutas de configuracion previa..."; \
  Flags: runhidden waituntilterminated runasoriginaluser; \
  Tasks: cleaninstall

; Desbloquear todos los .exe / .dll copiados (quita el flag "downloaded from internet"
; que SmartScreen aplica y a veces impide ejecutar). Silencioso, no bloquea.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-ChildItem -Path '{app}' -Recurse -Include *.exe,*.dll | Unblock-File -ErrorAction SilentlyContinue"""; \
  StatusMsg: "Desbloqueando archivos..."; \
  Flags: runhidden waituntilterminated

; Agregar carpeta de instalacion a exclusiones de Microsoft Defender (evita
; falsos positivos sobre PilotX.exe / CoreX.exe). Silencioso si Defender
; no esta o no se puede agregar.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""try {{ Add-MpPreference -ExclusionPath '{app}' -ErrorAction Stop }} catch {{}}"""; \
  StatusMsg: "Configurando exclusion en Microsoft Defender..."; \
  Flags: runhidden waituntilterminated

; ── MODO EMBED (kiosko anti-tanques) ──────────────────────────────────────
; Solo si el usuario marco la task "embedmode". Corre embed-enable.ps1 al final
; del install. El script:
;   - crea usuario "pilotx" con AutoLogon
;   - reemplaza Shell por PilotX.exe
;   - registra watchdog scheduled task
;   - desactiva ~30 servicios (Spooler, DiagTrack, WSearch, SysMain, etc.)
;   - plan de energia HighPerformance
;   - bloquea Cortana / Lockscreen / Consumer features / OneDrive
;   - Windows Update sin reboot automatico
; Backup completo en C:\ProgramData\PilotX\embed-backup.json
; Para revertir: powershell -File "{app}\scripts\embed-disable.ps1"
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\embed-enable.ps1"" -InstallDir ""{app}"""; \
  StatusMsg: "Activando modo EMBED (kiosko anti-tanques)... esto puede tardar 1-2 min"; \
  Flags: runhidden waituntilterminated; \
  Tasks: embedmode

Filename: "{app}\{#AppExeAOG}"; Description: "Iniciar AgOpenGPS"; Flags: nowait postinstall skipifsilent; Check: not WizardIsTaskSelected('embedmode')

[InstallDelete]
; Si el user pide "Reinstalar / Limpiar", borramos archivos de config conocidos
; que apunten a paths viejos. NO borramos Fields/, Vehicles/, Tools/.
Type: files; Name: "{userdocs}\AgOpenGPS\aog_settings.json";   Tasks: wipesettings
Type: files; Name: "{userdocs}\AgOpenGPS\vistaX.json";         Tasks: wipesettings
Type: files; Name: "{userdocs}\AgOpenGPS\quantiX.json";        Tasks: wipesettings
Type: files; Name: "{userdocs}\AgOpenGPS\quantiX_motores.json";Tasks: wipesettings
Type: files; Name: "{userdocs}\AgOpenGPS\sectionX.json";       Tasks: wipesettings
Type: files; Name: "{userdocs}\AgOpenGPS\orbitX.json";         Tasks: wipesettings
Type: files; Name: "{userdocs}\AgOpenGPS\gps_status.json";     Tasks: wipesettings

; Nota: NO usamos [Registry] con Root: HKCU porque al correr el setup elevado,
; HKCU apunta al admin elevado en vez del usuario real. Lo hacemos en [Run]
; con runasoriginaluser (ver mas abajo).

[UninstallDelete]
; No borramos {userdocs}\AgOpenGPS - son los lotes/vehiculos del usuario
Type: filesandordirs; Name: "{app}\firmware-cache"

[Code]
// ---------------------------------------------------------------------------
// Verifica .NET Framework 4.8 (release >= 528040 segun MS Docs)
// https://learn.microsoft.com/dotnet/framework/migration-guide/how-to-determine-which-versions-are-installed
// ---------------------------------------------------------------------------
function IsDotNet48Installed(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := (Release >= 528040);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet48Installed() then
  begin
    if MsgBox(
      '.NET Framework 4.8 no esta instalado.' + #13#10 + #13#10 +
      'AgOpenGPS lo necesita para funcionar.' + #13#10 +
      'Descargalo desde: https://dotnet.microsoft.com/download/dotnet-framework/net48' + #13#10 + #13#10 +
      'Continuar igual?',
      mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

// ---------------------------------------------------------------------------
// Antes de empezar a copiar archivos, matamos cualquier proceso AgroParallel
// que pueda tener locks sobre los .exe/.dll. Incluye:
//   - PilotX.exe (UI principal)
//   - CoreX.exe (broker MQTT embebido en puerto 1883)
//   - GPS_Out.exe / AgDiag.exe / ModSim.exe (utilidades)
//   - node.exe (servidores legacy de VistaX/OrbitX-Sync)
// Si node.exe esta corriendo otra cosa que no sea AgroParallel, igualmente lo
// matamos solo si su path contiene "AgroParallel" o "vistax" o "orbitx".
// ---------------------------------------------------------------------------
procedure KillAgroParallelProcesses();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM PilotX.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM CoreX.exe /T',      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM GPS_Out.exe /T',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM AgDiag.exe /T',    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM ModSim.exe /T',    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Servidores Node legacy (VistaX server, OrbitX-Sync) - solo si su ventana
  // tiene "vistax", "orbitx" o "agro" en el titulo (evita matar otros node)
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /FI "WINDOWTITLE eq *vistax*"  /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /FI "WINDOWTITLE eq *orbitx*"  /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /FI "WINDOWTITLE eq *agro*"    '   , '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Liberar el puerto 1883 si quedo algun broker zombie
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillAgroParallelProcesses();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Mismo cleanup en uninstall
    Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM PilotX.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM CoreX.exe /T',      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM GPS_Out.exe /T',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// ---------------------------------------------------------------------------
// Detecta instalacion previa rota (workingDirectory en registry apuntando
// a una ruta inexistente). Avisa al usuario al inicio para que sepa que la
// task "Reinstalar / Limpiar" es la que lo va a arreglar.
// ---------------------------------------------------------------------------
function HasStaleRegistry(): Boolean;
var
  WorkingDir: String;
begin
  Result := False;
  if RegQueryStringValue(HKCU, 'SOFTWARE\AgOpenGPS', 'workingDirectory', WorkingDir) then
  begin
    if (WorkingDir <> '') and (not DirExists(WorkingDir)) then
      Result := True;
  end;
end;

procedure InitializeWizard();
begin
  if HasStaleRegistry() then
  begin
    MsgBox(
      'Se detecto una instalacion previa de AgOpenGPS con configuracion ROTA:' + #13#10 +
      'el registry apunta a una carpeta de trabajo que ya no existe.' + #13#10 + #13#10 +
      'La opcion "Reinstalar / Limpiar instalacion previa" ya esta marcada por defecto y ' +
      'va a corregir esto automaticamente.',
      mbInformation, MB_OK);
  end;
end;

// ---------------------------------------------------------------------------
// Auto-fix de seguridad: aunque el usuario haya desmarcado "Reinstalar / Limpiar",
// si despues de instalar el registry sigue stale, lo arreglamos igual.
// Tambien garantiza que existan las carpetas de datos (defensa en profundidad
// frente al [Dirs] que puede no resolver bien con UAC + admin install).
// ---------------------------------------------------------------------------
procedure EnsureUserDirs();
var
  Base: String;
begin
  Base := ExpandConstant('{userdocs}\AgOpenGPS');
  ForceDirectories(Base);
  ForceDirectories(Base + '\Fields');
  ForceDirectories(Base + '\Vehicles');
  ForceDirectories(Base + '\Logs');
  ForceDirectories(Base + '\Tools');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    EnsureUserDirs();

    // Auto-fix: si aun queda registry stale, borramos los valores conflictivos.
    // OJO: el setup corre elevado, asi que usamos ExecAsOriginalUser para que
    // reg.exe modifique el HKCU del usuario REAL, no el del admin elevado.
    // Esto cubre el caso donde el user desmarco la task "cleaninstall".
    if HasStaleRegistry() then
    begin
      ExecAsOriginalUser(ExpandConstant('{cmd}'),
        '/C reg delete "HKCU\SOFTWARE\AgOpenGPS" /v workingDirectory /f & ' +
        'reg delete "HKCU\SOFTWARE\AgOpenGPS" /v vehicleFileName /f & ' +
        'exit /b 0',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
