using System;
using System.Collections.Generic;

namespace AgIO
{
    /// <summary>
    /// Snapshot thread-safe del estado runtime de CoreX. El hilo UI publica
    /// un DTO nuevo cada segundo (oneSecondLoopTimer) y el web host (:5181)
    /// lo lee desde sus threads. Publicar el objeto entero evita tearing:
    /// nunca se muta un DTO ya publicado.
    /// </summary>
    public sealed class CoreXState
    {
        public static readonly CoreXState Instance = new CoreXState();

        private readonly object _lock = new object();
        private CoreXStatusDto _current = new CoreXStatusDto();

        public void Publish(CoreXStatusDto dto)
        {
            if (dto == null) return;
            lock (_lock) _current = dto;
        }

        public CoreXStatusDto Snapshot()
        {
            lock (_lock) return _current;
        }
    }

    // AgpJson serializa a snake_case: Version→version, KbTotal→kb_total, etc.
    public class CoreXStatusDto
    {
        public bool Ok { get; set; } = true;
        public string Version { get; set; } = "";
        public string Profile { get; set; } = "";
        public CoreXGpsDto Gps { get; set; } = new CoreXGpsDto();
        public CoreXNtripDto Ntrip { get; set; } = new CoreXNtripDto();
        public CoreXMqttDto Mqtt { get; set; } = new CoreXMqttDto();
        public CoreXModulesDto Modules { get; set; } = new CoreXModulesDto();
    }

    public class CoreXGpsDto
    {
        public bool Alive { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        // Detalle GPS/IMU (port de FormGPSData). Los imu_* son crudos tal como
        // llegan en PANDA (heading/roll/pitch ×10); el frontend los escala.
        public string FixQuality { get; set; } = "";
        public int Sats { get; set; }
        public double Hdop { get; set; }
        public double SpeedKmh { get; set; }
        public double AltitudeM { get; set; }
        public double AgeSec { get; set; }
        public double RollDeg { get; set; }
        public double HeadingTrue { get; set; }
        public double HeadingDual { get; set; }
        public int ImuHeading { get; set; }
        public int ImuRoll { get; set; }
        public int ImuPitch { get; set; }
        public int ImuYawRate { get; set; }

        // null mientras la captura de sentencias está apagada (se enciende
        // sola mientras la página GPS de la web esté abierta).
        public CoreXNmeaDto Nmea { get; set; }
    }

    public class CoreXNmeaDto
    {
        public string Gga { get; set; } = "";
        public string Vtg { get; set; } = "";
        public string Panda { get; set; } = "";
        public string Paogi { get; set; } = "";
        public string Hdt { get; set; } = "";
        public string Avr { get; set; } = "";
        public string Hpd { get; set; } = "";
        public string Ksxt { get; set; } = "";
    }

    public class CoreXNtripDto
    {
        public bool RequiredOn { get; set; }
        public bool Connected { get; set; }
        public bool Connecting { get; set; }
        public long KbTotal { get; set; }
        public string CasterIp { get; set; } = "";
    }

    public class CoreXMqttDto
    {
        public bool Running { get; set; }
        public int Port { get; set; }
        public int Clients { get; set; }
        public long Messages { get; set; }
        public long UptimeSec { get; set; }
        public List<string> RecentTopics { get; set; } = new List<string>();
    }

    public class CoreXModulesDto
    {
        public bool SteerConfigured { get; set; }
        public bool SteerHello { get; set; }
        public bool MachineConfigured { get; set; }
        public bool MachineHello { get; set; }
        public bool ImuConfigured { get; set; }
        public bool ImuHello { get; set; }
    }
}
