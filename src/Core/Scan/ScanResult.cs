using System;
using System.Collections.Generic;
using System.Linq;

namespace Cre8Labs.NetTools.Core.Scan
{
    [Flags]
    public enum FoundBy { None = 0, Arp = 1, Ping = 2, Onvif = 4, Ssdp = 8, Mdns = 16 }

    /// <summary>One device found by a scan or a discovery protocol. Results for the same IP are merged.</summary>
    public sealed class ScanResult
    {
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Hostname { get; set; } = "";
        public long? LatencyMs { get; set; }
        public List<int> OpenPorts { get; set; } = new List<int>();
        public FoundBy Sources { get; set; }

        /// <summary>From ONVIF scopes or the UPnP description.</summary>
        public string DeviceName { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";

        public uint IpValue => IpTools.IsIPv4(Ip) ? IpTools.ToUInt(Ip) : 0;

        /// <summary>Copies anything new from <paramref name="other"/> (same IP) into this result.</summary>
        public void Merge(ScanResult other)
        {
            if (Mac.Length == 0) Mac = other.Mac;
            if (Vendor.Length == 0) Vendor = other.Vendor;
            if (Hostname.Length == 0) Hostname = other.Hostname;
            if (LatencyMs == null) LatencyMs = other.LatencyMs;
            OpenPorts = OpenPorts.Union(other.OpenPorts).OrderBy(p => p).ToList();
            Sources |= other.Sources;
            if (DeviceName.Length == 0) DeviceName = other.DeviceName;
            if (Manufacturer.Length == 0) Manufacturer = other.Manufacturer;
            if (Model.Length == 0) Model = other.Model;
        }
    }
}
