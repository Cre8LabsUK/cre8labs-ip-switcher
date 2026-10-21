# Design: Scan page (1.2)

**Status:** built and unit tested; needs a bench check on real devices.

## How a scan works

All traffic leaves from the selected adapter's own address.

1. **Sweep** (`src/Core/Scan/SubnetScanner.cs`), 64 addresses at a time:
   - ARP via `SendARP`, which finds devices that ignore ping (same subnet only).
   - ICMP ping via `PingService`.
   - Reverse DNS with a 0.8 second limit.
   - A TCP connect check on ports 80, 443, 554, 8000, 37777, 22, 23 and 3389, with a 0.4 second limit each.
2. **Discovery** (`src/Core/Scan/Discovery.cs`), for 4 seconds alongside the sweep:
   - ONVIF WS-Discovery, typed and untyped probes to 239.255.255.250:3702. The device IP is taken from XAddrs.
   - SSDP M-SEARCH to 239.255.255.250:1900, then the device's own description XML for its name and model (only when the link points back at the device that answered).
   - An mDNS query for `_services._dns-sd._udp.local` with the unicast-response bit set.
3. Results are merged by IP. The vendor comes from `src/Core/Oui/oui.csv`, built by `tools/update-oui.ps1` from the IEEE registry.

Replies to discovery arrive as unicast answers to the app's own probes, which Windows Firewall allows for a few seconds after each multicast send, so discovery needs no extra firewall rule.

## Safety

- Sweeps are limited to a /22 (1,022 addresses).
- Ranges outside the adapter's subnet, or larger than 256 addresses, need a confirmation.
- XML from devices is parsed with DTDs and external entities turned off.
- Reach it only adds a second address, so the adapter's current address is kept, and it only works on a static adapter.

## Bench checklist

1. Scan a camera VLAN. Check that cameras show the right vendor, ports 80, 554 and 8000 or 37777, and that the web button opens them.
2. Plug in a factory-default camera on another subnet, with ONVIF enabled. It should appear with an ONVIF badge and "Other subnet".
3. Click Reach it, and check that its web page opens.
4. Check that an NVR or router appears with a UPnP badge and its model name.
5. Stop a scan halfway, and check it stops within a couple of seconds.
