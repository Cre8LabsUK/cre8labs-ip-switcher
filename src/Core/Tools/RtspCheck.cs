using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Cre8Labs.NetTools.Core.Tools
{
    public sealed class RtspResult
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string Summary { get; set; } = "";
        public string Server { get; set; } = "";
        public List<string> Streams { get; set; } = new List<string>();
    }

    /// <summary>
    /// Proves a camera is serving a stream without playing it: RTSP OPTIONS, then DESCRIBE, with Basic or
    /// Digest login if the camera asks. Reads the SDP to list the video and audio codecs.
    /// </summary>
    public static class RtspCheck
    {
        public static async Task<RtspResult> RunAsync(IPAddress source, string url, string user, string password, int timeoutMs = 4000)
        {
            if (NetworkService.DemoMode) return DemoResult(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp")
                return new RtspResult { Summary = "Use an address such as rtsp://192.168.1.20:554/Streaming/Channels/101" };

            // Credentials in the URL (rtsp://user:pass@host/...) are used if the boxes are empty.
            if (string.IsNullOrEmpty(user) && uri.UserInfo.Length > 0)
            {
                var parts = uri.UserInfo.Split(new[] { ':' }, 2);
                user = Uri.UnescapeDataString(parts[0]);
                password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            }
            var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
            var port = uri.Port > 0 ? uri.Port : 554;

            using (var client = new TcpClient(new IPEndPoint(source, 0)))
            {
                try
                {
                    var connect = client.ConnectAsync(uri.Host, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false) != connect)
                        return new RtspResult { Summary = $"No answer on port {port}. The camera may be off, or RTSP may be disabled." };
                    await connect.ConfigureAwait(false);
                }
                catch (SocketException ex) { return new RtspResult { Summary = $"Could not connect to port {port}: {ex.Message}" }; }

                client.ReceiveTimeout = client.SendTimeout = timeoutMs;
                var stream = client.GetStream();
                var cseq = 1;

                var options = await RequestAsync(stream, "OPTIONS", clean, cseq++, null).ConfigureAwait(false);
                if (options == null) return new RtspResult { Summary = "The device did not answer in RTSP." };

                var describe = await RequestAsync(stream, "DESCRIBE", clean, cseq++, null).ConfigureAwait(false);
                if (describe != null && describe.Status == 401)
                {
                    if (string.IsNullOrEmpty(user))
                        return new RtspResult { Status = 401, Server = describe.Header("Server"), Summary = "The camera answered but needs a username and password." };
                    var auth = Authorization(describe.Header("WWW-Authenticate"), user, password, "DESCRIBE", clean);
                    describe = await RequestAsync(stream, "DESCRIBE", clean, cseq++, auth).ConfigureAwait(false);
                }
                if (describe == null) return new RtspResult { Summary = "The camera stopped answering after OPTIONS." };

                var result = new RtspResult { Status = describe.Status, Server = describe.Header("Server") ?? options.Header("Server") ?? "" };
                if (describe.Status == 200)
                {
                    result.Ok = true;
                    result.Streams = DescribeStreams(describe.Body);
                    result.Summary = "Stream OK" + (result.Streams.Count > 0 ? ": " + string.Join(", ", result.Streams) : "");
                }
                else if (describe.Status == 401) result.Summary = "Login refused. Check the username and password.";
                else if (describe.Status == 404) result.Summary = "The camera answered, but there is no stream at that path.";
                else result.Summary = $"The camera answered {describe.Status} {describe.Reason}.";
                return result;
            }
        }

        sealed class Response
        {
            public int Status;
            public string Reason = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body = "";
            public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        }

        static async Task<Response> RequestAsync(Stream stream, string method, string url, int cseq, string authorization)
        {
            var request = new StringBuilder()
                .Append(method).Append(' ').Append(url).Append(" RTSP/1.0\r\n")
                .Append("CSeq: ").Append(cseq).Append("\r\n")
                .Append("User-Agent: Cre8Labs Net Tools\r\n");
            if (method == "DESCRIBE") request.Append("Accept: application/sdp\r\n");
            if (authorization != null) request.Append("Authorization: ").Append(authorization).Append("\r\n");
            request.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(request.ToString());
            try
            {
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                return await ReadResponseAsync(stream).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { return null; }
        }

        static async Task<Response> ReadResponseAsync(Stream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            // Headers end at a blank line.
            while (buffer.Count < 16384)
            {
                if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) return null;
                buffer.Add(one[0]);
                var n = buffer.Count;
                if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') break;
            }
            var response = ParseHead(Encoding.ASCII.GetString(buffer.ToArray()));
            if (response == null) return null;
            if (int.TryParse(response.Header("Content-Length"), out var length) && length > 0 && length < 65536)
            {
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var got = await stream.ReadAsync(body, read, length - read).ConfigureAwait(false);
                    if (got == 0) break;
                    read += got;
                }
                response.Body = Encoding.UTF8.GetString(body, 0, read);
            }
            return response;
        }

        static Response ParseHead(string head)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(new[] { ' ' }, 3);
            if (first.Length < 2 || !first[0].StartsWith("RTSP/") || !int.TryParse(first[1], out var status)) return null;
            var response = new Response { Status = status, Reason = first.Length > 2 ? first[2] : "" };
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                // Cameras often offer Digest and Basic; keep Digest, the safer one.
                if (response.Headers.ContainsKey(name) && name.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) &&
                    !value.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) continue;
                response.Headers[name] = value;
            }
            return response;
        }

        /// <summary>"H.264 video", "AAC audio" and so on, from the m= and a=rtpmap lines of an SDP body.</summary>
        public static List<string> DescribeStreams(string sdp)
        {
            var streams = new List<string>();
            string media = null;
            foreach (var raw in (sdp ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("m="))
                {
                    if (media != null) streams.Add(media);
                    var kind = line.Substring(2).Split(' ')[0];
                    media = kind;
                }
                else if (line.StartsWith("a=rtpmap:") && media != null && !media.Contains(" "))
                {
                    var codec = line.Split(' ').ElementAtOrDefault(1)?.Split('/')[0] ?? "";
                    media = $"{Friendly(codec)} {media}";
                }
            }
            if (media != null) streams.Add(media);
            return streams;
        }

        static string Friendly(string codec)
        {
            switch (codec.ToUpperInvariant())
            {
                case "H264": return "H.264";
                case "H265": return "H.265";
                case "MPEG4-GENERIC": return "AAC";
                case "PCMU": return "G.711u";
                case "PCMA": return "G.711a";
                default: return codec;
            }
        }

        /// <summary>Builds the Authorization header for a Basic or Digest challenge (RFC 2617).</summary>
        public static string Authorization(string challenge, string user, string password, string method, string uri, string cnonce = null)
        {
            challenge = challenge ?? "";
            if (!challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
                return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

            var p = ParseChallenge(challenge.Substring(6));
            p.TryGetValue("realm", out var realm);
            p.TryGetValue("nonce", out var nonce);
            p.TryGetValue("qop", out var qop);
            p.TryGetValue("opaque", out var opaque);
            var ha1 = Md5($"{user}:{realm}:{password}");
            var ha2 = Md5($"{method}:{uri}");
            string response, extra = "";
            if (!string.IsNullOrEmpty(qop) && qop.Split(',').Any(q => q.Trim() == "auth"))
            {
                const string nc = "00000001";
                cnonce = cnonce ?? Guid.NewGuid().ToString("N").Substring(0, 16);
                response = Md5($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
                extra = $", qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
            }
            else response = Md5($"{ha1}:{nonce}:{ha2}");
            var header = $"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\"{extra}";
            if (!string.IsNullOrEmpty(opaque)) header += $", opaque=\"{opaque}\"";
            return header;
        }

        static Dictionary<string, string> ParseChallenge(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && (text[i] == ' ' || text[i] == ',')) i++;
                var eq = text.IndexOf('=', i);
                if (eq < 0) break;
                var key = text.Substring(i, eq - i).Trim();
                i = eq + 1;
                string value;
                if (i < text.Length && text[i] == '"')
                {
                    var end = text.IndexOf('"', i + 1);
                    if (end < 0) end = text.Length;
                    value = text.Substring(i + 1, end - i - 1);
                    i = end + 1;
                }
                else
                {
                    var end = text.IndexOf(',', i);
                    if (end < 0) end = text.Length;
                    value = text.Substring(i, end - i).Trim();
                    i = end;
                }
                values[key] = value;
            }
            return values;
        }

        static string Md5(string text)
        {
            using (var md5 = MD5.Create())
                return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        static RtspResult DemoResult(string url) => url.Contains("192.168.1.20")
            ? new RtspResult { Ok = true, Status = 200, Server = "Hikvision-Webs", Streams = { "H.265 video", "G.711u audio" }, Summary = "Stream OK: H.265 video, G.711u audio" }
            : new RtspResult { Summary = "No answer on port 554. The camera may be off, or RTSP may be disabled." };
    }
}
