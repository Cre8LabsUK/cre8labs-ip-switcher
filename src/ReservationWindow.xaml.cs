using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Cre8Labs.IPSwitcher.Core;
using Cre8Labs.IPSwitcher.Core.Dhcp;

namespace Cre8Labs.IPSwitcher
{
    /// <summary>Adds, changes or removes one device's reserved address. Validation reuses the pool's own rules.</summary>
    public partial class ReservationWindow : Window
    {
        readonly PoolSettings _pool;
        readonly string _originalMac;

        /// <summary>The pool settings with the change applied. Only set when the dialog returns true.</summary>
        public PoolSettings Result { get; private set; }
        public bool Removed { get; private set; }

        /// <param name="mac">The device's MAC, or null to type one in (for a camera not plugged in yet).</param>
        public ReservationWindow(PoolSettings pool, string mac, string suggestedName, string suggestedIp)
        {
            InitializeComponent();
            _pool = pool;
            _originalMac = mac == null ? null : Reservation.NormalizeMac(mac);

            var existing = _originalMac == null ? null : pool.Reservations.FirstOrDefault(r => Reservation.NormalizeMac(r.Mac) == _originalMac);
            TitleText.Text = Title = existing != null ? "Change reservation" : "Reserve an IP";
            NameBox.Text = existing?.Name ?? suggestedName ?? "";
            MacBox.Text = _originalMac ?? "";
            MacBox.IsReadOnly = _originalMac != null;
            IpBox.Text = existing?.Ip ?? suggestedIp ?? "";
            IpBox.Tag = pool.Validate(out _)?.NetworkText ?? "";
            RemoveButton.Visibility = existing != null ? Visibility.Visible : Visibility.Collapsed;

            var minutes = Math.Max(1, pool.LeaseMinutes / 2);
            InfoText.Text = $"A device already on another address moves to this one at its next renewal, within about {minutes} minute{(minutes == 1 ? "" : "s")}. Restarting the device moves it straight away.";

            Loaded += (s, e) => { (string.IsNullOrEmpty(NameBox.Text) ? NameBox : IpBox).Focus(); };
        }

        static void ShowError(TextBox box, TextBlock label, string message)
        {
            label.Text = message ?? "";
            label.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
            if (message == null) box.ClearValue(BorderBrushProperty);
            else box.BorderBrush = (System.Windows.Media.Brush)box.FindResource("Danger");
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            var mac = Reservation.NormalizeMac(MacBox.Text);
            ShowError(MacBox, MacError, mac == null ? "Enter the 12-digit MAC, e.g. 44:19:B6:12:34:56." : null);
            ShowError(IpBox, IpError, IpTools.IsIPv4(IpBox.Text) ? null : "Enter an IPv4 address.");
            if (mac == null || !IpTools.IsIPv4(IpBox.Text)) return;

            var updated = _pool.Clone();
            updated.Reservations.RemoveAll(r => Reservation.NormalizeMac(r.Mac) == mac || (_originalMac != null && Reservation.NormalizeMac(r.Mac) == _originalMac));
            updated.Reservations.Add(new Reservation { Mac = mac, Ip = IpTools.Canonical(IpBox.Text), Name = NameBox.Text.Trim() });

            // The pool's validation catches clashes with other cameras, the server address and the network.
            if (updated.Validate(out var errors) == null)
            {
                var message = errors.TryGetValue(nameof(PoolSettings.Reservations), out var m) ? m : string.Join(" ", errors.Values);
                var colon = message.IndexOf(": ", StringComparison.Ordinal);
                ShowError(IpBox, IpError, colon >= 0 ? char.ToUpper(message[colon + 2]) + message.Substring(colon + 3) : message);
                return;
            }
            Result = updated;
            DialogResult = true;
        }

        void Remove_Click(object sender, RoutedEventArgs e)
        {
            var updated = _pool.Clone();
            updated.Reservations.RemoveAll(r => Reservation.NormalizeMac(r.Mac) == _originalMac);
            Result = updated;
            Removed = true;
            DialogResult = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
