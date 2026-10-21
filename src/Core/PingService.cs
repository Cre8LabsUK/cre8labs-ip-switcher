using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Cre8Labs.NetTools.Core
{
    public sealed class PingReplyInfo
    {
        public bool Ok { get; set; }
        public long RoundTripMs { get; set; }
        public string From { get; set; }
        public string Error { get; set; }

        /// <summary>A router along the way answered because the TTL ran out (used by traceroute). <see cref="From"/> is that router.</summary>
        public bool TtlExpired { get; set; }
    }

    /// <summary>
    /// ICMP echo bound to a source address, so the ping leaves through the selected adapter
    /// rather than whichever adapter Windows would route it through (System.Net.Ping cannot do this).
    /// </summary>
    public static class PingService
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern IntPtr IcmpCreateFile();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern bool IcmpCloseHandle(IntPtr handle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
            uint sourceAddress, uint destinationAddress, byte[] requestData, ushort requestSize,
            IntPtr requestOptions, IntPtr replyBuffer, uint replySize, uint timeout);

        const int ReplyBufferSize = 1024;

        public static IPAddress Resolve(string target)
        {
            if (IPAddress.TryParse(target, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) return ip;
            return Dns.GetHostAddresses(target).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new ArgumentException($"{target} has no IPv4 address.");
        }

        static uint ToNetworkOrder(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

        const uint StatusTtlExpired = 11013;

        /// <summary>Blocking; call from a background thread. A <paramref name="ttl"/> above 0 limits the hops (for traceroute).</summary>
        public static PingReplyInfo Send(IPAddress source, IPAddress destination, int timeoutMs, int ttl = 0)
        {
            if (NetworkService.DemoMode) return DemoData.Ping(destination.ToString(), ttl);

            var handle = IcmpCreateFile();
            if (handle == new IntPtr(-1)) return new PingReplyInfo { Error = new Win32Exception().Message };
            var buffer = Marshal.AllocHGlobal(ReplyBufferSize);
            // IP_OPTION_INFORMATION: Ttl, Tos, Flags, OptionsSize (bytes), then a pointer. Zeroed apart from the TTL.
            var options = IntPtr.Zero;
            if (ttl > 0)
            {
                options = Marshal.AllocHGlobal(IntPtr.Size * 2);
                for (var i = 0; i < IntPtr.Size * 2; i++) Marshal.WriteByte(options, i, 0);
                Marshal.WriteByte(options, 0, (byte)Math.Min(ttl, 255));
            }
            try
            {
                var payload = new byte[32];
                var count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    ToNetworkOrder(source), ToNetworkOrder(destination), payload, (ushort)payload.Length,
                    options, buffer, ReplyBufferSize, (uint)timeoutMs);

                // ICMP_ECHO_REPLY starts with Address, Status, RoundTripTime (all 32-bit).
                var status = count == 0 ? (uint)Marshal.GetLastWin32Error() : (uint)Marshal.ReadInt32(buffer, 4);
                var from = count > 0 ? new IPAddress((uint)Marshal.ReadInt32(buffer, 0)).ToString() : "";
                if (count > 0 && status == 0)
                    return new PingReplyInfo { Ok = true, RoundTripMs = (uint)Marshal.ReadInt32(buffer, 8), From = from };
                if (status == StatusTtlExpired)
                    return new PingReplyInfo { TtlExpired = true, From = from, RoundTripMs = count > 0 ? (uint)Marshal.ReadInt32(buffer, 8) : 0, Error = "TTL expired" };
                return new PingReplyInfo { Error = Describe(status) };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                if (options != IntPtr.Zero) Marshal.FreeHGlobal(options);
                IcmpCloseHandle(handle);
            }
        }

        static string Describe(uint status)
        {
            switch (status)
            {
                case 11010: return "timed out";
                case 11003: return "host unreachable";
                case 11002: return "network unreachable";
                case 11013: return "TTL expired";
                case 11050: return "general failure";
                case 1231: return "network unreachable";
                case 1232: return "host unreachable";
                default: return new Win32Exception((int)status).Message;
            }
        }
    }
}
