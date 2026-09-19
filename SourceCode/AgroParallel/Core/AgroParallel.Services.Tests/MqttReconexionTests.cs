// ============================================================================
// MqttReconexionTests.cs
//
// Cubre el arreglo de la carrera de arranque MQTT y del publish sin conexión:
//
//   · MqttBackoff            — la escalera de reintentos y la regla de ruido
//                              del log.
//   · MqttPerdidasContador   — que una consigna descartada quede contada y
//                              avisada (resumida, no una línea por tick).
//   · MqttPublisherLink      — que publicar sin enlace devuelva false en vez de
//                              tirar, que se cuente, y que el enlace reintente
//                              hasta que el broker embebido levante.
//
// Todo con un transporte falso: no hace falta broker ni nodos ESP32.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgroParallel.Services;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class MqttBackoffTests
    {
        [Test]
        public void Escalera_duplica_desde_la_espera_inicial_y_se_topa()
        {
            var b = new MqttBackoff(500, 15000);

            Assert.That(b.RegistrarFallo(), Is.EqualTo(500), "primer fallo: el broker embebido suele estar levantando");
            Assert.That(b.RegistrarFallo(), Is.EqualTo(1000));
            Assert.That(b.RegistrarFallo(), Is.EqualTo(2000));
            Assert.That(b.RegistrarFallo(), Is.EqualTo(4000));
            Assert.That(b.RegistrarFallo(), Is.EqualTo(8000));
            Assert.That(b.RegistrarFallo(), Is.EqualTo(15000), "se topa, no sigue duplicando");
            Assert.That(b.RegistrarFallo(), Is.EqualTo(15000));
            Assert.That(b.EnTope, Is.True);
        }

        [Test]
        public void Conectar_resetea_la_escalera()
        {
            var b = new MqttBackoff(500, 15000);
            b.RegistrarFallo();
            b.RegistrarFallo();
            b.RegistrarFallo();
            Assert.That(b.Fallos, Is.EqualTo(3));

            b.Reset();

            Assert.That(b.Fallos, Is.EqualTo(0));
            Assert.That(b.Limpio, Is.True);
            Assert.That(b.RegistrarFallo(), Is.EqualTo(500), "tras reconectar vuelve a reaccionar rápido");
        }

        [Test]
        public void EsperaTrasOtroFallo_anticipa_lo_que_devolveria_registrar()
        {
            var b = new MqttBackoff(500, 15000);
            for (int i = 0; i < 8; i++)
            {
                int anticipada = b.EsperaTrasOtroFalloMs;
                int real = b.RegistrarFallo();
                Assert.That(anticipada, Is.EqualTo(real), "el log promete la espera real (fallo " + (i + 1) + ")");
            }
        }

        [Test]
        public void Muchos_fallos_no_desbordan_ni_bajan_la_espera()
        {
            var b = new MqttBackoff(500, 15000);
            int ultima = 0;
            for (int i = 0; i < 10000; i++) ultima = b.RegistrarFallo();
            Assert.That(ultima, Is.EqualTo(15000));
            Assert.That(b.EsperaActualMs, Is.EqualTo(15000));
        }

        [Test]
        public void Espera_maxima_menor_que_la_inicial_se_normaliza()
        {
            var b = new MqttBackoff(1000, 100);
            Assert.That(b.RegistrarFallo(), Is.EqualTo(1000));
            Assert.That(b.RegistrarFallo(), Is.EqualTo(1000));
        }

        [Test]
        public void DebeLoguearFallo_avisa_el_primero_no_repite_y_vuelve_cada_tanto()
        {
            Assert.That(MqttBackoff.DebeLoguearFallo(1, null, "Broker no responde"), Is.True,
                "el primer fallo siempre se loguea");
            Assert.That(MqttBackoff.DebeLoguearFallo(2, "Broker no responde", "Broker no responde"), Is.False,
                "mismo motivo: no llenar el log de líneas idénticas");
            Assert.That(MqttBackoff.DebeLoguearFallo(3, "Broker no responde", "Timeout conectando"), Is.True,
                "cambió el motivo: eso es información nueva");
            Assert.That(MqttBackoff.DebeLoguearFallo(10, "Broker no responde", "Broker no responde"), Is.True,
                "cada 10 deja rastro de que sigue caído");
            Assert.That(MqttBackoff.DebeLoguearFallo(11, "Broker no responde", "Broker no responde"), Is.False);
        }
    }

    [TestFixture]
    public class MqttPerdidasContadorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc);

        [Test]
        public void Primera_perdida_avisa_y_las_siguientes_se_resumen()
        {
            var c = new MqttPerdidasContador(5000);

            string primera = c.Anotar("sin enlace con el broker", T0);
            Assert.That(primera, Is.Not.Null, "la primera consigna perdida se avisa al toque");
            Assert.That(primera, Does.Contain("CONSIGNA PERDIDA"));

            // El tick es de 200 ms: sin resumen serían 5 líneas por segundo.
            Assert.That(c.Anotar("sin enlace con el broker", T0.AddMilliseconds(200)), Is.Null);
            Assert.That(c.Anotar("sin enlace con el broker", T0.AddMilliseconds(400)), Is.Null);
            Assert.That(c.Anotar("sin enlace con el broker", T0.AddMilliseconds(4999)), Is.Null);
        }

        [Test]
        public void Pasada_la_ventana_avisa_cuantas_se_perdieron_desde_el_aviso_anterior()
        {
            var c = new MqttPerdidasContador(5000);
            c.Anotar("sin enlace", T0);                       // aviso 1 (1 perdida)
            for (int i = 1; i <= 24; i++)
                c.Anotar("sin enlace", T0.AddMilliseconds(i * 200));   // hasta 4800 ms

            string aviso = c.Anotar("sin enlace", T0.AddMilliseconds(5000));
            Assert.That(aviso, Is.Not.Null);
            Assert.That(aviso, Does.Contain("25 publicacion(es)"), "las 25 que no se avisaron una por una");
            Assert.That(aviso, Does.Contain("Total perdidas: 26"));
            Assert.That(c.Total, Is.EqualTo(26));
        }

        [Test]
        public void Guarda_el_ultimo_motivo_para_la_UI()
        {
            var c = new MqttPerdidasContador(5000);
            c.Anotar("sin enlace con el broker", T0);
            c.Anotar("Broker no responde", T0.AddMilliseconds(100));
            Assert.That(c.UltimoMotivo, Is.EqualTo("Broker no responde"));
        }

        [Test]
        public void Reset_vuelve_a_cero()
        {
            var c = new MqttPerdidasContador(5000);
            c.Anotar("sin enlace", T0);
            c.Reset();
            Assert.That(c.Total, Is.EqualTo(0));
            Assert.That(c.UltimoMotivo, Is.Null);
            Assert.That(c.Anotar("sin enlace", T0.AddMilliseconds(10)), Is.Not.Null, "vuelve a avisar de entrada");
        }
    }

    [TestFixture]
    public class MqttPublisherLinkTests
    {
        /// <summary>
        /// Transporte de mentira: simula el broker embebido levantando tarde
        /// (la carrera de arranque real) y cayéndose en medio del trabajo.
        /// </summary>
        private sealed class TransporteFalso : IMqttTransporte
        {
            private readonly object _lock = new object();
            private bool _conectado;

            /// <summary>El broker todavía no escucha en :1883.</summary>
            public bool BrokerCaido = true;

            /// <summary>Publicar tira aunque el transporte se crea conectado
            /// (el caso "The MQTT client is not connected" de fx_bridge.log).</summary>
            public bool PublicarTira;

            public int IntentosDeConexion;
            public readonly List<string> Publicados = new List<string>();

            public bool Conectado { get { lock (_lock) return _conectado; } }

            public event EventHandler<Exception> Desconectado;

            public Task ConectarAsync(string host, int puerto, string clientId)
            {
                IntentosDeConexion++;
                if (BrokerCaido)
                    throw new System.Net.Sockets.SocketException(10061); // connection refused
                lock (_lock) _conectado = true;
                return Task.CompletedTask;
            }

            public Task PublicarAsync(string topic, string payload)
            {
                if (PublicarTira) throw new InvalidOperationException("The MQTT client is not connected.");
                lock (_lock) Publicados.Add(topic + "|" + payload);
                return Task.CompletedTask;
            }

            public Task DesconectarAsync()
            {
                lock (_lock) _conectado = false;
                return Task.CompletedTask;
            }

            /// <summary>El broker se cae en medio del trabajo.</summary>
            public void SimularCaida()
            {
                lock (_lock) _conectado = false;
                var h = Desconectado;
                if (h != null) h(this, new Exception("broker caido"));
            }

            public void Dispose() { }
        }

        private static MqttPublisherLink Nuevo(TransporteFalso t, List<string> log)
        {
            return new MqttPublisherLink("Test", "TST", m => { lock (log) log.Add(m); }, t);
        }

        [Test]
        public async Task Arranque_con_broker_caido_no_aborta_el_bridge()
        {
            var t = new TransporteFalso { BrokerCaido = true };
            var log = new List<string>();
            using (var link = Nuevo(t, log))
            {
                bool ok = await link.StartAsync();

                Assert.That(ok, Is.False, "no conectó: el broker embebido todavía no levantó");
                Assert.That(link.Conectado, Is.False);
                Assert.That(t.IntentosDeConexion, Is.GreaterThanOrEqualTo(1));
                link.Stop();
            }
        }

        [Test]
        public async Task Reintenta_hasta_que_el_broker_levanta_y_resetea_el_backoff()
        {
            var t = new TransporteFalso { BrokerCaido = true };
            var log = new List<string>();
            using (var link = Nuevo(t, log))
            {
                Assert.That(await link.StartAsync(), Is.False);
                Assert.That(link.FallosDeConexion, Is.GreaterThanOrEqualTo(1));

                // CoreXEngineHost.StartServices() terminó de levantar el broker.
                t.BrokerCaido = false;
                bool ok = await link.IntentarConectarAsync();

                Assert.That(ok, Is.True);
                Assert.That(link.Conectado, Is.True);
                Assert.That(link.FallosDeConexion, Is.EqualTo(0), "conectado: la escalera vuelve a cero");
                link.Stop();
            }
        }

        [Test]
        public async Task Publicar_sin_enlace_devuelve_false_y_queda_contado()
        {
            var t = new TransporteFalso { BrokerCaido = true };
            var log = new List<string>();
            using (var link = Nuevo(t, log))
            {
                await link.StartAsync();

                bool enviado = await link.PublicarAsync("agp/flow/ABC/target", "{\"t\":12.3}");

                Assert.That(enviado, Is.False, "no tira: devuelve false para que el bridge no actualice su dedup");
                Assert.That(link.Perdidas, Is.EqualTo(1), "la consigna perdida se cuenta");
                Assert.That(link.UltimoMotivo, Is.Not.Null);
                lock (log)
                    Assert.That(log.Exists(l => l.Contains("CONSIGNA PERDIDA")), Is.True,
                        "el operario se tiene que poder enterar, no morir en un catch vacío");
                link.Stop();
            }
        }

        [Test]
        public async Task Caida_en_medio_del_trabajo_baja_el_enlace_y_no_publica_a_ciegas()
        {
            var t = new TransporteFalso { BrokerCaido = false };
            var log = new List<string>();
            using (var link = Nuevo(t, log))
            {
                Assert.That(await link.StartAsync(), Is.True);
                Assert.That(await link.PublicarAsync("agp/flow/ABC/target", "{\"t\":10}"), Is.True);
                Assert.That(link.Perdidas, Is.EqualTo(0));

                // Se cae el broker. Este es el bug de FlowX: antes nadie
                // escuchaba esto, el bridge se creía conectado y seguía
                // mandando targets de válvula al vacío.
                t.BrokerCaido = true;
                t.SimularCaida();

                Assert.That(link.Conectado, Is.False, "el estado no puede mentir");
                bool enviado = await link.PublicarAsync("agp/flow/ABC/target", "{\"t\":11}");
                Assert.That(enviado, Is.False);
                Assert.That(link.Perdidas, Is.EqualTo(1));
                link.Stop();
            }
        }

        [Test]
        public async Task Publicacion_que_tira_no_rompe_el_tick_y_se_cuenta()
        {
            var t = new TransporteFalso { BrokerCaido = false };
            var log = new List<string>();
            using (var link = Nuevo(t, log))
            {
                await link.StartAsync();
                t.PublicarTira = true;   // "The MQTT client is not connected."

                bool enviado = false;
                Assert.DoesNotThrowAsync(async () =>
                {
                    enviado = await link.PublicarAsync("agp/quantix/ABC/target", "{\"pps\":42}");
                });

                Assert.That(enviado, Is.False);
                Assert.That(link.Perdidas, Is.EqualTo(1));
                link.Stop();
            }
        }

        [Test]
        public async Task Publicar_con_enlace_entrega_el_payload_tal_cual()
        {
            var t = new TransporteFalso { BrokerCaido = false };
            var log = new List<string>();
            using (var link = Nuevo(t, log))
            {
                await link.StartAsync();

                Assert.That(await link.PublicarAsync("agp/flow/ABC/target", "{\"t\":7.5}"), Is.True);

                Assert.That(link.Publicadas, Is.EqualTo(1));
                Assert.That(link.Perdidas, Is.EqualTo(0));
                lock (t.Publicados)
                    Assert.That(t.Publicados, Does.Contain("agp/flow/ABC/target|{\"t\":7.5}"));
                link.Stop();
            }
        }

        [Test]
        public async Task Stop_deja_de_publicar()
        {
            var t = new TransporteFalso { BrokerCaido = false };
            var log = new List<string>();
            var link = Nuevo(t, log);
            await link.StartAsync();
            link.Stop();

            Assert.That(await link.PublicarAsync("agp/flow/ABC/target", "{\"t\":1}"), Is.False);
            Assert.That(link.Conectado, Is.False);
            link.Dispose();
        }
    }
}
