<p align="center">
  <img src="docs/logo.png" width="96" alt="Cre8Labs logo">
</p>

<h1 align="center">Cre8Labs IP Switcher</h1>

<p align="center">
  Switch your Windows network adapter between saved IP addresses in one click.<br>
  Built for engineers who plug into cameras, switches, PLCs and routers all day.
</p>

<p align="center">
  <a href="../../releases/latest"><img src="https://img.shields.io/badge/download-latest-14AFCB" alt="Download"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-7F23A6" alt="Windows 10 and 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-104F85" alt="MIT license"></a>
</p>

<p align="center">
  <img src="docs/screenshot.png" alt="Cre8Labs IP Switcher main window" width="820">
</p>

## Why

Changing a static IP in Windows means five dialogs deep in Control Panel, every time. If you commission or service network kit, you do that dozens of times a day. IP Switcher keeps your networks as presets and applies one with a click or a single key press.

## Features

- **One-click presets** for IP, subnet mask, gateway and two DNS servers
- **Back to DHCP** in one click (or press `0`)
- **Keyboard shortcuts**: `1` to `9` apply a preset, `0` is DHCP, `Ctrl+N` new preset, `F5` refresh
- **Live adapter status**: address, DHCP or static, link speed, gateway, DNS and MAC, updated automatically
- **Works with the cable unplugged**: set the IP first, then plug in. Saved settings are shown until the link comes up
- **Add as extra IP**: add a preset's address alongside the current one, handy for reaching a device on a second subnet
- **Quick ping** to the gateway or any address, always sent from the selected adapter (even when Wi-Fi or a VPN is on the same subnet)
- **Active preset highlight**, so you always know which network you are on
- **Smart defaults**: wired Ethernet is picked before Wi-Fi, VPN and virtual adapters
- **Preset editor** with live subnet info and warnings, for example when the gateway is outside the subnet
- **Undo** for deleted presets, and an activity log of every change
- **Portable mode**: keep `presets.json` next to the exe and carry it on a USB stick

<p align="center">
  <img src="docs/preset-editor.png" alt="Preset editor" width="420">
</p>

## Download

Get the latest version from the [Releases page](../../releases/latest).

| File | Use it when |
|---|---|
| `Cre8Labs-IPSwitcher-Setup-x.y.z.exe` | You want a normal install with a Start Menu shortcut and an uninstaller |
| `Cre8Labs-IPSwitcher-x.y.z-portable.exe` | You want a single exe with no install, for example on a USB stick |

**Requirements:** Windows 10 (May 2019 update or later) or Windows 11. .NET Framework 4.8 is already part of these versions of Windows.

> **Windows SmartScreen:** the builds are not code-signed yet, so Windows may say it "protected your PC". Click **More info**, then **Run anyway**. You can check a download against `SHA256SUMS.txt` on the release page with `Get-FileHash <file>` in PowerShell.

## Usage

1. Start **Cre8Labs IP Switcher** and accept the administrator prompt. Windows only lets administrators change IP settings.
2. Pick your network adapter on the left.
3. Click a preset tile, or press its number key.

Right-click a tile (or use its `...` button) to edit, duplicate, reorder or delete it.

### Where presets are stored

| Mode | Location |
|---|---|
| Installed | `%APPDATA%\Cre8Labs\IP Switcher\presets.json` |
| Portable | `presets.json` in the same folder as the exe (used whenever that file exists) |

The file is plain JSON, so you can back it up or share it with your team:

```json
{
  "Adapter": "Ethernet",
  "Presets": [
    {
      "Name": "Site A cameras",
      "IP": "192.168.1.250",
      "Mask": "255.255.255.0",
      "Gateway": "",
      "DNS1": "",
      "DNS2": ""
    }
  ]
}
```

Uninstalling leaves your presets in place.

## How it works

IP Switcher is a small WPF app. It reads adapter status with the .NET `NetworkInterface` API and changes settings by running Windows' own `netsh` tool, for example:

```
netsh interface ipv4 set address name="Ethernet" source=static address=192.168.1.250 mask=255.255.255.0 gateway=none
netsh interface ipv4 set address name="Ethernet" source=dhcp
```

It does not install drivers or services, does not run in the background, and makes no network connections other than the pings you start yourself.

## Building from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (any recent version) and, for the installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
winget install JRSoftware.InnoSetup
powershell -ExecutionPolicy Bypass -File build.ps1
```

The app, installer and checksums are written to `dist\`.

For quick UI work without the admin prompt, build a read-only preview:

```powershell
dotnet build src -c Release -p:NoElevate=true -o preview-build
```

### Project layout

| Path | What is in it |
|---|---|
| `src/Core/` | Networking, IP maths and preset storage, with no UI code |
| `src/*.xaml` | Windows, styles and the Cre8Labs theme (`App.xaml`) |
| `installer/` | Inno Setup script and wizard artwork |
| `.github/workflows/` | CI build, and releases when a `v*` tag is pushed |

### Releasing

Bump `<Version>` in `src/IPSwitcher.csproj`, add an entry to `CHANGELOG.md`, then push a tag:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

GitHub Actions builds the installer and portable exe and attaches them to a new release.

## Contributing

Bug reports, ideas and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). To report a security problem, see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © 2026 Cre8Labs. The Cre8Labs name and logo are trademarks of Cre8Labs and are not covered by the MIT license.

<p align="center"><a href="https://cre8labs.uk">cre8labs.uk</a></p>
