using System;
using System.IO;
using AgLibrary.Logging;
using EmbedIO;
using EmbedIO.Files;
using EmbedIO.WebApi;

namespace AgIO
{
    /// <summary>
    /// Web host local de CoreX: dashboard + API en 127.0.0.1:5181 (solo
    /// loopback; el :5180 es del Hub PilotX). Sirve wwwroot-corex/ y
    /// /api/corex/*. Ciclo de vida atado a FormLoop (Load/Closing).
    /// </summary>
    public sealed class CoreXWebHost : IDisposable
    {
        public const int Port = 5181;
        public static string Url => "http://127.0.0.1:" + Port + "/";

        private WebServer _server;
        private readonly FormLoop _form;

        public CoreXWebHost(FormLoop form)
        {
            _form = form;
        }

        public void Start()
        {
            if (_server != null) return;

            try
            {
                string wwwroot = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "wwwroot-corex");

                // WebApi ANTES del static folder: EmbedIO matchea módulos en
                // orden de registro (mismo patrón que AgpWebHost).
                _server = new WebServer(o => o
                        .WithUrlPrefix(Url)
                        .WithMode(HttpListenerMode.EmbedIO))
                    .WithWebApi("/api", m =>
                    {
                        m.WithController(() => new CoreXStatusController());
                        m.WithController(() => new CoreXCommandController(_form));
                    });

                if (Directory.Exists(wwwroot))
                {
                    // Sin cache: CoreX corre en el tractor y queremos que un
                    // reemplazo de archivos se vea al refrescar.
                    _server = _server.WithStaticFolder("/", wwwroot, false,
                        m => m.WithContentCaching(false));
                }

                _ = _server.RunAsync();
                Log.EventWriter("CoreX web host escuchando en " + Url);
            }
            catch (Exception ex)
            {
                // Un fallo del dashboard no debe tumbar CoreX: logueamos y
                // seguimos sin web host (el bridge UDP/MQTT sigue andando).
                Log.EventWriter("CoreX web host no pudo arrancar: " + ex);
                _server = null;
            }
        }

        public void Dispose()
        {
            try { _server?.Dispose(); } catch { /* shutdown */ }
            _server = null;
        }
    }
}
