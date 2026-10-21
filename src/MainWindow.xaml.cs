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
using Cre8Labs.NetTools.Core;
using Cre8Labs.NetTools.Input;

namespace Cre8Labs.NetTools
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
        GlobalHotkeys _globalHotkeys;

        sealed class ShortcutAction
        {
            public string Label;
            public Func<Task> Run;
            public bool NeedsWindow; // opens a dialog, so bring the window forward first
        }

        readonly Dictionary<Shortcut, ShortcutAction> _bindings = new Dictionary<Shortcut, ShortcutAction>();

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<AdapterInfo> Adapters { get; } = new ObservableCollection<AdapterInfo>();
        public ObservableCollection<PresetItem> Presets { get; } = new ObservableCollection<PresetItem>();
        public ObservableCollection<LogEntry> Log { get; } = new ObservableCollection<LogEntry>();
        public ObservableCollection<PingResult> PingResults { get; } = new ObservableCollection<PingResult>();
        public TileState DhcpItem { get; } = new TileState();
        public bool NotAdmin { get; } = !NetworkService.IsAdmin();
        public bool NoPresets => Presets.Count == 0;

        public string ShortcutHint => _config.Shortcuts.Enabled
            ? "Click a tile to switch, or press the key shown on it."
            : "Click a tile to switch. Keyboard shortcuts are off.";

        public StatusView Status
        {
            get => _status;
            private set
            {
                _status = value;
                Raise();
                Raise(nameof(PingSource));
            }
        }

        public string PingSource =>
            Status.IsUp && IpTools.IsIPv4(Status.FirstIp)
                ? $"Sent from {SelectedAdapterName} ({Status.FirstIp})"
                : "Selected adapter has no live address";

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
                await CheckPoolAdapterAsync();
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
                if (_busy)
                {
                    e.Cancel = true;
                    Notify("info", "Still applying settings. Close again in a moment.");
                    return;
                }
                if (_pool != null)
                {
                    // Stop serving and put the adapter back before closing.
                    e.Cancel = true;
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await StopPoolAsync(restore: true, message: null);
                        Close();
                    }));
                }
            };
            SourceInitialized += (s, e) =>
            {
                UseRoundedCorners();
                var source = (HwndSource)PresentationSource.FromVisual(this);
                _globalHotkeys = new GlobalHotkeys(source.Handle);
                _globalHotkeys.Pressed += OnGlobalHotkey;
                source.AddHook(_globalHotkeys.WndProc);
            };
            StateChanged += (s, e) => OnStateChanged();
        }

        void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            BrandText.Text = $"v{Assembly.GetEntryAssembly().GetName().Version.ToString(3)}   cre8labs.uk";
            RebuildPresets();
            LoadPoolForm();
            if (!System.IO.File.Exists(_store.FilePath)) SaveConfig();
            AddLog("info", NetworkService.DemoMode
                ? "Demo mode: sample adapters and presets. Nothing is changed or saved."
                : "Presets file: " + _store.FilePath);
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
            _globalHotkeys?.Clear();
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
            RaisePool();
            UpdatePoolPreview();
            await RefreshStatusAsync();
        }

        // ------------------------------------------------------------------ presets

        void RebuildPresets()
        {
            Presets.Clear();
            for (var i = 0; i < _config.Presets.Count; i++) Presets.Add(new PresetItem(_config.Presets[i], i, _config.Shortcuts.Enabled));
            Raise(nameof(NoPresets));
            UpdateActiveTiles();
            RebuildBindings();
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

            if (_pool != null && alias == _poolAdapter)
                await StopPoolAsync(false, $"DHCP pool stopped because {alias} is switching to {item.Name}.");

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
            if (_pool != null && alias == _poolAdapter)
                await StopPoolAsync(false, $"DHCP pool stopped because {alias} is switching to DHCP.");
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

        Preset ShowEditor(Preset preset, string title, Preset editing = null)
        {
            var editor = new PresetEditorWindow(preset, Status, title, s => UsedBy(s, editing), _config.Shortcuts.Enabled && _config.Shortcuts.Global)
            {
                Owner = this,
            };
            // Background hotkeys would swallow the keys being recorded, so pause them while the dialog is open.
            _globalHotkeys?.Clear();
            try { return editor.ShowDialog() == true ? editor.Result : null; }
            finally { RegisterGlobalHotkeys(); }
        }

        void NewPreset()
        {
            var preset = ShowEditor(new Preset { Hotkey = _config.NextFreeDigit() }, "New preset");
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
            var edited = ShowEditor(item.Model, "Edit preset", item.Model);
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
            copy.Hotkey = _config.NextFreeDigit();
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

            // Ping from the selected adapter's own address so it cannot leave through Wi-Fi or a VPN instead.
            var adapter = SelectedAdapterName;
            if (adapter == null || !Status.IsUp || !IpTools.IsIPv4(Status.FirstIp))
            {
                Notify("error", $"{adapter ?? "No adapter"} has no live address to ping from. Connect it or apply a preset first.");
                return;
            }
            var source = System.Net.IPAddress.Parse(Status.FirstIp);

            PingButton.IsEnabled = false;
            PingResults.Clear();
            var replies = 0;
            try
            {
                System.Net.IPAddress destination;
                try { destination = await Task.Run(() => PingService.Resolve(target)); }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException || ex is ArgumentException)
                {
                    PingResults.Add(new PingResult { Ok = false, Text = $"{target}: {ex.Message}" });
                    return;
                }

                for (var i = 0; i < 4; i++)
                {
                    var reply = await Task.Run(() => PingService.Send(source, destination, 1000));
                    if (reply.Ok) replies++;
                    PingResults.Add(new PingResult
                    {
                        Ok = reply.Ok,
                        Text = reply.Ok ? $"{reply.From}  {reply.RoundTripMs} ms" : $"{destination}  {reply.Error}",
                    });
                    if (i < 3) await Task.Delay(300);
                }
                AddLog(replies > 0 ? "ok" : "error", $"Ping {target} from {adapter} ({source}): {replies} of 4 replies");
            }
            finally
            {
                PingButton.IsEnabled = true;
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
            if (!_config.Shortcuts.Enabled || e.Handled) return;
            var shortcut = Shortcut.FromKeyEvent(e);
            if (shortcut.IsEmpty || !_bindings.TryGetValue(shortcut, out var action)) return;
            // Plain keys keep typing normally in text boxes; only Ctrl, Alt or Win combinations act there.
            if (Keyboard.FocusedElement is TextBox && !shortcut.HasStrongModifier) return;
            e.Handled = true;
            await action.Run();
        }

        async void OnGlobalHotkey(Shortcut shortcut)
        {
            if (!_bindings.TryGetValue(shortcut, out var action)) return;
            if (action.NeedsWindow)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }
            await action.Run();
        }

        /// <summary>Maps each enabled shortcut to its action, updates tile badges, and registers background hotkeys.</summary>
        void RebuildBindings()
        {
            _bindings.Clear();
            var s = _config.Shortcuts;
            DhcpItem.HotkeyText = s.Enabled ? Shortcut.Parse(s.Dhcp).Display : "";
            Raise(nameof(ShortcutHint));
            if (s.Enabled)
            {
                Bind(s.Dhcp, "Switch to DHCP", ApplyDhcpAsync);
                Bind(s.NewPreset, "New preset", () => { NewPreset(); return Task.CompletedTask; }, needsWindow: true);
                Bind(s.Refresh, "Refresh adapters", ReloadAdaptersAsync);
                foreach (var item in Presets)
                {
                    var target = item;
                    Bind(target.Model.Hotkey, target.Name, () => ApplyPresetAsync(target));
                }
            }
            RegisterGlobalHotkeys();
        }

        void Bind(string gesture, string label, Func<Task> run, bool needsWindow = false)
        {
            var shortcut = Shortcut.Parse(gesture);
            // First one wins if a hand-edited file has duplicates.
            if (shortcut.IsEmpty || shortcut.IsReserved || _bindings.ContainsKey(shortcut)) return;
            _bindings[shortcut] = new ShortcutAction { Label = label, Run = run, NeedsWindow = needsWindow };
        }

        void RegisterGlobalHotkeys()
        {
            if (_globalHotkeys == null) return;
            _globalHotkeys.Clear();
            if (!_config.Shortcuts.Enabled || !_config.Shortcuts.Global) return;

            var taken = _bindings.Keys.Where(k => k.HasStrongModifier && !_globalHotkeys.Register(k)).Select(k => k.Display).ToList();
            if (taken.Count > 0)
                Notify("error", $"Another app already uses {string.Join(", ", taken)}, so " +
                    (taken.Count == 1 ? "it only works" : "they only work") + " while Net Tools is focused.");
        }

        /// <summary>The action already using a shortcut, ignoring <paramref name="exclude"/>, or null.</summary>
        string UsedBy(Shortcut shortcut, Preset exclude)
        {
            if (shortcut.IsEmpty) return null;
            var s = _config.Shortcuts;
            if (Shortcut.Parse(s.Dhcp).Equals(shortcut)) return "Switch to DHCP";
            if (Shortcut.Parse(s.NewPreset).Equals(shortcut)) return "New preset";
            if (Shortcut.Parse(s.Refresh).Equals(shortcut)) return "Refresh adapters";
            var preset = _config.Presets.FirstOrDefault(p => p != exclude && Shortcut.Parse(p.Hotkey).Equals(shortcut));
            return preset == null ? null : $"\u201C{preset.Name}\u201D";
        }

        void Shortcuts_Click(object sender, RoutedEventArgs e)
        {
            _globalHotkeys?.Clear();
            var window = new ShortcutsWindow(_config) { Owner = this };
            if (window.ShowDialog() != true)
            {
                RegisterGlobalHotkeys();
                return;
            }
            _config.Shortcuts = window.ResultSettings;
            for (var i = 0; i < _config.Presets.Count; i++) _config.Presets[i].Hotkey = window.ResultPresetHotkeys[i];
            CommitPresets();
            Notify("ok", _config.Shortcuts.Enabled ? "Keyboard shortcuts saved" : "Keyboard shortcuts are off");
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
