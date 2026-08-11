using System;
using System.Collections.Generic;

namespace BenchX.Sim;

// Port del ReceiveFromUDP de ModSim, sin sockets: entra la trama, salen las
// respuestas a mandar y el estado queda legible para la UI. Igual que ModSim,
// NO se valida el checksum entrante. Cualquier trama rota se ignora en silencio
// (el try/catch de afuera del original acá es el guard de longitud).
public sealed class PgnProcessor
{
    // --- lo setea el ViewModel (estado del vehículo simulado) ---
    public double SteerAngleActual;
    public int WorkSwitch = 1, SteerSwitch = 1, RemoteSwitch = 1; // activo-bajo, 1 = suelto
    public byte Subred1, Subred2, Subred3;

    // --- recibido de PilotX (PGN 254 / 239 / 229) ---
    public byte GuidanceStatus;
    public double SteerAngleSetPoint;
    public double GpsSpeedPilotX;
    public byte Xte, Relay, RelayHi;
    public byte UTurn;
    public double GpsSpeedMaquina;
    public int HydLift, Tramline, RelayLoM, RelayHiM;
    public byte[] Zonas { get; } = new byte[8];
    public bool ScanRespondido;

    // --- settings de dirección (PGN 252 / 251) ---
    public byte Kp = 120, HighPwm = 160, LowPwm = 30, MinPwm = 25;
    public double SensorCounts = 30;
    public int WasOffset;
    public double AckermanPct = 100;
    public byte InvertWas, RelayActiveHigh, MotorDir, SingleInputWas = 1, Cytron = 1,
                SteerSwitchCfg, SteerButtonCfg, ShaftEncoder, PulseCountMax = 5,
                Danfoss, PressureSensor, CurrentSensor, UseYAxis;

    // --- config de máquina (PGN 238) ---
    public byte RaiseTime = 2, LowerTime = 4, EnableToolLift, RelayActiveHighM,
                User1, User2, User3, User4;

    public sealed class Resultado
    {
        public List<byte[]> Respuestas { get; } = new();
        public (byte S1, byte S2, byte S3)? NuevaSubred;
    }

    public Resultado Procesar(byte[] data)
    {
        // OJO: los hellos (200) y el scan (202) son tramas de 9 bytes; el guard
        // general solo valida el header, la longitud se chequea por caso.
        var res = new Resultado();
        if (data.Length < 5 || data[0] != 0x80 || data[1] != 0x81 || data[2] != 0x7F)
            return res;

        switch (data[3])
        {
            case 254: // datos de guiado a 10 Hz
            {
                if (data.Length < 13) break;
                GpsSpeedPilotX = (data[5] | (data[6] << 8)) * 0.1;
                GuidanceStatus = data[7];
                SteerAngleSetPoint = (short)(data[8] | (data[9] << 8)) * 0.01;
                Xte = data[10];
                Relay = data[11];
                RelayHi = data[12];
                res.Respuestas.Add(ArmarPgn253());
                break;
            }
            case 252: // settings PID
            {
                if (data.Length < 13) break;
                Kp = data[5];
                HighPwm = data[6];
                MinPwm = data[8];
                LowPwm = (byte)(MinPwm * 1.2f); // ModSim pisa el lowPWM recibido
                SensorCounts = data[9];
                WasOffset = data[10] | (data[11] << 8);
                AckermanPct = data[12]; // se muestra *1 (el original guardaba *0.01 y mostraba *100)
                break;
            }
            case 251: // flags de config
            {
                if (data.Length < 9) break;
                int s0 = data[5];
                InvertWas       = (byte)((s0 >> 0) & 1);
                RelayActiveHigh = (byte)((s0 >> 1) & 1);
                MotorDir        = (byte)((s0 >> 2) & 1);
                SingleInputWas  = (byte)((s0 >> 3) & 1);
                Cytron          = (byte)((s0 >> 4) & 1);
                SteerSwitchCfg  = (byte)((s0 >> 5) & 1);
                SteerButtonCfg  = (byte)((s0 >> 6) & 1);
                ShaftEncoder    = (byte)((s0 >> 7) & 1);
                PulseCountMax = data[6];
                int s1 = data[8];
                Danfoss        = (byte)((s1 >> 0) & 1);
                PressureSensor = (byte)((s1 >> 1) & 1);
                CurrentSensor  = (byte)((s1 >> 2) & 1);
                UseYAxis       = (byte)((s1 >> 3) & 1);
                break;
            }
            case 200: // hello de CoreX → contestan los 3 módulos simulados
            {
                int sa = (int)(SteerAngleActual * 100);
                // El 71 final es el CRC congelado de ModSim (nunca lo recalculó): parity.
                var steer = new byte[] { 128, 129, 126, 126, 5,
                    unchecked((byte)sa), unchecked((byte)(sa >> 8)), 0, 0, (byte)SwitchByte(), 71 };
                var machine = new byte[] { 128, 129, 123, 123, 5,
                    (byte)RelayLoM, (byte)RelayHiM, 0, 0, 0, 71 };
                var imu = new byte[] { 128, 129, 121, 121, 5, 0, 0, 0, 0, 0, 71 };
                res.Respuestas.Add(steer);
                res.Respuestas.Add(machine);
                res.Respuestas.Add(imu);
                break;
            }
            case 201: // cambio de subred
            {
                if (data.Length < 10) break;
                if (data[4] == 5 && data[5] == 201 && data[6] == 201)
                    res.NuevaSubred = (data[7], data[8], data[9]);
                break;
            }
            case 202: // scan → un reply por módulo (steer/machine/imu)
            {
                if (data.Length < 7) break;
                if (data[4] == 3 && data[5] == 202 && data[6] == 202)
                {
                    ScanRespondido = true;
                    foreach (byte modulo in new byte[] { 126, 123, 121 })
                        res.Respuestas.Add(ArmarScanReply(modulo));
                }
                break;
            }
            case 239: // datos de máquina
            {
                if (data.Length < 13) break;
                UTurn = data[5];
                GpsSpeedMaquina = data[6] * 0.1;
                HydLift = data[7];
                Tramline = data[8];
                RelayLoM = data[11];
                RelayHiM = data[12];
                break;
            }
            case 229: // zonas de secciones
            {
                if (data.Length < 13) break;
                for (int i = 0; i < 8; i++) Zonas[i] = data[5 + i];
                break;
            }
            case 238: // config de máquina
            {
                if (data.Length < 13) break;
                RaiseTime = data[5];
                LowerTime = data[6];
                EnableToolLift = data[7];
                RelayActiveHighM = (byte)(data[8] & 1);
                User1 = data[9]; User2 = data[10]; User3 = data[11]; User4 = data[12];
                break;
            }
        }
        return res;
    }

    private int SwitchByte() => (RemoteSwitch << 2) | (SteerSwitch << 1) | WorkSwitch;

    // PGN 253: el "estado del autosteer" que PilotX espera de vuelta a 10 Hz.
    private byte[] ArmarPgn253()
    {
        int sa = (int)(SteerAngleActual * 100);
        var r = new byte[] { 128, 129, 126, 253, 8,
            unchecked((byte)sa), unchecked((byte)(sa >> 8)),
            unchecked((byte)9999), unchecked((byte)(9999 >> 8)),   // heading dummy histórico
            unchecked((byte)8888), unchecked((byte)(8888 >> 8)),   // roll dummy histórico
            (byte)SwitchByte(), 44,                                // 44 = pwmDisplay congelado
            0 };
        return ConCrc(r);
    }

    private byte[] ArmarScanReply(byte modulo)
    {
        var r = new byte[] { 128, 129, modulo, 203, 7,
            Subred1, Subred2, Subred3, modulo,
            Subred1, Subred2, Subred3, 0 };
        return ConCrc(r);
    }

    // CRC de PGN: suma de bytes [2..n-2] en el último byte.
    private static byte[] ConCrc(byte[] r)
    {
        int ck = 0;
        for (int i = 2; i < r.Length - 1; i++) ck += r[i];
        r[^1] = unchecked((byte)ck);
        return r;
    }
}
