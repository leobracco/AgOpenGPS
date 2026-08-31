// AgpErrorMapperTests.cs
// Tests de contrato para AgpErrorMapper: verifican que los codigos AGP-MQTT-*
// y AGP-SYS-009 se asignan correctamente segun la excepcion recibida.

using System;
using System.IO;
using System.Net.Sockets;
using NUnit.Framework;
using AgroParallel.Services;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class AgpErrorMapperTests
    {
        // SocketConnectionRefused (SocketError 10061) debe mapearse a AGP-MQTT-001.
        // El mapper detecta rootTypeName == "SocketException" y lo clasifica
        // como error MQTT aunque no sea una excepcion MQTTnet pura.
        [Test]
        public void SocketConnectionRefused_MapeaA_MQTT001()
        {
            var ex = new SocketException((int)SocketError.ConnectionRefused);

            AgpError result = AgpErrorMapper.FromException(ex);

            Assert.That(result.Code, Does.StartWith("AGP-MQTT"),
                "ConnectionRefused debe mapearse a un codigo AGP-MQTT-*");
            Assert.That(result.Code, Is.EqualTo("AGP-MQTT-001"),
                "ConnectionRefused (10061) debe dar AGP-MQTT-001");
            Assert.That(result.Friendly, Is.Not.Empty,
                "El mensaje amigable no debe estar vacio");
            Assert.That(result.Technical, Is.Not.Empty,
                "El campo tecnico debe contener el tipo de excepcion");
        }

        // Una excepcion que no es socket, ni MQTT, ni ninguna de las de sistema
        // clasificadas, tiene que caer al fallback.
        //
        // Antes este test usaba InvalidOperationException, pero esa paso a tener
        // codigo propio (AGP-SYS-007) al clasificar las fallas comunes de .NET.
        // Se cambia por un tipo que de verdad no esta clasificado, para que el
        // test siga probando lo que decia probar: el fallback.
        [Test]
        public void ExcepcionDesconocida_MapeaA_SYS009()
        {
            var ex = new ApplicationException("falla sin clasificar de prueba");

            AgpError result = AgpErrorMapper.FromException(ex);

            Assert.That(result.Code, Is.EqualTo("AGP-SYS-009"),
                "Una excepcion sin clasificar debe caer en el fallback AGP-SYS-009");
            Assert.That(result.Friendly, Is.Not.Empty,
                "El mensaje amigable no debe estar vacio en el fallback");
        }

        // Los codigos de sistema existen para que el operario nos pase un numero
        // que ya diga por donde arrancar. El caso que motivo esto fue justamente
        // un "valor fuera de rango" que solo mostraba el cartel de Windows.
        [TestCase(typeof(IndexOutOfRangeException),    "AGP-SYS-001")]
        [TestCase(typeof(ArgumentOutOfRangeException), "AGP-SYS-001")]
        [TestCase(typeof(NullReferenceException),      "AGP-SYS-002")]
        [TestCase(typeof(FileNotFoundException),       "AGP-SYS-003")]
        [TestCase(typeof(DirectoryNotFoundException),  "AGP-SYS-003")]
        [TestCase(typeof(UnauthorizedAccessException), "AGP-SYS-004")]
        [TestCase(typeof(IOException),                 "AGP-SYS-005")]
        [TestCase(typeof(FormatException),             "AGP-SYS-006")]
        [TestCase(typeof(OverflowException),           "AGP-SYS-006")]
        [TestCase(typeof(InvalidOperationException),   "AGP-SYS-007")]
        public void FallasDeSistema_TienenCodigoPropio(Type tipo, string esperado)
        {
            var ex = (Exception)Activator.CreateInstance(tipo);

            AgpError result = AgpErrorMapper.FromException(ex);

            Assert.That(result.Code, Is.EqualTo(esperado), tipo.Name);
            Assert.That(result.Friendly, Is.Not.Empty, "sin mensaje para el operario");
            Assert.That(result.Technical, Does.Contain(tipo.Name), "el detalle tecnico debe decir el tipo");
        }

        // La causa raiz manda: si la excepcion de arriba envuelve a otra, el
        // codigo tiene que salir de la de adentro, que es la que explica que paso.
        [Test]
        public void CausaRaiz_DecideElCodigo()
        {
            var ex = new Exception("envoltorio", new IndexOutOfRangeException("el de adentro"));

            AgpError result = AgpErrorMapper.FromException(ex);

            Assert.That(result.Code, Is.EqualTo("AGP-SYS-001"));
        }

        // El mapper SI inspecciona InnerException: recorre el arbol hasta la raiz.
        // Un SocketException(TimedOut) envuelto en Exception generica debe mapearse
        // a AGP-MQTT-002 porque el mapper llega a la raiz por el loop while.
        // HALLAZGO: la condicion `if (isMqttException || rootTypeName == "SocketException")`
        // evalua rootTypeName, por lo que inner exceptions de tipo SocketException
        // SI son clasificadas como MQTT. Esto es comportamiento documentado e intencional.
        [Test]
        public void InnerException_SeInspecciona()
        {
            var innerSocket = new SocketException((int)SocketError.TimedOut); // 10060
            var outer = new Exception("falla en la capa superior", innerSocket);

            AgpError result = AgpErrorMapper.FromException(outer);

            // El mapper inspecciona InnerException y clasifica segun la raiz SocketException.
            Assert.That(result.Code, Does.StartWith("AGP-"),
                "Una excepcion con inner SocketException debe mapearse a algun codigo AGP-*");
            // Dado que el mapper llega al inner SocketException(TimedOut=10060),
            // debe dar AGP-MQTT-002.
            Assert.That(result.Code, Is.EqualTo("AGP-MQTT-002"),
                "Inner SocketException(TimedOut) debe mapearse a AGP-MQTT-002 via inspeccion de raiz");
        }

        // Verificar que el campo Technical contiene informacion util para soporte.
        [Test]
        public void Technical_ContieneNombreDeExcepcion()
        {
            var ex = new InvalidOperationException("mensaje de prueba");

            AgpError result = AgpErrorMapper.FromException(ex);

            Assert.That(result.Technical, Does.Contain("InvalidOperationException"),
                "Technical debe incluir el nombre del tipo de excepcion para soporte");
        }

        // null no debe lanzar excepcion — devuelve AGP-SYS-009 con fallback.
        [Test]
        public void NullException_DevuelveSYS009SinExplotar()
        {
            AgpError result = AgpErrorMapper.FromException(null);

            Assert.That(result.Code, Is.EqualTo("AGP-SYS-009"));
            Assert.That(result.Friendly, Is.Not.Empty);
        }

        // Los AGP-USB-* los arma directo el flasheo USB (UsbFlashService /
        // UsbDriverInstaller / EsptoolOutputParser), sin pasar por una
        // excepcion. FriendlyForCode() es la unica forma de que el
        // controller/UI consiga el texto amigable a partir de ese codigo.
        [TestCase("AGP-USB-001", "puerto")]
        [TestCase("AGP-USB-002", "módulo")]
        [TestCase("AGP-USB-007", "flasheo")]
        public void FriendlyForCode_MapeaCodigosUsb(string codigo, string palabraEsperada)
        {
            string friendly = AgpErrorMapper.FriendlyForCode(codigo);

            Assert.That(friendly, Is.Not.Null.And.Not.Empty, "codigo " + codigo + " sin mensaje amigable");
            Assert.That(friendly.ToLowerInvariant(), Does.Contain(palabraEsperada));
        }

        // Codigo no mapeado -> null, para que el llamante decida el fallback
        // (no queremos que FriendlyForCode invente texto).
        [Test]
        public void FriendlyForCode_CodigoDesconocido_DevuelveNull()
        {
            Assert.That(AgpErrorMapper.FriendlyForCode("AGP-USB-999"), Is.Null);
        }
    }
}
