using System.Windows;
using System.Windows.Controls;
using Cre8Labs.IPSwitcher.Core;

namespace Cre8Labs.IPSwitcher
{
    public partial class PresetEditorWindow : Window
    {
        readonly StatusView _current;

        public Preset Result { get; private set; }

        public PresetEditorWindow(Preset preset, StatusView current, string title)
        {
            InitializeComponent();
            _current = current;
            Title = title;
            TitleText.Text = title;

            var p = preset ?? new Preset();
            NameBox.Text = p.Name;
            IpBox.Text = p.IP;
            MaskBox.Text = p.Mask;
            GatewayBox.Text = p.Gateway;
            Dns1Box.Text = p.DNS1;
            Dns2Box.Text = p.DNS2;

            Loaded += (s, e) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
                UpdateInfo();
            };
        }

        void Field_Changed(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) UpdateInfo();
        }

        /// <summary>Live summary of the subnet plus soft warnings that do not block saving.</summary>
        void UpdateInfo()
        {
            var ip = IpBox.Text.Trim();
            var mask = IpTools.NormalizeMask(MaskBox.Text);
            var gateway = GatewayBox.Text.Trim();
            string warning = null;

            if (IpTools.IsIPv4(ip) && mask != null)
            {
                InfoText.Text = IpTools.Describe(ip, mask);
                var prefix = IpTools.PrefixLength(mask);
                var host = IpTools.ToUInt(ip) & ~IpTools.ToUInt(mask);
                if (prefix < 31 && (host == 0 || host == ~IpTools.ToUInt(mask)))
                    warning = "This is the network or broadcast address, so most devices will reject it.";
                else if (IpTools.IsIPv4(gateway) && !IpTools.SameSubnet(ip, gateway, mask))
                    warning = "The gateway is outside this subnet, so Windows will not be able to reach it.";
            }
            else
            {
                InfoText.Text = "Enter an IP address and mask";
            }

            WarningText.Text = warning ?? "";
            WarningRow.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        }

        bool Check(TextBox box, TextBlock error, string message)
        {
            var ok = message == null;
            error.Text = message ?? "";
            error.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (ok) box.ClearValue(BorderBrushProperty);
            else box.BorderBrush = (System.Windows.Media.Brush)FindResource("Danger");
            return ok;
        }

        static string Optional(string text) =>
            text.Trim().Length == 0 || IpTools.IsIPv4(text) ? null : "Use the format 0.0.0.0";

        static string Clean(string text) => IpTools.IsIPv4(text) ? IpTools.Canonical(text) : "";

        void Save_Click(object sender, RoutedEventArgs e)
        {
            var mask = IpTools.NormalizeMask(MaskBox.Text);
            var ok = Check(NameBox, NameError, NameBox.Text.Trim().Length == 0 ? "Give the preset a name." : null);
            ok &= Check(IpBox, IpError, IpTools.IsHostAddress(IpBox.Text) ? null : "Enter a valid IPv4 host address.");
            ok &= Check(MaskBox, MaskError, mask != null ? null : "Use 255.255.255.0 or /24.");
            ok &= Check(GatewayBox, GatewayError, Optional(GatewayBox.Text));
            ok &= Check(Dns1Box, Dns1Error, Optional(Dns1Box.Text));
            ok &= Check(Dns2Box, Dns2Error, Optional(Dns2Box.Text));
            if (!ok) return;

            Result = new Preset
            {
                Name = NameBox.Text.Trim(),
                IP = IpTools.Canonical(IpBox.Text),
                Mask = mask,
                Gateway = Clean(GatewayBox.Text),
                DNS1 = Clean(Dns1Box.Text),
                DNS2 = Clean(Dns2Box.Text),
            };
            DialogResult = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        void UseCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || string.IsNullOrEmpty(_current.FirstIp)) return;
            IpBox.Text = _current.FirstIp;
            MaskBox.Text = _current.FirstMask;
            GatewayBox.Text = _current.FirstGateway;
            Dns1Box.Text = _current.Dns1;
            Dns2Box.Text = _current.Dns2;
        }
    }
}
