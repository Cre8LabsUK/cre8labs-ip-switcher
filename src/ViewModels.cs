using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Cre8Labs.NetTools.Core;

namespace Cre8Labs.NetTools
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>State shared by every clickable tile (presets and the DHCP tile).</summary>
    public class TileState : Observable
    {
        bool _isActive;
        bool _isBusy;
        string _hotkeyText = "";

        public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

        /// <summary>Shortcut shown on the tile badge, for example "1" or "Ctrl+F3". Empty hides the badge.</summary>
        public string HotkeyText
        {
            get => _hotkeyText;
            set { if (Set(ref _hotkeyText, value ?? "")) Raise(nameof(HasHotkey)); }
        }

        public bool HasHotkey => HotkeyText.Length > 0;
        public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    }

    public sealed class PresetItem : TileState
    {
        public PresetItem(Preset model, int index, bool shortcutsEnabled)
        {
            Model = model;
            Index = index;
            HotkeyText = shortcutsEnabled ? Input.Shortcut.Parse(model.Hotkey).Display : "";
        }

        public Preset Model { get; }
        public int Index { get; }

        public string Name => Model.Name;
        public string Ip => Model.IP;
        public string Mask => IpTools.NormalizeMask(Model.Mask);
        public bool IsValid =>
            IpTools.IsHostAddress(Model.IP) && Mask != null &&
            new[] { Model.Gateway, Model.DNS1, Model.DNS2 }.All(v => string.IsNullOrWhiteSpace(v) || IpTools.IsIPv4(v));
        public string PrefixText => Mask != null ? "/" + IpTools.PrefixLength(Mask) : " bad mask";

        public string Subtitle => IpTools.IsIPv4(Model.Gateway) ? "Gateway " + Model.Gateway : "No gateway";

        public string Tooltip
        {
            get
            {
                var dns = string.Join(", ", new[] { Model.DNS1, Model.DNS2 }.Where(d => !string.IsNullOrWhiteSpace(d)));
                var key = HasHotkey ? $"\nShortcut: {HotkeyText}" : "";
                return $"Mask {Model.Mask}\nDNS {(dns.Length > 0 ? dns : "none")}{key}";
            }
        }
    }

    public sealed class StatusView
    {
        public static readonly StatusView Empty = new StatusView { Ip = "No adapter", LinkText = "Pick an adapter", ModeText = "n/a" };

        public string Ip { get; private set; }
        public string Prefix { get; private set; } = "";
        public bool IsDhcp { get; private set; }
        public string ModeText { get; private set; }
        public bool IsUp { get; private set; }
        public string LinkText { get; private set; }
        public string Gateway { get; private set; } = "none";
        public string Dns { get; private set; } = "none";
        public string Mac { get; private set; } = "";
        public string Extra { get; private set; } = "";
        public bool HasExtra => Extra.Length > 0;
        public string Note { get; private set; } = "";
        public bool HasNote => Note.Length > 0;

        public string FirstIp { get; private set; } = "";
        public string FirstMask { get; private set; } = "";
        public string FirstGateway { get; private set; } = "";
        public string Dns1 { get; private set; } = "";
        public string Dns2 { get; private set; } = "";
        public HashSet<string> AllIps { get; private set; } = new HashSet<string>();

        public static StatusView From(AdapterStatus s)
        {
            if (s == null) return Empty;
            var first = s.Addresses.FirstOrDefault();
            var v = new StatusView
            {
                Ip = first?.Ip ?? "No address",
                Prefix = first != null && first.Prefix > 0 ? "/" + first.Prefix : "",
                IsDhcp = s.IsDhcp,
                ModeText = s.IsDhcp ? "DHCP" : "Static",
                IsUp = s.IsUp,
                LinkText = s.IsUp ? "Connected" + FormatSpeed(s.Speed) : "No link",
                Gateway = s.Gateways.Count > 0 ? string.Join(", ", s.Gateways) : "none",
                Dns = s.Dns.Count > 0 ? string.Join(", ", s.Dns) : "none",
                Mac = s.Mac,
                Extra = string.Join(", ", s.Addresses.Skip(1).Select(a => a.Ip + "/" + a.Prefix)),
                FirstIp = first?.Ip ?? "",
                FirstMask = first != null && first.Prefix > 0 ? IpTools.PrefixToMask(first.Prefix) : "",
                FirstGateway = s.Gateways.FirstOrDefault() ?? "",
                Dns1 = s.Dns.ElementAtOrDefault(0) ?? "",
                Dns2 = s.Dns.ElementAtOrDefault(1) ?? "",
                AllIps = new HashSet<string>(s.Addresses.Select(a => a.Ip)),
            };
            if (s.FromSavedConfig) v.Note = "Saved settings. They go live when a cable is connected.";
            else if (s.IsDhcp && !s.IsUp) v.Note = "No cable or network. Connect to get an address.";
            else if (s.IsDhcp && first != null && first.Ip.StartsWith("169.254.")) v.Note = "No DHCP server answered, so Windows picked a fallback address.";
            return v;
        }

        static string FormatSpeed(long bps)
        {
            if (bps <= 0 || bps == long.MaxValue) return "";
            if (bps >= 1_000_000_000) return $", {bps / 1e9:0.#} Gbps";
            return $", {bps / 1e6:0} Mbps";
        }
    }

    /// <summary>One row in the Devices panel: a DHCP client, or a reservation not seen yet.</summary>
    public sealed class DeviceItem
    {
        public string Mac { get; set; }
        public string Ip { get; set; } = "";
        public string Name { get; set; }
        public string Detail { get; set; } = "";

        /// <summary>"active", "offered", "idle" (released or expired) or "waiting" (reserved, not seen yet).</summary>
        public string Kind { get; set; }
        public string StateText { get; set; }
        public string TimeLeft { get; set; } = "";
        public bool IsReserved { get; set; }
        public string ReservedIp { get; set; } = "";

        /// <summary>Shown when a device still holds an old address and will move to its reservation.</summary>
        public string MovingNote { get; set; } = "";
        public bool HasMovingNote => MovingNote.Length > 0;
        public bool HasDetail => Detail.Length > 0;

        /// <summary>MAC, then host name and vendor, on one muted line under the device name.</summary>
        public string SubLine => Detail.Length > 0 ? Mac + "  \u00B7  " + Detail : Mac;
        public bool CanOpen => Kind == "active" && Ip.Length > 0;
        public string AssignLabel => IsReserved ? "Change reserved IP" : "Reserve an IP for this device";
    }

    /// <summary>One row on the Scan page.</summary>
    public sealed class ScanItem
    {
        public string Ip { get; set; }
        public string Mac { get; set; } = "";
        public string Title { get; set; }
        public string Detail { get; set; } = "";
        public string Badge { get; set; } = "";
        public string PortsText { get; set; } = "";
        public string IpNote { get; set; } = "";
        public bool IsOtherSubnet { get; set; }
        public bool CanOpenWeb { get; set; }
        public string WebUrl { get; set; } = "";
        public bool HasBadge => Badge.Length > 0;
        public bool HasMac => Mac.Length > 0;
    }

    public sealed class LogEntry

    {
        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Text { get; set; }
        public string Kind { get; set; }
    }

    public sealed class PingResult
    {
        public string Text { get; set; }
        public bool Ok { get; set; }
    }
}
