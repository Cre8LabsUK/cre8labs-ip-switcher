using System;
using System.Collections.Generic;

namespace Cre8Labs.NetTools.Core.Scan
{
    /// <summary>Parses what the user types into the scan box into a list of host addresses.</summary>
    public static class ScanRange
    {
        /// <summary>Largest sweep allowed: a /22 (1,022 hosts).</summary>
        public const int MaxHosts = 1022;

        /// <summary>
        /// Accepts "192.168.1.0/24", "192.168.1.10-192.168.1.50", "192.168.1.10-50" or a single address.
        /// Network and broadcast addresses are left out of subnets. Returns null with a reason when invalid or too large.
        /// </summary>
        public static List<uint> Parse(string text, out string error)
        {
            error = null;
            text = (text ?? "").Trim();
            uint first, last;

            if (text.Contains("/"))
            {
                var parts = text.Split('/');
                var mask = IpTools.NormalizeMask(parts[1]);
                if (parts.Length != 2 || !IpTools.IsIPv4(parts[0]) || mask == null) { error = "Use a range such as 192.168.1.0/24."; return null; }
                var m = IpTools.ToUInt(mask);
                var network = IpTools.ToUInt(parts[0]) & m;
                var broadcast = network | ~m;
                var prefix = IpTools.PrefixLength(mask);
                first = prefix >= 31 ? network : network + 1;
                last = prefix >= 31 ? broadcast : broadcast - 1;
            }
            else if (text.Contains("-"))
            {
                var parts = text.Split('-');
                if (parts.Length != 2 || !IpTools.IsIPv4(parts[0])) { error = "Use a range such as 192.168.1.10-192.168.1.50."; return null; }
                first = IpTools.ToUInt(parts[0]);
                var end = parts[1].Trim();
                if (IpTools.IsIPv4(end)) last = IpTools.ToUInt(end);
                else if (byte.TryParse(end, out var octet)) last = (first & 0xFFFFFF00) | octet;
                else { error = "Use a range such as 192.168.1.10-50."; return null; }
                if (last < first) { error = "The range must end after it starts."; return null; }
            }
            else if (IpTools.IsIPv4(text))
            {
                first = last = IpTools.ToUInt(text);
            }
            else
            {
                error = "Use a range such as 192.168.1.0/24.";
                return null;
            }

            if ((long)last - first + 1 > MaxHosts)
            {
                error = $"That is {(long)last - first + 1:N0} addresses. Scan at most {MaxHosts:N0} at a time (a /22).";
                return null;
            }
            var hosts = new List<uint>((int)(last - first + 1));
            for (var ip = first; ip <= last && ip != 0; ip++)
            {
                hosts.Add(ip);
                if (ip == uint.MaxValue) break;
            }
            return hosts;
        }

        /// <summary>The adapter's own subnet as scan text, capped to a /22 around the adapter for large networks.</summary>
        public static string ForAdapter(string ip, string mask)
        {
            if (!IpTools.IsIPv4(ip) || IpTools.NormalizeMask(mask) == null) return "";
            var prefix = Math.Max(22, IpTools.PrefixLength(mask));
            var m = IpTools.ToUInt(IpTools.PrefixToMask(prefix));
            return $"{IpTools.FromUInt(IpTools.ToUInt(ip) & m)}/{prefix}";
        }
    }
}
