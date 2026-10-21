using System;
using System.Collections.Generic;
using System.Linq;

namespace Cre8Labs.NetTools.Core.Dhcp
{
    public enum LeaseState { Offered, Active, Released, Expired }

    /// <summary>A snapshot of one client, safe to hand to the UI thread.</summary>
    public sealed class LeaseInfo
    {
        public string Mac { get; set; }
        public uint Ip { get; set; }
        public string Hostname { get; set; } = "";
        public string VendorClass { get; set; } = "";
        public LeaseState State { get; set; }
        public DateTime Expires { get; set; }
        public DateTime FirstSeen { get; set; }
        public string IpText => IpTools.FromUInt(Ip);

        /// <summary>The reserved address for this device, or 0.</summary>
        public uint ReservedIp { get; set; }
    }

    /// <summary>
    /// The DHCP server's decisions, with no sockets: give it a request, get back the reply to send (or null).
    /// Not thread-safe on its own; <see cref="DhcpServer"/> calls it from one thread and reads snapshots under its lock.
    /// </summary>
    public sealed class DhcpEngine
    {
        static readonly TimeSpan OfferHold = TimeSpan.FromSeconds(60);
        static readonly TimeSpan ConflictHold = TimeSpan.FromMinutes(10);
        const int MaxPingChecks = 6;

        readonly PoolConfig _config;
        readonly Func<uint, bool> _addressAnswers;
        readonly Dictionary<string, LeaseInfo> _byMac = new Dictionary<string, LeaseInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<uint, DateTime> _conflicts = new Dictionary<uint, DateTime>();

        /// <param name="addressAnswers">Optional check (a ping) run before offering a new address, to skip ones already in use.</param>
        public DhcpEngine(PoolConfig config, Func<uint, bool> addressAnswers = null)
        {
            _config = config;
            _addressAnswers = addressAnswers;
        }

        public PoolConfig Config => _config;

        /// <summary>Replaces the reservations while running. Devices move to a new reserved address at their next renewal.</summary>
        public void SetReservations(Dictionary<string, uint> reservations) =>
            _config.Reservations = new Dictionary<string, uint>(reservations, StringComparer.OrdinalIgnoreCase);

        uint ReservedFor(string mac) => _config.Reservations.TryGetValue(mac, out var ip) ? ip : 0;

        /// <summary>A device was given an address it did not have before.</summary>
        public event Action<LeaseInfo> LeaseBound;

        /// <summary>A client picked another DHCP server: there is a live server on this network.</summary>
        public event Action<uint> OtherServerSeen;

        public event Action<string> Log;

        public List<LeaseInfo> Snapshot() =>
            _byMac.Values.Select(l => new LeaseInfo
            {
                Mac = l.Mac, Ip = l.Ip, Hostname = l.Hostname, VendorClass = l.VendorClass,
                State = l.State, Expires = l.Expires, FirstSeen = l.FirstSeen, ReservedIp = ReservedFor(l.Mac),
            }).OrderBy(l => l.Ip).ToList();

        public DhcpPacket Handle(DhcpPacket request, DateTime now)
        {
            if (request.Op != 1 || request.HType != 1 || request.HLen != 6) return null;
            Expire(now);
            switch (request.MessageType)
            {
                case DhcpMessageType.Discover: return OnDiscover(request, now);
                case DhcpMessageType.Request: return OnRequest(request, now);
                case DhcpMessageType.Decline: OnDecline(request, now); return null;
                case DhcpMessageType.Release: OnRelease(request, now); return null;
                case DhcpMessageType.Inform: return Reply(request, DhcpMessageType.Ack, 0, includeLease: false);
                default: return null;
            }
        }

        /// <summary>Moves offers and leases past their time to Expired.</summary>
        public void Expire(DateTime now)
        {
            foreach (var lease in _byMac.Values)
                if ((lease.State == LeaseState.Active || lease.State == LeaseState.Offered) && lease.Expires <= now)
                    lease.State = LeaseState.Expired;
            foreach (var ip in _conflicts.Where(c => c.Value <= now).Select(c => c.Key).ToList())
                _conflicts.Remove(ip);
        }

        DhcpPacket OnDiscover(DhcpPacket request, DateTime now)
        {
            var ip = Allocate(request.Mac, request.GetIp(DhcpPacket.OptRequestedIp), now);
            if (ip == 0)
            {
                Log?.Invoke($"No free address left for {request.Mac}. The pool is full.");
                return null;
            }
            var lease = Upsert(request, ip, now);
            if (lease.State != LeaseState.Active) { lease.State = LeaseState.Offered; lease.Expires = now + OfferHold; }
            return Reply(request, DhcpMessageType.Offer, ip, includeLease: true);
        }

        DhcpPacket OnRequest(DhcpPacket request, DateTime now)
        {
            var serverId = request.GetIp(DhcpPacket.OptServerId);
            _byMac.TryGetValue(request.Mac, out var lease);

            if (serverId != 0 && serverId != _config.Server)
            {
                // The client accepted another server's offer, so withdraw ours.
                if (lease != null && lease.State == LeaseState.Offered) _byMac.Remove(request.Mac);
                OtherServerSeen?.Invoke(serverId);
                return null;
            }

            var ip = request.GetIp(DhcpPacket.OptRequestedIp);
            if (ip == 0) ip = request.ClientIp;
            if (ip == 0) return null;

            if (!_config.InNetwork(ip))
                return Nak(request, "Address is not on this network");

            // A reserved device must use its reserved address; refusing anything else makes it ask again and get it.
            var reserved = ReservedFor(request.Mac);
            if (reserved != 0 && ip != reserved)
                return Nak(request, "This device is reserved for " + IpTools.FromUInt(reserved));

            var owned = lease != null && lease.Ip == ip && lease.State != LeaseState.Released;
            var allowed = reserved == ip || _config.InRange(ip);
            if (!owned && !(allowed && IsFree(ip, request.Mac, now)))
                return Nak(request, "Address is not available");

            var isNew = lease == null || lease.Ip != ip || lease.State != LeaseState.Active;
            lease = Upsert(request, ip, now);
            lease.State = LeaseState.Active;
            lease.Expires = now + _config.Lease;
            if (isNew) LeaseBound?.Invoke(lease);
            return Reply(request, DhcpMessageType.Ack, ip, includeLease: true);
        }

        void OnDecline(DhcpPacket request, DateTime now)
        {
            var ip = request.GetIp(DhcpPacket.OptRequestedIp);
            if (ip == 0) return;
            _conflicts[ip] = now + ConflictHold;
            if (_byMac.TryGetValue(request.Mac, out var lease) && lease.Ip == ip) _byMac.Remove(request.Mac);
            Log?.Invoke($"{request.Mac} reported {IpTools.FromUInt(ip)} is already in use. Skipping it for 10 minutes.");
        }

        void OnRelease(DhcpPacket request, DateTime now)
        {
            if (!_byMac.TryGetValue(request.Mac, out var lease) || lease.Ip != request.ClientIp) return;
            lease.State = LeaseState.Released;
            lease.Expires = now;
        }

        uint Allocate(string mac, uint requested, DateTime now)
        {
            var reserved = ReservedFor(mac);
            if (reserved != 0)
            {
                if (IsFree(reserved, mac, now)) return reserved;
                Log?.Invoke($"{mac} is reserved for {IpTools.FromUInt(reserved)}, but another device is using it.");
                return 0;
            }

            // Same device, same address, whenever possible.
            if (_byMac.TryGetValue(mac, out var existing) && _config.InRange(existing.Ip) && IsFree(existing.Ip, mac, now))
                return existing.Ip;
            if (requested != 0 && _config.InRange(requested) && IsFree(requested, mac, now) && !Answers(requested, now))
                return requested;

            // Prefer addresses no one has had yet, then reuse expired or released ones.
            var everUsed = new HashSet<uint>(_byMac.Values.Select(l => l.Ip));
            var checks = 0;
            foreach (var pass in new[] { false, true })
            {
                for (var ip = _config.RangeStart; ip <= _config.RangeEnd && ip != 0; ip++)
                {
                    if (everUsed.Contains(ip) != pass || !IsFree(ip, mac, now)) continue;
                    if (checks++ < MaxPingChecks && Answers(ip, now)) continue;
                    return ip;
                }
            }
            return 0;
        }

        /// <summary>Pings the address; if something answers, it is in use (for example a device with a static IP).</summary>
        bool Answers(uint ip, DateTime now)
        {
            if (_addressAnswers == null || !_addressAnswers(ip)) return false;
            _conflicts[ip] = now + ConflictHold;
            Log?.Invoke($"{IpTools.FromUInt(ip)} already answers on the network, so it was skipped.");
            return true;
        }

        bool IsFree(uint ip, string mac, DateTime now)
        {
            if (ip == _config.Server || ip == _config.Gateway || ip == _config.Network || ip == _config.Broadcast) return false;
            if (_conflicts.TryGetValue(ip, out var until) && until > now) return false;
            // Addresses reserved for another device are never handed out to anyone else.
            if (_config.Reservations.Any(r => r.Value == ip && !string.Equals(r.Key, mac, StringComparison.OrdinalIgnoreCase))) return false;
            return !_byMac.Values.Any(l => l.Ip == ip && !string.Equals(l.Mac, mac, StringComparison.OrdinalIgnoreCase) &&
                                           (l.State == LeaseState.Active || l.State == LeaseState.Offered) && l.Expires > now);
        }

        LeaseInfo Upsert(DhcpPacket request, uint ip, DateTime now)
        {
            if (!_byMac.TryGetValue(request.Mac, out var lease))
                _byMac[request.Mac] = lease = new LeaseInfo { Mac = request.Mac, FirstSeen = now };
            lease.Ip = ip;
            var host = request.GetText(DhcpPacket.OptHostName);
            if (host.Length > 0) lease.Hostname = host;
            var vendor = request.GetText(DhcpPacket.OptVendorClass);
            if (vendor.Length > 0) lease.VendorClass = vendor;
            return lease;
        }

        DhcpPacket Reply(DhcpPacket request, DhcpMessageType type, uint yourIp, bool includeLease)
        {
            var reply = new DhcpPacket
            {
                Op = 2, HType = request.HType, HLen = request.HLen, Xid = request.Xid, Flags = request.Flags,
                ClientIp = type == DhcpMessageType.Ack ? request.ClientIp : 0,
                YourIp = yourIp, RelayIp = request.RelayIp,
                ClientHardware = (byte[])request.ClientHardware.Clone(),
            };
            reply.Set(DhcpPacket.OptMessageType, new[] { (byte)type });
            reply.SetIp(DhcpPacket.OptServerId, _config.Server);
            if (includeLease)
            {
                var seconds = (uint)_config.Lease.TotalSeconds;
                reply.SetUInt(DhcpPacket.OptLeaseTime, seconds);
                reply.SetUInt(DhcpPacket.OptRenewalTime, seconds / 2);
                reply.SetUInt(DhcpPacket.OptRebindingTime, seconds / 8 * 7);
            }
            reply.SetIp(DhcpPacket.OptSubnetMask, _config.Mask);
            if (_config.Gateway != 0) reply.SetIp(DhcpPacket.OptRouter, _config.Gateway);
            if (_config.Dns.Length > 0) reply.SetIps(DhcpPacket.OptDns, _config.Dns);
            return reply;
        }

        DhcpPacket Nak(DhcpPacket request, string message)
        {
            var nak = new DhcpPacket
            {
                Op = 2, HType = request.HType, HLen = request.HLen, Xid = request.Xid,
                Flags = 0x8000, RelayIp = request.RelayIp, ClientHardware = (byte[])request.ClientHardware.Clone(),
            };
            nak.Set(DhcpPacket.OptMessageType, new[] { (byte)DhcpMessageType.Nak });
            nak.SetIp(DhcpPacket.OptServerId, _config.Server);
            nak.Set(DhcpPacket.OptMessage, System.Text.Encoding.ASCII.GetBytes(message));
            Log?.Invoke($"Refused {request.Mac}: {message.ToLowerInvariant()}.");
            return nak;
        }
    }
}
