// AgpErrorMapperTests.cs
// Tests de contrato para AgpErrorMapper: verifican que los codigos AGP-MQTT-*
// y AGP-SYS-009 se asignan correctamente segun la excepcion recibida.

using System;
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

        // Excepcion desconocida (InvalidOperationException) debe caer al fallback
        // AGP-SYS-009 ya que no es socket ni MQTT.
        [Test]
        public void ExcepcionDesconocida_MapeaA_SYS009()
        {
            var ex = new InvalidOperationException("estado invalido de prueba");

            AgpError result = AgpErrorMapper.FromException(ex);

            Assert.That(result.Code, Is.EqualTo("AGP-SYS-009"),
                "InvalidOperationException debe caer en el fallback AGP-SYS-009");
            Assert.That(result.Friendly, Is.Not.Empty,
                "El mensaje amigable no debe estar vacio en el fallback");
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
    }
}
