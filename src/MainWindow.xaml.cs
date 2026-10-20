using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cre8Labs.IPSwitcher.Core;

namespace Cre8Labs.IPSwitcher
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        readonly ConfigStore _store = new ConfigStore();
        readonly AppConfig _config;
        readonly DispatcherTimer _pollTimer;
        readonly DispatcherTimer _networkChangedTimer;
        readonly DispatcherTimer _toastTimer;

        StatusView _status = StatusView.Empty;
        bool _busy;
        bool _refreshing;
        bool _refreshAgain;
        bool _suppressSelection;
        Action _toastAction;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<AdapterInfo> Adapters { get; } = new ObservableCollection<AdapterInfo>();
        public ObservableCollection<PresetItem> Presets { get; } = new ObservableCollection<PresetItem>();
        public ObservableCollection<LogEntry> Log { get; } = new ObservableCollection<LogEntry>();
        public ObservableCollection<PingResult> PingResults { get; } = new ObservableCollection<PingResult>();
        public TileState DhcpItem { get; } = new TileState();
        public bool NotAdmin { get; } = !NetworkService.IsAdmin();
        public bool NoPresets => Presets.Count == 0;

        public StatusView Status
        {
            get => _status;
            private set { _status = value; Raise(); }
        }

        string SelectedAdapterName => (AdapterCombo.SelectedItem as AdapterInfo)?.Name;

        public MainWindow()
        {
            InitializeComponent();
            _config = _store.Load();
            DataContext = this;

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _pollTimer.Tick += async (s, e) =>
            {
                if (WindowState != WindowState.Minimized && !_busy) await RefreshStatusAsync();
            };

            // Windows fires address-change events in bursts, so wait for it to settle.
            _networkChangedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _networkChangedTimer.Tick += async (s, e) =>
            {
                _networkChangedTimer.Stop();
                await ReloadAdaptersAsync();
            };

            _toastTimer = new DispatcherTimer();
            _toastTimer.Tick += (s, e) => HideToast();

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

            Loaded += OnLoaded;
            Closed += OnClosed;
            Closing += (s, e) =>
            {
                // Closing mid-apply could leave the address changed but DNS not, so wait for it.
                if (!_busy) return;
                e.Cancel = true;
                Notify("info", "Still applying settings. Close again in a moment.");
            };
            SourceInitialized += (s, e) => UseRoundedCorners();
            StateChanged += (s, e) => OnStateChanged();
        }

        void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            BrandText.Text = $"v{Assembly.GetEntryAssembly().GetName().Version.ToString(3)}   cre8labs.uk";
            RebuildPresets();
            if (!System.IO.File.Exists(_store.FilePath)) SaveConfig();
            AddLog("info", "Presets file: " + _store.FilePath);
            if (_store.ReadOnly)
                Notify("error", "presets.json is locked by another program, so defaults are shown and nothing will be saved. Restart to try again.");
            await ReloadAdaptersAsync();
            _pollTimer.Start();
        }

        void OnClosed(object sender, EventArgs e)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            _pollTimer.Stop();
        }

        void OnNetworkChanged(object sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _networkChangedTimer.Stop();
                _networkChangedTimer.Start();
            }));

        // ------------------------------------------------------------------ adapters and status

        async Task ReloadAdaptersAsync()
        {
            List<AdapterInfo> list;
            try { list = await Task.Run(() => NetworkService.ListAdapters()); }
            catch (NetworkInformationException ex) { AddLog("error", "Could not list adapters: " + ex.Message); return; }

            var unchanged = list.Count == Adapters.Count && list.Zip(Adapters, (a, b) =>
                a.Name == b.Name && a.IsUp == b.IsUp && a.Description == b.Description).All(same => same);

            if (!unchanged && !AdapterCombo.IsDropDownOpen)
            {
                var keep = SelectedAdapterName ?? _config.Adapter;
                _suppressSelection = true;
                Adapters.Clear();
                foreach (var a in list) Adapters.Add(a);
                AdapterCombo.SelectedItem = Adapters.FirstOrDefault(a => a.Name == keep) ?? Adapters.FirstOrDefault();
                _suppressSelection = false;
            }
            await RefreshStatusAsync();
        }

        async Task RefreshStatusAsync()
        {
            if (_refreshing) { _refreshAgain = true; return; }
            _refreshing = true;
            try
            {
                do
                {
                    _refreshAgain = false;
                    var name = SelectedAdapterName;
                    var status = name == null ? null : await Task.Run(() => NetworkService.ReadStatus(name));
                    if (name == SelectedAdapterName)
                    {
                        Status = StatusView.From(status);
                        UpdateActiveTiles();
                    }
                } while (_refreshAgain);
            }
            catch (Exception ex)
            {
                // Called from the poll timer, so log quietly instead of raising the crash dialog every 3 seconds.
                AddLog("error", "Could not read adapter status: " + ex.Message);
            }
            finally
            {
                _refreshing = false;
            }
        }

        void UpdateActiveTiles()
        {
            var hasAdapter = Status != StatusView.Empty;
            foreach (var p in Presets)
                p.IsActive = hasAdapter && !Status.IsDhcp && IpTools.IsIPv4(p.Ip) && Status.AllIps.Contains(IpTools.Canonical(p.Ip));
            DhcpItem.IsActive = hasAdapter && Status.IsDhcp;
        }

        async void Adapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection) return;
            _config.Adapter = SelectedAdapterName ?? "";
            SaveConfig();
            await RefreshStatusAsync();
        }

        // ------------------------------------------------------------------ presets

        void RebuildPresets()
        {
            Presets.Clear();
            for (var i = 0; i < _config.Presets.Count; i++) Presets.Add(new PresetItem(_config.Presets[i], i));
            Raise(nameof(NoPresets));
            UpdateActiveTiles();
        }

        double _tileWidth = 236;

        /// <summary>Tile width that fills the row exactly, so the grid has no ragged gap on the right.</summary>
        public double TileWidth
        {
            get => _tileWidth;
            private set { if (Math.Abs(_tileWidth - value) > 0.5) { _tileWidth = value; Raise(); } }
        }

        // The list is as wide as the scroll viewport, so it also resizes when the scroll bar appears.
        void PresetList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            const double gap = 14, minTile = 220;
            var available = e.NewSize.Width;
            if (available <= 0) return;
            // Every tile, including the last in a row, carries a right margin of one gap.
            var columns = Math.Max(1, Math.Floor(available / (minTile + gap)));
            TileWidth = Math.Floor(available / columns - gap);
        }

        void SaveConfig()
        {
            try { _store.Save(_config); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                Notify("error", "Could not save presets: " + ex.Message);
            }
        }

        void CommitPresets()
        {
            SaveConfig();
            RebuildPresets();
        }

        bool CanChangeSettings(out string alias)
        {
            alias = SelectedAdapterName;
            if (NotAdmin)
            {
                Notify("error", "Changing IP settings needs administrator rights. Use Restart as admin.");
                return false;
            }
            if (alias == null)
            {
                Notify("error", "Pick a network adapter first.");
                return false;
            }
            return !_busy;
        }

        async Task ApplyPresetAsync(PresetItem item)
        {
            if (!CanChangeSettings(out var alias)) return;
            if (!item.IsValid)
            {
                Notify("error", $"'{item.Name}' has an invalid IP or mask. Right-click it and choose Edit.");
                return;
            }

            var addOnly = AddExtraSwitch.IsChecked == true;
            var preset = item.Model.Clone();
            var result = await RunBusyAsync(item, () => addOnly ? NetworkService.AddAddress(alias, preset) : NetworkService.ApplyStatic(alias, preset));

            if (result.Ok)
                Notify("ok", addOnly
                    ? $"Added {preset.IP}{item.PrefixText} to {alias}"
                    : $"{alias} is now {preset.IP}{item.PrefixText} ({preset.Name})");
            else
                Notify("error", result.Message);
            await RefreshStatusAsync();
        }

        async Task ApplyDhcpAsync()
        {
            if (!CanChangeSettings(out var alias)) return;
            var result = await RunBusyAsync(DhcpItem, () => NetworkService.SetDhcp(alias));

            if (result.Ok) Notify("ok", $"{alias} is on DHCP. The router may take a few seconds to answer.");
            else Notify("error", result.Message);
            await RefreshStatusAsync();
        }

        /// <summary>Runs a network change off the UI thread. The busy state is always cleared, even if it throws.</summary>
        async Task<OpResult> RunBusyAsync(TileState tile, Func<OpResult> work)
        {
            SetBusy(tile, true);
            try { return await Task.Run(work); }
            catch (Exception ex) { return OpResult.Fail("Unexpected error: " + ex.Message); }
            finally { SetBusy(tile, false); }
        }

        void SetBusy(TileState tile, bool busy)
        {
            _busy = busy;
            tile.IsBusy = busy;
            Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
        }

        Preset ShowEditor(Preset preset, string title)
        {
            var editor = new PresetEditorWindow(preset, Status, title) { Owner = this };
            return editor.ShowDialog() == true ? editor.Result : null;
        }

        void NewPreset()
        {
            var preset = ShowEditor(null, "New preset");
            if (preset == null) return;
            _config.Presets.Add(preset);
            CommitPresets();
            Notify("ok", $"Saved preset '{preset.Name}'");
        }

        static PresetItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PresetItem;

        async void PresetTile_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item != null) await ApplyPresetAsync(item);
        }

        async void Dhcp_Click(object sender, RoutedEventArgs e) => await ApplyDhcpAsync();

        void NewPreset_Click(object sender, RoutedEventArgs e) => NewPreset();

        void More_Click(object sender, RoutedEventArgs e)
        {
            // Stop the click bubbling up and applying the preset.
            e.Handled = true;
            var button = (Button)sender;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        async void MenuApply_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item != null) await ApplyPresetAsync(item);
        }

        void MenuEdit_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            var edited = ShowEditor(item.Model, "Edit preset");
            if (edited == null) return;
            _config.Presets[item.Index] = edited;
            CommitPresets();
            Notify("ok", $"Updated '{edited.Name}'");
        }

        void MenuDuplicate_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            var copy = item.Model.Clone();
            copy.Name += " copy";
            _config.Presets.Insert(item.Index + 1, copy);
            CommitPresets();
        }

        void MenuMoveLeft_Click(object sender, RoutedEventArgs e) => Move(ItemOf(sender), -1);

        void MenuMoveRight_Click(object sender, RoutedEventArgs e) => Move(ItemOf(sender), 1);

        void Move(PresetItem item, int delta)
        {
            if (item == null) return;
            var target = item.Index + delta;
            if (target < 0 || target >= _config.Presets.Count) return;
            var list = _config.Presets;
            (list[item.Index], list[target]) = (list[target], list[item.Index]);
            CommitPresets();
        }

        void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            var deleted = item.Model;
            var index = item.Index;
            _config.Presets.RemoveAt(index);
            CommitPresets();
            AddLog("info", $"Deleted preset '{item.Name}'");
            ShowToast("info", $"Deleted '{item.Name}'", "Undo", () =>
            {
                // Put back only the deleted preset, keeping any changes made since.
                _config.Presets.Insert(Math.Min(index, _config.Presets.Count), deleted);
                CommitPresets();
                Notify("ok", $"Restored '{item.Name}'");
            });
        }

        // ------------------------------------------------------------------ left column actions

        async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAdaptersAsync();

        void CopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Status.FirstIp)) return;
            try
            {
                Clipboard.SetText(Status.FirstIp);
                ShowToast("ok", "Copied " + Status.FirstIp);
            }
            catch (ExternalException)
            {
                Notify("error", "The clipboard is busy. Try again.");
            }
        }

        void OpenNcpa_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("ncpa.cpl"); }
            catch (System.ComponentModel.Win32Exception ex) { Notify("error", "Could not open Network Connections: " + ex.Message); }
        }

        async void Ping_Click(object sender, RoutedEventArgs e) => await RunPingAsync();

        async void PingBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunPingAsync();
        }

        async Task RunPingAsync()
        {
            if (!PingButton.IsEnabled) return;
            var target = PingBox.Text.Trim();
            if (target.Length == 0) target = Status.FirstGateway;
            if (string.IsNullOrEmpty(target))
            {
                Notify("info", "No gateway to ping. Type an address first.");
                return;
            }

            PingButton.IsEnabled = false;
            PingResults.Clear();
            var replies = 0;
            try
            {
            using (var ping = new Ping())
            {
                for (var i = 0; i < 4; i++)
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(target, 1000);
                        var ok = reply.Status == IPStatus.Success;
                        if (ok) replies++;
                        PingResults.Add(new PingResult
                        {
                            Ok = ok,
                            Text = ok ? $"{reply.Address}  {reply.RoundtripTime} ms" : $"{target}  {Friendly(reply.Status)}",
                        });
                    }
                    catch (PingException ex)
                    {
                        PingResults.Add(new PingResult { Ok = false, Text = ex.InnerException?.Message ?? ex.Message });
                        break;
                    }
                    catch (ArgumentException ex)
                    {
                        PingResults.Add(new PingResult { Ok = false, Text = ex.Message });
                        break;
                    }
                    if (i < 3) await Task.Delay(300);
                }
            }
            AddLog(replies > 0 ? "ok" : "error", $"Ping {target}: {replies} of 4 replies");
            }
            finally
            {
                PingButton.IsEnabled = true;
            }
        }

        static string Friendly(IPStatus status)
        {
            switch (status)
            {
                case IPStatus.TimedOut: return "timed out";
                case IPStatus.DestinationHostUnreachable: return "host unreachable";
                case IPStatus.DestinationNetworkUnreachable: return "network unreachable";
                default: return status.ToString();
            }
        }

        void RestartAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location) { UseShellExecute = true, Verb = "runas" });
                Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Notify("info", "Restart cancelled.");
            }
        }

        // ------------------------------------------------------------------ keyboard

        async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await ReloadAdaptersAsync();
                return;
            }
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                NewPreset();
                return;
            }
            if (Keyboard.FocusedElement is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;

            var n = -1;
            if (e.Key >= Key.D0 && e.Key <= Key.D9) n = e.Key - Key.D0;
            else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) n = e.Key - Key.NumPad0;
            if (n < 0) return;

            e.Handled = true;
            if (n == 0) await ApplyDhcpAsync();
            else if (n <= Presets.Count) await ApplyPresetAsync(Presets[n - 1]);
        }

        // ------------------------------------------------------------------ feedback

        void AddLog(string kind, string text)
        {
            Log.Insert(0, new LogEntry { Kind = kind, Text = text });
            while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
        }

        void Notify(string kind, string text)
        {
            AddLog(kind, text);
            FooterText.Text = text;
            FooterDot.Fill = (Brush)FindResource(kind == "ok" ? "Success" : kind == "error" ? "Danger" : "Faint");
            ShowToast(kind, text);
        }

        void ShowToast(string kind, string text, string actionLabel = null, Action action = null)
        {
            ToastIcon.Text = kind == "ok" ? "" : kind == "error" ? "" : "";
            ToastIcon.Foreground = new SolidColorBrush(kind == "ok" ? Color.FromRgb(0x5E, 0xE0, 0x9A)
                : kind == "error" ? Color.FromRgb(0xFF, 0x86, 0x90) : Color.FromRgb(0x7F, 0xDC, 0xEC));
            ToastText.Text = text;
            _toastAction = action;
            ToastAction.Content = actionLabel;
            ToastAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            Toast.IsHitTestVisible = action != null;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(action != null ? 6 : kind == "error" ? 5 : 2.8);
            _toastTimer.Start();
        }

        void HideToast()
        {
            _toastTimer.Stop();
            _toastAction = null;
            Toast.IsHitTestVisible = false;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
            ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.FromMilliseconds(150)));
        }

        void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            var action = _toastAction;
            HideToast();
            action?.Invoke();
        }

        // ------------------------------------------------------------------ window chrome

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Max_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void OnStateChanged()
        {
            // A maximized chromeless window overhangs the screen edge, so pad it back in.
            var maximized = WindowState == WindowState.Maximized;
            RootBorder.Padding = maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = maximized ? "" : "";
            MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        void UseRoundedCorners()
        {
            // Windows 11 rounded corners. Ignored on Windows 10.
            const int DwmwaWindowCornerPreference = 33;
            var round = 2;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
    }
}
