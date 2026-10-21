using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Cre8Labs.NetTools.Core.Tools
{
    /// <summary>
    /// A small DNS client for the Tools page. It can ask a specific server (for example the one a camera is set to)
    /// from the selected adapter, which the Windows resolver cannot do.
    /// </summary>
    public static class DnsQuery
    {
        /// <summary>Names to addresses, or an address to its name. Empty server means the Windows resolver.</summary>
        public static async Task<List<string>> LookupAsync(IPAddress source, string name, string server, int timeoutMs = 2500)
        {
            name = (name ?? "").Trim().TrimEnd('.');
            if (IpTools.IsIPv4(name))
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(IPAddress.Parse(name)).ConfigureAwait(false);
                    return new List<string> { entry.HostName };
                }
                catch (SocketException) { return new List<string>(); }
            }
            if (string.IsNullOrWhiteSpace(server))
            {
                var addresses = await Dns.GetHostAddressesAsync(name).ConfigureAwait(false);
                return addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            }

            var id = (ushort)new Random().Next(1, 65535);
            var query = BuildQuery(id, name);
            using (var client = new UdpClient(new IPEndPoint(source, 0)))
            {
                await client.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse(server.Trim()), 53)).ConfigureAwait(false);
                var receive = client.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(timeoutMs)).ConfigureAwait(false) != receive)
                    throw new TimeoutException($"{server} did not answer within {timeoutMs / 1000.0:0.#} seconds.");
                return ParseAnswers((await receive.ConfigureAwait(false)).Buffer, id);
            }
        }

        /// <summary>A recursive A-record query.</summary>
        public static byte[] BuildQuery(ushort id, string name)
        {
            var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in name.Split('.'))
            {
                var data = Encoding.ASCII.GetBytes(label);
                if (data.Length == 0 || data.Length > 63) throw new ArgumentException("That is not a valid host name.");
                bytes.Add((byte)data.Length);
                bytes.AddRange(data);
            }
            bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // end, type A, class IN
            return bytes.ToArray();
        }

        /// <summary>The IPv4 addresses in a reply, or an explanation when the server says the name does not exist.</summary>
        public static List<string> ParseAnswers(byte[] packet, ushort id)
        {
            if (packet.Length < 12 || (packet[0] << 8 | packet[1]) != id) throw new InvalidOperationException("The reply did not match the question.");
            var rcode = packet[3] & 0x0F;
            if (rcode == 3) throw new InvalidOperationException("That name does not exist (NXDOMAIN).");
            if (rcode != 0) throw new InvalidOperationException($"The server refused the query (code {rcode}).");
            int questions = packet[4] << 8 | packet[5], answers = packet[6] << 8 | packet[7];
            var pos = 12;
            for (var i = 0; i < questions; i++) { SkipName(packet, ref pos); pos += 4; }
            var results = new List<string>();
            for (var i = 0; i < answers && pos + 10 <= packet.Length; i++)
            {
                SkipName(packet, ref pos);
                int type = packet[pos] << 8 | packet[pos + 1], length = packet[pos + 8] << 8 | packet[pos + 9];
                pos += 10;
                if (type == 1 && length == 4 && pos + 4 <= packet.Length) results.Add($"{packet[pos]}.{packet[pos + 1]}.{packet[pos + 2]}.{packet[pos + 3]}");
                pos += length;
            }
            return results;
        }

        static void SkipName(byte[] packet, ref int pos)
        {
            while (pos < packet.Length)
            {
                var length = packet[pos];
                if (length == 0) { pos++; return; }
                if ((length & 0xC0) == 0xC0) { pos += 2; return; }
                pos += length + 1;
            }
        }
    }
}
