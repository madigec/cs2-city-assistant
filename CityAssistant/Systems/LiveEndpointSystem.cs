using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CityAssistant.Export;
using Colossal.Serialization.Entities;
using Game;

namespace CityAssistant.Systems
{
    /// <summary>
    /// The live endpoint (Phase 3): answers "what are the numbers right now?" while the game runs, so the
    /// MCP server does not have to wait for the next snapshot file.
    ///
    /// http://127.0.0.1:51842/status          which city is open, game date and clock, paused or not
    /// http://127.0.0.1:51842/live/&lt;name&gt;    one export (overview, finance, ... see ExportCatalog), built now
    /// http://127.0.0.1:51842/map/&lt;grids|network|buildings&gt;  the map layer (MapExport), built now
    /// http://127.0.0.1:51842/check/districts  self-test: fast district residents vs the game's own calculation
    /// POST http://127.0.0.1:51842/act?action=...  Phase 5 co-pilot: one approved numeric change (CityActions)
    ///
    /// Only reachable from this PC (bound to 127.0.0.1). Everything is read-only except POST /act, which also
    /// needs the secret key the mod writes to ModsData\CityAssistant\endpoint-key.txt at every game start
    /// (header X-CityAssistant-Key) and is refused for anything a web browser sends (Origin header), so a web
    /// page cannot change the city. Requests arrive on a background
    /// thread but game data may only be read on the main thread, so they wait in a queue that OnUpdate
    /// answers. This system runs in the UI phase, which keeps running while the game is paused.
    /// </summary>
    public partial class LiveEndpointSystem : GameSystemBase
    {
        public const int kPort = 51842;

        // How long a request waits for the main thread (the game may be loading a save).
        private const int kAnswerTimeoutMs = 15000;

        private class Request
        {
            public string Path;
            public string Method = "GET";
            public NameValueCollection Query = new NameValueCollection();
            public int Status;
            public string Body;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private readonly ConcurrentQueue<Request> m_Requests = new ConcurrentQueue<Request>();
        private TcpListener m_Listener;
        private Thread m_AcceptThread;
        private volatile bool m_Running;
        private bool m_InGame;
        private string m_Key;

        public static string KeyFile => Path.Combine(ModFiles.DataFolder, "endpoint-key.txt");

        protected override void OnCreate()
        {
            base.OnCreate();
            try
            {
                // A new random key per game start; only programs that can read this user's files can change the city.
                m_Key = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(ModFiles.DataFolder);
                File.WriteAllText(KeyFile, m_Key);
            }
            catch (Exception e)
            {
                m_Key = null; // no key file = no changes possible, reading still works
                Mod.log.Warn($"Could not write the endpoint key, co-pilot changes are off: {e.Message}");
            }
            try
            {
                m_Listener = new TcpListener(IPAddress.Loopback, kPort);
                m_Listener.Start();
                m_Running = true;
                m_AcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "CityAssistant live endpoint" };
                m_AcceptThread.Start();
                Mod.log.Info($"Live endpoint listening on http://127.0.0.1:{kPort}/");
            }
            catch (Exception e)
            {
                // Most likely the port is taken; the file exports keep working without the endpoint.
                Mod.log.Warn($"Live endpoint could not start on port {kPort}: {e.Message}");
                m_Listener = null;
            }
        }

        protected override void OnDestroy()
        {
            m_Running = false;
            try { m_Listener?.Stop(); } catch (Exception) { }
            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_InGame = mode == GameMode.Game;
        }

        protected override void OnUpdate()
        {
            while (m_Requests.TryDequeue(out var request))
            {
                try
                {
                    Answer(request);
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Live request '{request.Path}' failed: {e}");
                    request.Status = 500;
                    request.Body = Error(e.GetType().Name + ": " + e.Message);
                }
                request.Done.Set();
            }
        }

        /// <summary>Main thread: fills in the answer to one request.</summary>
        private void Answer(Request request)
        {
            var exportSystem = World.GetExistingSystemManaged<CityExportSystem>();
            var g = exportSystem?.Data;

            if (request.Path == "/status")
            {
                var status = new JsonObject
                {
                    { "schema", "cityassistant.status/1" },
                    { "modVersion", typeof(Mod).Assembly.GetName().Version.ToString() },
                    { "cityOpen", m_InGame && g != null },
                };
                if (m_InGame && g != null)
                {
                    status.Add("city", g.CityName);
                    status.Add("gameMonth", g.GameMonthKey);
                    status.Add("gameClock", g.GameClock);
                    status.Add("paused", g.Simulation.selectedSpeed == 0f);
                    status.Add("gameHoursSinceLoad", g.GameHoursSinceLoad.HasValue ? (object)Math.Round(g.GameHoursSinceLoad.Value, 1) : null);
                }
                status.Add("exports", ExportCatalog.Names);
                request.Status = 200;
                request.Body = status.ToString();
                return;
            }

            const string livePrefix = "/live/";
            if (request.Path.StartsWith(livePrefix))
            {
                if (!m_InGame || g == null)
                {
                    request.Status = 503;
                    request.Body = Error("No city is open in the game.");
                    return;
                }
                var name = request.Path.Substring(livePrefix.Length);
                var export = ExportCatalog.Build(name, g, World, "live");
                if (export == null)
                {
                    request.Status = 404;
                    request.Body = Error($"Unknown export '{name}'. Known: {string.Join(", ", ExportCatalog.Names)}");
                    return;
                }
                request.Status = 200;
                request.Body = export.ToString();
                return;
            }

            const string mapPrefix = "/map/";
            if (request.Path.StartsWith(mapPrefix))
            {
                if (!m_InGame || g == null)
                {
                    request.Status = 503;
                    request.Body = Error("No city is open in the game.");
                    return;
                }
                var name = "map-" + request.Path.Substring(mapPrefix.Length);
                var map = MapExport.Build(name, g, World, "live");
                if (map == null)
                {
                    request.Status = 404;
                    request.Body = Error($"Unknown map '{name}'. Known: {string.Join(", ", MapExport.Names)}");
                    return;
                }
                request.Status = 200;
                request.Body = map.ToString();
                return;
            }

            if (request.Path == "/act")
            {
                if (request.Method != "POST")
                {
                    request.Status = 405;
                    request.Body = Error("Changes need POST.");
                    return;
                }
                if (!m_InGame || g == null)
                {
                    request.Status = 503;
                    request.Body = Error("No city is open in the game.");
                    return;
                }
                request.Body = CityActions.Run(g, World, request.Query, out request.Status).ToString();
                return;
            }

            if (request.Path == "/check/districts")
            {
                if (!m_InGame || g == null)
                {
                    request.Status = 503;
                    request.Body = Error("No city is open in the game.");
                    return;
                }
                request.Status = 200;
                request.Body = DistrictsExport.BuildCheck(g, World).ToString();
                return;
            }

            if (request.Path == "/check/road-users")
            {
                if (!m_InGame || g == null)
                {
                    request.Status = 503;
                    request.Body = Error("No city is open in the game.");
                    return;
                }
                if (!float.TryParse(request.Query["x"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rx)
                    || !float.TryParse(request.Query["z"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rz))
                {
                    request.Status = 400;
                    request.Body = Error("Give x and z (world metres).");
                    return;
                }
                request.Status = 200;
                request.Body = LeftoverExports.RoadUsers(g, World, rx, rz).ToString();
                return;
            }

            request.Status = 404;
            request.Body = Error("Unknown path. Use /status, /live/<name>, /map/<grids|network|buildings|utilities|zones>, /check/districts or POST /act.");
        }

        private static string Error(string message) => new JsonObject { { "error", message } }.ToString();

        // ---- Background threads: plain HTTP over a socket, just enough for GET requests from this PC. ----

        private void AcceptLoop()
        {
            while (m_Running)
            {
                TcpClient client;
                try
                {
                    client = m_Listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    // Listener stopped (game closing).
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 15000;
                    var stream = client.GetStream();
                    var head = ReadHead(stream);
                    var status = 400;
                    var body = Error("Bad request.");

                    var lines = head?.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines != null && lines.Length > 0 ? lines[0].Split(' ') : new string[0];
                    var isPost = parts.Length >= 2 && parts[0] == "POST";
                    if (isPost && (m_Key == null || Header(lines, "X-CityAssistant-Key") != m_Key || Header(lines, "Origin") != null))
                    {
                        status = 403;
                        body = Error("Changes need the mod's key and are refused from web pages.");
                    }
                    else if (parts.Length >= 2 && (parts[0] == "GET" || isPost) && HostIsLocal(lines))
                    {
                        var target = parts[1].Split(new[] { '?' }, 2);
                        var request = new Request { Path = target[0], Method = parts[0], Query = ParseQuery(target.Length > 1 ? target[1] : "") };
                        m_Requests.Enqueue(request);
                        if (request.Done.Wait(kAnswerTimeoutMs))
                        {
                            status = request.Status;
                            body = request.Body;
                        }
                        else
                        {
                            status = 504;
                            body = Error("The game did not answer in time (it may be loading).");
                        }
                    }
                    WriteResponse(stream, status, body);
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Live endpoint connection failed: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Only answer requests addressed to this PC by name. A web page can't read our answers anyway
        /// (no CORS header), but this also stops one renaming itself to 127.0.0.1 ("DNS rebinding").
        /// </summary>
        private static bool HostIsLocal(string[] headerLines)
        {
            foreach (var line in headerLines)
            {
                if (!line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var host = line.Substring(5).Trim();
                return host == $"127.0.0.1:{kPort}" || host == $"localhost:{kPort}";
            }
            return false;
        }

        private static string Header(string[] headerLines, string name)
        {
            for (int i = 1; i < headerLines.Length; i++)
            {
                var colon = headerLines[i].IndexOf(':');
                if (colon > 0 && string.Equals(headerLines[i].Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return headerLines[i].Substring(colon + 1).Trim();
            }
            return null;
        }

        private static NameValueCollection ParseQuery(string query)
        {
            var result = new NameValueCollection();
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0)
                    continue;
                var kv = pair.Split(new[] { '=' }, 2);
                string Decode(string x) => Uri.UnescapeDataString(x.Replace('+', ' '));
                result[Decode(kv[0])] = kv.Length > 1 ? Decode(kv[1]) : "";
            }
            return result;
        }

        /// <summary>The request line and headers, up to the blank line (8 KB at most).</summary>
        private static string ReadHead(Stream stream)
        {
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read <= 0)
                    break;
                length += read;
                var text = Encoding.ASCII.GetString(buffer, 0, length);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                    return text.Substring(0, end);
            }
            return null;
        }

        private static void WriteResponse(Stream stream, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : status == 403 ? "Forbidden" : status == 405 ? "Method Not Allowed" : status == 409 ? "Conflict" : status == 404 ? "Not Found" : status == 503 ? "Service Unavailable" : status == 504 ? "Gateway Timeout" : status == 500 ? "Internal Server Error" : "Bad Request";
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
