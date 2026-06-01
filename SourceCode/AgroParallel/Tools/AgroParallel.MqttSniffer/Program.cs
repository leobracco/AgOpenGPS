// =============================================================================
// AgroParallel.MqttSniffer
// Herramienta de diagnóstico para el ecosistema Agro Parallel: se engancha al
// broker MQTT (embebido en AgIO o cualquier broker compatible) y vuelca todo el
// tráfico con coloreado por producto + JSON pretty-print + comandos interactivos.
//
// Comandos en runtime (teclear y ENTER):
//   p <topic> <payload>   publica un mensaje (payload puede contener espacios)
//   pr <topic> <payload>  publica con retain=true
//   s <topic>             suscribe a un topic adicional
//   u <topic>             des-suscribe
//   f <regex>             filtra: sólo muestra topics que matcheen la regex
//   F                     limpia el filtro
//   m / M                 mute / unmute (deja de imprimir pero sigue contando)
//   r                     resumen: topics vistos + cuenta + last_seen
//   c                     clear screen
//   q                     salir
//   ?                     ayuda
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;

namespace AgroParallel.MqttSniffer
{
    internal static class Program
    {
        private static IMqttClient _client;
        private static readonly object _consoleLock = new object();
        private static readonly Dictionary<string, TopicStat> _stats =
            new Dictionary<string, TopicStat>(StringComparer.OrdinalIgnoreCase);
        private static Regex _filter;
        private static bool _muted;
        private static bool _showRetained = true;
        private static string _host = "127.0.0.1";
        private static int _port = 1883;
        private static List<string> _initialTopics = new List<string> { "#" };

        private static async Task Main(string[] args)
        {
            ParseArgs(args);

            Console.OutputEncoding = Encoding.UTF8;
            PrintBanner();

            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            _client.ApplicationMessageReceivedAsync += OnMessage;
            _client.DisconnectedAsync += OnDisconnected;
            _client.ConnectedAsync += OnConnected;

            var opts = new MqttClientOptionsBuilder()
                .WithClientId("AgpSniffer_" + Guid.NewGuid().ToString("N").Substring(0, 8))
                .WithTcpServer(_host, _port)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithTimeout(TimeSpan.FromSeconds(5))
                .Build();

            try
            {
                Info($"Conectando a {_host}:{_port} ...");
                await _client.ConnectAsync(opts, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Error($"No se pudo conectar: {ex.Message}");
                Console.WriteLine("Presioná ENTER para salir.");
                Console.ReadLine();
                return;
            }

            foreach (var t in _initialTopics) await SubscribeAsync(t);

            // Loop de comandos interactivos.
            await CommandLoop();

            try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
        }

        // ----------------------------------------------------------------- Args
        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if ((a == "-h" || a == "--host") && i + 1 < args.Length) { _host = args[++i]; }
                else if ((a == "-p" || a == "--port") && i + 1 < args.Length) { int.TryParse(args[++i], out _port); }
                else if ((a == "-t" || a == "--topic") && i + 1 < args.Length)
                {
                    if (_initialTopics.Count == 1 && _initialTopics[0] == "#") _initialTopics.Clear();
                    _initialTopics.Add(args[++i]);
                }
                else if (a == "--no-retained") { _showRetained = false; }
                else if (a == "--help" || a == "-?" || a == "/?")
                {
                    PrintCliHelp();
                    Environment.Exit(0);
                }
            }
        }

        private static void PrintCliHelp()
        {
            Console.WriteLine("AgroParallel.MqttSniffer — sniffer MQTT del ecosistema");
            Console.WriteLine("Uso:");
            Console.WriteLine("  AgroParallel.MqttSniffer.exe [-h <host>] [-p <port>] [-t <topic>]... [--no-retained]");
            Console.WriteLine("Defaults: host=127.0.0.1 port=1883 topic=#");
            Console.WriteLine("Ej:  AgroParallel.MqttSniffer.exe -h 192.168.5.10 -t agp/vistax/# -t vistax/#");
        }

        // ----------------------------------------------------------------- MQTT
        private static Task OnConnected(MqttClientConnectedEventArgs e)
        {
            Info($"✓ Conectado al broker {_host}:{_port}");
            return Task.CompletedTask;
        }

        private static Task OnDisconnected(MqttClientDisconnectedEventArgs e)
        {
            Warn($"✗ Desconectado: {e.Reason} ({e.ReasonString ?? "sin detalle"})");
            return Task.CompletedTask;
        }

        private static async Task SubscribeAsync(string topic)
        {
            try
            {
                await _client.SubscribeAsync(topic);
                Info($"+ subscribe → {topic}");
            }
            catch (Exception ex)
            {
                Error($"subscribe {topic}: {ex.Message}");
            }
        }

        private static async Task UnsubscribeAsync(string topic)
        {
            try
            {
                await _client.UnsubscribeAsync(topic);
                Info($"− unsubscribe → {topic}");
            }
            catch (Exception ex)
            {
                Error($"unsubscribe {topic}: {ex.Message}");
            }
        }

        private static Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            var msg = e.ApplicationMessage;
            var topic = msg.Topic ?? "";
            string payload = "";
            try
            {
                if (msg.PayloadSegment.Count > 0)
                {
                    payload = Encoding.UTF8.GetString(
                        msg.PayloadSegment.Array,
                        msg.PayloadSegment.Offset,
                        msg.PayloadSegment.Count);
                }
            }
            catch { payload = "<binary>"; }

            // Stats siempre, incluso si muteado.
            lock (_stats)
            {
                if (!_stats.TryGetValue(topic, out var st))
                {
                    st = new TopicStat { Topic = topic };
                    _stats[topic] = st;
                }
                st.Count++;
                st.LastSeen = DateTime.UtcNow;
                st.LastPayloadLen = payload?.Length ?? 0;
                st.LastRetained = msg.Retain;
            }

            if (_muted) return Task.CompletedTask;
            if (!_showRetained && msg.Retain) return Task.CompletedTask;
            if (_filter != null && !_filter.IsMatch(topic)) return Task.CompletedTask;

            PrintMessage(topic, payload, msg.Retain);
            return Task.CompletedTask;
        }

        // ----------------------------------------------------------------- Print
        private static void PrintMessage(string topic, string payload, bool retained)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            var color = ColorFor(topic);
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{ts}] ");
                Console.ForegroundColor = color;
                Console.Write(topic);
                if (retained)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write(" [R]");
                }
                Console.ResetColor();
                Console.WriteLine();
                // Payload pretty si es JSON, raw si no.
                string body = TryPrettyJson(payload) ?? payload;
                if (!string.IsNullOrEmpty(body))
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    foreach (var line in body.Split('\n'))
                        Console.WriteLine("    " + line.TrimEnd('\r'));
                    Console.ResetColor();
                }
            }
        }

        private static ConsoleColor ColorFor(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return ConsoleColor.Gray;
            var t = topic.ToLowerInvariant();
            if (t.StartsWith("agp/vistax/")   || t.StartsWith("vistax/"))   return ConsoleColor.Cyan;
            if (t.StartsWith("agp/quantix/")  || t.StartsWith("quantix/"))  return ConsoleColor.Yellow;
            if (t.StartsWith("agp/sectionx/") || t.StartsWith("sectionx/")) return ConsoleColor.Magenta;
            if (t.StartsWith("agp/flowx/")    || t.StartsWith("flowx/"))    return ConsoleColor.Blue;
            if (t.StartsWith("agp/stormx/")   || t.StartsWith("stormx/"))   return ConsoleColor.DarkCyan;
            if (t.StartsWith("agp/cowx/")     || t.StartsWith("cowx/"))     return ConsoleColor.DarkMagenta;
            if (t.StartsWith("agp/soilx/")    || t.StartsWith("soilx/"))    return ConsoleColor.DarkYellow;
            if (t.StartsWith("agp/signalx/")  || t.StartsWith("signalx/"))  return ConsoleColor.DarkBlue;
            if (t.StartsWith("agp/linex/")    || t.StartsWith("linex/"))    return ConsoleColor.DarkRed;
            if (t.StartsWith("agp/aog/")      || t.StartsWith("aog/"))      return ConsoleColor.Green;
            return ConsoleColor.White;
        }

        private static string TryPrettyJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw.TrimStart();
            if (!(trimmed.StartsWith("{") || trimmed.StartsWith("["))) return null;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                }
            }
            catch { return null; }
        }

        // ----------------------------------------------------------------- CLI
        private static async Task CommandLoop()
        {
            Console.WriteLine();
            // Si stdin está redirigido / cerrado (lanzado en background con > log)
            // no podemos leer comandos: nos quedamos vivos sólo escuchando MQTT.
            if (Console.IsInputRedirected)
            {
                Info("stdin redirigido — modo escucha-only. Mátalo con Ctrl+C o killing el proceso.");
                Console.WriteLine();
                await Task.Delay(Timeout.Infinite);
                return;
            }

            Info("Escuchando. Comandos: p|pr|s|u|f|F|m|M|r|c|q|?  (ENTER tras el comando)");
            Console.WriteLine();

            while (true)
            {
                string line;
                try { line = Console.ReadLine(); }
                catch { break; }
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                try { await HandleCommand(line); }
                catch (Exception ex) { Error("cmd: " + ex.Message); }
                if (line == "q" || line == "quit" || line == "exit") break;
            }
        }

        private static async Task HandleCommand(string line)
        {
            // Comandos sin argumento.
            switch (line)
            {
                case "?":
                case "help":
                    PrintInteractiveHelp();
                    return;
                case "F":
                    _filter = null;
                    Info("filtro limpio");
                    return;
                case "m":
                    _muted = true;
                    Info("muted (sigue contando stats)");
                    return;
                case "M":
                    _muted = false;
                    Info("unmuted");
                    return;
                case "r":
                    PrintStats();
                    return;
                case "c":
                    Console.Clear();
                    PrintBanner();
                    return;
                case "q":
                case "quit":
                case "exit":
                    Info("saliendo…");
                    return;
            }

            // Comandos con argumentos: parsea por espacios pero respeta el "resto".
            var parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0];
            switch (cmd)
            {
                case "p":
                case "pr":
                    if (parts.Length < 2) { Warn("uso: p <topic> [payload]"); return; }
                    {
                        var topic = parts[1];
                        var payload = parts.Length >= 3 ? parts[2] : "";
                        bool retain = cmd == "pr";
                        await PublishAsync(topic, payload, retain);
                    }
                    return;
                case "s":
                    if (parts.Length < 2) { Warn("uso: s <topic>"); return; }
                    await SubscribeAsync(parts[1]);
                    return;
                case "u":
                    if (parts.Length < 2) { Warn("uso: u <topic>"); return; }
                    await UnsubscribeAsync(parts[1]);
                    return;
                case "f":
                    if (parts.Length < 2) { Warn("uso: f <regex>"); return; }
                    {
                        var pattern = line.Substring(2).Trim();
                        try
                        {
                            _filter = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            Info($"filtro: /{pattern}/i");
                        }
                        catch (Exception ex) { Error("regex inválida: " + ex.Message); }
                    }
                    return;
                default:
                    Warn($"comando desconocido: '{cmd}'.  Tipeá '?' para ayuda.");
                    return;
            }
        }

        private static async Task PublishAsync(string topic, string payload, bool retain)
        {
            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload ?? "")
                    .WithRetainFlag(retain)
                    .Build();
                await _client.PublishAsync(msg);
                Info($"→ pub {topic} {(retain ? "[R]" : "")} ({(payload ?? "").Length} B)");
            }
            catch (Exception ex)
            {
                Error("publish: " + ex.Message);
            }
        }

        private static void PrintStats()
        {
            List<TopicStat> snapshot;
            lock (_stats) snapshot = _stats.Values.OrderByDescending(s => s.Count).ToList();
            lock (_consoleLock)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"=== {snapshot.Count} topics vistos ===");
                Console.ResetColor();
                foreach (var s in snapshot)
                {
                    var age = (DateTime.UtcNow - s.LastSeen).TotalSeconds;
                    Console.ForegroundColor = ColorFor(s.Topic);
                    Console.Write($"  {s.Count,6}  ");
                    Console.Write($"{age,6:0.0}s ago  ");
                    Console.Write($"{s.LastPayloadLen,5} B  ");
                    if (s.LastRetained) Console.Write("[R] ");
                    Console.WriteLine(s.Topic);
                }
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        // ----------------------------------------------------------------- Misc
        private static void PrintBanner()
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
                Console.WriteLine("│  AgroParallel.MqttSniffer  ·  diagnóstico de bus MQTT       │");
                Console.WriteLine("└─────────────────────────────────────────────────────────────┘");
                Console.ResetColor();
            }
        }

        private static void PrintInteractiveHelp()
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine();
                Console.WriteLine("Comandos:");
                Console.WriteLine("  p <topic> <payload>     publica");
                Console.WriteLine("  pr <topic> <payload>    publica con retain=true");
                Console.WriteLine("  s <topic>               subscribe (+)");
                Console.WriteLine("  u <topic>               unsubscribe (−)");
                Console.WriteLine("  f <regex>               filtra topics que matcheen");
                Console.WriteLine("  F                       limpia filtro");
                Console.WriteLine("  m / M                   mute / unmute");
                Console.WriteLine("  r                       resumen de topics (orden por #msgs)");
                Console.WriteLine("  c                       clear screen");
                Console.WriteLine("  q                       salir");
                Console.WriteLine();
                Console.ResetColor();
            }
        }

        private static void Info(string s)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("[i] ");
                Console.ResetColor();
                Console.WriteLine(s);
            }
        }
        private static void Warn(string s)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("[!] ");
                Console.ResetColor();
                Console.WriteLine(s);
            }
        }
        private static void Error(string s)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("[x] ");
                Console.ResetColor();
                Console.WriteLine(s);
            }
        }

        private sealed class TopicStat
        {
            public string Topic;
            public long Count;
            public DateTime LastSeen;
            public int LastPayloadLen;
            public bool LastRetained;
        }
    }
}
