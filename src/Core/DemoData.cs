using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cre8Labs.NetTools.Core
{
    /// <summary>
    /// Made-up adapters and presets for "--demo" mode, used for screenshots and UI work.
    /// Nothing here touches the real network or the real presets file.
    /// </summary>
    public static class DemoData
    {
        static readonly object Gate = new object();

        static readonly Dictionary<string, AdapterStatus> Adapters = new Dictionary<string, AdapterStatus>
        {
            ["Ethernet"] = new AdapterStatus
            {
                Name = "Ethernet", IsUp = true, Speed = 1_000_000_000, Mac = "00-1A-2B-3C-4D-5E",
                Addresses = { new IpEntry { Ip = "192.168.1.250", Prefix = 24 } },
            },
            ["Wi-Fi"] = new AdapterStatus
            {
                Name = "Wi-Fi", IsUp = true, IsDhcp = true, Speed = 866_000_000, Mac = "00-1A-2B-3C-4D-6F",
                Addresses = { new IpEntry { Ip = "10.20.0.37", Prefix = 24 } },
                Gateways = { "10.20.0.1" }, Dns = { "10.20.0.1" },
            },
            ["Ethernet 2"] = new AdapterStatus { Name = "Ethernet 2", Mac = "00-1A-2B-3C-4D-70" },
        };

        static readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>
        {
            ["Ethernet"] = "Intel(R) Ethernet Connection I219-LM",
            ["Wi-Fi"] = "Intel(R) Wi-Fi 6E AX211 160MHz",
            ["Ethernet 2"] = "USB 3.0 to Gigabit Ethernet Adapter",
        };

        public static AppConfig Config() => new AppConfig
        {
            Adapter = "Ethernet",
            Presets = new List<Preset>
            {
                new Preset { Name = "Office LAN", IP = "192.168.1.50", Gateway = "192.168.1.1", DNS1 = "192.168.1.1", DNS2 = "1.1.1.1" },
                new Preset { Name = "Site A cameras", IP = "192.168.1.250" },
                new Preset { Name = "Switch setup", IP = "10.0.0.50", Gateway = "10.0.0.1" },
                new Preset { Name = "Router default", IP = "192.168.0.50", Gateway = "192.168.0.1", DNS1 = "192.168.0.1" },
                new Preset { Name = "PLC network", IP = "172.16.0.20", Mask = "255.255.0.0", Gateway = "172.16.0.1" },
                new Preset { Name = "Access control", IP = "192.168.10.20", Gateway = "192.168.10.1" },
            }
        };

        public static List<AdapterInfo> ListAdapters()
        {
            lock (Gate)
                return Adapters.Values.Select(a => new AdapterInfo { Name = a.Name, Description = Descriptions[a.Name], IsUp = a.IsUp }).ToList();
        }

        public static AdapterStatus ReadStatus(string name)
        {
            lock (Gate)
            {
                if (!Adapters.TryGetValue(name, out var a)) return null;
                return new AdapterStatus
                {
                    Name = a.Name, IsUp = a.IsUp, Speed = a.Speed, Mac = a.Mac, IsDhcp = a.IsDhcp,
                    Addresses = a.Addresses.ToList(), Gateways = a.Gateways.ToList(), Dns = a.Dns.ToList(),
                };
            }
        }

        public static OpResult ApplyStatic(string alias, Preset p, bool addOnly)
        {
            Thread.Sleep(700); // long enough to see the busy spinner
            lock (Gate)
            {
                var a = Adapters[alias];
                var entry = new IpEntry { Ip = IpTools.Canonical(p.IP), Prefix = IpTools.PrefixLength(IpTools.NormalizeMask(p.Mask)) };
                if (addOnly) { a.Addresses.Add(entry); return OpResult.Success(); }
                a.IsDhcp = false;
                a.Addresses = new List<IpEntry> { entry };
                a.Gateways = IpTools.IsIPv4(p.Gateway) ? new List<string> { p.Gateway } : new List<string>();
                a.Dns = new[] { p.DNS1, p.DNS2 }.Where(IpTools.IsIPv4).ToList();
            }
            return OpResult.Success();
        }

        // ------------------------------------------------------------------ scan and discovery

        static readonly Scan.ScanResult[] ScanDevices =
        {
            new Scan.ScanResult { Ip = "192.168.1.1", Mac = "00-1A-2B-50-00-01", Vendor = "Ubiquiti Inc", Hostname = "core-switch.site.local", LatencyMs = 1, OpenPorts = { 22, 80, 443 }, Sources = Scan.FoundBy.Arp | Scan.FoundBy.Ping },
            new Scan.ScanResult { Ip = "192.168.1.20", Mac = "00-1A-2B-10-00-01", Vendor = "Hangzhou Hikvision Digital Technology Co.,Ltd.", LatencyMs = 2, OpenPorts = { 80, 443, 554, 8000 }, Sources = Scan.FoundBy.Arp | Scan.FoundBy.Ping },
            new Scan.ScanResult { Ip = "192.168.1.21", Mac = "00-1A-2B-10-00-02", Vendor = "Axis Communications AB", LatencyMs = 1, OpenPorts = { 80, 443, 554 }, Sources = Scan.FoundBy.Arp },
            new Scan.ScanResult { Ip = "192.168.1.40", Mac = "00-1A-2B-20-00-01", Vendor = "Synology Incorporated", Hostname = "nvr-01", LatencyMs = 1, OpenPorts = { 80, 443, 22 }, Sources = Scan.FoundBy.Arp | Scan.FoundBy.Ping },
        };

        public static async Task ScanAsync(IReadOnlyList<uint> hosts, IProgress<Scan.ScanResult> found, IProgress<int> completed, CancellationToken token)
        {
            for (var i = 0; i < hosts.Count; i++)
            {
                if (token.IsCancellationRequested) return;
                if (i % 16 == 0) await Task.Delay(220);
                completed.Report(i + 1);
                var match = ScanDevices.FirstOrDefault(d => IpTools.ToUInt(d.Ip) == hosts[i]);
                if (match != null) found.Report(Copy(match));
            }
        }

        public static async Task DiscoverAsync(IProgress<Scan.ScanResult> found, CancellationToken token)
        {
            await Task.Delay(900);
            if (token.IsCancellationRequested) return;
            found.Report(new Scan.ScanResult { Ip = "192.168.1.20", Sources = Scan.FoundBy.Onvif, DeviceName = "Gate camera", Manufacturer = "HIKVISION", Model = "DS-2CD2143G2-I" });
            // A factory-default camera on another subnet: only ONVIF finds it.
            found.Report(new Scan.ScanResult { Ip = "192.168.0.64", Sources = Scan.FoundBy.Onvif, DeviceName = "IP CAMERA", Manufacturer = "HIKVISION", Model = "DS-2CD2387G2-LU" });
            await Task.Delay(600);
            if (token.IsCancellationRequested) return;
            found.Report(new Scan.ScanResult { Ip = "192.168.1.40", Sources = Scan.FoundBy.Ssdp, DeviceName = "NVR-01", Manufacturer = "Synology", Model = "DVA3221" });
        }

        static readonly Random Jitter = new Random();

        /// <summary>
        /// Demo ping: 1 to 4 ms replies, a two-hop route for traceroute, and the Axis camera (192.168.1.21)
        /// dropping out for 12 seconds of every 40, so the Monitor page has something to alert on.
        /// </summary>
        public static PingReplyInfo Ping(string ip, int ttl)
        {
            Thread.Sleep(30);
            var hops = new[] { "192.168.1.1", "10.20.0.1" };
            var outside = !ip.StartsWith("192.168.1.");
            if (ttl > 0 && outside && ttl <= hops.Length)
                return new PingReplyInfo { TtlExpired = true, From = hops[ttl - 1], RoundTripMs = 2 * ttl, Error = "TTL expired" };
            if (ip == "192.168.1.21" && DateTime.Now.Second % 40 >= 10 && DateTime.Now.Second % 40 < 22)
                return new PingReplyInfo { Error = "timed out" };
            int rtt;
            lock (Jitter) rtt = 1 + Jitter.Next(4) + (outside ? 8 : 0);
            return new PingReplyInfo { Ok = true, RoundTripMs = rtt, From = ip };
        }

        /// <summary>Open ports of the sample scan devices, for the demo port checker.</summary>
        public static System.Collections.Generic.List<int> DemoPorts(string ip) =>
            ScanDevices.FirstOrDefault(d => d.Ip == ip)?.OpenPorts.ToList() ?? new System.Collections.Generic.List<int>();

        static Scan.ScanResult Copy(Scan.ScanResult r) => new Scan.ScanResult
        {
            Ip = r.Ip, Mac = r.Mac, Vendor = r.Vendor, Hostname = r.Hostname, LatencyMs = r.LatencyMs,
            OpenPorts = r.OpenPorts.ToList(), Sources = r.Sources,
        };

        public static OpResult SetDhcp(string alias)
        {
            Thread.Sleep(700);
            lock (Gate)
            {
                var a = Adapters[alias];
                a.IsDhcp = true;
                a.Addresses = new List<IpEntry> { new IpEntry { Ip = "192.168.1.104", Prefix = 24 } };
                a.Gateways = new List<string> { "192.168.1.1" };
                a.Dns = new List<string> { "192.168.1.1" };
            }
            return OpResult.Success();
        }
    }
}
