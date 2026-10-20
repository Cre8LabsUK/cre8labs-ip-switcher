using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Win32;

namespace Cre8Labs.IPSwitcher.Core
{
    public sealed class AdapterInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsUp { get; set; }
    }

    public sealed class IpEntry
    {
        public string Ip { get; set; }
        public int Prefix { get; set; }
    }

    public sealed class AdapterStatus
    {
        public string Name { get; set; }
        public bool IsUp { get; set; }
        public long Speed { get; set; }
        public string Mac { get; set; }
        public bool IsDhcp { get; set; }
        public List<IpEntry> Addresses { get; set; } = new List<IpEntry>();
        public List<string> Gateways { get; set; } = new List<string>();
        public List<string> Dns { get; set; } = new List<string>();

        /// <summary>True when the adapter has no link and the addresses come from its saved static config.</summary>
        public bool FromSavedConfig { get; set; }
    }

    public sealed class OpResult
    {
        public bool Ok { get; private set; }
        public string Message { get; private set; }

        public static OpResult Success(string message = "") => new OpResult { Ok = true, Message = message };
        public static OpResult Fail(string message) => new OpResult { Ok = false, Message = message };
    }

    public static class NetworkService
    {
        /// <summary>Set by "--demo": fake adapters for screenshots, nothing touches the real network.</summary>
        public static bool DemoMode { get; set; }

        public static bool IsAdmin()
        {
            if (DemoMode) return true;
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static IEnumerable<NetworkInterface> Usable() =>
            NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                n.Supports(NetworkInterfaceComponent.IPv4));

        static readonly string[] VirtualHints =
            { "virtual", "hyper-v", "vpn", "tap-", "tunnel", "tailscale", "wireguard", "vmware", "virtualbox", "zerotier", "loopback", "bluetooth" };

        // Real wired adapters first (the ones you plug into kit), then Wi-Fi, then virtual and VPN adapters.
        static int Rank(NetworkInterface n)
        {
            var text = (n.Name + " " + n.Description).ToLowerInvariant();
            if (VirtualHints.Any(text.Contains)) return 2;
            return n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0;
        }

        public static List<AdapterInfo> ListAdapters() =>
            DemoMode ? DemoData.ListAdapters() : Usable()
                .OrderBy(Rank)
                .ThenBy(n => n.OperationalStatus == OperationalStatus.Up ? 0 : 1)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => new AdapterInfo { Name = n.Name, Description = n.Description, IsUp = n.OperationalStatus == OperationalStatus.Up })
                .ToList();

        public static AdapterStatus ReadStatus(string name)
        {
            if (DemoMode) return DemoData.ReadStatus(name);
            var ni = Usable().FirstOrDefault(n => n.Name == name);
            if (ni == null) return null;

            var props = ni.GetIPProperties();
            var status = new AdapterStatus
            {
                Name = ni.Name,
                IsUp = ni.OperationalStatus == OperationalStatus.Up,
                Speed = ni.Speed,
                Mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
            };
            try { status.IsDhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? false; }
            catch (NetworkInformationException) { }

            foreach (var a in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var mask = a.IPv4Mask?.ToString() ?? "0.0.0.0";
                status.Addresses.Add(new IpEntry { Ip = a.Address.ToString(), Prefix = IpTools.IsIPv4(mask) ? IpTools.PrefixLength(mask) : 0 });
            }
            status.Gateways = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && g.Address.ToString() != "0.0.0.0")
                .Select(g => g.Address.ToString()).Distinct().ToList();
            status.Dns = props.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString()).Distinct().ToList();

            // With no cable, Windows reports no addresses. Show the saved static config instead.
            if (status.Addresses.Count == 0 && !status.IsDhcp) ReadSavedConfig(ni.Id, status);
            return status;
        }

        static void ReadSavedConfig(string id, AdapterStatus status)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{id}"))
                {
                    if (key == null) return;
                    var ips = key.GetValue("IPAddress") as string[] ?? new string[0];
                    var masks = key.GetValue("SubnetMask") as string[] ?? new string[0];
                    for (var i = 0; i < ips.Length; i++)
                    {
                        if (!IpTools.IsIPv4(ips[i]) || ips[i] == "0.0.0.0") continue;
                        var mask = i < masks.Length ? IpTools.NormalizeMask(masks[i]) : null;
                        status.Addresses.Add(new IpEntry { Ip = ips[i], Prefix = mask == null ? 0 : IpTools.PrefixLength(mask) });
                    }
                    if (status.Gateways.Count == 0 && key.GetValue("DefaultGateway") is string[] gws)
                        status.Gateways = gws.Where(IpTools.IsIPv4).ToList();
                    if (status.Dns.Count == 0 && key.GetValue("NameServer") is string ns)
                        status.Dns = ns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(IpTools.IsIPv4).ToList();
                    status.FromSavedConfig = status.Addresses.Count > 0;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
            {
            }
        }

        // ------------------------------------------------------------- changes (need admin)

        static string NameArg(string alias) => "name=\"" + alias.Replace("\"", "") + "\"";

        static (int Code, string Output) Netsh(string args)
        {
            // Full system path: this process is elevated, so never pick up a netsh.exe planted next to the app.
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"), args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
            }
        }

        public static OpResult ApplyStatic(string alias, Preset preset)
        {
            if (DemoMode) return DemoData.ApplyStatic(alias, preset, false);
            var mask = IpTools.NormalizeMask(preset.Mask);
            var gateway = IpTools.IsIPv4(preset.Gateway) ? "gateway=" + IpTools.Canonical(preset.Gateway) : "gateway=none";
            var name = NameArg(alias);

            var r = Netsh($"interface ipv4 set address {name} source=static address={IpTools.Canonical(preset.IP)} mask={mask} {gateway}");
            if (r.Code != 0) return OpResult.Fail("Could not set the address. " + r.Output);

            var dns = new[] { preset.DNS1, preset.DNS2 }.Where(IpTools.IsIPv4).Select(IpTools.Canonical).ToList();
            if (dns.Count == 0)
            {
                r = Netsh($"interface ipv4 set dnsservers {name} source=static address=none validate=no");
                if (r.Code != 0) return OpResult.Fail("Address set, but clearing DNS failed. " + r.Output);
            }
            else
            {
                r = Netsh($"interface ipv4 set dnsservers {name} source=static address={dns[0]} register=primary validate=no");
                if (r.Code != 0) return OpResult.Fail("Address set, but DNS failed. " + r.Output);
                if (dns.Count > 1)
                {
                    r = Netsh($"interface ipv4 add dnsservers {name} address={dns[1]} index=2 validate=no");
                    if (r.Code != 0) return OpResult.Fail("Address set, but DNS 2 failed. " + r.Output);
                }
            }
            return OpResult.Success();
        }

        public static OpResult AddAddress(string alias, Preset preset)
        {
            if (DemoMode) return DemoData.ApplyStatic(alias, preset, true);
            // Check the state up front rather than parsing netsh text, which is localised.
            if (ReadStatus(alias)?.IsDhcp == true)
                return OpResult.Fail("Extra IPs need a static adapter. Apply a static preset first, then add extras.");
            var mask = IpTools.NormalizeMask(preset.Mask);
            var r = Netsh($"interface ipv4 add address {NameArg(alias)} address={IpTools.Canonical(preset.IP)} mask={mask}");
            return r.Code == 0 ? OpResult.Success() : OpResult.Fail("Could not add the address. " + r.Output);
        }

        public static OpResult SetDhcp(string alias)
        {
            if (DemoMode) return DemoData.SetDhcp(alias);
            var name = NameArg(alias);
            // netsh fails if DHCP is already on, so only switch the address when it is static.
            if (ReadStatus(alias)?.IsDhcp != true)
            {
                var r = Netsh($"interface ipv4 set address {name} source=dhcp");
                if (r.Code != 0) return OpResult.Fail("Could not switch to DHCP. " + r.Output);
            }
            // Resetting DNS to automatic is safe to repeat; only report a failure if DNS is still static.
            var dns = Netsh($"interface ipv4 set dnsservers {name} source=dhcp");
            if (dns.Code != 0 && ReadStatus(alias)?.IsDhcp != true)
                return OpResult.Fail("DHCP is on, but automatic DNS failed. " + dns.Output);
            return OpResult.Success();
        }
    }
}
