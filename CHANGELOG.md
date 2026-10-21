# Changelog

All notable changes to this project are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [semantic versioning](https://semver.org/).

## [Unreleased]

### Added

- **Navigation rail** with Adapter, DHCP pool and Scan pages. Ctrl+1, Ctrl+2 and Ctrl+3 switch pages, and can be reassigned
- **Scan page:** sweep a subnet or range from the selected adapter and see each device's IP, MAC, vendor, host name, latency and open ports (web, RTSP, Hikvision and Dahua SDK, SSH, Telnet, RDP)
- **Camera discovery** with ONVIF, UPnP and mDNS, which also finds factory-default cameras on other subnets
- **Reach it:** one click adds an address in a device's subnet to the adapter, then opens its web page
- **Vendor lookup** from the IEEE registry, shown in scan results and the DHCP Devices list
- Reserve a scanned device straight into the DHCP pool, and ping or open it from the results
- Ping results now show as a pop-up too

### Changed

- The DHCP Devices list moved from the bottom panel to its own page

## [1.1.0] - 2026-10-21


### Changed

- **Renamed to Cre8Labs Net Tools** (was Cre8Labs IP Switcher), now that it does more than switch IPs. The exe is `Cre8Labs-NetTools.exe` and the repository is `cre8labs-net-tools`. Upgrading keeps your presets, and the installer removes the old shortcuts.

### Added

- Keyboard shortcuts settings (keyboard button in the title bar): turn shortcuts on or off, reassign any key or combination per preset and for DHCP, New preset and Refresh, with conflict checks and Reset to defaults
- Optional background mode: shortcuts with Ctrl, Alt or Win also work while Net Tools is not focused
- Shortcut field in the preset editor. Shortcuts now belong to the preset, so reordering tiles keeps them
- Built-in DHCP pool (preview): serve a network you choose from a wired adapter, with a live Devices list, reservations so each camera always gets its planned address, Reserve all, and safety checks (wired adapters only, no overlap with other adapters, warning on live networks, stops if another DHCP server answers)
- Automated tests for the DHCP pool, run on every build
- `--demo` mode with sample adapters and presets, for screenshots and UI work. It never changes the network or saves presets.
- Screen reader names for icon-only buttons and preset tiles

### Fixed

- Typed text in input fields now lines up with the placeholder text
- The IP on the active preset tile no longer shifts up when the Active badge appears

## [1.0.0] - 2026-10-20

### Added

- One-click static IP presets (IP, mask, gateway, two DNS servers) and a DHCP tile
- Keyboard shortcuts: 1 to 9 for presets, 0 for DHCP, Ctrl+N, F5
- Live adapter status, including saved settings while the cable is unplugged
- Add as extra IP mode
- Preset editor with live subnet info and validation
- Quick ping bound to the selected adapter's address, activity log, and undo for deleted presets
- Portable mode (`presets.json` next to the exe)
- Installer with Start Menu and optional desktop shortcuts
