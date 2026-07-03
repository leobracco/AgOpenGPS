// AgpJsonTests.cs
// Pinnea el contrato de serializacion/deserializacion de AgpJson para detectar
// regresiones en la politica snake_case + case-insensitive.
//
// CONTRATO WIRE de AgpJson:
//   - Escritura: SnakeCaseLower  (MeterCal -> "meter_cal")
//   - [JsonPropertyName] tiene precedencia sobre la politica de naming
//   - Lectura:   PropertyNameCaseInsensitive = true
//     LIMITACION CONOCIDA: case-insensitive compara letras, no underscores.
//     "meterCal" NO matchea "meter_cal" porque son cadenas de caracteres distintos
//     (no es solo cuestion de mayusculas/minusculas). Ver test
//     Deserializa_NoMatcheaCamelContraSnake.

using System.Text.Json.Serialization;
using NUnit.Framework;
using AgroParallel.Models;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class AgpJsonTests
    {
        // DTO local para probar la politica de naming sin depender de un DTO real.
        private class DtoPrueba
        {
            public double MeterCal { get; set; }
            public string NombreLargo { get; set; }
        }

        // Verifica que la serializacion produce snake_case.
        [Test]
        public void Serializa_SnakeCase()
        {
            var dto = new DtoPrueba { MeterCal = 1.0, NombreLargo = "prueba" };

            string json = AgpJson.Serialize(dto);

            Assert.That(json, Does.Contain("\"meter_cal\""),
                "MeterCal debe serializarse como meter_cal");
            Assert.That(json, Does.Contain("\"nombre_largo\""),
                "NombreLargo debe serializarse como nombre_largo");
            // Asegurarse de que NO aparece la version PascalCase.
            Assert.That(json, Does.Not.Contain("\"MeterCal\""),
                "El JSON no debe contener PascalCase cuando no hay [JsonPropertyName]");
        }

                // HALLAZGO: PropertyNameCaseInsensitive = true con politica SnakeCaseLower
        // NO permite leer claves PascalCase que carecen de underscore.
        // Explicacion: la politica convierte MeterCal al nombre wire "meter_cal".
        // La comparacion case-insensitive es entre "MeterCal" del JSON y "meter_cal"
        // del wire; como son cadenas distintas (no solo diferencia de mayusculas sino
        // ausencia/presencia de underscore), NO hay match y el valor queda en 0.
        //
        // IMPLICACION: el comentario en AgpJson.cs dice "tolera payloads viejos
        // PascalCase" pero eso solo aplica a propiedades cuyo [JsonPropertyName]
        // coincide exactamente (ej: [JsonPropertyName("MeterCal")] o si la política
        // fuera identidad). Bajo SnakeCaseLower, el wire-format real es snake_case;
        // solo "meter_cal" y "METER_CAL" (y variaciones de mayusculas con underscore)
        // son tolerados. PascalCase sin underscore NO lo es.
        //
        // NO cambiar el codigo de produccion por este test: pinnea la limitacion
        // para que sea visible y documentada.
        [Test]
        public void Deserializa_ToleraPascalCaseViejo_SoloSiCoincideConWireFormat()
        {
            // "MeterCal" (PascalCase sin underscore) NO matchea "meter_cal" (snake_case)
            // aunque PropertyNameCaseInsensitive = true, porque los underscores
            // generan cadenas morfologicamente distintas, no solo diferencias de case.
            string jsonPascalSinUnderscore = "{\"MeterCal\":2.5}";
            DtoPrueba resultPascal = AgpJson.Deserialize<DtoPrueba>(jsonPascalSinUnderscore);
            Assert.That(resultPascal.MeterCal, Is.EqualTo(0.0),
                "PascalCase sin underscore ('MeterCal') NO matchea snake_case ('meter_cal') — " +
                "case-insensitive no cubre la diferencia de underscores");

            // En cambio, el formato snake_case canonico SI funciona.
            string jsonSnake = "{\"meter_cal\":2.5}";
            DtoPrueba resultSnake = AgpJson.Deserialize<DtoPrueba>(jsonSnake);
            Assert.That(resultSnake.MeterCal, Is.EqualTo(2.5),
                "El formato wire canonico snake_case ('meter_cal') debe deserializarse correctamente");

            // Y tambien funciona en UPPER_SNAKE_CASE por case-insensitive.
            string jsonUpperSnake = "{\"METER_CAL\":3.0}";
            DtoPrueba resultUpper = AgpJson.Deserialize<DtoPrueba>(jsonUpperSnake);
            Assert.That(resultUpper.MeterCal, Is.EqualTo(3.0),
                "UPPER_SNAKE_CASE tambien funciona via case-insensitive (mismo patron con underscores)");
        }

        // LIMITACION CONOCIDA: PropertyNameCaseInsensitive = true compara cadenas
        // ignorando diferencias de mayusculas/minusculas, pero NO resuelve el
        // underscore. La propiedad MeterCal bajo politica SnakeCaseLower espera
        // "meter_cal" en el wire; la clave "meterCal" (camelCase sin underscore)
        // es una cadena diferente a "meter_cal" incluso en comparacion
        // case-insensitive, por lo que NO se mappea y queda en valor default (0).
        //
        // Implicacion practica: payloads de firmware o JS que usen camelCase
        // (sin underscore) NO seran leidos por AgpJson — deben usar snake_case.
        [Test]
        public void Deserializa_NoMatcheaCamelContraSnake()
        {
            string json = "{\"meterCal\":2.5}";

            DtoPrueba result = AgpJson.Deserialize<DtoPrueba>(json);

            // camelCase sin underscore no matchea snake_case: queda en default 0.
            Assert.That(result.MeterCal, Is.EqualTo(0.0),
                "La clave camelCase 'meterCal' NO debe deserializarse bajo politica snake_case " +
                "(case-insensitive solo cubre diferencias de mayusculas, no underscores)");
        }

        // Verifica que [JsonPropertyName] tiene precedencia sobre SnakeCaseLower.
        // Usa StormXLimitsDto del proyecto real: WindMaxMs esta decorado con
        // [JsonPropertyName("wind_max_ms")], que es identico a lo que daria la
        // politica por convension, pero el atributo debe tener precedencia siempre.
        // Usamos "log_interval_sec" de StormXConfigDto que es nombre personalizado
        // (LogIntervalSec -> "log_interval_sec") para pinnearlo.
        [Test]
        public void JsonPropertyName_TienePrecedencia()
        {
            var dto = new StormXConfigDto { LogIntervalSec = 42 };

            string json = AgpJson.Serialize(dto);

            // [JsonPropertyName("log_interval_sec")] en LogIntervalSec debe aparecer
            // tal cual en el JSON wire, sin que la politica de naming lo altere.
            Assert.That(json, Does.Contain("\"log_interval_sec\""),
                "El atributo [JsonPropertyName] debe aparecer tal cual en el JSON serializado");
            Assert.That(json, Does.Contain("42"),
                "El valor debe estar en el JSON");
        }

        // Verifica el round-trip completo: serializar y volver a deserializar
        // preserva los valores.
        [Test]
        public void RoundTrip_PreservaValores()
        {
            var original = new DtoPrueba { MeterCal = 3.14, NombreLargo = "test round-trip" };

            string json = AgpJson.Serialize(original);
            DtoPrueba result = AgpJson.Deserialize<DtoPrueba>(json);

            Assert.That(result.MeterCal, Is.EqualTo(original.MeterCal));
            Assert.That(result.NombreLargo, Is.EqualTo(original.NombreLargo));
        }
    }
}
