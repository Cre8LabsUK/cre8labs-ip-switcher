using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Cre8Labs.NetTools.Core;
using Cre8Labs.NetTools.Core.Dhcp;
using Cre8Labs.NetTools.Core.Monitor;

namespace Cre8Labs.NetTools
{
    /// <summary>The Monitor page: ping many devices once a second and alert when one drops.</summary>
    public partial class MainWindow
    {
        DispatcherTimer _monitorTimer;
        IPAddress _monitorSource;
        string _monitorAdapter;
        bool _monitorTicking;

        public ObservableCollection<MonitorItem> MonitorItems { get; } = new ObservableCollection<MonitorItem>();
        public bool MonitorRunning => _monitorTimer?.IsEnabled == true;
        public string MonitorButtonText => MonitorRunning ? "Stop" : "Start";
        public string MonitorButtonIcon => MonitorRunning ? "" : "";
        public bool NoMonitorItems => MonitorItems.Count == 0;

        public string MonitorSummary
        {
            get
            {
                if (MonitorItems.Count == 0) return "Pings every device once a second from the selected adapter, and alerts you when one drops.";
                var down = MonitorItems.Count(m => m.Stats.State == TargetState.Down);
                var slow = MonitorItems.Count(m => m.Stats.State == TargetState.Slow);
                var up = MonitorItems.Count(m => m.Stats.State == TargetState.Up);
                if (!MonitorRunning) return $"{MonitorItems.Count} device{(MonitorItems.Count == 1 ? "" : "s")} ready. Press Start to begin watching.";
                return $"Watching {MonitorItems.Count} from {_monitorAdapter}: {up} up" + (slow > 0 ? $", {slow} slow" : "") + (down > 0 ? $", {down} down" : "") + ".";
            }
        }

        void RaiseMonitor()
        {
            foreach (var name in new[] { nameof(MonitorRunning), nameof(MonitorButtonText), nameof(MonitorButtonIcon), nameof(NoMonitorItems), nameof(MonitorSummary) })
                Raise(name);
        }

        void LoadMonitorTargets()
        {
            foreach (var t in _config.MonitorTargets) MonitorItems.Add(new MonitorItem(t.Name, t.Ip));
            RaiseMonitor();
        }

        void SaveMonitorTargets()
        {
            _config.MonitorTargets = MonitorItems.Select(m => new MonitorTarget { Name = m.Name, Ip = m.Ip }).ToList();
            SaveConfig();
            RaiseMonitor();
        }

        /// <summary>Adds a device unless it is already watched. Returns true if added.</summary>
        bool AddMonitorTarget(string name, string ip)
        {
            if (!IpTools.IsIPv4(ip)) return false;
            ip = IpTools.Canonical(ip);
            if (MonitorItems.Any(m => m.Ip == ip)) return false;
            var item = new MonitorItem(string.IsNullOrWhiteSpace(name) ? ip : name.Trim(), ip);
            var index = 0;
            while (index < MonitorItems.Count && IpTools.ToUInt(MonitorItems[index].Ip) < IpTools.ToUInt(ip)) index++;
            MonitorItems.Insert(index, item);
            return true;
        }

        void MonitorAdd_Click(object sender, RoutedEventArgs e) => AddFromBox();

        void MonitorAdd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddFromBox();
        }

        void AddFromBox()
        {
            var text = MonitorAddBox.Text.Trim();
            var equals = text.IndexOf('=');
            var name = equals > 0 ? text.Substring(0, equals) : "";
            var ip = equals > 0 ? text.Substring(equals + 1).Trim() : text;
            if (!IpTools.IsIPv4(ip)) { Notify("error", "Type an IPv4 address, or Name=IP such as Gate camera=192.168.1.20."); return; }
            if (!AddMonitorTarget(name, ip)) { Notify("info", $"{ip} is already being watched."); return; }
            MonitorAddBox.Text = "";
            SaveMonitorTargets();
        }

        void MonitorFromScan_Click(object sender, RoutedEventArgs e)
        {
            if (ScanItems.Count == 0) { Notify("info", "Run a scan first, then add its results here."); return; }
            var added = ScanItems.Count(s => AddMonitorTarget(s.Title == "Unknown device" ? "" : s.Title, s.Ip));
            SaveMonitorTargets();
            Notify("ok", added == 0 ? "Every scanned device is already being watched." : $"Added {added} device{(added == 1 ? "" : "s")} from the scan.");
        }

        void MonitorFromPool_Click(object sender, RoutedEventArgs e)
        {
            var devices = Devices.Where(d => d.Kind == "active" || d.IsReserved).ToList();
            if (devices.Count == 0) { Notify("info", "No DHCP pool devices yet. Start the pool, or reserve devices, first."); return; }
            var added = devices.Count(d => AddMonitorTarget(d.Name == "Unknown device" ? "" : d.Name, d.Ip));
            SaveMonitorTargets();
            Notify("ok", added == 0 ? "Every pool device is already being watched." : $"Added {added} device{(added == 1 ? "" : "s")} from the DHCP pool.");
        }

        void MonitorRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is MonitorItem item)) return;
            MonitorItems.Remove(item);
            SaveMonitorTargets();
            if (MonitorItems.Count == 0) StopMonitor();
        }

        void MonitorToggle_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorRunning) { StopMonitor(); return; }
            if (MonitorItems.Count == 0) { Notify("info", "Add a device to watch first."); return; }
            var alias = SelectedAdapterName;
            if (alias == null || !Status.IsUp || !IpTools.IsIPv4(Status.FirstIp))
            {
                Notify("error", $"{alias ?? "The adapter"} has no live address to ping from. Connect it, or apply a preset first.");
                return;
            }
            _monitorSource = IPAddress.Parse(Status.FirstIp);
            _monitorAdapter = alias;
            if (_monitorTimer == null)
            {
                _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _monitorTimer.Tick += async (s, args) => await MonitorTickAsync();
            }
            _monitorTimer.Start();
            AddLog("info", $"Monitoring {MonitorItems.Count} devices from {alias} ({Status.FirstIp}).");
            RaiseMonitor();
        }

        void StopMonitor()
        {
            _monitorTimer?.Stop();
            RaiseMonitor();
        }

        async Task MonitorTickAsync()
        {
            if (_monitorTicking) return; // a slow round is still running; skip rather than pile up
            _monitorTicking = true;
            try
            {
                var source = _monitorSource;
                var items = MonitorItems.ToList();
                var replies = await Task.WhenAll(items.Select(item =>
                    Task.Run(() => PingService.Send(source, IPAddress.Parse(item.Ip), 800))));
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var changed = item.Stats.Add(replies[i].Ok, replies[i].RoundTripMs);
                    item.Refresh();
                    if (changed != null) OnMonitorStateChanged(item, changed.Value);
                }
                Raise(nameof(MonitorSummary));
            }
            finally { _monitorTicking = false; }
        }

        void OnMonitorStateChanged(MonitorItem item, TargetState state)
        {
            if (state == TargetState.Down)
            {
                item.DownSince = DateTime.Now;
                Notify("error", $"{item.Name} ({item.Ip}) is not answering.");
                FlashTaskbar();
            }
            else if ((state == TargetState.Up || state == TargetState.Slow) && item.DownSince != null)
            {
                var seconds = (int)(DateTime.Now - item.DownSince.Value).TotalSeconds;
                item.DownSince = null;
                Notify("ok", $"{item.Name} ({item.Ip}) is back after {seconds} s.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }

        [DllImport("user32.dll")]
        static extern bool FlashWindowEx(ref FlashInfo info);

        /// <summary>Flashes the taskbar button until the window is brought forward, when it is in the background.</summary>
        void FlashTaskbar()
        {
            if (IsActive) return;
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
                Hwnd = new WindowInteropHelper(this).Handle,
                Flags = 0x2 | 0xC, // taskbar, until the window comes to the front
            };
            FlashWindowEx(ref info);
        }
    }
}
