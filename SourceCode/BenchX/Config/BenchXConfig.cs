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
    public double Latitud { get; set; } = 53.4360564;   // arranque histórico de ModSim
    public double Longitud { get; set; } = -111.160047;
    public bool Gga { get; set; }
    public bool Vtg { get; set; }
    public bool Avr { get; set; }
    public bool Hdt { get; set; }
    public bool Rmc { get; set; }
    public bool Ogi { get; set; }
    public bool Nda { get; set; } = true;      // PANDA: la que usa PilotX
    public bool Ksxt { get; set; }
    public bool SoloGps { get; set; }          // banco con ECU real: no emular módulos

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
