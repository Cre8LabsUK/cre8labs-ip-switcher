using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cre8Labs.IPSwitcher.Core.Dhcp
{
    public enum DhcpMessageType : byte
    {
        None = 0, Discover = 1, Offer = 2, Request = 3, Decline = 4, Ack = 5, Nak = 6, Release = 7, Inform = 8,
    }

    /// <summary>
    /// A BOOTP/DHCP message (RFC 2131) with its options (RFC 2132).
    /// IPv4 addresses are held as host-order uints, the same as <see cref="IpTools.ToUInt"/>.
    /// </summary>
    public sealed class DhcpPacket
    {
        public const byte OptPad = 0, OptSubnetMask = 1, OptRouter = 3, OptDns = 6, OptHostName = 12, OptRequestedIp = 50,
            OptLeaseTime = 51, OptMessageType = 53, OptServerId = 54, OptParamList = 55, OptMessage = 56,
            OptRenewalTime = 58, OptRebindingTime = 59, OptVendorClass = 60, OptClientId = 61, OptEnd = 255;

        const int FixedLength = 236;
        static readonly byte[] MagicCookie = { 99, 130, 83, 99 };

        public byte Op { get; set; } = 1;
        public byte HType { get; set; } = 1;
        public byte HLen { get; set; } = 6;
        public byte Hops { get; set; }
        public uint Xid { get; set; }
        public ushort Secs { get; set; }
        public ushort Flags { get; set; }
        public uint ClientIp { get; set; }   // ciaddr
        public uint YourIp { get; set; }     // yiaddr
        public uint ServerIp { get; set; }   // siaddr
        public uint RelayIp { get; set; }    // giaddr
        public byte[] ClientHardware { get; set; } = new byte[16];

        /// <summary>Options in the order they were added; order matters for some clients.</summary>
        public List<KeyValuePair<byte, byte[]>> Options { get; } = new List<KeyValuePair<byte, byte[]>>();

        public bool BroadcastFlag => (Flags & 0x8000) != 0;

        public DhcpMessageType MessageType
        {
            get { var v = Get(OptMessageType); return v != null && v.Length == 1 ? (DhcpMessageType)v[0] : DhcpMessageType.None; }
        }

        /// <summary>Client hardware address as "AA-BB-CC-DD-EE-FF".</summary>
        public string Mac => string.Join("-", ClientHardware.Take(Math.Min((int)HLen, 16)).Select(b => b.ToString("X2")));

        public byte[] Get(byte code) => Options.FirstOrDefault(o => o.Key == code).Value;

        public uint GetIp(byte code)
        {
            var v = Get(code);
            return v != null && v.Length >= 4 ? ReadUInt(v, 0) : 0;
        }

        /// <summary>A text option such as the host name, trimmed of trailing NULs and limited to printable characters.</summary>
        public string GetText(byte code)
        {
            var v = Get(code);
            if (v == null) return "";
            var s = Encoding.ASCII.GetString(v).TrimEnd('\0');
            return new string(s.Where(c => c >= 0x20 && c < 0x7F).ToArray()).Trim();
        }

        public void Set(byte code, byte[] value)
        {
            Options.RemoveAll(o => o.Key == code);
            Options.Add(new KeyValuePair<byte, byte[]>(code, value));
        }

        public void SetIp(byte code, uint ip) => Set(code, ToBytes(ip));
        public void SetIps(byte code, IEnumerable<uint> ips) => Set(code, ips.SelectMany(ToBytes).ToArray());
        public void SetUInt(byte code, uint value) => Set(code, ToBytes(value));

        /// <summary>Returns null when the data is not a well-formed DHCP message.</summary>
        public static DhcpPacket Parse(byte[] data, int length)
        {
            if (data == null || length < FixedLength + 4 || length > data.Length) return null;
            for (var i = 0; i < 4; i++) if (data[FixedLength + i] != MagicCookie[i]) return null;

            var p = new DhcpPacket
            {
                Op = data[0], HType = data[1], HLen = data[2], Hops = data[3],
                Xid = ReadUInt(data, 4),
                Secs = (ushort)((data[8] << 8) | data[9]),
                Flags = (ushort)((data[10] << 8) | data[11]),
                ClientIp = ReadUInt(data, 12), YourIp = ReadUInt(data, 16),
                ServerIp = ReadUInt(data, 20), RelayIp = ReadUInt(data, 24),
            };
            if (p.HLen > 16) return null;
            Array.Copy(data, 28, p.ClientHardware, 0, 16);

            var pos = FixedLength + 4;
            while (pos < length)
            {
                var code = data[pos++];
                if (code == OptPad) continue;
                if (code == OptEnd) break;
                if (pos >= length) return null;
                int len = data[pos++];
                if (pos + len > length) return null;
                var value = new byte[len];
                Array.Copy(data, pos, value, 0, len);
                pos += len;
                if (p.Get(code) == null) p.Options.Add(new KeyValuePair<byte, byte[]>(code, value));
            }
            return p.MessageType == DhcpMessageType.None ? null : p;
        }

        public byte[] ToBytes()
        {
            var bytes = new List<byte>(320) { Op, HType, HLen, Hops };
            bytes.AddRange(ToBytes(Xid));
            bytes.Add((byte)(Secs >> 8)); bytes.Add((byte)Secs);
            bytes.Add((byte)(Flags >> 8)); bytes.Add((byte)Flags);
            bytes.AddRange(ToBytes(ClientIp));
            bytes.AddRange(ToBytes(YourIp));
            bytes.AddRange(ToBytes(ServerIp));
            bytes.AddRange(ToBytes(RelayIp));
            bytes.AddRange(ClientHardware.Take(16));
            bytes.AddRange(new byte[192]); // sname and file
            bytes.AddRange(MagicCookie);
            foreach (var o in Options)
            {
                bytes.Add(o.Key);
                bytes.Add((byte)o.Value.Length);
                bytes.AddRange(o.Value);
            }
            bytes.Add(OptEnd);
            // Some older clients ignore replies shorter than the 300-byte BOOTP minimum.
            while (bytes.Count < 300) bytes.Add(OptPad);
            return bytes.ToArray();
        }

        static uint ReadUInt(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
        static byte[] ToBytes(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }
}
