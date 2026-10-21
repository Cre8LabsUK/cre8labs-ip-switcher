using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Cre8Labs.NetTools.Core.Oui;

namespace Cre8Labs.NetTools.Core.Scan
{
    /// <summary>
    /// Multicast discovery on the adapter's own segment: ONVIF WS-Discovery (cameras, even on another subnet),
    /// SSDP/UPnP (NVRs, routers, media devices) and mDNS (printers, Apple and IoT kit).
    /// Every socket is bound to the adapter's address so probes only go out of that adapter.
    /// </summary>
    public static class Discovery
    {
        static readonly IPEndPoint WsDiscovery = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702);
        static readonly IPEndPoint Ssdp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        static readonly IPEndPoint Mdns = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);

        public static async Task DiscoverAsync(IPAddress source, TimeSpan listen, IProgress<ScanResult> found, CancellationToken token)
        {
            if (NetworkService.DemoMode)
            {
                await DemoData.DiscoverAsync(found, token);
                return;
            }
            await Task.WhenAll(
                OnvifAsync(source, listen, found, token),
                SsdpAsync(source, listen, found, token),
                MdnsAsync(source, listen, found, token)).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ shared socket plumbing

        static UdpClient Open(IPAddress source)
        {
            var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.Bind(new IPEndPoint(source, 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, source.GetAddressBytes());
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            return client;
        }

        /// <summary>Sends the probes, then hands every reply received within the listen window to <paramref name="handle"/>.</summary>
        static async Task ExchangeAsync(IPAddress source, IPEndPoint group, IEnumerable<byte[]> probes, TimeSpan listen,
            Action<UdpReceiveResult> handle, CancellationToken token)
        {
            UdpClient client;
            try { client = Open(source); }
            catch (SocketException) { return; }
            using (client)
            {
                try { foreach (var probe in probes) await client.SendAsync(probe, probe.Length, group).ConfigureAwait(false); }
                catch (SocketException) { return; }

                var deadline = DateTime.UtcNow + listen;
                while (!token.IsCancellationRequested)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return;
                    var receive = client.ReceiveAsync();
                    try
                    {
                        if (await Task.WhenAny(receive, Task.Delay(left, token)).ConfigureAwait(false) != receive) return;
                        handle(await receive.ConfigureAwait(false));
                    }
                    catch (Exception ex) when (ex is SocketException || ex is TaskCanceledException || ex is ObjectDisposedException) { return; }
                }
            }
        }

        // ------------------------------------------------------------------ ONVIF

        public static byte[] OnvifProbe(string types)
        {
            var typesXml = types == null ? "" : $"<d:Types>{types}</d:Types>";
            var xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
                "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
                "<e:Header><w:MessageID>uuid:" + Guid.NewGuid() + "</w:MessageID>" +
                "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
                "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header>" +
                "<e:Body><d:Probe>" + typesXml + "</d:Probe></e:Body></e:Envelope>";
            return Encoding.UTF8.GetBytes(xml);
        }

        static Task OnvifAsync(IPAddress source, TimeSpan listen, IProgress<ScanResult> found, CancellationToken token)
        {
            // Some cameras only answer a typed probe, others only an untyped one, so send both.
            var probes = new[] { OnvifProbe("dn:NetworkVideoTransmitter"), OnvifProbe(null) };
            var seen = new HashSet<string>();
            return ExchangeAsync(source, WsDiscovery, probes, listen, reply =>
            {
                var result = ParseProbeMatch(Encoding.UTF8.GetString(reply.Buffer), reply.RemoteEndPoint.Address.ToString());
                if (result != null && seen.Add(result.Ip)) found.Report(result);
            }, token);
        }

        /// <summary>Reads the device address and ONVIF scopes from a ProbeMatches reply. Null if it is not one.</summary>
        public static ScanResult ParseProbeMatch(string xml, string senderIp)
        {
            var doc = new XmlDocument { XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    doc.Load(reader);
            }
            catch (XmlException) { return null; }

            string Text(string localName) => doc.SelectSingleNode($"//*[local-name()='{localName}']")?.InnerText ?? "";
            if (doc.SelectSingleNode("//*[local-name()='ProbeMatch']") == null) return null;

            // The camera's own address comes from XAddrs, which is right even when it replies via another route.
            var ip = senderIp;
            foreach (var url in Text("XAddrs").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IpTools.IsIPv4(uri.Host)) { ip = uri.Host; break; }
            }

            var result = new ScanResult { Ip = ip, Sources = FoundBy.Onvif, OpenPorts = new List<int>() };
            foreach (var scope in Text("Scopes").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                const string prefix = "onvif://www.onvif.org/";
                if (!scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = scope.Substring(prefix.Length).Split(new[] { '/' }, 2);
                if (parts.Length != 2) continue;
                var value = Uri.UnescapeDataString(parts[1]).Replace('_', ' ').Trim();
                switch (parts[0].ToLowerInvariant())
                {
                    case "name": if (result.DeviceName.Length == 0) result.DeviceName = value; break;
                    case "hardware": if (result.Model.Length == 0) result.Model = value; break;
                    case "manufacturer": case "mfr": if (result.Manufacturer.Length == 0) result.Manufacturer = value; break;
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ SSDP / UPnP

        static async Task SsdpAsync(IPAddress source, TimeSpan listen, IProgress<ScanResult> found, CancellationToken token)
        {
            var search = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
            var locations = new Dictionary<string, string>();
            await ExchangeAsync(source, Ssdp, new[] { search }, listen, reply =>
            {
                var ip = reply.RemoteEndPoint.Address.ToString();
                var headers = ParseSsdp(Encoding.ASCII.GetString(reply.Buffer));
                if (headers == null) return;
                if (!locations.ContainsKey(ip) || locations[ip] == null)
                    locations[ip] = headers.TryGetValue("LOCATION", out var location) ? location : null;
            }, token).ConfigureAwait(false);

            // Read each device's description once, for its friendly name and model.
            await Task.WhenAll(locations.Select(async pair =>
            {
                var result = new ScanResult { Ip = pair.Key, Sources = FoundBy.Ssdp };
                if (pair.Value != null) await ReadUpnpDescriptionAsync(pair.Key, pair.Value, result).ConfigureAwait(false);
                found.Report(result);
            })).ConfigureAwait(false);
        }

        /// <summary>Parses an SSDP reply's headers (upper-case keys). Null if it is not an HTTP 200 reply.</summary>
        public static Dictionary<string, string> ParseSsdp(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim().ToUpperInvariant()] = line.Substring(colon + 1).Trim();
            }
            return headers;
        }

        static async Task ReadUpnpDescriptionAsync(string ip, string location, ScanResult result)
        {
            // Only follow description links that point back at the device that answered.
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Host != ip || (uri.Scheme != "http" && uri.Scheme != "https")) return;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Timeout = request.ReadWriteTimeout = 1500;
                request.Proxy = null;
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var stream = response.GetResponseStream())
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                {
                    var doc = new XmlDocument { XmlResolver = null };
                    doc.Load(reader);
                    string Text(string name) => doc.SelectSingleNode($"//*[local-name()='device']/*[local-name()='{name}']")?.InnerText.Trim() ?? "";
                    result.DeviceName = Text("friendlyName");
                    result.Manufacturer = Text("manufacturer");
                    result.Model = Text("modelName");
                }
            }
            catch (Exception ex) when (ex is WebException || ex is XmlException || ex is IOException || ex is InvalidOperationException) { }
        }

        // ------------------------------------------------------------------ mDNS

        /// <summary>A PTR query for "_services._dns-sd._udp.local", asking for unicast replies (QU bit) so they come back to this socket.</summary>
        public static byte[] MdnsQuery()
        {
            var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in "_services._dns-sd._udp.local".Split('.'))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(Encoding.ASCII.GetBytes(label));
            }
            bytes.AddRange(new byte[] { 0, 0, 12, 0x80, 1 }); // end of name, type PTR, class IN with the unicast-response bit
            return bytes.ToArray();
        }

        static Task MdnsAsync(IPAddress source, TimeSpan listen, IProgress<ScanResult> found, CancellationToken token)
        {
            var seen = new HashSet<string>();
            return ExchangeAsync(source, Mdns, new[] { MdnsQuery() }, listen, reply =>
            {
                var ip = reply.RemoteEndPoint.Address.ToString();
                if (!seen.Add(ip)) return;
                found.Report(new ScanResult { Ip = ip, Sources = FoundBy.Mdns, Hostname = MdnsHostName(reply.Buffer) });
            }, token);
        }

        /// <summary>The first A-record name in an mDNS reply, such as "printer.local", or empty.</summary>
        public static string MdnsHostName(byte[] packet)
        {
            try
            {
                if (packet.Length < 12) return "";
                int questions = packet[4] << 8 | packet[5], records = (packet[6] << 8 | packet[7]) + (packet[8] << 8 | packet[9]) + (packet[10] << 8 | packet[11]);
                var pos = 12;
                for (var i = 0; i < questions; i++) { ReadName(packet, ref pos); pos += 4; }
                for (var i = 0; i < records && pos + 10 <= packet.Length; i++)
                {
                    var name = ReadName(packet, ref pos);
                    int type = packet[pos] << 8 | packet[pos + 1], length = packet[pos + 8] << 8 | packet[pos + 9];
                    pos += 10 + length;
                    if (type == 1 && name.Length > 0) return name;
                }
            }
            catch (IndexOutOfRangeException) { }
            return "";
        }

        static string ReadName(byte[] packet, ref int pos)
        {
            var labels = new List<string>();
            var jumped = false;
            var cursor = pos;
            for (var guard = 0; guard < 64; guard++)
            {
                var length = packet[cursor];
                if (length == 0) { cursor++; break; }
                if ((length & 0xC0) == 0xC0)
                {
                    var target = (length & 0x3F) << 8 | packet[cursor + 1];
                    if (!jumped) pos = cursor + 2;
                    jumped = true;
                    cursor = target;
                    continue;
                }
                labels.Add(Encoding.UTF8.GetString(packet, cursor + 1, length));
                cursor += length + 1;
            }
            if (!jumped) pos = cursor;
            return string.Join(".", labels);
        }

        /// <summary>Adds the vendor for results that carry a MAC.</summary>
        public static void FillVendor(ScanResult result)
        {
            if (result.Vendor.Length == 0 && result.Mac.Length > 0) result.Vendor = OuiLookup.Vendor(result.Mac);
        }
    }
}
