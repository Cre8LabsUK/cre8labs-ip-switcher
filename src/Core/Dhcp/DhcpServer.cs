using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cre8Labs.IPSwitcher.Core.Dhcp
{
    public enum PoolState { Stopped, WaitingForLink, Checking, Running }

    /// <summary>What the UI talks to, so demo mode can swap in a simulated pool.</summary>
    public interface IPoolServer
    {
        PoolConfig Config { get; }
        PoolState State { get; }
        event Action<PoolState> StateChanged;
        event Action<string> Log;
        event Action<LeaseInfo> LeaseBound;

        /// <summary>Another DHCP server is active on this network. The pool has already stopped answering.</summary>
        event Action<string> OtherServerFound;

        void Start();
        void Stop();
        List<LeaseInfo> Leases();
        void SetReservations(Dictionary<string, uint> reservations);
    }

    /// <summary>
    /// Serves the pool on the adapter that owns <see cref="PoolConfig.Server"/>.
    /// Binding to that specific address (not 0.0.0.0) means only devices on that adapter are heard,
    /// and it can share port 67 with Windows' own Internet Connection Sharing on other adapters.
    /// </summary>
    public sealed class DhcpServer : IPoolServer
    {
        const int ServerPort = 67, ClientPort = 68;
        static readonly TimeSpan ProbeTime = TimeSpan.FromSeconds(3);

        readonly DhcpEngine _engine;
        readonly object _gate = new object();
        readonly byte[] _probeMac = new byte[16];
        CancellationTokenSource _cts;
        UdpClient _socket;
        PoolState _state;

        public DhcpServer(PoolConfig config, Func<uint, bool> addressAnswers = null)
        {
            _engine = new DhcpEngine(config, addressAnswers);
            _engine.Log += m => Log?.Invoke(m);
            _engine.LeaseBound += l => LeaseBound?.Invoke(l);
            _engine.OtherServerSeen += ip =>
                OtherServerFound?.Invoke($"A device accepted an address from another DHCP server at {IpTools.FromUInt(ip)}.");

            // A random, locally administered MAC for the probe, so its traffic is easy to tell apart.
            var rnd = new Random();
            for (var i = 0; i < 6; i++) _probeMac[i] = (byte)rnd.Next(256);
            _probeMac[0] = (byte)((_probeMac[0] & 0xFC) | 0x02);
        }

        public PoolConfig Config => _engine.Config;
        public PoolState State => _state;

        public event Action<PoolState> StateChanged;
        public event Action<string> Log;
        public event Action<LeaseInfo> LeaseBound;
        public event Action<string> OtherServerFound;

        /// <summary>Tests only: send replies back to the sender instead of broadcasting.</summary>
        internal bool ReplyToSender { get; set; }

        /// <summary>Tests only: skip the check for other DHCP servers.</summary>
        internal bool SkipProbe { get; set; }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => RunAsync(token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            lock (_gate) { _socket?.Dispose(); _socket = null; }
            SetState(PoolState.Stopped);
        }

        public List<LeaseInfo> Leases()
        {
            lock (_gate)
            {
                _engine.Expire(DateTime.UtcNow);
                return _engine.Snapshot();
            }
        }

        public void SetReservations(Dictionary<string, uint> reservations)
        {
            lock (_gate) _engine.SetReservations(reservations);
        }

        void SetState(PoolState state)
        {
            // After Stop, the background loop may still be winding down; it must not report itself as running.
            if (state != PoolState.Stopped && _cts != null && _cts.IsCancellationRequested) return;
            if (_state == state) return;
            _state = state;
            StateChanged?.Invoke(state);
        }

        async Task RunAsync(CancellationToken token)
        {
            var serverEndpoint = new IPEndPoint(ToAddress(Config.Server), ServerPort);
            var probed = SkipProbe;
            var waitingLogged = false;

            while (!token.IsCancellationRequested)
            {
                // The address only exists once the adapter has its static IP and a link, so keep trying.
                UdpClient socket;
                try
                {
                    socket = new UdpClient(AddressFamily.InterNetwork);
                    socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                    socket.EnableBroadcast = true;
                    socket.Client.Bind(serverEndpoint);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        Log?.Invoke($"Another program is already serving DHCP on {Config.ServerText}. Close it and start the pool again.");
                        OtherServerFound?.Invoke("Another program on this PC is already using the DHCP port on this address.");
                        return;
                    }
                    if (!waitingLogged) { Log?.Invoke("Waiting for the adapter to come up (check the cable)."); waitingLogged = true; }
                    SetState(PoolState.WaitingForLink);
                    if (await Delay(TimeSpan.FromSeconds(2), token)) return;
                    continue;
                }
                waitingLogged = false;
                lock (_gate)
                {
                    if (token.IsCancellationRequested) { socket.Dispose(); return; }
                    _socket = socket;
                }

                if (!probed)
                {
                    SetState(PoolState.Checking);
                    var other = await ProbeForOtherServersAsync(token);
                    if (token.IsCancellationRequested) return;
                    if (other != null)
                    {
                        lock (_gate) { _socket?.Dispose(); _socket = null; }
                        OtherServerFound?.Invoke(other);
                        return;
                    }
                    probed = true;
                }

                SetState(PoolState.Running);
                Log?.Invoke($"Serving {Config.RangeText} from {Config.ServerText}.");
                await ServeAsync(socket, token);
                lock (_gate) { if (_socket == socket) _socket = null; }
                socket.Dispose();
                // Back round the loop: the link dropped or the address went away, so wait and rebind.
            }
        }

        async Task ServeAsync(UdpClient socket, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await socket.ReceiveAsync(); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                catch (SocketException) { return; }

                var request = DhcpPacket.Parse(received.Buffer, received.Buffer.Length);
                if (request == null || request.Op != 1) continue;
                if (request.RelayIp != 0) continue; // relayed requests are not supported
                if (SameBytes(request.ClientHardware, _probeMac)) continue; // our own probe, looped back

                DhcpPacket reply;
                lock (_gate) reply = _engine.Handle(request, DateTime.UtcNow);
                if (reply == null) continue;

                var target = ReplyToSender ? received.RemoteEndPoint
                    : reply.MessageType == DhcpMessageType.Ack && request.ClientIp != 0 && !request.BroadcastFlag
                        ? new IPEndPoint(ToAddress(request.ClientIp), ClientPort)  // renewing clients already have an address
                        : new IPEndPoint(IPAddress.Broadcast, ClientPort);
                try
                {
                    var bytes = reply.ToBytes();
                    await socket.SendAsync(bytes, bytes.Length, target);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex) { Log?.Invoke("Could not send a reply: " + ex.Message); }
            }
        }

        /// <summary>
        /// Broadcasts one DISCOVER from the server address and listens for offers.
        /// Returns a description of the other server, or null if none answered (or the check could not run).
        /// </summary>
        async Task<string> ProbeForOtherServersAsync(CancellationToken token)
        {
            UdpClient client;
            try
            {
                client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                client.Client.Bind(new IPEndPoint(ToAddress(Config.Server), ClientPort));
            }
            catch (SocketException ex)
            {
                Log?.Invoke("Could not check for other DHCP servers (" + ex.SocketErrorCode + "). Watching for them while running instead.");
                return null;
            }

            using (client)
            {
                var xid = (uint)new Random().Next();
                var discover = new DhcpPacket { Op = 1, Xid = xid, Flags = 0x8000, ClientHardware = (byte[])_probeMac.Clone() };
                discover.Set(DhcpPacket.OptMessageType, new[] { (byte)DhcpMessageType.Discover });
                discover.Set(DhcpPacket.OptParamList, new byte[] { DhcpPacket.OptSubnetMask, DhcpPacket.OptRouter, DhcpPacket.OptDns });
                var bytes = discover.ToBytes();
                try { await client.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, ServerPort)); }
                catch (SocketException ex) { Log?.Invoke("Could not check for other DHCP servers: " + ex.Message); return null; }

                var deadline = DateTime.UtcNow + ProbeTime;
                while (!token.IsCancellationRequested)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return null;
                    var receive = client.ReceiveAsync();
                    if (await Task.WhenAny(receive, Task.Delay(left, token)) != receive) return null;
                    UdpReceiveResult result;
                    try { result = await receive; }
                    catch (SocketException) { continue; }

                    var offer = DhcpPacket.Parse(result.Buffer, result.Buffer.Length);
                    if (offer == null || offer.Op != 2 || offer.Xid != xid || offer.MessageType != DhcpMessageType.Offer) continue;
                    var id = offer.GetIp(DhcpPacket.OptServerId);
                    var who = id != 0 ? IpTools.FromUInt(id) : result.RemoteEndPoint.Address.ToString();
                    return $"Another DHCP server ({who}) is already handing out addresses on this network.";
                }
                return null;
            }
        }

        static async Task<bool> Delay(TimeSpan time, CancellationToken token)
        {
            try { await Task.Delay(time, token); return false; }
            catch (TaskCanceledException) { return true; }
        }

        static IPAddress ToAddress(uint ip) => IPAddress.Parse(IpTools.FromUInt(ip));

        static bool SameBytes(byte[] a, byte[] b)
        {
            for (var i = 0; i < 6; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
