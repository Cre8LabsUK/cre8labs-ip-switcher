using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Cre8Labs.NetTools.Core;
using Cre8Labs.NetTools.Core.Dhcp;
using Cre8Labs.NetTools.Core.Oui;
using Cre8Labs.NetTools.Core.Scan;

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
            Run("scan ranges", ScanRanges);
            Run("scan range for an adapter is capped at /22", ScanRangeForAdapter);
            Run("ONVIF probe match parsing", OnvifProbeMatch);
            Run("ONVIF probe message", OnvifProbeMessage);
            Run("SSDP reply parsing", SsdpReply);
            Run("mDNS query and reply", MdnsPackets);
            Run("scan results merge by IP", ScanMerge);
            Run("vendor list format", OuiFormat);

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

        // ------------------------------------------------------------------ scanning and discovery

        static void ScanRanges()
        {
            var subnet = ScanRange.Parse("192.168.1.0/24", out var e1);
            Check(subnet != null && subnet.Count == 254, "/24 should be 254 hosts: " + e1);
            Check(Text(subnet[0]) == "192.168.1.1" && Text(subnet[253]) == "192.168.1.254", "/24 should skip network and broadcast");
            var anyHost = ScanRange.Parse("192.168.1.77/24", out _);
            Check(anyHost.Count == 254 && Text(anyHost[0]) == "192.168.1.1", "host address in CIDR not reduced to its network");
            var range = ScanRange.Parse("10.0.0.10-10.0.0.20", out _);
            Check(range.Count == 11, "full range");
            var shortRange = ScanRange.Parse("10.0.0.10-20", out _);
            Check(shortRange.Count == 11 && Text(shortRange[10]) == "10.0.0.20", "short range");
            Check(ScanRange.Parse("10.0.0.5", out _).Count == 1, "single address");
            Check(ScanRange.Parse("10.0.0.0/16", out var e2) == null && e2.Contains("1,022"), "a /16 was not refused");
            Check(ScanRange.Parse("10.0.0.0/22", out _).Count == 1022, "/22 should be allowed");
            Check(ScanRange.Parse("10.0.0.20-10", out _) == null, "backwards range accepted");
            Check(ScanRange.Parse("cameras", out _) == null, "nonsense accepted");
        }

        static void ScanRangeForAdapter()
        {
            Check(ScanRange.ForAdapter("192.168.1.250", "255.255.255.0") == "192.168.1.0/24", "/24 adapter");
            Check(ScanRange.ForAdapter("10.20.30.40", "255.255.0.0") == "10.20.28.0/22", "/16 adapter not capped to its /22");
            Check(ScanRange.ForAdapter("", "") == "", "no address");
        }

        // A ProbeMatches reply shaped like the ones IP cameras send (sample written for this test).
        const string SampleProbeMatch =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><env:Envelope xmlns:env=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:wsa=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\">" +
            "<env:Header><wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</wsa:Action></env:Header>" +
            "<env:Body><d:ProbeMatches><d:ProbeMatch><wsa:EndpointReference><wsa:Address>urn:uuid:1234</wsa:Address></wsa:EndpointReference>" +
            "<d:Types>dn:NetworkVideoTransmitter</d:Types>" +
            "<d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/name/Gate%20Camera " +
            "onvif://www.onvif.org/hardware/DS-2CD2143G2-I onvif://www.onvif.org/location/city/ onvif://www.onvif.org/manufacturer/HIKVISION</d:Scopes>" +
            "<d:XAddrs>http://192.168.0.64/onvif/device_service http://[fe80::1]/onvif/device_service</d:XAddrs>" +
            "<d:MetadataVersion>1</d:MetadataVersion></d:ProbeMatch></d:ProbeMatches></env:Body></env:Envelope>";

        static void OnvifProbeMatch()
        {
            var r = Discovery.ParseProbeMatch(SampleProbeMatch, "10.9.9.9");
            Check(r != null, "did not parse");
            Check(r.Ip == "192.168.0.64", "IP should come from XAddrs, got " + r.Ip);
            Check(r.DeviceName == "Gate Camera", "name: " + r.DeviceName);
            Check(r.Model == "DS-2CD2143G2-I", "model: " + r.Model);
            Check(r.Manufacturer == "HIKVISION", "manufacturer: " + r.Manufacturer);
            Check(r.Sources == FoundBy.Onvif, "source flag");
            Check(Discovery.ParseProbeMatch("<not xml", "1.1.1.1") == null, "accepted broken XML");
            Check(Discovery.ParseProbeMatch("<a><b/></a>", "1.1.1.1") == null, "accepted XML that is not a probe match");
            var dtd = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e;</x>";
            Check(Discovery.ParseProbeMatch(dtd, "1.1.1.1") == null, "processed a DTD");
        }

        static void OnvifProbeMessage()
        {
            var xml = System.Text.Encoding.UTF8.GetString(Discovery.OnvifProbe("dn:NetworkVideoTransmitter"));
            Check(xml.Contains("<d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe>"), "typed probe body");
            Check(xml.Contains("discovery/Probe</w:Action>"), "probe action");
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
            Check(System.Text.Encoding.UTF8.GetString(Discovery.OnvifProbe(null)).Contains("<d:Probe></d:Probe>"), "untyped probe body");
        }

        static void SsdpReply()
        {
            var headers = Discovery.ParseSsdp("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLocation: http://192.168.1.40:5000/desc.xml\r\nSERVER: Linux UPnP/1.0\r\nST: upnp:rootdevice\r\n\r\n");
            Check(headers != null && headers["LOCATION"] == "http://192.168.1.40:5000/desc.xml", "location header");
            Check(headers["SERVER"] == "Linux UPnP/1.0", "server header");
            Check(Discovery.ParseSsdp("NOTIFY * HTTP/1.1\r\nHOST: x\r\n\r\n") == null, "accepted a NOTIFY as a reply");
        }

        static void MdnsPackets()
        {
            var q = Discovery.MdnsQuery();
            Check(q[5] == 1, "one question");
            Check(q[q.Length - 4] == 0 && q[q.Length - 3] == 12 && q[q.Length - 2] == 0x80 && q[q.Length - 1] == 1, "PTR, IN, unicast bit");

            // Reply: 1 answer, an A record for printer.local, with the name written once and pointed to.
            var reply = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 };
            reply.AddRange(new byte[] { 7 }); reply.AddRange(System.Text.Encoding.ASCII.GetBytes("printer"));
            reply.AddRange(new byte[] { 5 }); reply.AddRange(System.Text.Encoding.ASCII.GetBytes("local"));
            reply.Add(0);
            reply.AddRange(new byte[] { 0, 1, 0x80, 1, 0, 0, 0, 120, 0, 4, 192, 168, 1, 60 });
            Check(Discovery.MdnsHostName(reply.ToArray()) == "printer.local", "A record name: " + Discovery.MdnsHostName(reply.ToArray()));

            // Same record, but the name is a pointer back to an earlier copy (DNS compression).
            var compressed = new List<byte> { 0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0 };
            compressed.AddRange(new byte[] { 3 }); compressed.AddRange(System.Text.Encoding.ASCII.GetBytes("nvr"));
            compressed.AddRange(new byte[] { 5 }); compressed.AddRange(System.Text.Encoding.ASCII.GetBytes("local"));
            compressed.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            compressed.AddRange(new byte[] { 0xC0, 12, 0, 1, 0x80, 1, 0, 0, 0, 120, 0, 4, 192, 168, 1, 40 });
            Check(Discovery.MdnsHostName(compressed.ToArray()) == "nvr.local", "compressed name");
            Check(Discovery.MdnsHostName(new byte[] { 1, 2, 3 }) == "", "short packet");
        }

        static void ScanMerge()
        {
            var arp = new ScanResult { Ip = "192.168.1.20", Mac = "AA-BB-CC-00-00-01", LatencyMs = 2, OpenPorts = new List<int> { 80, 554 }, Sources = FoundBy.Arp | FoundBy.Ping };
            arp.Merge(new ScanResult { Ip = "192.168.1.20", DeviceName = "Gate", Model = "X1", OpenPorts = new List<int> { 443, 80 }, Sources = FoundBy.Onvif });
            Check(arp.Mac == "AA-BB-CC-00-00-01" && arp.DeviceName == "Gate" && arp.Model == "X1", "fields not merged");
            Check(string.Join(",", arp.OpenPorts) == "80,443,554", "ports: " + string.Join(",", arp.OpenPorts));
            Check(arp.Sources == (FoundBy.Arp | FoundBy.Ping | FoundBy.Onvif), "sources not combined");
        }

        static void OuiFormat()
        {
            var table = OuiLookup.Parse(new System.IO.StringReader("A1B2C3,Example Vendor\nbad line\n0011ZZ,Not hex\nD4E5F6, Padded Name \n"));
            Check(table.Count == 2, "should keep 2 valid lines, kept " + table.Count);
            Check(table["A1B2C3"] == "Example Vendor" && table["D4E5F6"] == "Padded Name", "values");
            Check(OuiLookup.Vendor("02:00:00:00:00:01") == "", "locally administered MAC should have no vendor");
            Check(OuiLookup.Vendor("short") == "", "short input");
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
