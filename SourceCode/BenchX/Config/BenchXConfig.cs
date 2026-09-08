using System;
using System.IO;
using System.Text.Json;

namespace BenchX.Config;

// Config en JSON junto al exe. Reemplaza al Properties.Settings del WinForms,
// que perdió la subred al migrar de versión (v1.0.25) — un archivo visible y
// versionable no tiene ese problema.
public sealed class BenchXConfig
{
    public byte Subred1 { get; set; } = 127;   // default banco loopback
    public byte Subred2 { get; set; } = 255;
    public byte Subred3 { get; set; } = 255;
    // Arranque en campo de Tres Arroyos (Buenos Aires), zona de Agro Parallel;
    // antes era el campo canadiense historico de ModSim (53.436, -111.160).
    public double Latitud { get; set; } = -38.3450;
    public double Longitud { get; set; } = -60.2650;
    public bool Gga { get; set; }
    public bool Vtg { get; set; }
    public bool Avr { get; set; }
    public bool Hdt { get; set; }
    public bool Rmc { get; set; }
    public bool Ogi { get; set; }
    public bool Nda { get; set; } = true;      // PANDA: la que usa PilotX
    public bool Ksxt { get; set; }

    // Qué emula BenchX en el banco. Se apaga lo que maneje la ECU real conectada.
    public bool EmularGps { get; set; } = true;      // sentencias NMEA
    public bool EmularWas { get; set; } = true;      // módulo autosteer en el wire: 253 + hello/scan
    public bool EmularMotor { get; set; } = true;    // cinemática sigue el setpoint (motor "perfecto")
    public bool EmularMaquina { get; set; } = true;
    public bool EmularImu { get; set; } = true;      // hello/scan 121 + campos IMU del PANDA

    // Nodos emulados por MQTT contra el broker embebido de PilotX (:1883).
    // QuantiX: un nodo de 7 motores (placa V0.4) que obedece los /target del
    // bridge y devuelve status_live por motor. VistaX: un nodo con N cables
    // cuyo flujo de semillas sale de lo que dosifican esos motores. Con esto
    // el panel QuantiX/VistaX de PilotX cobra vida sin ningun ESP32.
    public bool EmularQuantiX { get; set; } = true;
    public bool EmularVistaX { get; set; } = true;
    public string BrokerHost { get; set; } = "127.0.0.1";
    public int BrokerPort { get; set; } = 1883;
    public string QxUid { get; set; } = "QX-BENCH000001";
    public int QxMotores { get; set; } = 7;          // 14 surcos, 2 por motor
    public int SurcosPorMotor { get; set; } = 2;
    public string VxUid { get; set; } = "VX-BENCH000001";
    public int VxCables { get; set; } = 14;          // un sensor por surco
    public double SemillasPorVuelta { get; set; } = 24;   // mismo default que la config de motores
    public double DientesEngranaje { get; set; } = 600;   // pulsos por vuelta del motor

    public static string RutaDefault => Path.Combine(AppContext.BaseDirectory, "benchx.json");

    private static readonly JsonSerializerOptions Opciones = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static BenchXConfig Cargar(string ruta)
    {
        BenchXConfig config;
        try
        {
            config = JsonSerializer.Deserialize<BenchXConfig>(File.ReadAllText(ruta), Opciones)
                     ?? new BenchXConfig();
        }
        catch
        {
            // ausente o corrupto: defaults, y se reescribe para que quede sano
            config = new BenchXConfig();
        }
        try { config.Guardar(ruta); } catch { /* disco de solo lectura: se sigue en memoria */ }
        return config;
    }

    public void Guardar(string ruta) =>
        File.WriteAllText(ruta, JsonSerializer.Serialize(this, Opciones));
}
