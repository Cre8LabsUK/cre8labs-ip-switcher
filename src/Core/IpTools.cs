using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cre8Labs.IPSwitcher.Core
{
    /// <summary>IPv4 parsing and subnet maths.</summary>
    public static class IpTools
    {
        // [0-9] rather than \d, which also matches non-ASCII digits that int.Parse rejects.
        static readonly Regex Dotted = new Regex(@"^[0-9]{1,3}(\.[0-9]{1,3}){3}$", RegexOptions.Compiled);

        public static bool IsIPv4(string text)
        {
            text = (text ?? "").Trim();
            if (!Dotted.IsMatch(text)) return false;
            return text.Split('.').All(o => int.Parse(o) <= 255);
        }

        /// <summary>Canonical form, so "010.000.001.005" becomes "10.0.1.5" before it reaches netsh.</summary>
        public static string Canonical(string ip) => FromUInt(ToUInt(ip));

        /// <summary>A usable host address: not 0.0.0.0, not multicast or reserved (224 and up).</summary>
        public static bool IsHostAddress(string ip)
        {
            if (!IsIPv4(ip)) return false;
            var v = ToUInt(ip);
            return v != 0 && (v >> 24) < 224;
        }

        public static uint ToUInt(string ip)
        {
            var o = ip.Trim().Split('.').Select(uint.Parse).ToArray();
            return (o[0] << 24) | (o[1] << 16) | (o[2] << 8) | o[3];
        }

        public static string FromUInt(uint v) =>
            $"{(v >> 24) & 255}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

        public static string PrefixToMask(int prefix) =>
            FromUInt(prefix == 0 ? 0u : uint.MaxValue << (32 - prefix));

        /// <summary>Accepts "255.255.255.0", "24" or "/24". Returns a dotted mask, or null if invalid.</summary>
        public static string NormalizeMask(string text)
        {
            text = (text ?? "").Trim().TrimStart('/');
            if (Regex.IsMatch(text, @"^[0-9]{1,2}$"))
            {
                var prefix = int.Parse(text);
                return prefix >= 1 && prefix <= 32 ? PrefixToMask(prefix) : null;
            }
            if (!IsIPv4(text)) return null;
            var bits = ToUInt(text);
            // A valid mask is a run of ones followed by zeros.
            var inverted = ~bits;
            if (bits == 0 || (inverted & (inverted + 1)) != 0) return null;
            return FromUInt(bits);
        }

        public static int PrefixLength(string mask)
        {
            var bits = ToUInt(mask);
            var count = 0;
            while (bits != 0) { count += (int)(bits & 1); bits >>= 1; }
            return count;
        }

        public static bool SameSubnet(string a, string b, string mask)
        {
            var m = ToUInt(mask);
            return (ToUInt(a) & m) == (ToUInt(b) & m);
        }

        public static string Describe(string ip, string mask)
        {
            var prefix = PrefixLength(mask);
            var network = FromUInt(ToUInt(ip) & ToUInt(mask));
            var hosts = prefix >= 31 ? Math.Pow(2, 32 - prefix) : Math.Pow(2, 32 - prefix) - 2;
            return $"Network {network}/{prefix}, {hosts:N0} usable hosts";
        }
    }
}
