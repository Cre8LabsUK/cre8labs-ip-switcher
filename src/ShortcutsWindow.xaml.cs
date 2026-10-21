using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Cre8Labs.NetTools.Core;
using Cre8Labs.NetTools.Input;

namespace Cre8Labs.NetTools
{
    public sealed class ShortcutRow : Observable
    {
        string _gesture;
        string _error = "";

        public ShortcutRow(string label, string gesture, string defaultGesture)
        {
            Label = label;
            _gesture = gesture ?? "";
            DefaultGesture = defaultGesture;
        }

        public string Label { get; }
        public string DefaultGesture { get; }

        public string Gesture { get => _gesture; set => Set(ref _gesture, value ?? ""); }

        public string Error
        {
            get => _error;
            set { if (Set(ref _error, value ?? "")) Raise(nameof(HasError)); }
        }

        public bool HasError => Error.Length > 0;
    }

    public partial class ShortcutsWindow : Window
    {
        readonly ShortcutRow _dhcp, _newPreset, _refresh, _navAdapter, _navPool, _navScan, _navMonitor, _navTools;
        readonly List<ShortcutRow> _presetRows = new List<ShortcutRow>();

        public ObservableCollection<ShortcutRow> Rows { get; } = new ObservableCollection<ShortcutRow>();

        public ShortcutSettings ResultSettings { get; private set; }

        /// <summary>One hotkey per preset, in preset order.</summary>
        public List<string> ResultPresetHotkeys { get; private set; }

        public ShortcutsWindow(AppConfig config)
        {
            InitializeComponent();
            DataContext = this;

            var s = config.Shortcuts;
            EnabledSwitch.IsChecked = s.Enabled;
            GlobalSwitch.IsChecked = s.Global;

            Rows.Add(_dhcp = new ShortcutRow("Switch to DHCP", s.Dhcp, ShortcutSettings.DefaultDhcp));
            Rows.Add(_newPreset = new ShortcutRow("New preset", s.NewPreset, ShortcutSettings.DefaultNewPreset));
            Rows.Add(_refresh = new ShortcutRow("Refresh adapters", s.Refresh, ShortcutSettings.DefaultRefresh));
            Rows.Add(_navAdapter = new ShortcutRow("Go to Adapter page", s.NavAdapter, ShortcutSettings.DefaultNavAdapter));
            Rows.Add(_navPool = new ShortcutRow("Go to DHCP pool page", s.NavPool, ShortcutSettings.DefaultNavPool));
            Rows.Add(_navScan = new ShortcutRow("Go to Scan page", s.NavScan, ShortcutSettings.DefaultNavScan));
            Rows.Add(_navMonitor = new ShortcutRow("Go to Monitor page", s.NavMonitor, ShortcutSettings.DefaultNavMonitor));
            Rows.Add(_navTools = new ShortcutRow("Go to Tools page", s.NavTools, ShortcutSettings.DefaultNavTools));
            for (var i = 0; i < config.Presets.Count; i++)
            {
                var row = new ShortcutRow($"Apply “{config.Presets[i].Name}”", config.Presets[i].Hotkey, i < 9 ? "D" + (i + 1) : "");
                _presetRows.Add(row);
                Rows.Add(row);
            }

            foreach (var row in Rows)
                row.PropertyChanged += (sender, e) => { if (e.PropertyName == nameof(ShortcutRow.Gesture)) Validate(); };
            EnabledSwitch.Checked += (sender, e) => Validate();
            EnabledSwitch.Unchecked += (sender, e) => Validate();
            GlobalSwitch.Checked += (sender, e) => Validate();
            GlobalSwitch.Unchecked += (sender, e) => Validate();
            Validate();
        }

        void Validate()
        {
            var enabled = EnabledSwitch.IsChecked == true;
            var global = enabled && GlobalSwitch.IsChecked == true;
            GlobalSwitch.IsEnabled = enabled;
            ListScroll.IsEnabled = enabled;
            ListScroll.Opacity = enabled ? 1 : 0.45;

            foreach (var row in Rows)
            {
                var shortcut = Shortcut.Parse(row.Gesture);
                if (shortcut.IsEmpty) { row.Error = ""; continue; }
                var clash = Rows.FirstOrDefault(other => other != row && Shortcut.Parse(other.Gesture).Equals(shortcut));
                row.Error =
                    shortcut.IsReserved ? "Reserved for moving around the app. Pick another key." :
                    clash != null ? $"Also used by {clash.Label}." :
                    global && !shortcut.HasStrongModifier ? "Background shortcuts need Ctrl, Alt or Win." :
                    "";
            }
            // With shortcuts switched off, problems in the list don't matter until they are switched back on.
            SaveButton.IsEnabled = !enabled || Rows.All(r => !r.HasError);
        }

        void ClearRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ShortcutRow row) row.Gesture = "";
        }

        void Reset_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in Rows) row.Gesture = row.DefaultGesture;
            GlobalSwitch.IsChecked = false;
            EnabledSwitch.IsChecked = true;
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            ResultSettings = new ShortcutSettings
            {
                Enabled = EnabledSwitch.IsChecked == true,
                Global = GlobalSwitch.IsChecked == true,
                Dhcp = _dhcp.Gesture,
                NewPreset = _newPreset.Gesture,
                Refresh = _refresh.Gesture,
                NavAdapter = _navAdapter.Gesture,
                NavPool = _navPool.Gesture,
                NavScan = _navScan.Gesture,
                NavMonitor = _navMonitor.Gesture,
                NavTools = _navTools.Gesture,
            };
            ResultPresetHotkeys = _presetRows.Select(r => r.Gesture).ToList();
            DialogResult = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
