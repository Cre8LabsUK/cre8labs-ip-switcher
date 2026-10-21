using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Cre8Labs.NetTools.Core;
using Cre8Labs.NetTools.Core.Tools;

namespace Cre8Labs.NetTools
{
    /// <summary>The Tools page: port check, RTSP check, traceroute, DNS lookup and link health.</summary>
    public partial class MainWindow
    {
        static readonly Dictionary<int, string> PortNames = new Dictionary<int, string>
        {
            [21] = "FTP", [22] = "SSH", [23] = "Telnet", [53] = "DNS", [80] = "Web", [443] = "Web (HTTPS)", [445] = "File sharing",
            [554] = "RTSP", [3389] = "Remote Desktop", [5000] = "Web (alt)", [8000] = "Hikvision SDK", [8080] = "Web (alt)",
            [8443] = "Web (alt HTTPS)", [37777] = "Dahua SDK", [34567] = "XMEye", [9000] = "Web (alt)",
        };

        readonly TileState _toolTile = new TileState();
        CancellationTokenSource _traceCts;
        LinkHealth _lastLink;
        DateTime _lastLinkAt;

        public ObservableCollection<PortChip> PortChips { get; } = new ObservableCollection<PortChip>();
        public ObservableCollection<TraceHop> TraceHops { get; } = new ObservableCollection<TraceHop>();
        public string PortSummary { get; private set; } = "";
        public string RtspSummary { get; private set; } = "";
        public string DnsResult { get; private set; } = "";
        public string LinkReport { get; private set; } = "Press Refresh to read the selected adapter's counters.";
        public string LinkAdvice { get; private set; } = "";
        public string TraceButtonText => _traceCts != null ? "Stop" : "Trace";

        /// <summary>The selected adapter's address for tools to send from, or null with a message shown.</summary>
        IPAddress ToolSource()
        {
            if (Status.IsUp && IpTools.IsIPv4(Status.FirstIp)) return IPAddress.Parse(Status.FirstIp);
            Notify("error", $"{SelectedAdapterName ?? "The adapter"} has no live address. Connect it, or apply a preset first.");
            return null;
        }

        // ------------------------------------------------------------------ port check

        async void PortCheck_Click(object sender, RoutedEventArgs e)
        {
            var source = ToolSource();
            if (source == null) return;
            var host = PortHostBox.Text.Trim();
            var ports = Diagnostics.ParsePorts(PortListBox.Text);
            if (ports == null) { Notify("error", "Use ports such as 80, 443, 554 or a range like 8000-8010 (up to 1,024)."); return; }

            IPAddress target;
            try { target = await Task.Run(() => PingService.Resolve(host)); }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentException) { Notify("error", $"Could not find {host}: {ex.Message}"); return; }

            PortChips.Clear();
            PortSummary = $"Checking {ports.Count} port{(ports.Count == 1 ? "" : "s")} on {target}...";
            Raise(nameof(PortSummary));
            var open = await Diagnostics.OpenPortsAsync(source, target, ports, CancellationToken.None);
            foreach (var port in ports)
                PortChips.Add(new PortChip { Port = port, Open = open.Contains(port), Name = PortNames.TryGetValue(port, out var n) ? n : "" });
            PortSummary = open.Count == 0
                ? $"No open ports on {target}. It may be off, on another subnet, or firewalled."
                : $"{open.Count} of {ports.Count} open on {target}.";
            Raise(nameof(PortSummary));
            AddLog(open.Count > 0 ? "ok" : "info", $"Port check {target}: open {(open.Count > 0 ? string.Join(", ", open) : "none")}");
        }

        // ------------------------------------------------------------------ RTSP

        async void RtspCheck_Click(object sender, RoutedEventArgs e)
        {
            var source = ToolSource();
            if (source == null) return;
            var url = RtspUrlBox.Text.Trim();
            if (url.Length == 0) { Notify("error", "Enter the camera's RTSP address."); return; }
            if (IpTools.IsIPv4(url)) url = $"rtsp://{url}:554/";
            RtspSummary = "Asking the camera...";
            Raise(nameof(RtspSummary));
            var result = await RtspCheck.RunAsync(source, url, RtspUserBox.Text.Trim(), RtspPasswordBox.Password);
            RtspSummary = result.Summary + (result.Server.Length > 0 ? $"  (server: {result.Server})" : "");
            Raise(nameof(RtspSummary));
            Notify(result.Ok ? "ok" : "error", "RTSP: " + result.Summary);
        }

        void RtspPassword_Changed(object sender, RoutedEventArgs e) =>
            RtspPasswordHint.Visibility = RtspPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ------------------------------------------------------------------ traceroute

        async void Trace_Click(object sender, RoutedEventArgs e)
        {
            if (_traceCts != null) { _traceCts.Cancel(); return; }
            var source = ToolSource();
            if (source == null) return;
            var host = TraceTargetBox.Text.Trim();
            IPAddress target;
            try { target = await Task.Run(() => PingService.Resolve(host)); }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentException) { Notify("error", $"Could not find {host}: {ex.Message}"); return; }

            TraceHops.Clear();
            _traceCts = new CancellationTokenSource();
            Raise(nameof(TraceButtonText));
            try { await Diagnostics.TraceAsync(source, target, 30, new Progress<TraceHop>(h => TraceHops.Add(h)), _traceCts.Token); }
            catch (OperationCanceledException) { }
            _traceCts = null;
            Raise(nameof(TraceButtonText));
            var reached = TraceHops.Any(h => h.Reached);
            AddLog(reached ? "ok" : "info", $"Traceroute to {target}: {TraceHops.Count} hops{(reached ? "" : ", destination not reached")}");
        }

        // ------------------------------------------------------------------ DNS

        async void DnsLookup_Click(object sender, RoutedEventArgs e)
        {
            var name = DnsNameBox.Text.Trim();
            var server = DnsServerBox.Text.Trim();
            if (name.Length == 0) { Notify("error", "Enter a name or IP to look up."); return; }
            if (server.Length > 0 && !IpTools.IsIPv4(server)) { Notify("error", "The DNS server must be an IPv4 address."); return; }
            var source = server.Length > 0 ? ToolSource() : IPAddress.Any;
            if (source == null) return;
            DnsResult = "Looking up...";
            Raise(nameof(DnsResult));
            try
            {
                var answers = await DnsQuery.LookupAsync(source, name, server);
                DnsResult = answers.Count == 0 ? "No answer." : string.Join("\n", answers);
            }
            catch (Exception ex) when (ex is SocketException || ex is TimeoutException || ex is InvalidOperationException || ex is ArgumentException)
            {
                DnsResult = ex.Message;
            }
            DnsResult = $"{(server.Length > 0 ? server : "Windows DNS")}:\n{DnsResult}";
            Raise(nameof(DnsResult));
        }

        // ------------------------------------------------------------------ link health

        void LinkRefresh_Click(object sender, RoutedEventArgs e)
        {
            var alias = SelectedAdapterName;
            var link = alias == null ? null : Diagnostics.ReadLink(alias);
            if (link == null) { LinkReport = "No adapter selected."; Raise(nameof(LinkReport)); return; }

            string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" : $"{bytes / 1024.0:0} KB";
            var rate = "";
            if (_lastLink != null && (DateTime.Now - _lastLinkAt).TotalSeconds < 600)
            {
                var seconds = (DateTime.Now - _lastLinkAt).TotalSeconds;
                rate = $"\nSince last refresh: in {Size((long)((link.BytesReceived - _lastLink.BytesReceived) / seconds))}/s, out {Size((long)((link.BytesSent - _lastLink.BytesSent) / seconds))}/s";
            }
            LinkReport =
                $"{alias}, {Status.LinkText}\n" +
                $"Received  {Size(link.BytesReceived),9}  {link.PacketsReceived,12:N0} packets\n" +
                $"Sent      {Size(link.BytesSent),9}  {link.PacketsSent,12:N0} packets\n" +
                $"Errors    in {link.ErrorsIn:N0}, out {link.ErrorsOut:N0}\n" +
                $"Discarded in {link.DiscardsIn:N0}, out {link.DiscardsOut:N0}" + rate;
            LinkAdvice = link.ErrorsIn + link.ErrorsOut > 0
                ? "Errors usually mean a damaged cable, a bad patch or a speed or duplex mismatch with the switch port."
                : "";
            _lastLink = link;
            _lastLinkAt = DateTime.Now;
            Raise(nameof(LinkReport));
            Raise(nameof(LinkAdvice));
        }
    }
}
