// ============================================================================
// SoporteRemotoTests.cs — el filtro de credenciales del soporte remoto.
//
// Por qué estos tests y no otros: la salida de una acción de soporte VIAJA AL
// CLOUD y se lee desde el panel. Si el filtro falla, un pedido inocente de
// logs termina publicando el token con el que se comanda esa máquina. Es el
// único punto del canal donde un bug silencioso entrega el control del equipo,
// así que se prueba solo y a conciencia.
// ============================================================================

using AgroParallel.Soporte;
using Xunit;

namespace AgroParallel.Services.Tests
{
    public class SoporteRemotoTests
    {
        // Valor SINTETICO. Tiene la forma de un device token (64 hex) porque el
        // sanitizador se prueba contra esa forma, pero NO sale de ningun equipo:
        // el original de este archivo si era el token real de una pantalla en
        // produccion y quedo expuesto en un repo publico. Si hace falta cambiar
        // este dato, generalo al azar; nunca lo copies de un equipo.
        private const string Token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        [Fact]
        public void Sanitizar_tapa_el_token_del_equipo()
        {
            string texto = "GET /api/ota/catalogo con X-Auth-Token: " + Token + " -> 200";
            string limpio = AccionesSoporte.Sanitizar(texto, Token);

            // Lo que importa es que el token NO viaje; con qué marca se tapa es
            // cosmético (acá lo agarra el patrón "Token:", no el reemplazo
            // exacto). El resto de la línea se conserva para poder diagnosticar.
            Assert.DoesNotContain(Token, limpio);
            Assert.Contains("«oculto»", limpio);
            Assert.Contains("/api/ota/catalogo", limpio);
        }

        [Fact]
        public void Sanitizar_tapa_device_token_de_un_json_aunque_no_sea_el_del_equipo()
        {
            // Caso real: un log que imprime el orbitX.json de OTRA instalación,
            // o el token viejo tras regenerarlo. No alcanza con tapar el token
            // que conocemos.
            string json = "{\"enabled\":true,\"device_id\":\"VX-1\",\"device_token\":\"abc123def456ghi\"}";
            string limpio = AccionesSoporte.Sanitizar(json, Token);

            Assert.DoesNotContain("abc123def456ghi", limpio);
            Assert.Contains("«oculto»", limpio);
            // El resto del JSON se conserva: la idea es poder diagnosticar.
            Assert.Contains("device_id", limpio);
            Assert.Contains("VX-1", limpio);
        }

        [Theory]
        [InlineData("password: superclave123")]
        [InlineData("\"pass\":\"superclave123\"")]
        [InlineData("auth_token=superclave123")]
        public void Sanitizar_tapa_claves_en_varias_formas(string linea)
        {
            string limpio = AccionesSoporte.Sanitizar(linea, Token);
            Assert.DoesNotContain("superclave123", limpio);
        }

        [Fact]
        public void Sanitizar_no_rompe_texto_sin_credenciales()
        {
            string log = "15:50:10-> Fields Directory: C:\\Users\\agroparallel\\Documents";
            Assert.Equal(log, AccionesSoporte.Sanitizar(log, Token));
        }

        [Fact]
        public void Sanitizar_tolera_nulos_y_vacios()
        {
            Assert.Equal("", AccionesSoporte.Sanitizar(null, Token));
            Assert.Equal("", AccionesSoporte.Sanitizar("", Token));
            // Sin token conocido igual tiene que tapar lo que matchea por patrón.
            Assert.DoesNotContain("secreto12", AccionesSoporte.Sanitizar("token: secreto12", null));
        }

        [Fact]
        public void Acotar_corta_y_lo_dice()
        {
            string enorme = new string('x', AccionesSoporte.MaxSalida + 5000);
            string cortado = AccionesSoporte.Acotar(enorme);

            Assert.True(cortado.Length < enorme.Length);
            Assert.Contains("salida cortada", cortado);
        }

        [Fact]
        public void Acotar_deja_pasar_lo_que_entra()
        {
            string chico = "dos líneas\nnada más";
            Assert.Equal(chico, AccionesSoporte.Acotar(chico));
        }

        [Fact]
        public void Buscar_devuelve_null_para_una_accion_que_no_existe()
        {
            // El cloud no elige QUÉ se ejecuta: si el nombre no está en el
            // catálogo, no hay nada que correr.
            Assert.Null(AccionesSoporte.Buscar("rm -rf /"));
            Assert.Null(AccionesSoporte.Buscar("shell"));
            Assert.Null(AccionesSoporte.Buscar(""));
            Assert.Null(AccionesSoporte.Buscar(null));
        }

        [Fact]
        public void Buscar_encuentra_las_del_catalogo_sin_importar_mayusculas()
        {
            Assert.NotNull(AccionesSoporte.Buscar("estado"));
            Assert.NotNull(AccionesSoporte.Buscar("LOGS_PILOTX"));
            Assert.NotNull(AccionesSoporte.Buscar(" red "));
        }

        [Fact]
        public void El_catalogo_no_trae_acciones_destructivas_todavia()
        {
            // Reiniciar equipo/PilotX, mover una válvula de FlowX o poner el
            // maestro de secciones en manual se agregan al catálogo cuando esté
            // el flujo de confirmación del panel. Si alguien las suma antes,
            // este test lo frena: son las únicas que tocan la máquina.
            //
            // Las de FlowX existen escritas (nacieron de domar la reguladora de
            // Las Gringas a distancia) pero se registran sólo con la
            // habilitación LOCAL prendida, que es lo que este test da por
            // apagado — el default con el que sale cada pantalla.
            foreach (var a in AccionesSoporte.Catalogo.Values)
                Assert.False(a.EsAccion, "La acción '" + a.Nombre + "' modifica la máquina y todavía no hay confirmación en el panel.");
        }

        [Fact]
        public void Operar_la_maquina_a_distancia_viene_apagado_de_fabrica()
        {
            // El default no es un detalle: si esto viniera prendido, el guard de
            // arriba pasaría igual en el banco y en el campo el cloud podría
            // mover una válvula sin que nadie en la cabina diga que sí.
            Assert.False(AccionesSoporte.OperarHabilitado);
        }

        [Fact]
        public void Apagar_las_acciones_no_se_llevo_puesto_el_diagnostico()
        {
            // El arreglo correcto saca del catálogo lo que TOCA la máquina, no
            // lo que la MIRA: sin diagnóstico remoto el canal no sirve para
            // nada.
            Assert.NotNull(AccionesSoporte.Buscar("flowx_diag"));
            Assert.NotNull(AccionesSoporte.Buscar("corte_config"));
            Assert.NotNull(AccionesSoporte.Buscar("nodos_live"));

            // Y lo que toca la máquina no está disponible con el default.
            Assert.Null(AccionesSoporte.Buscar("flowx_pwm"));
            Assert.Null(AccionesSoporte.Buscar("flowx_pisos"));
            Assert.Null(AccionesSoporte.Buscar("flowx_config"));
            Assert.Null(AccionesSoporte.Buscar("secciones_manual"));
        }

        [Theory]
        [InlineData(new[] { "-ano" }, "-ano")]
        [InlineData(new[] { "show", "rule", "name=all" }, "show rule name=all")]
        [InlineData(new[] { "con espacio" }, "\"con espacio\"")]
        public void ArgsSeguros_cita_solo_cuando_hace_falta(string[] args, string esperado)
        {
            Assert.Equal(esperado, AccionesSoporte.ArgsSeguros(args));
        }
    }
}
