using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cre8Labs.NetTools.Core.Oui;

namespace Cre8Labs.NetTools.Core.Scan
{
    /// <summary>
    /// Sweeps a list of addresses from one adapter: ARP (finds devices that ignore ping), ping, reverse DNS
    /// and a quick TCP check of common camera and network ports.
    /// </summary>
    public static class SubnetScanner
    {
        /// <summary>Web, RTSP, Hikvision SDK, Dahua SDK, SSH, Telnet, RDP.</summary>
        public static readonly int[] DefaultPorts = { 80, 443, 554, 8000, 37777, 22, 23, 3389 };

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physicalAddrLen);

        static uint ToNetworkOrder(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

        /// <summary>The MAC of a device on the adapter's own subnet, or empty. Blocks for up to about 3 seconds.</summary>
        public static string Arp(IPAddress source, IPAddress target)
        {
            var mac = new byte[6];
            var length = mac.Length;
            if (SendARP(ToNetworkOrder(target), ToNetworkOrder(source), mac, ref length) != 0 || length < 6) return "";
            if (mac.All(b => b == 0)) return "";
            return string.Join("-", mac.Take(6).Select(b => b.ToString("X2")));
        }

        public static async Task ScanAsync(IPAddress source, IReadOnlyList<uint> hosts, IProgress<ScanResult> found,
            IProgress<int> completed, CancellationToken token, int parallel = 64)
        {
            if (NetworkService.DemoMode)
            {
                await DemoData.ScanAsync(hosts, found, completed, token);
                return;
            }

            var gate = new SemaphoreSlim(parallel);
            var done = 0;
            var tasks = hosts.Select(async host =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested) return;
                    var result = await ProbeAsync(source, host, token).ConfigureAwait(false);
                    if (result != null && !token.IsCancellationRequested) found.Report(result);
                }
                finally
                {
                    gate.Release();
                    completed.Report(Interlocked.Increment(ref done));
                }
            }).ToList();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        static async Task<ScanResult> ProbeAsync(IPAddress source, uint host, CancellationToken token)
        {
            var target = IPAddress.Parse(IpTools.FromUInt(host));
            if (target.Equals(source)) return null;

            var arp = Task.Run(() => Arp(source, target), token);
            var ping = Task.Run(() => PingService.Send(source, target, 600), token);
            await Task.WhenAll(arp, ping).ConfigureAwait(false);
            var mac = arp.Result;
            if (mac.Length == 0 && !ping.Result.Ok) return null;

            var result = new ScanResult
            {
                Ip = target.ToString(),
                Mac = mac,
                Vendor = OuiLookup.Vendor(mac),
                LatencyMs = ping.Result.Ok ? ping.Result.RoundTripMs : (long?)null,
                Sources = (mac.Length > 0 ? FoundBy.Arp : 0) | (ping.Result.Ok ? FoundBy.Ping : 0),
            };
            var dns = ReverseDnsAsync(target);
            var ports = OpenPortsAsync(source, target, DefaultPorts, 400, token);
            await Task.WhenAll(dns, ports).ConfigureAwait(false);
            result.Hostname = dns.Result;
            result.OpenPorts = ports.Result;
            return result;
        }

        static async Task<string> ReverseDnsAsync(IPAddress target)
        {
            try
            {
                var lookup = Dns.GetHostEntryAsync(target);
                if (await Task.WhenAny(lookup, Task.Delay(800)).ConfigureAwait(false) != lookup) return "";
                var name = lookup.Result.HostName ?? "";
                return name == target.ToString() ? "" : name;
            }
            catch (Exception ex) when (ex is SocketException || ex is AggregateException) { return ""; }
        }

        /// <summary>TCP connect checks from the adapter's own address, so they leave through the right adapter.</summary>
        public static async Task<List<int>> OpenPortsAsync(IPAddress source, IPAddress target, IEnumerable<int> ports, int timeoutMs, CancellationToken token)
        {
            if (NetworkService.DemoMode) return DemoData.DemoPorts(target.ToString()).Where(ports.Contains).ToList();
            var checks = ports.Select(async port =>
            {
                using (var client = new TcpClient(new IPEndPoint(source, 0)))
                {
                    try
                    {
                        var connect = client.ConnectAsync(target, port);
                        if (await Task.WhenAny(connect, Task.Delay(timeoutMs, token)).ConfigureAwait(false) != connect) return 0;
                        await connect.ConfigureAwait(false);
                        return port;
                    }
                    catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is TaskCanceledException) { return 0; }
                }
            });
            return (await Task.WhenAll(checks).ConfigureAwait(false)).Where(p => p != 0).OrderBy(p => p).ToList();
        }
    }
}
