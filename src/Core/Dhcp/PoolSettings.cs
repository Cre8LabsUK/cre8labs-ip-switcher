using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace Cre8Labs.IPSwitcher.Core.Dhcp
{
    /// <summary>A device that always gets the same address, for example a CCTV camera.</summary>
    [DataContract]
    public sealed class Reservation
    {
        [DataMember(Order = 1)] public string Mac { get; set; } = "";
        [DataMember(Order = 2)] public string Ip { get; set; } = "";
        [DataMember(Order = 3)] public string Name { get; set; } = "";

        /// <summary>"aa:bb:cc:dd:ee:ff", "AABB.CCDD.EEFF" and similar become "AA-BB-CC-DD-EE-FF"; null if not a MAC.</summary>
        public static string NormalizeMac(string text)
        {
            var hex = new string((text ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            if (hex.Length != 12) return null;
            return string.Join("-", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
        }
    }

    /// <summary>The DHCP pool as the user configures it. Saved in presets.json under "Pool".</summary>
    [DataContract]
    public sealed class PoolSettings
    {
        public PoolSettings() => SetDefaults();

        /// <summary>The network the pool serves, for example "192.168.1.0/24".</summary>
        [DataMember(Order = 1)] public string Network { get; set; }

        /// <summary>The PC's own address in that network. The adapter is set to this while the pool runs.</summary>
        [DataMember(Order = 2)] public string Server { get; set; }

        [DataMember(Order = 3)] public string RangeStart { get; set; }
        [DataMember(Order = 4)] public string RangeEnd { get; set; }
        [DataMember(Order = 5)] public int LeaseMinutes { get; set; }

        /// <summary>Optional gateway handed to devices. Empty by default so devices stay local.</summary>
        [DataMember(Order = 6)] public string Gateway { get; set; }

        /// <summary>Optional DNS servers, comma separated.</summary>
        [DataMember(Order = 7)] public string Dns { get; set; }

        /// <summary>Devices that always get the same address. They may sit outside the range, but must be inside the network.</summary>
        [DataMember(Order = 8)] public List<Reservation> Reservations { get; set; }

        [OnDeserializing]
        void OnDeserializing(StreamingContext context) => SetDefaults();

        void SetDefaults()
        {
            Network = "192.168.1.0/24";
            Server = "192.168.1.1";
            RangeStart = "192.168.1.100";
            RangeEnd = "192.168.1.199";
            LeaseMinutes = 10;
            Gateway = "";
            Dns = "";
            Reservations = new List<Reservation>();
        }

        public PoolSettings Clone()
        {
            var copy = (PoolSettings)MemberwiseClone();
            copy.Reservations = (Reservations ?? new List<Reservation>())
                .Select(r => new Reservation { Mac = r.Mac, Ip = r.Ip, Name = r.Name }).ToList();
            return copy;
        }

        /// <summary>Splits "192.168.1.0/24" or "192.168.1.0 255.255.255.0" into a network and mask.</summary>
        public static bool TryParseNetwork(string text, out uint network, out uint mask)
        {
            network = mask = 0;
            var parts = (text ?? "").Trim().Split(new[] { '/', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !IpTools.IsIPv4(parts[0])) return false;
            var dotted = IpTools.NormalizeMask(parts[1]);
            if (dotted == null) return false;
            mask = IpTools.ToUInt(dotted);
            // Allow typing any address inside the network, such as 192.168.1.5/24.
            network = IpTools.ToUInt(parts[0]) & mask;
            return IpTools.PrefixLength(dotted) <= 30;
        }

        /// <summary>Server .1 and a range of .100 to .199 for a /24, scaled down sensibly for smaller networks.</summary>
        public static PoolSettings Suggest(uint network, uint mask, PoolSettings keep = null)
        {
            var broadcast = network | ~mask;
            var hosts = broadcast - network - 1;
            uint start, end;
            if (hosts >= 254) { start = network + 100; end = network + 199; }
            else { start = network + 2 + hosts / 4; end = broadcast - 1; }

            var s = keep?.Clone() ?? new PoolSettings();
            s.Network = $"{IpTools.FromUInt(network)}/{IpTools.PrefixLength(IpTools.FromUInt(mask))}";
            s.Server = IpTools.FromUInt(network + 1);
            s.RangeStart = IpTools.FromUInt(start);
            s.RangeEnd = IpTools.FromUInt(end);
            return s;
        }

        /// <summary>Checks everything and builds the runtime config. Problems are returned as field name to message.</summary>
        public PoolConfig Validate(out Dictionary<string, string> errors)
        {
            var problems = new Dictionary<string, string>();
            errors = problems;
            return Build(problems);
        }

        PoolConfig Build(Dictionary<string, string> errors)
        {
            if (!TryParseNetwork(Network, out var network, out var mask))
            {
                errors[nameof(Network)] = "Use a network such as 192.168.1.0/24 (no smaller than /30).";
                return null;
            }
            var broadcast = network | ~mask;
            bool Inside(uint ip) => (ip & mask) == network && ip != network && ip != broadcast;

            uint Address(string field, string text, bool required)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (required) errors[field] = "Required.";
                    return 0;
                }
                if (!IpTools.IsIPv4(text)) { errors[field] = "Use the format 0.0.0.0"; return 0; }
                var ip = IpTools.ToUInt(text);
                if (!Inside(ip)) errors[field] = $"Must be inside {IpTools.FromUInt(network)}/{IpTools.PrefixLength(IpTools.FromUInt(mask))}.";
                return ip;
            }

            var server = Address(nameof(Server), Server, true);
            var start = Address(nameof(RangeStart), RangeStart, true);
            var end = Address(nameof(RangeEnd), RangeEnd, true);
            var gateway = Address(nameof(Gateway), Gateway, false);

            if (!errors.ContainsKey(nameof(RangeStart)) && !errors.ContainsKey(nameof(RangeEnd)) && start > end)
                errors[nameof(RangeEnd)] = "The range must end after it starts.";
            if (!errors.ContainsKey(nameof(Server)) && server >= start && server <= end && start <= end && end - start < 1)
                errors[nameof(Server)] = "The range has no room left once the server address is taken out.";
            if (LeaseMinutes < 1 || LeaseMinutes > 10080)
                errors[nameof(LeaseMinutes)] = "Between 1 minute and 7 days.";

            var dns = new List<uint>();
            foreach (var d in (Dns ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IpTools.IsIPv4(d)) { errors[nameof(Dns)] = "Use addresses separated by commas."; break; }
                dns.Add(IpTools.ToUInt(d));
            }
            if (dns.Count > 3) errors[nameof(Dns)] = "Up to 3 DNS servers.";

            var reserved = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Reservations ?? new List<Reservation>())
            {
                var mac = Reservation.NormalizeMac(r.Mac);
                var who = string.IsNullOrWhiteSpace(r.Name) ? r.Mac : r.Name;
                if (mac == null) { errors[nameof(Reservations)] = $"{who}: the MAC address is not valid."; break; }
                if (!IpTools.IsIPv4(r.Ip) || !Inside(IpTools.ToUInt(r.Ip)))
                { errors[nameof(Reservations)] = $"{who}: {r.Ip} is not inside the pool network."; break; }
                var ip = IpTools.ToUInt(r.Ip);
                if (ip == server || (gateway != 0 && ip == gateway))
                { errors[nameof(Reservations)] = $"{who}: {r.Ip} is the server or gateway address."; break; }
                if (reserved.ContainsKey(mac)) { errors[nameof(Reservations)] = $"{who}: this device is reserved twice."; break; }
                if (reserved.ContainsValue(ip)) { errors[nameof(Reservations)] = $"{who}: {r.Ip} is reserved for another device."; break; }
                reserved[mac] = ip;
            }

            if (errors.Count > 0) return null;
            return new PoolConfig
            {
                Network = network, Mask = mask, Server = server, RangeStart = start, RangeEnd = end,
                Gateway = gateway, Dns = dns.ToArray(), Lease = TimeSpan.FromMinutes(LeaseMinutes),
                Reservations = reserved,
            };
        }
    }

    /// <summary>Validated pool settings, as host-order uints.</summary>
    public sealed class PoolConfig
    {
        public uint Network { get; set; }
        public uint Mask { get; set; }
        public uint Server { get; set; }
        public uint RangeStart { get; set; }
        public uint RangeEnd { get; set; }
        public uint Gateway { get; set; }
        public uint[] Dns { get; set; } = new uint[0];
        public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>MAC ("AA-BB-...") to reserved address.</summary>
        public Dictionary<string, uint> Reservations { get; set; } = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        public uint Broadcast => Network | ~Mask;
        public int Prefix => IpTools.PrefixLength(IpTools.FromUInt(Mask));
        public string MaskText => IpTools.FromUInt(Mask);
        public string ServerText => IpTools.FromUInt(Server);
        public string RangeText => $"{IpTools.FromUInt(RangeStart)} to {IpTools.FromUInt(RangeEnd)}";

        /// <summary>"192.168.1.100 to .199" when both ends share the first three octets, for narrow spaces.</summary>
        public string ShortRangeText =>
            (RangeStart >> 8) == (RangeEnd >> 8) ? $"{IpTools.FromUInt(RangeStart)} to .{RangeEnd & 255}" : RangeText;
        public string NetworkText => $"{IpTools.FromUInt(Network)}/{Prefix}";
        public int Size => (int)(RangeEnd - RangeStart + 1) - (Server >= RangeStart && Server <= RangeEnd ? 1 : 0);

        public bool InNetwork(uint ip) => (ip & Mask) == Network;
        public bool InRange(uint ip) => ip >= RangeStart && ip <= RangeEnd;

        /// <summary>True when the two networks share any address.</summary>
        public bool Overlaps(uint otherIp, int otherPrefix)
        {
            var otherMask = otherPrefix == 0 ? 0u : uint.MaxValue << (32 - otherPrefix);
            var shared = Mask & otherMask; // the wider of the two masks
            return (Network & shared) == (otherIp & shared);
        }
    }
}
