using System.Text.Json;

namespace AgroParallel.Models
{
    /// <summary>
    /// Opciones JSON únicas de todo el backend PilotX.
    /// Escritura: snake_case (política global; [JsonPropertyName] tiene precedencia).
    /// Lectura: case-insensitive (tolera payloads viejos PascalCase).
    /// </summary>
    public static class AgpJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
        public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }
}
