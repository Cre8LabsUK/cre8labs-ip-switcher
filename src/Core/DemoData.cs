using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Cre8Labs.IPSwitcher.Core
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
