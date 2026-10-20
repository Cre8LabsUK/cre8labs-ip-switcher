using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cre8Labs.IPSwitcher.Core.Dhcp
{
    /// <summary>
    /// "--demo" stand-in for <see cref="DhcpServer"/>: no sockets, but sample cameras go through the real
    /// <see cref="DhcpEngine"/>, so leases, reservations and the UI behave as they would on a real bench.
    /// </summary>
    public sealed class DemoPoolServer : IPoolServer
    {
        static readonly (string Mac, string Host, string Vendor, int DelayMs)[] Devices =
        {
            ("00-1A-2B-10-00-01", "CAM-Gate", "udhcp 1.31.1", 1800),
            ("00-1A-2B-10-00-02", "CAM-Car-Park", "udhcp 1.31.1", 2600),
            ("00-1A-2B-10-00-03", "CAM-Reception", "udhcp 1.31.1", 3400),
            ("00-1A-2B-20-00-01", "NVR-01", "dhcpcd-9.4.1", 4600),
            ("00-1A-2B-30-00-01", "", "", 6200),
        };

        readonly DhcpEngine _engine;
        readonly object _gate = new object();
        CancellationTokenSource _cts;
        PoolState _state;

        public DemoPoolServer(PoolConfig config)
        {
            _engine = new DhcpEngine(config);
            _engine.LeaseBound += l => LeaseBound?.Invoke(l);
        }

        public PoolConfig Config => _engine.Config;
        public PoolState State => _state;
        public event Action<PoolState> StateChanged;
        public event Action<string> Log;
        public event Action<LeaseInfo> LeaseBound;
        public event Action<string> OtherServerFound { add { } remove { } }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                SetState(PoolState.Checking);
                await Task.Delay(900);
                if (token.IsCancellationRequested) return;
                SetState(PoolState.Running);
                Log?.Invoke($"Serving {Config.RangeText} from {Config.ServerText}.");
                var started = DateTime.UtcNow;
                foreach (var d in Devices)
                {
                    var wait = started.AddMilliseconds(d.DelayMs) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    if (token.IsCancellationRequested) return;
                    Join(d.Mac, d.Host, d.Vendor);
                }
            });
        }

        void Join(string mac, string host, string vendor)
        {
            lock (_gate)
            {
                var discover = Packet(mac, host, vendor, DhcpMessageType.Discover);
                var offer = _engine.Handle(discover, DateTime.UtcNow);
                if (offer == null) return;
                var request = Packet(mac, host, vendor, DhcpMessageType.Request);
                request.SetIp(DhcpPacket.OptServerId, Config.Server);
                request.SetIp(DhcpPacket.OptRequestedIp, offer.YourIp);
                _engine.Handle(request, DateTime.UtcNow);
            }
        }

        static DhcpPacket Packet(string mac, string host, string vendor, DhcpMessageType type)
        {
            var p = new DhcpPacket { Xid = (uint)mac.GetHashCode() };
            var bytes = mac.Split('-');
            for (var i = 0; i < 6; i++) p.ClientHardware[i] = Convert.ToByte(bytes[i], 16);
            p.Set(DhcpPacket.OptMessageType, new[] { (byte)type });
            if (host.Length > 0) p.Set(DhcpPacket.OptHostName, System.Text.Encoding.ASCII.GetBytes(host));
            if (vendor.Length > 0) p.Set(DhcpPacket.OptVendorClass, System.Text.Encoding.ASCII.GetBytes(vendor));
            return p;
        }

        public void Stop()
        {
            _cts?.Cancel();
            SetState(PoolState.Stopped);
        }

        public List<LeaseInfo> Leases()
        {
            lock (_gate) return _engine.Snapshot();
        }

        /// <summary>In the demo a new reservation applies at once, as if the camera had just renewed.</summary>
        public void SetReservations(Dictionary<string, uint> reservations)
        {
            lock (_gate)
            {
                _engine.SetReservations(reservations);
                foreach (var lease in _engine.Snapshot())
                    if (lease.State == LeaseState.Active && reservations.TryGetValue(lease.Mac, out var ip) && ip != lease.Ip)
                        Join(lease.Mac, lease.Hostname, lease.VendorClass);
            }
        }

        void SetState(PoolState state)
        {
            if (_state == state) return;
            _state = state;
            StateChanged?.Invoke(state);
        }
    }
}
