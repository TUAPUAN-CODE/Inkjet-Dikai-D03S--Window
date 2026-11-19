using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dikai.App
{
    /// <summary>
    /// HTTP server ขนาดเล็กสำหรับหน้าจอโปรแกรม — ฟังเฉพาะ 127.0.0.1 (เครื่องตัวเอง) เท่านั้น
    /// ป้องกันเว็บอื่นในเบราว์เซอร์เดียวกันสั่งเครื่องพิมพ์: API ต้องมี X-Token, ตรวจ Host/Origin (กัน DNS rebinding)
    /// ไม่ใช้ HttpListener เพราะต้องขอสิทธิ์ URL ACL บน Windows
    /// </summary>
    internal sealed class WebServer : IDisposable
    {
        private const int MaxHeaderBytes = 32 * 1024;
        private const int MaxBodyBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private readonly TcpListener _listener;
        private readonly string _token;
        private readonly Func<string, JObj, Task<object>> _api;
        private readonly Func<string> _html;
        private readonly List<SseClient> _clients = new List<SseClient>();
        private readonly Timer _heartbeat;
        private volatile bool _disposed;

        public int Port { get; private set; }
        public bool EverConnected { get; private set; }
        public DateTime LastClientSeen { get; private set; } = DateTime.Now;

        public int ClientCount
        {
            get { lock (_clients) return _clients.Count; }
        }

        public WebServer(int port, string token, Func<string, JObj, Task<object>> api, Func<string> html)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _token = token;
            _api = api;
            _html = html;
            _heartbeat = new Timer(_ => Broadcast(null, null), null, 15000, 15000);
        }

        public void Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_disposed)
            {
                TcpClient c;
                try
                {
                    c = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (_disposed) return;
                    continue;
                }
                var _ = Task.Run(() => HandleAsync(c));
            }
        }

        private sealed class Request
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = new byte[0];
        }

        private async Task HandleAsync(TcpClient client)
        {
            bool keepOpen = false;
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var req = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (req == null) return;

                if (!HostAllowed(req))
                {
                    await WriteAsync(stream, 403, "text/plain", Utf8.GetBytes("forbidden")).ConfigureAwait(false);
                    return;
                }

                if (req.Method == "GET" && (req.Path == "/" || req.Path == "/index.html"))
                {
                    await WriteAsync(stream, 200, "text/html; charset=utf-8", Utf8.GetBytes(_html())).ConfigureAwait(false);
                    return;
                }

                if (req.Path == "/api/events")
                {
                    if (!req.Query.TryGetValue("t", out var t) || !TokenEquals(t))
                    {
                        await WriteAsync(stream, 401, "text/plain", Utf8.GetBytes("unauthorized")).ConfigureAwait(false);
                        return;
                    }
                    keepOpen = true;
                    await StartSseAsync(client, stream).ConfigureAwait(false);
                    return;
                }

                if (req.Path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    if (!req.Headers.TryGetValue("X-Token", out var t) || !TokenEquals(t) || !OriginAllowed(req))
                    {
                        await WriteJsonAsync(stream, 401, new Dictionary<string, object> { { "ok", false }, { "error", "unauthorized" } }).ConfigureAwait(false);
                        return;
                    }
                    Dictionary<string, object> result;
                    try
                    {
                        var body = req.Body.Length > 0 ? Json.Parse(Utf8.GetString(req.Body)) : null;
                        var data = await _api(req.Path.Substring(5), new JObj(body)).ConfigureAwait(false);
                        result = new Dictionary<string, object> { { "ok", true }, { "data", data } };
                    }
                    catch (Exception ex)
                    {
                        var msg = ex is Dgcp.DgcpException || ex is ArgumentException || ex is FormatException || ex is AdminLockedException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                        result = new Dictionary<string, object> { { "ok", false }, { "error", msg } };
                        if (ex is AdminLockedException) result["code"] = "locked"; // หน้าจอใช้รหัสนี้เด้งกลับไปหน้าล็อก
                    }
                    await WriteJsonAsync(stream, 200, result).ConfigureAwait(false);
                    return;
                }

                await WriteAsync(stream, 404, "text/plain", Utf8.GetBytes("not found")).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // client ปิดกลางทาง
            }
            finally
            {
                if (!keepOpen)
                {
                    try { client.Close(); } catch { }
                }
            }
        }

        private bool TokenEquals(string t)
        {
            if (t == null || t.Length != _token.Length) return false;
            int diff = 0;
            for (int i = 0; i < t.Length; i++) diff |= t[i] ^ _token[i];
            return diff == 0;
        }

        private bool HostAllowed(Request req)
        {
            if (!req.Headers.TryGetValue("Host", out var host)) return false;
            return host == "127.0.0.1:" + Port || host == "localhost:" + Port;
        }

        private bool OriginAllowed(Request req)
        {
            if (!req.Headers.TryGetValue("Origin", out var origin)) return true; // same-origin GET บางเบราว์เซอร์ไม่ส่ง
            return origin == "http://127.0.0.1:" + Port || origin == "http://localhost:" + Port;
        }

        private static async Task<Request> ReadRequestAsync(NetworkStream stream)
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                if (n <= 0) return null;
                ms.Write(buf, 0, n);
                if (ms.Length > MaxHeaderBytes + MaxBodyBytes) return null;
                headerEnd = IndexOf(ms.GetBuffer(), (int)ms.Length, new byte[] { 13, 10, 13, 10 });
                if (headerEnd < 0 && ms.Length > MaxHeaderBytes) return null;
            }

            var all = ms.ToArray();
            var head = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var req = new Request { Method = first[0].ToUpperInvariant() };
            var target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            if (q >= 0)
            {
                foreach (var pair in target.Substring(q + 1).Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    req.Query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                }
            }
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) req.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }

            int bodyStart = headerEnd + 4;
            int length = 0;
            if (req.Headers.TryGetValue("Content-Length", out var cl) && !int.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out length)) return null;
            if (length < 0 || length > MaxBodyBytes) return null;
            var body = new byte[length];
            int have = Math.Min(length, all.Length - bodyStart);
            Buffer.BlockCopy(all, bodyStart, body, 0, have);
            while (have < length)
            {
                int n = await stream.ReadAsync(body, have, length - have).ConfigureAwait(false);
                if (n <= 0) return null;
                have += n;
            }
            req.Body = body;
            return req;
        }

        private static int IndexOf(byte[] data, int count, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= count; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        private static Task WriteJsonAsync(Stream s, int status, object value)
        {
            return WriteAsync(s, status, "application/json; charset=utf-8", Utf8.GetBytes(Json.Serialize(value)));
        }

        private static async Task WriteAsync(Stream s, int status, string contentType, byte[] body)
        {
            var head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\n" +
                       "Content-Type: " + contentType + "\r\n" +
                       "Content-Length: " + body.Length + "\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "X-Content-Type-Options: nosniff\r\n" +
                       "Referrer-Policy: no-referrer\r\n" +
                       (contentType.StartsWith("text/html", StringComparison.Ordinal)
                           ? "Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'\r\n"
                           : "") +
                       "Connection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length).ConfigureAwait(false);
            await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                default: return "Error";
            }
        }

        // ------------------------------------------------------------------ Server-Sent Events

        private sealed class SseClient
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
        }

        private async Task StartSseAsync(TcpClient client, NetworkStream stream)
        {
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\nConnection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\n" +
                "retry: 1500\n\n");
            await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            var c = new SseClient { Client = client, Stream = stream };
            lock (_clients)
            {
                _clients.Add(c);
                EverConnected = true;
                LastClientSeen = DateTime.Now;
            }
            // อ่านทิ้งจนกว่าเบราว์เซอร์จะปิด เพื่อรู้ว่าหน้าต่างถูกปิด
            var buf = new byte[256];
            try
            {
                while (!_disposed)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (n <= 0) break;
                }
            }
            catch (Exception)
            {
                // ปิดแล้ว
            }
            Remove(c);
        }

        private void Remove(SseClient c)
        {
            lock (_clients)
            {
                if (_clients.Remove(c)) LastClientSeen = DateTime.Now;
            }
            try { c.Client.Close(); } catch { }
        }

        /// <summary>ส่ง event ไปทุกหน้าจอที่เปิดอยู่ (eventName = null คือ heartbeat)</summary>
        public void Broadcast(string eventName, object data)
        {
            byte[] payload = Utf8.GetBytes(eventName == null
                ? ": ping\n\n"
                : "event: " + eventName + "\ndata: " + Json.Serialize(data) + "\n\n");
            List<SseClient> targets;
            lock (_clients) targets = new List<SseClient>(_clients);
            foreach (var c in targets)
            {
                try
                {
                    lock (c.WriteLock)
                    {
                        c.Stream.Write(payload, 0, payload.Length);
                        c.Stream.Flush();
                    }
                }
                catch (Exception)
                {
                    Remove(c);
                }
            }
            if (targets.Count > 0) LastClientSeen = DateTime.Now;
        }

        public void Dispose()
        {
            _disposed = true;
            _heartbeat.Dispose();
            try { _listener.Stop(); } catch { }
            List<SseClient> all;
            lock (_clients)
            {
                all = new List<SseClient>(_clients);
                _clients.Clear();
            }
            foreach (var c in all)
            {
                try { c.Client.Close(); } catch { }
            }
        }
    }
}
