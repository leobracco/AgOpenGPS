// ICoreXEcuService — proxy HTTP entre el Hub y el firmware CoreX-ECU (Teensy 4.1).
// Maneja la config persistida (IP, timeout) y reenvía las llamadas REST del
// firmware v1.11+: /api/status, /api/params (GET/POST), /api/wassrc (GET/POST),
// /api/zero, /api/reboot, /api/motor/* y /api/calibration/pwm-sweep.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Models;

namespace AgroParallel.Services.Abstractions
{
    public interface ICoreXEcuService
    {
        CoreXEcuConfigDto LoadConfig();
        void SaveConfig(CoreXEcuConfigDto dto);

        /// <summary>RMW atómico bajo lock — usar para Load → modify → Save desde controllers
        /// sin perder cambios contra requests concurrentes. El delegate devuelve true
        /// para persistir, false para descartar.</summary>
        CoreXEcuConfigDto UpdateConfig(Func<CoreXEcuConfigDto, bool> mutate);

        /// <summary>GET /api/status — telemetría runtime (IMU/WAS/GPS/CAN/autosteer).</summary>
        Task<CoreXEcuStatusDto> GetStatusAsync();

        /// <summary>GET /api/params — config persistida en EEPROM del Teensy.</summary>
        Task<CoreXEcuParamsDto> GetParamsAsync();

        /// <summary>POST /api/params — update parcial. El `patch` es un objeto FLAT
        /// con cualquier subset de claves (`beta`, `ticksPerDeg`, `emaYaw`, etc.).
        /// El firmware enrutea cada clave a su grupo y reporta qué grupos se
        /// re-escribieron en `updated.{autoZero,keya,imu}`.</summary>
        Task<CoreXEcuParamsUpdateResultDto> UpdateParamsAsync(Dictionary<string, object> patch);

        /// <summary>GET /api/wassrc — fuente activa del WAS + estado del ADS1115 (v1.11+).</summary>
        Task<CoreXEcuWassrcDto> GetWassrcAsync();

        /// <summary>POST /api/wassrc — cambia la fuente del WAS en caliente y persiste
        /// a EEPROM (v1.11+). Valores aceptados: "keya", "ads_se", "ads_diff". Si la
        /// nueva fuente es ADS, el firmware prueba I²C y devuelve `probed=true` +
        /// `ads_present`. 400 invalid_source si el valor no es uno de los tres.</summary>
        Task<CoreXEcuWassrcUpdateResultDto> SetWassrcAsync(string source);

        /// <summary>POST /api/zero — captura el encoder actual como nuevo centro.</summary>
        Task<CoreXEcuZeroResultDto> ForceZeroAsync();

        /// <summary>POST /api/reboot — soft-reset por watchdog en el Teensy. La
        /// respuesta llega ANTES del reset; el device queda inalcanzable ~3-5 s.</summary>
        Task<bool> RebootAsync();

        /// <summary>POST /api/motor/test — mueve el motor manualmente (dead-man).
        /// 409 si guidance_active=true. Pensado para joystick: re-postear cada 300-500ms
        /// con duration_ms=1000 — si el Hub corta, el motor se frena solo.</summary>
        Task<CoreXEcuMotorTestResultDto> MotorTestAsync(int pwm, int durationMs);

        /// <summary>POST /api/motor/stop — detiene test manual y/o sweep en curso.</summary>
        Task<CoreXEcuOkResultDto> MotorStopAsync();

        /// <summary>POST /api/calibration/pwm-sweep — inicia barrido async (firmware v1.10+).
        /// 409 sweep_already_running o guidance_active según corresponda.</summary>
        Task<CoreXEcuSweepStartResultDto> StartSweepAsync(int stepDurationMs, int settleMs);

        /// <summary>GET /api/calibration/pwm-sweep — estado + resultados (poll durante running).</summary>
        Task<CoreXEcuSweepStatusDto> GetSweepAsync();

        /// <summary>DELETE /api/calibration/pwm-sweep — aborta y frena motor.</summary>
        Task<CoreXEcuOkResultDto> CancelSweepAsync();

        /// <summary>POST /api/firmware (Teensy) — streamea el .hex cacheado del producto
        /// "corex-ecu" a la unidad y reboota. 409 si el guiado está activo, 400 si el
        /// HEX es inválido / no es de este equipo. La verificación de versión nueva la
        /// hace la UI releyendo /status tras el reboot.</summary>
        Task<CoreXEcuFlashResultDto> FlashFirmwareAsync(string version);
    }
}
