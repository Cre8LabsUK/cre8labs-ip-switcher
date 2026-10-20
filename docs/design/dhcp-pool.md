# Design: built-in DHCP pool

**Status:** core, safety checks, reservations and UI built and unit tested. Phase 1 (validation on real Windows networking and devices) in progress.
**Goal:** hand out IP addresses to devices plugged into the selected adapter, and show which device got which address.

## Why

Factory-reset or brand-new kit (IP cameras, NVRs, access panels, switches, IoT controllers) often starts on DHCP. On a bench, or with a laptop plugged straight into a device, there is no router to give it an address, so you cannot find or reach it. Today engineers carry a separate tool (Tftpd64, a travel router, or a spare switch with DHCP) for this. IP Switcher already sets static IPs on the laptop, so it can also own a small network end to end:

1. Choose the network the pool should serve, for example `192.168.1.0/24`, either typed in or taken from a preset.
2. Click **Start pool**. IP Switcher gives the adapter its server address in that network (for example `192.168.1.1`) and starts handing out the rest of the range.
3. Plug in the device. It appears in a lease table with its MAC, vendor, hostname and new IP.
4. Click the IP to open its web page, ping it, or copy it.
5. Click **Stop pool**. The adapter goes back to the preset or DHCP setting it had before.

## Scope

**In scope (v1)**

- A DHCPv4 server built into the app, running only while the user has it switched on.
- One pool at a time, on the selected adapter.
- **The pool is defined by a network you assign to it**, not by whatever the adapter currently has:
  - **Network:** address and mask, for example `192.168.1.0/24` (typed, or "Use network from preset").
  - **Server address:** the PC's own IP in that network, `.1` by default, editable.
  - **Range:** start and end, suggested as `.100` to `.199` of a /24 (or the same proportion of other sizes), editable.
  - **Lease time**, optional **gateway** (option 3) and optional **DNS** (option 6).
- Starting the pool **assigns the adapter** its server address and mask (static, no gateway), using the same netsh path as presets. Stopping restores the adapter's previous settings.
- A live lease table: MAC, vendor, hostname, IP, time left, plus actions (open in browser, ping, copy, release).
- Safety checks that stop it from ever acting as a rogue DHCP server on a real network (see below).

**Out of scope for v1, possible later**

- Reservations (fixed IP per MAC), PXE or TFTP boot options, DHCPv6, relay agents, several pools at once.

## User experience

- New **DHCP pool** card under Quick ping. Its main field is **Network** (`192.168.1.0/24`), with a dropdown of networks taken from your presets. The server address and range fill in automatically, and can be expanded under "Advanced".
- **Start pool** shows exactly what will happen before it does it: `Ethernet will be set to 192.168.1.1/24 and serve 192.168.1.100 to 192.168.1.199`.
- While running, the card shows `Serving 192.168.1.100 to 192.168.1.199 on Ethernet`, and the adapter panel shows the server address.
- The range always skips the server address, the network and broadcast addresses, and the gateway if one is set.
- While running, a coloured banner across the top: **DHCP pool running on Ethernet**, with a Stop button, so it is never left on by accident.
- A toast for each new lease: `Hikvision camera got 192.168.1.101`.
- The lease table opens from the card ("3 devices") as a panel, the same way the Activity log does.
- Presets can optionally carry pool settings, with a **Start the DHCP pool when applied** checkbox in the preset editor, for example for "Site A cameras".

## Safety rules

A DHCP server on the wrong network can take down a whole office, so the server refuses to start, with a clear explanation, when any of these is true:

1. The adapter currently has a default gateway or a DHCP lease from a router. That usually means it is plugged into a real network. The user must confirm ("This adapter looks connected to a live network") before the pool takes it over.
2. The assigned network overlaps another adapter's network on this PC (for example Wi-Fi is also `192.168.1.0/24`). Serving it would confuse routing on the laptop, so starting is blocked with the name of the clashing adapter.
3. **Another DHCP server answers.** Before starting, send a DHCPDISCOVER probe on the adapter and listen for about 3 seconds. Any DHCPOFFER blocks the start and names the server.
4. The adapter is Wi-Fi, VPN or virtual, unless the user explicitly allows it.

While running:

- It only answers on the chosen adapter, by checking the receiving interface of every packet.
- It never hands out the PC's own address or the gateway, and it pings each address before offering it, so an address a device already uses is skipped.
- It stops automatically if anything else changes the adapter's IP (another preset, Windows, or another tool), and when the app closes. Unplugging the cable pauses it instead, so swapping devices on a bench does not stop the pool.
- Before taking over the adapter it saves the current settings, and **Stop pool** puts them back.
- Every lease, decline and stop is written to the Activity log.

## Technical design

### Components (in `src/Core/Dhcp/`, with no UI code)

| File | Responsibility |
|---|---|
| `DhcpPacket.cs` | Parse and build BOOTP/DHCP packets (RFC 2131) and options (RFC 2132) |
| `LeasePool.cs` | Pick addresses, track leases and expiry, handle the conflict and decline lists |
| `DhcpServer.cs` | Socket I/O, the DISCOVER/OFFER/REQUEST/ACK state machine, start and stop |
| `RogueServerProbe.cs` | Send one DISCOVER and collect any OFFERs before start |
| `OuiLookup.cs` | MAC prefix to vendor name, from a small bundled list |

The UI gets a `DhcpPoolViewModel` with an `ObservableCollection<LeaseItem>`, updated from server events through the dispatcher.

### Protocol coverage

- Messages: DISCOVER, OFFER, REQUEST (selecting, renewing and rebinding), ACK, NAK, DECLINE, RELEASE, INFORM.
- Options sent: 1 subnet mask, 3 router (optional), 6 DNS (optional), 51 lease time, 53 message type, 54 server identifier, 58 and 59 renewal and rebinding times.
- Options read: 12 hostname, 50 requested IP, 55 parameter request list, 60 vendor class, 61 client identifier.
- The broadcast flag is honoured. Replies go to 255.255.255.255:68 when the client has no address yet, otherwise unicast.

### Sockets on Windows (checked first, in a spike)

- One UDP socket bound to `0.0.0.0:67` with `SO_BROADCAST`, because client broadcasts to 255.255.255.255 are only delivered to a wildcard bind. Packets are filtered by the arriving interface using `IP_PKTINFO` (`Socket.ReceiveMessageFrom`).
- Replies use a second socket bound to the adapter's IP, so broadcasts leave through the right adapter.
- Port 67 may already be taken by Internet Connection Sharing (the SharedAccess service), the Hyper-V Default Switch, or another DHCP tool. On a bind failure, look up the owning process (`GetExtendedUdpTable`) and name it in the error.

### Windows Firewall

Inbound UDP 67 is blocked by default. The installer adds a program-scoped rule, removed on uninstall:

```
netsh advfirewall firewall add rule name="Cre8Labs IP Switcher DHCP" dir=in action=allow protocol=UDP localport=67 program="{app}\Cre8Labs-IPSwitcher.exe"
```

The portable exe asks once and adds the same rule at runtime, since it already runs elevated.

### Settings storage

The last pool settings are stored in `presets.json` under `"Pool"` (network, mask, server address, start, end, lease minutes, gateway, DNS), so the next start is one click. Presets can also carry their own optional `"Pool"` object with an auto-start flag. Older files without them keep working.

## Testing

- **Unit tests** (new `tests/` xUnit project, run in CI): packet encoding and decoding against captured real packets, pool allocation, expiry, NAK cases, and the address and option edge cases.
- **Integration test:** a Hyper-V VM on an internal switch, or a spare device, with `ipconfig /release` and `/renew` in a loop. Check leases, renewals and release.
- **Real devices** before release: at least one IP camera brand, a managed switch and a Windows laptop. Use Wireshark to confirm packets are correct and never leave on other adapters.
- **Safety tests:** start with a router present (must refuse), on Wi-Fi (must refuse), on a DHCP adapter (must refuse), and unplug the cable while running (must stop).

## Delivery plan

| Phase | Work | Result |
|---|---|---|
| 1. Spike | Socket binding, interface filtering, firewall rule, detecting what holds port 67 | A go or no-go on the approach, with findings |
| 2. Core | `DhcpPacket`, `LeasePool`, `DhcpServer`, plus unit tests and the CI test step | A server that leases correctly in the VM test |
| 3. Safety | Rogue server probe, the start rules, auto-stop, and Activity log entries | It refuses to start anywhere unsafe |
| 4. UI | Pool card, running banner, lease table, new-device toasts, demo-mode data | Usable end to end |
| 5. Extras | Vendor lookup, pool settings on presets with auto-start, CSV export of leases | Release as v1.2 |

## Open questions

1. **Server address:** `.1` is the usual choice, but some kit defaults to `.1` itself (many routers do). Should the default be `.1`, or the top of the range, for example `.254`?
2. **Gateway option:** by default, offer no gateway (safest, devices stay local) or offer the PC's own IP? No gateway is suggested.
3. **Default lease time:** short (10 minutes) keeps the table fresh while devices are swapped. Longer suits a device that stays plugged in.
4. **Vendor list size:** a curated list of common security and network vendors (about 50 KB), or the full IEEE OUI list (about 1 MB in the exe)?
5. **Reservations:** do you need "this MAC always gets this IP" in v1, for example for repeat site visits?
