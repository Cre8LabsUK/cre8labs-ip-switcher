using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Cre8Labs.NetTools.Core;
using Cre8Labs.NetTools.Core.Dhcp;

namespace Cre8Labs.NetTools.Tests
{
    static class Program
    {
        static int _passed, _failed;

        static int Main()
        {
            Run("packet round trip", PacketRoundTrip);
            Run("packet parser rejects bad data", PacketRejectsBadData);
            Run("full discover, offer, request, ack", FullHandshake);
            Run("second device gets the next address", SecondDevice);
            Run("renewal keeps the address", Renewal);
            Run("request for another network is refused", ForeignNetwork);
            Run("client choosing another server is detected", OtherServer);
            Run("requested address is honoured", RequestedAddress);
            Run("server and gateway are never handed out", ServerAndGatewayExcluded);
            Run("address that answers ping is skipped", PingConflict);
            Run("declined address is skipped", Decline);
            Run("released address can be reused", Release);
            Run("full pool gives no offer", Exhaustion);
            Run("reservation outside the range", ReservationOutsideRange);
            Run("reserved address never goes to another device", ReservationProtected);
            Run("new reservation moves a device at renewal", ReservationMovesDevice);
            Run("settings validation", SettingsValidation);
            Run("suggested ranges", Suggestions);
            Run("network overlap check", Overlap);
            Run("MAC address formats", MacFormats);
            Run("server answers over a real socket", SocketSmokeTest);

            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ helpers

        static void Run(string name, Action test)
        {
            try { test(); _passed++; Console.WriteLine("  ok    " + name); }
            catch (Exception ex) { _failed++; Console.WriteLine("  FAIL  " + name + ": " + ex.Message); }
        }

        static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }

        static uint Ip(string s) => IpTools.ToUInt(s);
        static string Text(uint ip) => IpTools.FromUInt(ip);
        static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        static PoolConfig Config(string start = "192.168.1.100", string end = "192.168.1.199", string gateway = null) =>
            new PoolConfig
            {
                Network = Ip("192.168.1.0"), Mask = Ip("255.255.255.0"), Server = Ip("192.168.1.1"),
                RangeStart = Ip(start), RangeEnd = Ip(end), Gateway = gateway == null ? 0 : Ip(gateway),
                Lease = TimeSpan.FromMinutes(10),
            };

        static DhcpPacket Msg(DhcpMessageType type, int device, uint requested = 0, uint serverId = 0, uint clientIp = 0)
        {
            var p = new DhcpPacket { Xid = (uint)(1000 + device), ClientIp = clientIp, Flags = 0x8000 };
            var mac = new byte[] { 0x00, 0x1A, 0x2B, 0x00, 0x00, (byte)device };
            Array.Copy(mac, p.ClientHardware, 6);
            p.Set(DhcpPacket.OptMessageType, new[] { (byte)type });
            if (requested != 0) p.SetIp(DhcpPacket.OptRequestedIp, requested);
            if (serverId != 0) p.SetIp(DhcpPacket.OptServerId, serverId);
            p.Set(DhcpPacket.OptHostName, System.Text.Encoding.ASCII.GetBytes("cam-" + device));
            return p;
        }

        static string Mac(int device) => $"00-1A-2B-00-00-{device:X2}";

        /// <summary>Discover then request, as a real client does. Returns the acknowledged address.</summary>
        static uint Join(DhcpEngine engine, int device, DateTime now)
        {
            var offer = engine.Handle(Msg(DhcpMessageType.Discover, device), now);
            Check(offer != null && offer.MessageType == DhcpMessageType.Offer, $"device {device} got no offer");
            var ack = engine.Handle(Msg(DhcpMessageType.Request, device, offer.YourIp, engine.Config.Server), now);
            Check(ack != null && ack.MessageType == DhcpMessageType.Ack, $"device {device} got no ack");
            return ack.YourIp;
        }

        // ------------------------------------------------------------------ packets

        static void PacketRoundTrip()
        {
            var p = Msg(DhcpMessageType.Discover, 7, requested: Ip("192.168.1.150"));
            var bytes = p.ToBytes();
            Check(bytes.Length >= 300, "shorter than the 300-byte BOOTP minimum");
            var back = DhcpPacket.Parse(bytes, bytes.Length);
            Check(back != null, "did not parse");
            Check(back.Xid == p.Xid && back.Flags == 0x8000, "header fields changed");
            Check(back.MessageType == DhcpMessageType.Discover, "message type changed");
            Check(back.Mac == Mac(7), "MAC changed: " + back.Mac);
            Check(back.GetIp(DhcpPacket.OptRequestedIp) == Ip("192.168.1.150"), "requested IP changed");
            Check(back.GetText(DhcpPacket.OptHostName) == "cam-7", "host name changed");
        }

        static void PacketRejectsBadData()
        {
            Check(DhcpPacket.Parse(new byte[10], 10) == null, "accepted a 10-byte packet");
            var bytes = Msg(DhcpMessageType.Discover, 1).ToBytes();
            bytes[236] = 0; // break the magic cookie
            Check(DhcpPacket.Parse(bytes, bytes.Length) == null, "accepted a bad magic cookie");
            var good = Msg(DhcpMessageType.Discover, 1).ToBytes();
            good[241] = 200; // option length runs past the end
            Check(DhcpPacket.Parse(good, 250) == null, "accepted a truncated option");
        }

        // ------------------------------------------------------------------ engine

        static void FullHandshake()
        {
            var engine = new DhcpEngine(Config());
            var bound = 0;
            engine.LeaseBound += l => bound++;

            var offer = engine.Handle(Msg(DhcpMessageType.Discover, 1), T0);
            Check(offer.MessageType == DhcpMessageType.Offer, "not an offer");
            Check(Text(offer.YourIp) == "192.168.1.100", "offered " + Text(offer.YourIp));
            Check(offer.GetIp(DhcpPacket.OptServerId) == Ip("192.168.1.1"), "wrong server id");
            Check(offer.GetIp(DhcpPacket.OptSubnetMask) == Ip("255.255.255.0"), "wrong mask");
            Check(offer.Get(DhcpPacket.OptRouter) == null, "sent a gateway when none was set");
            Check(offer.GetIp(DhcpPacket.OptLeaseTime) == 600, "lease time not 600 seconds");
            Check(offer.Op == 2 && offer.Xid == 1001, "reply header wrong");
            Check(bound == 0, "bound before the request");

            var ack = engine.Handle(Msg(DhcpMessageType.Request, 1, offer.YourIp, Ip("192.168.1.1")), T0);
            Check(ack.MessageType == DhcpMessageType.Ack && ack.YourIp == offer.YourIp, "no matching ack");
            Check(bound == 1, "LeaseBound fired " + bound + " times");
            var lease = engine.Snapshot().Single();
            Check(lease.State == LeaseState.Active && lease.Hostname == "cam-1", "lease not active with host name");
            Check(lease.Expires == T0.AddMinutes(10), "wrong expiry");
        }

        static void SecondDevice()
        {
            var engine = new DhcpEngine(Config());
            Check(Text(Join(engine, 1, T0)) == "192.168.1.100", "first device");
            Check(Text(Join(engine, 2, T0)) == "192.168.1.101", "second device");
            Check(Text(Join(engine, 1, T0)) == "192.168.1.100", "first device asking again changed address");
        }

        static void Renewal()
        {
            var engine = new DhcpEngine(Config());
            var ip = Join(engine, 1, T0);
            var renew = engine.Handle(Msg(DhcpMessageType.Request, 1, clientIp: ip), T0.AddMinutes(5));
            Check(renew.MessageType == DhcpMessageType.Ack && renew.YourIp == ip, "renewal not acknowledged");
            Check(renew.ClientIp == ip, "renewal ack should echo ciaddr");
            Check(engine.Snapshot().Single().Expires == T0.AddMinutes(15), "lease not extended");
        }

        static void ForeignNetwork()
        {
            var engine = new DhcpEngine(Config());
            var reply = engine.Handle(Msg(DhcpMessageType.Request, 1, Ip("10.0.0.50")), T0);
            Check(reply != null && reply.MessageType == DhcpMessageType.Nak, "expected a NAK");
        }

        static void OtherServer()
        {
            var engine = new DhcpEngine(Config());
            uint seen = 0;
            engine.OtherServerSeen += ip => seen = ip;
            engine.Handle(Msg(DhcpMessageType.Discover, 1), T0);
            var reply = engine.Handle(Msg(DhcpMessageType.Request, 1, Ip("192.168.1.100"), Ip("192.168.1.254")), T0);
            Check(reply == null, "answered a request meant for another server");
            Check(seen == Ip("192.168.1.254"), "other server not reported");
            Check(engine.Snapshot().Count == 0, "kept our withdrawn offer");
        }

        static void RequestedAddress()
        {
            var engine = new DhcpEngine(Config());
            var offer = engine.Handle(Msg(DhcpMessageType.Discover, 1, Ip("192.168.1.150")), T0);
            Check(Text(offer.YourIp) == "192.168.1.150", "offered " + Text(offer.YourIp));
        }

        static void ServerAndGatewayExcluded()
        {
            var engine = new DhcpEngine(Config("192.168.1.1", "192.168.1.5", gateway: "192.168.1.2"));
            Check(Text(Join(engine, 1, T0)) == "192.168.1.3", "did not skip the server and gateway");
            var asked = engine.Handle(Msg(DhcpMessageType.Discover, 2, Ip("192.168.1.1")), T0);
            Check(asked.YourIp != Ip("192.168.1.1"), "handed out the server address on request");
        }

        static void PingConflict()
        {
            var engine = new DhcpEngine(Config(), ip => ip == Ip("192.168.1.100"));
            Check(Text(Join(engine, 1, T0)) == "192.168.1.101", "used an address that answered ping");
        }

        static void Decline()
        {
            var engine = new DhcpEngine(Config());
            var ip = Join(engine, 1, T0);
            engine.Handle(Msg(DhcpMessageType.Decline, 1, ip), T0);
            Check(Join(engine, 2, T0) != ip, "re-offered a declined address");
            Check(Join(engine, 1, T0) != ip, "re-offered a declined address to the same device");
        }

        static void Release()
        {
            var engine = new DhcpEngine(Config("192.168.1.100", "192.168.1.100"));
            var ip = Join(engine, 1, T0);
            engine.Handle(Msg(DhcpMessageType.Release, 1, clientIp: ip), T0);
            Check(engine.Snapshot().Single().State == LeaseState.Released, "not marked released");
            Check(Join(engine, 2, T0.AddSeconds(1)) == ip, "released address was not reused");
        }

        static void Exhaustion()
        {
            var engine = new DhcpEngine(Config("192.168.1.100", "192.168.1.101"));
            Join(engine, 1, T0);
            Join(engine, 2, T0);
            Check(engine.Handle(Msg(DhcpMessageType.Discover, 3), T0) == null, "offered from a full pool");
            Check(Join(engine, 3, T0.AddMinutes(11)) != 0, "expired leases were not reused");
        }

        static Dictionary<string, uint> Reserve(params (int device, string ip)[] items) =>
            items.ToDictionary(i => Mac(i.device), i => Ip(i.ip), StringComparer.OrdinalIgnoreCase);

        static void ReservationOutsideRange()
        {
            var config = Config();
            config.Reservations = Reserve((5, "192.168.1.20"));
            var engine = new DhcpEngine(config);
            Check(Text(Join(engine, 5, T0)) == "192.168.1.20", "reserved device did not get its address");
            Check(engine.Snapshot().Single().ReservedIp == Ip("192.168.1.20"), "snapshot does not show the reservation");
        }

        static void ReservationProtected()
        {
            var config = Config("192.168.1.100", "192.168.1.102");
            config.Reservations = Reserve((5, "192.168.1.100"));
            var engine = new DhcpEngine(config);
            Check(Text(Join(engine, 1, T0)) == "192.168.1.101", "gave a reserved address to another device");
            var nak = engine.Handle(Msg(DhcpMessageType.Request, 2, Ip("192.168.1.100"), Ip("192.168.1.1")), T0);
            Check(nak.MessageType == DhcpMessageType.Nak, "acknowledged a request for someone else's reservation");
            Check(Text(Join(engine, 5, T0)) == "192.168.1.100", "reserved device lost its address");
        }

        static void ReservationMovesDevice()
        {
            var engine = new DhcpEngine(Config());
            var old = Join(engine, 1, T0);
            engine.SetReservations(Reserve((1, "192.168.1.21")));
            var renew = engine.Handle(Msg(DhcpMessageType.Request, 1, clientIp: old), T0.AddMinutes(5));
            Check(renew.MessageType == DhcpMessageType.Nak, "renewal of the old address was not refused");
            Check(Text(Join(engine, 1, T0.AddMinutes(5))) == "192.168.1.21", "device did not move to its reservation");
        }

        // ------------------------------------------------------------------ settings

        static void SettingsValidation()
        {
            var ok = new PoolSettings().Validate(out var e1);
            Check(ok != null && e1.Count == 0, "defaults invalid: " + string.Join("; ", e1.Values));
            Check(ok.Size == 100, "default pool size " + ok.Size);

            var s = new PoolSettings { Network = "banana" };
            Check(s.Validate(out var e2) == null && e2.ContainsKey("Network"), "accepted a bad network");

            s = new PoolSettings { RangeEnd = "192.168.2.10" };
            Check(s.Validate(out var e3) == null && e3.ContainsKey("RangeEnd"), "accepted a range outside the network");

            s = new PoolSettings { RangeStart = "192.168.1.200", RangeEnd = "192.168.1.100" };
            Check(s.Validate(out var e4) == null && e4.ContainsKey("RangeEnd"), "accepted a backwards range");

            s = new PoolSettings();
            s.Reservations.Add(new Reservation { Mac = "aa:bb:cc:dd:ee:01", Ip = "192.168.1.20", Name = "Gate" });
            s.Reservations.Add(new Reservation { Mac = "aa:bb:cc:dd:ee:02", Ip = "192.168.1.20", Name = "Car park" });
            Check(s.Validate(out var e5) == null && e5.ContainsKey("Reservations"), "accepted two devices on one address");

            s = new PoolSettings();
            s.Reservations.Add(new Reservation { Mac = "aa:bb:cc:dd:ee:01", Ip = "10.0.0.5", Name = "Gate" });
            Check(s.Validate(out var e6) == null && e6.ContainsKey("Reservations"), "accepted a reservation outside the network");

            s = new PoolSettings();
            s.Reservations.Add(new Reservation { Mac = "aa:bb:cc:dd:ee:01", Ip = "192.168.1.1" });
            Check(s.Validate(out var e7) == null && e7.ContainsKey("Reservations"), "accepted a reservation on the server address");

            s = new PoolSettings { Network = "192.168.1.77/24" };
            PoolSettings.TryParseNetwork(s.Network, out var net, out _);
            Check(Text(net) == "192.168.1.0", "host address in network field not reduced to the network");
        }

        static void Suggestions()
        {
            var a = PoolSettings.Suggest(Ip("10.0.5.0"), Ip("255.255.255.0"));
            Check(a.Server == "10.0.5.1" && a.RangeStart == "10.0.5.100" && a.RangeEnd == "10.0.5.199", "/24 suggestion");
            var b = PoolSettings.Suggest(Ip("192.168.9.16"), Ip("255.255.255.240"));
            Check(b.Validate(out var errors) != null, "/28 suggestion invalid: " + string.Join("; ", errors.Values));
            Check(b.Server == "192.168.9.17" && b.RangeEnd == "192.168.9.30", $"/28 suggestion {b.Server} {b.RangeStart}-{b.RangeEnd}");
        }

        static void Overlap()
        {
            var c = Config();
            Check(c.Overlaps(Ip("192.168.1.77"), 24), "same /24 not seen as overlapping");
            Check(c.Overlaps(Ip("192.168.200.1"), 16), "wider /16 not seen as overlapping");
            Check(!c.Overlaps(Ip("10.0.0.1"), 8), "10.0.0.0/8 seen as overlapping");
            Check(!c.Overlaps(Ip("192.168.2.1"), 24), "neighbouring /24 seen as overlapping");
        }

        static void MacFormats()
        {
            Check(Reservation.NormalizeMac("aa:bb:cc:dd:ee:ff") == "AA-BB-CC-DD-EE-FF", "colon format");
            Check(Reservation.NormalizeMac("AABB.CCDD.EEFF") == "AA-BB-CC-DD-EE-FF", "dotted format");
            Check(Reservation.NormalizeMac("aabbccddeeff") == "AA-BB-CC-DD-EE-FF", "bare format");
            Check(Reservation.NormalizeMac("aa:bb:cc") == null, "accepted a short MAC");
        }

        // ------------------------------------------------------------------ sockets

        /// <summary>
        /// Runs the real server on 127.0.0.1 (no admin needed, nothing leaves the PC) and sends it a DISCOVER.
        /// Replies go back to the sender here, because broadcasts do not travel over loopback.
        /// </summary>
        static void SocketSmokeTest()
        {
            var config = new PoolConfig
            {
                Network = Ip("127.0.0.0"), Mask = Ip("255.0.0.0"), Server = Ip("127.0.0.1"),
                RangeStart = Ip("127.0.0.100"), RangeEnd = Ip("127.0.0.110"),
            };
            var server = new DhcpServer(config) { ReplyToSender = true, SkipProbe = true };
            var running = new TaskCompletionSource<bool>();
            server.StateChanged += s => { if (s == PoolState.Running) running.TrySetResult(true); };
            server.Start();
            try
            {
                Check(running.Task.Wait(TimeSpan.FromSeconds(5)), "server never reached Running (state " + server.State + ")");
                using (var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                {
                    var discover = Msg(DhcpMessageType.Discover, 9).ToBytes();
                    client.Send(discover, discover.Length, new IPEndPoint(IPAddress.Loopback, 67));
                    var receive = client.ReceiveAsync();
                    Check(receive.Wait(TimeSpan.FromSeconds(3)), "no reply within 3 seconds");
                    var offer = DhcpPacket.Parse(receive.Result.Buffer, receive.Result.Buffer.Length);
                    Check(offer != null && offer.MessageType == DhcpMessageType.Offer, "reply was not an offer");
                    Check(Text(offer.YourIp) == "127.0.0.100", "offered " + Text(offer.YourIp));
                }
            }
            finally { server.Stop(); }
            Check(server.State == PoolState.Stopped, "did not stop");
        }
    }
}
