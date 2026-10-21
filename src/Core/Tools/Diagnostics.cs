using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cre8Labs.NetTools.Core.Tools
{
    public sealed class TraceHop
    {
        public int Hop { get; set; }
        public string Address { get; set; } = "";
        public string Name { get; set; } = "";
        public string Time { get; set; } = "";
        public bool Reached { get; set; }
    }

    public sealed class LinkHealth
    {
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public long PacketsReceived { get; set; }
        public long PacketsSent { get; set; }
        public long ErrorsIn { get; set; }
        public long ErrorsOut { get; set; }
        public long DiscardsIn { get; set; }
        public long DiscardsOut { get; set; }
    }

    /// <summary>Traceroute, port checks and link counters for the Tools page, all from the selected adapter.</summary>
    public static class Diagnostics
    {
        public static async Task TraceAsync(IPAddress source, IPAddress target, int maxHops, IProgress<TraceHop> hop, CancellationToken token)
        {
            for (var ttl = 1; ttl <= maxHops && !token.IsCancellationRequested; ttl++)
            {
                var t = ttl;
                var reply = await Task.Run(() => PingService.Send(source, target, 1500, t), token).ConfigureAwait(false);
                var result = new TraceHop { Hop = ttl };
                if (reply.Ok || reply.TtlExpired)
                {
                    result.Address = reply.From;
                    result.Time = $"{reply.RoundTripMs} ms";
                    result.Reached = reply.Ok;
                    result.Name = await ReverseAsync(reply.From).ConfigureAwait(false);
                }
                else
                {
                    result.Address = "*";
                    result.Time = reply.Error ?? "no reply";
                }
                hop.Report(result);
                if (reply.Ok) return;
            }
        }

        static async Task<string> ReverseAsync(string ip)
        {
            if (NetworkService.DemoMode || !IpTools.IsIPv4(ip)) return "";
            try
            {
                var lookup = Dns.GetHostEntryAsync(IPAddress.Parse(ip));
                if (await Task.WhenAny(lookup, Task.Delay(700)).ConfigureAwait(false) != lookup) return "";
                return lookup.Result.HostName == ip ? "" : lookup.Result.HostName;
            }
            catch (Exception ex) when (ex is SocketException || ex is AggregateException) { return ""; }
        }

        /// <summary>"80, 443, 554" or "80-90" style lists. Returns null if any part is invalid.</summary>
        public static List<int> ParsePorts(string text)
        {
            var ports = new SortedSet<int>();
            foreach (var part in (text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var range = part.Split('-');
                if (range.Length == 1 && int.TryParse(range[0], out var p) && p >= 1 && p <= 65535) ports.Add(p);
                else if (range.Length == 2 && int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b) && a >= 1 && b <= 65535 && a <= b && b - a < 1024)
                    for (var i = a; i <= b; i++) ports.Add(i);
                else return null;
            }
            return ports.Count == 0 || ports.Count > 1024 ? null : ports.ToList();
        }

        public static Task<List<int>> OpenPortsAsync(IPAddress source, IPAddress target, IEnumerable<int> ports, CancellationToken token)
        {
            if (NetworkService.DemoMode)
                return Task.FromResult(DemoData.DemoPorts(target.ToString()).Where(ports.Contains).ToList());
            return Scan.SubnetScanner.OpenPortsAsync(source, target, ports, 800, token);
        }

        public static LinkHealth ReadLink(string adapter)
        {
            if (NetworkService.DemoMode)
                return new LinkHealth { BytesReceived = 812_334_201, BytesSent = 96_455_120, PacketsReceived = 702_118, PacketsSent = 311_904, ErrorsIn = 0, DiscardsIn = 12 };
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapter);
            if (ni == null) return null;
            var s = ni.GetIPStatistics();
            return new LinkHealth
            {
                BytesReceived = s.BytesReceived, BytesSent = s.BytesSent,
                PacketsReceived = s.UnicastPacketsReceived + s.NonUnicastPacketsReceived,
                PacketsSent = s.UnicastPacketsSent + s.NonUnicastPacketsSent,
                ErrorsIn = s.IncomingPacketsWithErrors, ErrorsOut = s.OutgoingPacketsWithErrors,
                DiscardsIn = s.IncomingPacketsDiscarded, DiscardsOut = s.OutgoingPacketsDiscarded,
            };
        }
    }
}
