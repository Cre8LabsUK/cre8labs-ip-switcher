using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cre8Labs.IPSwitcher.Core;
using Cre8Labs.IPSwitcher.Core.Dhcp;

namespace Cre8Labs.IPSwitcher
{
    /// <summary>The DHCP pool: setup form, start and stop with safety checks, and the Devices panel.</summary>
    public partial class MainWindow
    {
        readonly TileState _poolTile = new TileState();
        IPoolServer _pool;
        string _poolAdapter;
        StatusView _poolPrevious;
        List<LeaseInfo> _lastLeases = new List<LeaseInfo>();
        DispatcherTimer _deviceTimer;
        bool _loadingPoolForm;
        bool _poolErrorsShown;
        int _activeDevices;

        public ObservableCollection<DeviceItem> Devices { get; } = new ObservableCollection<DeviceItem>();

        public bool PoolActive => _pool != null;
        public bool PoolIdle => _pool == null;
        public bool NoDevices => Devices.Count == 0;
        public string DevicesSummary { get; private set; } = "";

        public string PoolKind =>
            _pool == null ? "" :
            _pool.State == PoolState.Running ? "running" :
            _pool.State == PoolState.WaitingForLink ? "waiting" : "checking";

        public string PoolChipText =>
            PoolKind == "running" ? "Running" : PoolKind == "waiting" ? "Waiting for cable" : PoolKind == "checking" ? "Checking" : "";

        public string PoolSubtitle =>
            _pool == null ? $"Hand out addresses to cameras and other devices plugged into {SelectedAdapterName ?? "the selected adapter"}." :
            PoolKind == "waiting" ? $"Waiting for a link on {_poolAdapter}. Plug in the switch or device." :
            PoolKind == "checking" ? "Checking the network for other DHCP servers first." :
            $"Handing out addresses on {_poolAdapter}.";

        public string PoolBanner =>
            _pool == null ? "" :
            PoolKind == "waiting" ? $"DHCP pool on {_poolAdapter} is waiting for a cable" :
            PoolKind == "checking" ? $"DHCP pool on {_poolAdapter} is checking for other DHCP servers" :
            $"DHCP pool running on {_poolAdapter}, serving {_pool.Config.RangeText}";

        public string PoolAdapterText => _poolAdapter ?? "";
        public string PoolServerText => _pool == null ? "" : $"{_pool.Config.ServerText}/{_pool.Config.Prefix}";
        public string PoolRangeText => _pool?.Config.RangeText ?? "";
        public string DeviceCountText => _activeDevices == 1 ? "1 connected" : $"{_activeDevices} connected";
        public string DevicesButtonText => _activeDevices > 0 ? $"Devices ({_activeDevices})" : "Devices";

        void RaisePool()
        {
            foreach (var name in new[]
            {
                nameof(PoolActive), nameof(PoolIdle), nameof(PoolKind), nameof(PoolChipText), nameof(PoolSubtitle), nameof(PoolBanner),
                nameof(PoolAdapterText), nameof(PoolServerText), nameof(PoolRangeText), nameof(DeviceCountText), nameof(DevicesButtonText),
                nameof(DevicesSummary), nameof(NoDevices),
            })
                Raise(name);
        }

        // ------------------------------------------------------------------ setup form

        void LoadPoolForm()
        {
            _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _deviceTimer.Tick += (s, e) => RefreshDevices();

            _loadingPoolForm = true;
            var p = _config.Pool;
            PoolNetworkBox.Text = p.Network;
            PoolServerBox.Text = p.Server;
            PoolStartBox.Text = p.RangeStart;
            PoolEndBox.Text = p.RangeEnd;
            PoolLeaseBox.Text = p.LeaseMinutes.ToString();
            PoolGatewayBox.Text = p.Gateway;
            PoolDnsBox.Text = p.Dns;
            _loadingPoolForm = false;
            UpdatePoolPreview();
            RefreshDevices();
        }

        PoolSettings ReadPoolForm()
        {
            var s = _config.Pool.Clone();
            s.Network = PoolNetworkBox.Text.Trim();
            s.Server = PoolServerBox.Text.Trim();
            s.RangeStart = PoolStartBox.Text.Trim();
            s.RangeEnd = PoolEndBox.Text.Trim();
            s.LeaseMinutes = int.TryParse(PoolLeaseBox.Text.Trim(), out var minutes) ? minutes : -1;
            s.Gateway = PoolGatewayBox.Text.Trim();
            s.Dns = PoolDnsBox.Text.Trim();
            return s;
        }

        void PoolNetwork_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loadingPoolForm) return;
            // A new network brings a fresh suggestion for the PC's address and the range.
            if (PoolSettings.TryParseNetwork(PoolNetworkBox.Text, out var network, out var mask))
            {
                var suggestion = PoolSettings.Suggest(network, mask);
                _loadingPoolForm = true;
                PoolServerBox.Text = suggestion.Server;
                PoolStartBox.Text = suggestion.RangeStart;
                PoolEndBox.Text = suggestion.RangeEnd;
                _loadingPoolForm = false;
            }
            UpdatePoolPreview();
        }

        void PoolField_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_loadingPoolForm) UpdatePoolPreview();
        }

        void UpdatePoolPreview()
        {
            if (PoolPreview == null) return;
            var config = ReadPoolForm().Validate(out var errors);
            if (_poolErrorsShown) ShowPoolErrors(errors);
            var adapter = SelectedAdapterName ?? "The adapter";
            if (config == null)
            {
                PoolPreview.Text = errors.ContainsKey(nameof(PoolSettings.Network))
                    ? "Enter the network to serve, for example 192.168.1.0/24."
                    : "Check the settings under “Server address, range and lease”.";
                return;
            }
            var reserved = config.Reservations.Count;
            PoolPreview.Text =
                $"{adapter} will be set to {config.ServerText}/{config.Prefix} and hand out {config.RangeText} " +
                $"({config.Size} addresses, {(int)config.Lease.TotalMinutes} minute leases" +
                (reserved > 0 ? $", {reserved} reserved device{(reserved == 1 ? "" : "s")})." : ").");
        }

        void ShowPoolErrors(Dictionary<string, string> errors)
        {
            var fields = new (string Key, TextBox Box, TextBlock Label, bool Advanced)[]
            {
                (nameof(PoolSettings.Network), PoolNetworkBox, PoolNetworkError, false),
                (nameof(PoolSettings.Server), PoolServerBox, PoolServerError, true),
                (nameof(PoolSettings.RangeStart), PoolStartBox, PoolStartError, true),
                (nameof(PoolSettings.RangeEnd), PoolEndBox, PoolEndError, true),
                (nameof(PoolSettings.LeaseMinutes), PoolLeaseBox, PoolLeaseError, true),
                (nameof(PoolSettings.Gateway), PoolGatewayBox, PoolGatewayError, true),
                (nameof(PoolSettings.Dns), PoolDnsBox, PoolDnsError, true),
            };
            foreach (var f in fields)
            {
                errors.TryGetValue(f.Key, out var message);
                f.Label.Text = message ?? "";
                f.Label.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
                if (message == null) f.Box.ClearValue(BorderBrushProperty);
                else
                {
                    f.Box.BorderBrush = (System.Windows.Media.Brush)FindResource("Danger");
                    if (f.Advanced) PoolAdvancedToggle.IsChecked = true;
                }
            }
        }

        void PoolFromAdapter_Click(object sender, RoutedEventArgs e)
        {
            if (!IpTools.IsIPv4(Status.FirstIp) || string.IsNullOrEmpty(Status.FirstMask))
            {
                Notify("info", "This adapter has no address yet. Type the network instead, for example 192.168.1.0/24.");
                return;
            }
            PoolNetworkBox.Text = $"{Status.FirstIp}/{IpTools.PrefixLength(Status.FirstMask)}";
        }

        // ------------------------------------------------------------------ start and stop

        async void StartPool_Click(object sender, RoutedEventArgs e) => await StartPoolAsync();

        async void StopPool_Click(object sender, RoutedEventArgs e) => await StopPoolAsync(restore: true, message: null);

        async Task StartPoolAsync()
        {
            if (_pool != null || !CanChangeSettings(out var alias)) return;

            var settings = ReadPoolForm();
            var config = settings.Validate(out var errors);
            _poolErrorsShown = true;
            ShowPoolErrors(errors);
            UpdatePoolPreview();
            if (config == null)
            {
                if (errors.TryGetValue(nameof(PoolSettings.Reservations), out var reservationProblem))
                    Notify("error", "Reservation problem: " + reservationProblem);
                return;
            }

            // Safety 1: only wired adapters, so a pool never lands on Wi-Fi or a VPN.
            var rank = NetworkService.AdapterRank(alias);
            if (rank != 0)
            {
                Notify("error", $"{alias} is a {(rank == 1 ? "Wi-Fi" : "virtual or VPN")} adapter. The DHCP pool only runs on a wired adapter, " +
                                "so it cannot hand out addresses on someone else's network.");
                return;
            }

            // Safety 2: the pool network must not overlap another adapter on this PC.
            var clash = await Task.Run(() => FindOverlap(config, alias));
            if (clash != null)
            {
                Notify("error", $"{clash} already uses part of {config.NetworkText}. Pick a different network for the pool, or disconnect that adapter.");
                return;
            }

            // Safety 3: an adapter with a gateway or a real DHCP lease is probably on a live network, so ask first.
            var previous = Status;
            var live = previous.FirstGateway.Length > 0 ? $"a gateway ({previous.FirstGateway})"
                : previous.IsDhcp && IpTools.IsIPv4(previous.FirstIp) && !previous.FirstIp.StartsWith("169.254.") ? $"an address from a DHCP server ({previous.FirstIp})"
                : null;
            if (live != null && !ConfirmWindow.Ask(this, $"{alias} looks connected to a live network",
                    $"It currently has {live}. Running a DHCP pool on a network that already has one can give other people's devices the wrong address.\n\n" +
                    $"Only continue if {alias} is plugged into your own devices or a bench switch.", "Start anyway"))
                return;

            var server = config.ServerText;
            var mask = config.MaskText;
            var result = await RunBusyAsync(_poolTile, () =>
            {
                var firewall = NetworkService.EnsureDhcpFirewallRule();
                return firewall.Ok ? NetworkService.ApplyStatic(alias, new Preset { IP = server, Mask = mask }) : firewall;
            });
            if (!result.Ok)
            {
                Notify("error", result.Message);
                await RefreshStatusAsync();
                return;
            }

            _config.Pool = settings;
            SaveConfig();
            _poolAdapter = alias;
            _poolPrevious = previous;
            _poolErrorsShown = false;

            var source = IPAddress.Parse(server);
            _pool = NetworkService.DemoMode
                ? new DemoPoolServer(config)
                : (IPoolServer)new DhcpServer(config, ip => PingService.Send(source, IPAddress.Parse(IpTools.FromUInt(ip)), 300).Ok);
            WirePool(_pool);
            _pool.Start();
            _deviceTimer.Start();
            RaisePool();
            RefreshDevices();
            Notify("ok", $"DHCP pool started on {alias}. Plug devices in and they will appear under Devices.");
            await RefreshStatusAsync();
        }

        void WirePool(IPoolServer pool)
        {
            void OnUi(Action action) => Dispatcher.BeginInvoke(new Action(() => { if (pool == _pool) action(); }));

            pool.StateChanged += state => OnUi(RaisePool);
            pool.Log += message => Dispatcher.BeginInvoke(new Action(() => AddLog("info", "DHCP: " + message)));
            pool.LeaseBound += lease => OnUi(() =>
            {
                RefreshDevices();
                Notify("ok", $"{DeviceName(lease)} got {lease.IpText}");
            });
            pool.OtherServerFound += message => OnUi(async () =>
                await StopPoolAsync(restore: true, message: "DHCP pool stopped. " + message + " Two DHCP servers would give devices conflicting addresses.", kind: "error"));
        }

        async Task StopPoolAsync(bool restore, string message, string kind = "info")
        {
            var pool = _pool;
            if (pool == null) return;
            _lastLeases = pool.Leases();
            pool.Stop();
            _pool = null;
            _deviceTimer.Stop();
            var alias = _poolAdapter;
            var previous = _poolPrevious;
            _poolPrevious = null;
            RaisePool();
            RefreshDevices();

            if (restore && previous != null && alias != null)
            {
                var result = await RunBusyAsync(_poolTile, () => RestoreAdapter(alias, previous));
                if (!result.Ok) Notify("error", "DHCP pool stopped, but putting the adapter back failed. " + result.Message);
                else if (message == null) Notify("ok", $"DHCP pool stopped. {alias} is back to {(previous.IsDhcp || !IpTools.IsIPv4(previous.FirstIp) ? "DHCP" : previous.FirstIp + previous.Prefix)}.");
            }
            if (message != null) Notify(kind, message);
            else if (!restore) Notify("ok", "DHCP pool stopped.");
            await RefreshStatusAsync();
        }

        static OpResult RestoreAdapter(string alias, StatusView previous)
        {
            if (previous.IsDhcp || !IpTools.IsIPv4(previous.FirstIp)) return NetworkService.SetDhcp(alias);
            return NetworkService.ApplyStatic(alias, new Preset
            {
                IP = previous.FirstIp,
                Mask = string.IsNullOrEmpty(previous.FirstMask) ? "255.255.255.0" : previous.FirstMask,
                Gateway = previous.FirstGateway,
                DNS1 = previous.Dns1,
                DNS2 = previous.Dns2,
            });
        }

        /// <summary>The first other adapter whose network overlaps the pool, as "Wi-Fi (192.168.1.20/24)", or null.</summary>
        static string FindOverlap(PoolConfig config, string poolAdapter)
        {
            foreach (var adapter in NetworkService.ListAdapters().Where(a => a.Name != poolAdapter && a.IsUp))
            {
                var status = NetworkService.ReadStatus(adapter.Name);
                if (status == null) continue;
                foreach (var a in status.Addresses)
                {
                    if (a.Prefix <= 0 || a.Ip.StartsWith("169.254.") || !IpTools.IsIPv4(a.Ip)) continue;
                    if (config.Overlaps(IpTools.ToUInt(a.Ip), a.Prefix)) return $"{adapter.Name} ({a.Ip}/{a.Prefix})";
                }
            }
            return null;
        }

        /// <summary>Stops the pool if something else changed the adapter's address while it was running.</summary>
        async Task CheckPoolAdapterAsync()
        {
            var pool = _pool;
            if (pool == null || pool.State != PoolState.Running || _busy) return;
            var alias = _poolAdapter;
            var server = pool.Config.ServerText;
            var status = await Task.Run(() => NetworkService.ReadStatus(alias));
            if (pool != _pool) return;
            if (status == null)
                await StopPoolAsync(false, $"{alias} is no longer available, so the DHCP pool stopped.", "error");
            else if (status.IsUp && !status.Addresses.Any(a => a.Ip == server))
                await StopPoolAsync(false, $"{alias}'s address was changed by something else, so the DHCP pool stopped.", "error");
        }

        // ------------------------------------------------------------------ devices and reservations

        Dictionary<string, Reservation> ReservationsByMac() =>
            _config.Pool.Reservations
                .Select(r => new { Mac = Reservation.NormalizeMac(r.Mac), R = r })
                .Where(x => x.Mac != null)
                .GroupBy(x => x.Mac)
                .ToDictionary(g => g.Key, g => g.First().R, StringComparer.OrdinalIgnoreCase);

        string DeviceName(LeaseInfo lease)
        {
            var reservations = ReservationsByMac();
            if (reservations.TryGetValue(lease.Mac, out var r) && !string.IsNullOrWhiteSpace(r.Name)) return r.Name;
            return lease.Hostname.Length > 0 ? lease.Hostname : "A device (" + lease.Mac + ")";
        }

        void RefreshDevices()
        {
            var running = _pool != null;
            var leases = running ? _pool.Leases() : _lastLeases;
            var reservations = ReservationsByMac();
            var now = DateTime.UtcNow;
            var items = new List<DeviceItem>();

            foreach (var lease in leases)
            {
                reservations.TryGetValue(lease.Mac, out var reservation);
                var name = !string.IsNullOrWhiteSpace(reservation?.Name) ? reservation.Name
                    : lease.Hostname.Length > 0 ? lease.Hostname : "Unknown device";
                var detail = new List<string>();
                if (lease.Hostname.Length > 0 && lease.Hostname != name) detail.Add(lease.Hostname);
                if (lease.VendorClass.Length > 0) detail.Add(lease.VendorClass);

                string kind, state, time = "";
                if (!running) { kind = "idle"; state = "Last seen"; }
                else switch (lease.State)
                {
                    case LeaseState.Active:
                        kind = "active"; state = "Active";
                        var minutes = (lease.Expires - now).TotalMinutes;
                        time = minutes >= 1 ? $"{Math.Ceiling(minutes):0} min left" : "under 1 min";
                        break;
                    case LeaseState.Offered: kind = "offered"; state = "Offered"; break;
                    case LeaseState.Released: kind = "idle"; state = "Released"; break;
                    default: kind = "idle"; state = "Expired"; break;
                }

                var reservedIp = reservation != null && IpTools.IsIPv4(reservation.Ip) ? IpTools.Canonical(reservation.Ip) : "";
                items.Add(new DeviceItem
                {
                    Mac = lease.Mac, Ip = lease.IpText, Name = name, Detail = string.Join(", ", detail),
                    Kind = kind, StateText = state, TimeLeft = time,
                    IsReserved = reservation != null, ReservedIp = reservedIp,
                    MovingNote = reservedIp.Length > 0 && reservedIp != lease.IpText && running && kind != "idle"
                        ? $"Moves to {reservedIp} at renewal" : "",
                });
            }

            // Reserved cameras that have not asked for an address yet.
            foreach (var pair in reservations.Where(r => !leases.Any(l => string.Equals(l.Mac, r.Key, StringComparison.OrdinalIgnoreCase))))
            {
                items.Add(new DeviceItem
                {
                    Mac = pair.Key, Ip = IpTools.IsIPv4(pair.Value.Ip) ? IpTools.Canonical(pair.Value.Ip) : pair.Value.Ip,
                    Name = string.IsNullOrWhiteSpace(pair.Value.Name) ? "Reserved device" : pair.Value.Name,
                    Kind = "waiting", StateText = "Not seen yet", IsReserved = true, ReservedIp = pair.Value.Ip,
                });
            }

            Devices.Clear();
            foreach (var item in items.OrderBy(i => IpTools.IsIPv4(i.Ip) ? IpTools.ToUInt(i.Ip) : uint.MaxValue)) Devices.Add(item);

            _activeDevices = items.Count(i => i.Kind == "active");
            var reservedCount = reservations.Count;
            DevicesSummary = running
                ? $"{_activeDevices} connected, {reservedCount} reserved"
                : items.Count > 0 ? "The pool is stopped. Reservations are kept for next time." : "";
            Raise(nameof(NoDevices));
            Raise(nameof(DevicesSummary));
            Raise(nameof(DeviceCountText));
            Raise(nameof(DevicesButtonText));
        }

        static DeviceItem DeviceOf(object sender) => (sender as FrameworkElement)?.DataContext as DeviceItem;

        void OpenDevice_Click(object sender, RoutedEventArgs e)
        {
            var device = DeviceOf(sender);
            if (device == null || !IpTools.IsIPv4(device.Ip)) return;
            // Hand the URL to Explorer so the browser opens normally rather than inheriting this app's admin rights.
            try { Process.Start("explorer.exe", "http://" + device.Ip); }
            catch (System.ComponentModel.Win32Exception ex) { Notify("error", "Could not open the browser: " + ex.Message); }
        }

        async void PingDevice_Click(object sender, RoutedEventArgs e)
        {
            var device = DeviceOf(sender);
            if (device == null) return;
            PingBox.Text = device.Ip;
            await RunPingAsync();
        }

        void AssignDevice_Click(object sender, RoutedEventArgs e)
        {
            var device = DeviceOf(sender);
            if (device == null) return;
            var name = device.Name == "Unknown device" || device.Name == "Reserved device" ? "" : device.Name;
            EditReservation(device.Mac, name, device.IsReserved ? device.ReservedIp : device.Ip);
        }

        void AddReservation_Click(object sender, RoutedEventArgs e) => EditReservation(null, "", "");

        void EditReservation(string mac, string name, string ip)
        {
            // While stopped, check against the network in the form, which is what the next start will use.
            var basis = _pool != null ? _config.Pool : ReadPoolForm();
            var window = new ReservationWindow(basis, mac, name, ip) { Owner = this };
            if (window.ShowDialog() != true) return;
            _config.Pool.Reservations = window.Result.Reservations;
            SaveConfig();
            ApplyReservationsToPool();
            RefreshDevices();
            UpdatePoolPreview();
            Notify("ok", window.Removed ? "Reservation removed" : "Reservation saved. The device moves to it at its next renewal, or straight away if restarted.");
        }

        void ReserveAll_Click(object sender, RoutedEventArgs e)
        {
            var reservations = ReservationsByMac();
            var leases = (_pool?.Leases() ?? _lastLeases).Where(l => l.State == LeaseState.Active && !reservations.ContainsKey(l.Mac)).ToList();
            if (leases.Count == 0)
            {
                Notify("info", "Every connected device already has a reservation.");
                return;
            }
            var updated = _config.Pool.Clone();
            foreach (var lease in leases)
                updated.Reservations.Add(new Reservation { Mac = lease.Mac, Ip = lease.IpText, Name = lease.Hostname });
            if (updated.Validate(out var errors) == null)
            {
                Notify("error", "Could not reserve all: " + string.Join(" ", errors.Values));
                return;
            }
            _config.Pool.Reservations = updated.Reservations;
            SaveConfig();
            ApplyReservationsToPool();
            RefreshDevices();
            Notify("ok", $"Reserved {leases.Count} device{(leases.Count == 1 ? "" : "s")} at their current addresses.");
        }

        void ApplyReservationsToPool()
        {
            if (_pool == null) return;
            var config = _config.Pool.Validate(out _);
            if (config != null) _pool.SetReservations(config.Reservations);
        }

        void ShowDevices_Click(object sender, RoutedEventArgs e) => DevicesToggle.IsChecked = true;

        void DevicesToggle_Checked(object sender, RoutedEventArgs e)
        {
            LogToggle.IsChecked = false;
            RefreshDevices();
        }

        void LogToggle_Checked(object sender, RoutedEventArgs e) => DevicesToggle.IsChecked = false;
    }
}
