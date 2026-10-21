using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Cre8Labs.NetTools.Core;
using Cre8Labs.NetTools.Core.Scan;

namespace Cre8Labs.NetTools
{
    /// <summary>Navigation between pages, and the Scan page: sweep, discovery, and Reach it.</summary>
    public partial class MainWindow
    {
        readonly Dictionary<string, ScanResult> _scanResults = new Dictionary<string, ScanResult>();
        readonly HashSet<uint> _reachedNetworks = new HashSet<uint>(); // /24s added with Reach it
        readonly TileState _scanTile = new TileState();
        CancellationTokenSource _scanCts;
        int _scanTotal, _scanDone;
        uint _scanSource, _scanMask;

        public ObservableCollection<ScanItem> ScanItems { get; } = new ObservableCollection<ScanItem>();
        public bool ScanRunning => _scanCts != null;
        public string ScanButtonText => ScanRunning ? "Stop" : "Scan";
        public string ScanButtonIcon => ScanRunning ? "" : "";
        public double ScanFraction { get; private set; }
        public string ScanStatus { get; private set; } = "Press Scan to look for devices. Scanning only listens and asks; it changes nothing on the network.";
        public bool NoScanResults => ScanItems.Count == 0;
        public string ScanEmptyText => ScanRunning ? "Looking for devices..." : "No devices found yet.";

        void RaiseScan()
        {
            foreach (var name in new[] { nameof(ScanRunning), nameof(ScanButtonText), nameof(ScanButtonIcon), nameof(ScanFraction),
                                         nameof(ScanStatus), nameof(NoScanResults), nameof(ScanEmptyText) })
                Raise(name);
        }

        // ------------------------------------------------------------------ navigation

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (AdapterPage == null || PoolPage == null || ScanPage == null || MonitorPage == null || ToolsPage == null) return; // still loading
            var page = sender == NavPool ? "pool" : sender == NavScan ? "scan" : sender == NavMonitor ? "monitor" : sender == NavTools ? "tools" : "adapter";
            AdapterPage.Visibility = page == "adapter" ? Visibility.Visible : Visibility.Collapsed;
            PoolPage.Visibility = page == "pool" ? Visibility.Visible : Visibility.Collapsed;
            ScanPage.Visibility = page == "scan" ? Visibility.Visible : Visibility.Collapsed;
            MonitorPage.Visibility = page == "monitor" ? Visibility.Visible : Visibility.Collapsed;
            ToolsPage.Visibility = page == "tools" ? Visibility.Visible : Visibility.Collapsed;
            PoolCard.Visibility = page == "pool" ? Visibility.Visible : Visibility.Collapsed;
            // Keep the pool setup in view: the adapter card shrinks to the essentials on the DHCP pool page.
            AdapterExtra.Visibility = page == "pool" ? Visibility.Collapsed : Visibility.Visible;
            if (page == "pool") RefreshDevices();
            if (page == "scan" && ScanRangeBox.Text.Trim().Length == 0) ScanRangeBox.Text = ScanRange.ForAdapter(Status.FirstIp, Status.FirstMask);
        }

        void Navigate(string page)
        {
            var target = page == "pool" ? NavPool : page == "scan" ? NavScan : page == "monitor" ? NavMonitor : page == "tools" ? NavTools : NavAdapter;
            target.IsChecked = true;
            target.Focus();
        }

        // ------------------------------------------------------------------ scanning

        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (ScanRunning) _scanCts.Cancel();
            else await StartScanAsync();
        }

        async void ScanRange_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || ScanRunning) return;
            e.Handled = true;
            await StartScanAsync();
        }

        async Task StartScanAsync()
        {
            var alias = SelectedAdapterName;
            if (alias == null) { Notify("error", "Pick a network adapter first."); return; }
            if (!Status.IsUp || !IpTools.IsIPv4(Status.FirstIp) || string.IsNullOrEmpty(Status.FirstMask))
            {
                Notify("error", $"{alias} has no live address to scan from. Connect it, or apply a preset first.");
                return;
            }
            if (ScanRangeBox.Text.Trim().Length == 0) ScanRangeBox.Text = ScanRange.ForAdapter(Status.FirstIp, Status.FirstMask);
            var hosts = ScanRange.Parse(ScanRangeBox.Text, out var error);
            if (hosts == null) { Notify("error", error); return; }

            _scanSource = IpTools.ToUInt(Status.FirstIp);
            _scanMask = IpTools.ToUInt(Status.FirstMask);
            var outside = hosts.Count(h => (h & _scanMask) != (_scanSource & _scanMask));
            // Safety: sweeping beyond your own subnet, or a big range, deserves a second look.
            if ((outside > 0 || hosts.Count > 256) && !ConfirmWindow.Ask(this, $"Scan {hosts.Count:N0} addresses?",
                    (outside > 0 ? $"{outside:N0} of them are outside {alias}'s own subnet, so the scan goes through its gateway. " : "") +
                    "Only scan networks you look after. Some sites treat unexpected scans as a security alert.", "Scan"))
                return;

            _scanResults.Clear();
            ScanItems.Clear();
            _scanTotal = hosts.Count;
            _scanDone = 0;
            ScanFraction = 0;
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            UpdateScanStatus();
            RaiseScan();

            var source = IPAddress.Parse(Status.FirstIp);
            var found = new Progress<ScanResult>(AddScanResult);
            var progress = new Progress<int>(n =>
            {
                _scanDone = Math.Max(_scanDone, n);
                UpdateScanStatus();
            });
            var work = new List<Task> { SubnetScanner.ScanAsync(source, hosts, found, progress, token) };
            if (DiscoverSwitch.IsChecked == true) work.Add(Discovery.DiscoverAsync(source, TimeSpan.FromSeconds(4), found, token));
            try { await Task.WhenAll(work); }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException || ex is InvalidOperationException)
            {
                AddLog("error", "Scan problem: " + ex.Message);
            }

            var stopped = token.IsCancellationRequested;
            _scanCts = null;
            ScanFraction = 1;
            var other = _scanResults.Values.Count(IsOtherSubnet);
            ScanStatus = $"{(stopped ? "Stopped" : "Done")}. {_scanResults.Count} device{(_scanResults.Count == 1 ? "" : "s")} found" +
                         (other > 0 ? $", {other} on another subnet (use Reach it)." : ".");
            AddLog("ok", $"Scan of {ScanRangeBox.Text.Trim()} from {alias}: {ScanStatus}");
            RaiseScan();
        }

        void UpdateScanStatus()
        {
            ScanFraction = _scanTotal == 0 ? 0 : Math.Min(1, (double)_scanDone / _scanTotal);
            ScanStatus = $"Checked {_scanDone:N0} of {_scanTotal:N0} addresses. {_scanResults.Count} found so far.";
            Raise(nameof(ScanFraction));
            Raise(nameof(ScanStatus));
        }

        void AddScanResult(ScanResult result)
        {
            if (!IpTools.IsIPv4(result.Ip)) return;
            Discovery.FillVendor(result);
            if (_scanResults.TryGetValue(result.Ip, out var existing)) existing.Merge(result);
            else _scanResults[result.Ip] = result;
            RebuildScanItems();
            if (ScanRunning) UpdateScanStatus();
        }

        bool IsOtherSubnet(ScanResult r) =>
            (r.IpValue & _scanMask) != (_scanSource & _scanMask) && !_reachedNetworks.Contains(r.IpValue & 0xFFFFFF00);

        void RebuildScanItems()
        {
            ScanItems.Clear();
            foreach (var r in _scanResults.Values.OrderBy(r => r.IpValue)) ScanItems.Add(ToScanItem(r));
            Raise(nameof(NoScanResults));
        }

        ScanItem ToScanItem(ScanResult r)
        {
            var product = string.Join(" ", new[] { r.Manufacturer, r.Model }.Where(s => s.Length > 0));
            var title = r.DeviceName.Length > 0 ? r.DeviceName
                : r.Hostname.Length > 0 ? r.Hostname.Split('.')[0]
                : product.Length > 0 ? product
                : r.Vendor.Length > 0 ? ShortVendor(r.Vendor)
                : "Unknown device";
            var detail = new List<string>();
            if (product.Length > 0 && product != title) detail.Add(product);
            else if (r.Vendor.Length > 0 && ShortVendor(r.Vendor) != title) detail.Add(ShortVendor(r.Vendor));
            if (r.Hostname.Length > 0 && !r.Hostname.StartsWith(title, StringComparison.OrdinalIgnoreCase)) detail.Add(r.Hostname);
            if (r.Mac.Length > 0) detail.Add(r.Mac);

            var other = IsOtherSubnet(r);
            var web = r.OpenPorts.Contains(80) || r.OpenPorts.Contains(443) || (r.Sources & (FoundBy.Onvif | FoundBy.Ssdp)) != 0;
            var scanned = (r.Sources & (FoundBy.Arp | FoundBy.Ping)) != 0;
            return new ScanItem
            {
                Ip = r.Ip,
                Mac = r.Mac,
                Title = title,
                Detail = string.Join("  ·  ", detail),
                Badge = (r.Sources & FoundBy.Onvif) != 0 ? "ONVIF" : (r.Sources & FoundBy.Ssdp) != 0 ? "UPnP" : (r.Sources & FoundBy.Mdns) != 0 ? "mDNS" : "",
                PortsText = r.OpenPorts.Count > 0 ? string.Join(" ", r.OpenPorts) : scanned ? "none" : "",
                IsOtherSubnet = other,
                IpNote = other ? "Other subnet" : r.LatencyMs != null ? $"{r.LatencyMs} ms" : scanned ? "ignores ping" : "",
                CanOpenWeb = web,
                WebUrl = (r.OpenPorts.Contains(443) && !r.OpenPorts.Contains(80) ? "https://" : "http://") + r.Ip,
            };
        }

        /// <summary>"Hangzhou Hikvision Digital Technology Co.,Ltd." becomes "Hikvision"-style short names where it is safe to.</summary>
        static string ShortVendor(string vendor)
        {
            var v = vendor;
            foreach (var noise in new[] { " Co.,Ltd.", " Co., Ltd.", " Co.,Ltd", " Co., Ltd", ", Inc.", " Inc.", " Inc", " AB", " GmbH", " Ltd.", " Ltd", " Corporation", " Corp.", " Incorporated", " Limited", " LLC" })
                if (v.EndsWith(noise, StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - noise.Length);
            return v.Trim().TrimEnd(',');
        }

        static ScanItem ScanItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ScanItem;

        void OpenScanned_Click(object sender, RoutedEventArgs e)
        {
            var item = ScanItemOf(sender);
            if (item == null) return;
            try { Process.Start("explorer.exe", item.WebUrl); }
            catch (System.ComponentModel.Win32Exception ex) { Notify("error", "Could not open the browser: " + ex.Message); }
        }

        async void PingScanned_Click(object sender, RoutedEventArgs e)
        {
            var item = ScanItemOf(sender);
            if (item == null) return;
            PingBox.Text = item.Ip;
            await RunPingAsync();
        }

        void ReserveScanned_Click(object sender, RoutedEventArgs e)
        {
            var item = ScanItemOf(sender);
            if (item == null || !item.HasMac) return;
            Navigate("pool");
            EditReservation(item.Mac, item.Title == "Unknown device" ? "" : item.Title, item.Ip);
        }

        // ------------------------------------------------------------------ Reach it

        async void ReachIt_Click(object sender, RoutedEventArgs e)
        {
            var item = ScanItemOf(sender);
            if (item != null) await ReachItAsync(item);
        }

        /// <summary>
        /// Adds a spare address in the device's /24 to the adapter (keeping the current one), so a camera
        /// on its factory-default subnet can be reached without changing presets.
        /// </summary>
        async Task ReachItAsync(ScanItem item)
        {
            if (!CanChangeSettings(out var alias)) return;
            if (Status.IsDhcp)
            {
                Notify("error", "Reach it adds a second address, and Windows only allows that on a static adapter. Apply a preset first, then try again.");
                return;
            }
            var target = IpTools.ToUInt(item.Ip);
            var network = target & 0xFFFFFF00;
            var host = (target & 0xFF) == 250 ? 249u : 250u;
            var address = IpTools.FromUInt(network | host);

            var result = await RunBusyAsync(_scanTile, () => NetworkService.AddAddress(alias, new Preset { IP = address, Mask = "255.255.255.0" }));
            if (!result.Ok) { Notify("error", result.Message); return; }

            _reachedNetworks.Add(network);
            RebuildScanItems();
            Notify("ok", $"Added {address}/24 to {alias}, so you can reach {item.Ip}. Apply a preset or DHCP later to remove it.");
            await RefreshStatusAsync();
            if (item.CanOpenWeb)
            {
                try { Process.Start("explorer.exe", item.WebUrl); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
